import { bindCallbacks } from "./callbacks-preparation.js";
import { waitForIceGatheringComplete } from "./ice-utilities.js";
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

    const log = (message: string) => console.log(`[RTC][${processId}][${role}] ${message}`);
    const logError = (message: string, error?: unknown) => console.error(`[RTC][${processId}][${role}] ${message}`, error);

    log(`starting connection. iceServers=${iceServers.length}`);

    const peerConnection = iceServers?.length ? new RTCPeerConnection({ iceServers }) : new RTCPeerConnection();

    peerConnection.onconnectionstatechange = () => log(`connection state: ${peerConnection.connectionState}`);
    peerConnection.oniceconnectionstatechange = () => log(`ICE connection state: ${peerConnection.iceConnectionState}`);
    peerConnection.onsignalingstatechange = () => log(`signaling state: ${peerConnection.signalingState}`);
    peerConnection.onicegatheringstatechange = () => log(`ICE gathering state: ${peerConnection.iceGatheringState}`);
    peerConnection.onicecandidate = (e) => log(`ICE candidate: ${e.candidate ? `${e.candidate.type} ${e.candidate.protocol} ${e.candidate.address}` : "(end of candidates)"}`);

    const { getMediaState, setMediaState, setVideoTarget, setLocalVideoTarget } = bindMediaManager(peerConnection, isInitator, undefined, log);
    const { unbind, writeToChannel, whenConnectionOpened, handleDataChannel } = bindCallbacks(peerConnection, callbacks, log);

    if (isInitator) {
        const dataChannel = peerConnection.createDataChannel("data");
        handleDataChannel(dataChannel);
    }

    let id: RtcConnectionId;

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
        log(`local offer created. type=${offer.type}, hasSdp=${Boolean(offer.sdp)}`);
        await peerConnection.setLocalDescription(offer);

        await waitForIceGatheringComplete(peerConnection, 10000, log);

        const { answer, connectionId } = await exchangeInfo(peerConnection.localDescription!);
        
        if (!answer?.type || !answer?.sdp) {
            log(`no direct answer returned; it means the outgoing offer has been superseeded.`);
            unbind();
            peerConnection.close();
            return null; 
        }

        log(`remote answer received. type=${answer?.type}, hasSdp=${Boolean(answer?.sdp)}`);

        await peerConnection.setRemoteDescription(answer);

        id = connectionId;
    }
    else {
        const { offer, sendAnswerBack } = exchangeInfo;
        log(`remote offer received. type=${offer?.type}, hasSdp=${Boolean(offer?.sdp)}`);

        await peerConnection.setRemoteDescription(offer);

        // Chrome initialises auto-created transceivers as recvonly.
        // Explicitly set all of them to sendrecv before creating the answer
        // so both sides can send and receive audio/video.
        peerConnection.getTransceivers()
            .filter(t => t.direction !== "stopped")
            .forEach(t => { t.direction = "sendrecv"; });

        const answer = await peerConnection.createAnswer();

        if (!answer) { return null; }

        log(`local answer created. type=${answer?.type}, hasSdp=${Boolean(answer?.sdp)}`);
        await peerConnection.setLocalDescription(answer);

        await waitForIceGatheringComplete(peerConnection, 10000, log);

        const connectionId = await sendAnswerBack(peerConnection.localDescription!);

        if (!connectionId) {
            log(`local answer has not been accepted by the remote peer.`);
            unbind();
            peerConnection.close();
            return null;
        }

        id = connectionId;

        log(`local answer sent back.`);

        log(`waiting for connection completion ${id}.`);
    }

    try {
        
        await whenConnectionOpened;
        log(`RTC connection connected.`);
    } catch (e) {
        logError(`failed before channel open.`, e);
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