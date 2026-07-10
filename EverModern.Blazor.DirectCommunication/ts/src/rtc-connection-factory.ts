import { bindCallbacks } from "./callbacks-preparation.js";
import { createLogger, Logger, sdpChecksum, waitForIceGatheringComplete } from "./ice-utilities.js";
import { bindMediaManager } from "./media-manager.js";
import { RtcConnectionCallbacks } from "./rtc-connection.js";

type IceServerParameters = {
    urls: string[];
    username?: string;
    credential?: string;
};

type RtcConnectionId = string | number;

type DotNetObjectReference = {
    invokeMethodAsync<T = unknown>(methodName: string, ...args: unknown[]): Promise<T>;
};

type OfferAnswerExchange = {
    offer: RTCSessionDescriptionInit;
    sendAnswerBack: (answer: RTCSessionDescriptionInit) => Promise<RtcConnectionId>;
}

type RtcNegotiationAnswer = {
    answer: RTCSessionDescriptionInit;
    connectionId: RtcConnectionId;
}

type ConnectionDescriptionExchangeInfo = ((offer: RTCSessionDescriptionInit) => Promise<RtcNegotiationAnswer>) | OfferAnswerExchange;

let lastProcessId = 0;


async function createRtcConnection(
    exchangeInfo: ConnectionDescriptionExchangeInfo,
    iceServers: RTCIceServer[],
    callbacks: RtcConnectionCallbacks
) {
    const isInitator = typeof exchangeInfo === "function";
    const role = isInitator ? "initiator" : "acceptor";
    const processId = ++lastProcessId;

    const logger = createLogger(`RTC:${processId}:${role}`);

    logger.info(`starting connection. iceServers=${iceServers.length}`);

    const peerConnection = iceServers?.length ? new RTCPeerConnection({ iceServers }) : new RTCPeerConnection();

    // Low-level ICE / signaling / transport events — purely diagnostic.
    peerConnection.oniceconnectionstatechange = () =>
        logger.debug(`ICE connection state: ${peerConnection.iceConnectionState}`);
    peerConnection.onsignalingstatechange = () =>
        logger.debug(`signaling state: ${peerConnection.signalingState}`);
    peerConnection.onicegatheringstatechange = () =>
        logger.debug(`ICE gathering state: ${peerConnection.iceGatheringState}`);
    peerConnection.onicecandidate = (e) =>
        logger.debug(`ICE candidate: ${e.candidate ? `${e.candidate.type} ${e.candidate.protocol} ${e.candidate.address}` : "(end of candidates)"}`);

    // NOTE: peerConnection.onconnectionstatechange is set inside bindCallbacks
    // so logging + external callback happen in a single handler.

    const { getMediaState, setMediaState, setVideoTarget, setLocalVideoTarget } =
        bindMediaManager(peerConnection, isInitator, undefined, logger);
    const { unbind, writeToChannel, whenConnectionOpened, handleDataChannel } =
        bindCallbacks(peerConnection, callbacks, logger);

    if (isInitator) {
        const dataChannel = peerConnection.createDataChannel("data");
        handleDataChannel(dataChannel);
    } 

    let id: RtcConnectionId;

    const logSdp = (label: string, sdp: string | undefined) => {
        if (!sdp) return;
        const checksum = sdpChecksum(sdp);
        const lines = sdp.split("\r\n").length;
        logger.info(`${label} sdpChecksum=${checksum} sdpLines=${lines}`);
    };

    const connectionManager = {
        close: () => {
            unbind();
            peerConnection.close();
        }, getState: () => peerConnection.connectionState,
        writeToChannel,
        getMediaState,
        setMediaState,
        setVideoTarget,
        setLocalVideoTarget,
        peerConnection,
        getId: () => id
    };

    if (isInitator) {
        const offer = await peerConnection.createOffer();
        logger.info(`local offer created. type=${offer.type}`);
        logSdp("local-offer", offer.sdp);
        await peerConnection.setLocalDescription(offer);

        await waitForIceGatheringComplete(peerConnection, 10000, logger);

        const { answer, connectionId } = await exchangeInfo(peerConnection.localDescription!);

        if (!answer?.type || !answer?.sdp) {
            logger.info(`no direct answer returned; the outgoing offer has been superseded.`);
            unbind();
            peerConnection.close();
            return null;
        }

        logger.info(`remote answer received. type=${answer.type}`);
        logSdp("remote-answer", answer.sdp);

        await peerConnection.setRemoteDescription(answer);

        id = connectionId;
    }
    else {
        const { offer, sendAnswerBack } = exchangeInfo;
        logger.info(`remote offer received. type=${offer?.type}`);
        logSdp("remote-offer", offer?.sdp);

        await peerConnection.setRemoteDescription(offer);

        // Chrome initialises auto-created transceivers as recvonly.
        // Explicitly set all of them to sendrecv before creating the answer
        // so both sides can send and receive audio/video.
        peerConnection.getTransceivers()
            .filter(t => t.direction !== "stopped")
            .forEach(t => { t.direction = "sendrecv"; });

        const answer = await peerConnection.createAnswer();

        if (!answer) { return null; }

        logger.info(`local answer created. type=${answer?.type}`);
        logSdp("local-answer", answer.sdp);
        await peerConnection.setLocalDescription(answer);

        await waitForIceGatheringComplete(peerConnection, 10000, logger);

        const connectionId = await sendAnswerBack(peerConnection.localDescription!);

        if (!connectionId) {
            logger.info(`local answer has not been accepted by the remote peer.`);
            unbind();
            peerConnection.close();
            return null;
        }

        id = connectionId;

        logger.info(`local answer sent back. waiting for connection completion ${id}.`);
    }

    try {

        await whenConnectionOpened;
        logger.info(`RTC connection connected.`);
    } catch (e) {
        logger.error(`failed before channel open.`, e);
        unbind();
        peerConnection.close();
        throw e;
    }

    if (!(window as any).rtcConnectionManagers) {
        (window as any).rtcConnectionManagers = [connectionManager];
    } else {
        (window as any).rtcConnectionManagers.push(connectionManager);
    }

    return connectionManager;
}

async function initiateConnectionAsync(
    iceServers: IceServerParameters[],
    getAnswerAsync: DotNetObjectReference,
    onStateChangedAsync: DotNetObjectReference,
    onDataChannelMessageAsync: DotNetObjectReference
) {
    const getAnswer = (offer: RTCSessionDescriptionInit) => getAnswerAsync.invokeMethodAsync<RtcNegotiationAnswer>("invoke", offer);
    const onStateChanged = (state: RTCPeerConnectionState) => onStateChangedAsync.invokeMethodAsync("invoke", state) as Promise<void>;
    const onDataChannelMessage = (message: string) => onDataChannelMessageAsync.invokeMethodAsync("invoke", message) as Promise<void>;

    const connectionManager = await createRtcConnection(getAnswer, iceServers, { onStateChanged, onDataChannelMessage });

    return connectionManager;
}

async function acceptConnectionAsync(
    iceServers: IceServerParameters[],
    offer: RTCSessionDescriptionInit,
    sendAnswerBackAsync: DotNetObjectReference,
    onStateChangedAsync: DotNetObjectReference,
    onDataChannelMessageAsync: DotNetObjectReference
) {
    const onStateChanged = (state: RTCPeerConnectionState) => onStateChangedAsync.invokeMethodAsync("invoke", state) as Promise<void>;
    const onDataChannelMessage = (message: string) => onDataChannelMessageAsync.invokeMethodAsync("invoke", message) as Promise<void>;
    const sendAnswerBack = (answer: RTCSessionDescriptionInit) => sendAnswerBackAsync.invokeMethodAsync("invoke", answer) as Promise<RtcConnectionId>;
    const connectionManager = await createRtcConnection({ offer, sendAnswerBack }, iceServers, { onStateChanged, onDataChannelMessage });

    return connectionManager;
}

export const rtcConnectionFactory = { initiateConnectionAsync, acceptConnectionAsync }; 