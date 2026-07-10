import { bindCallbacks } from "./callbacks-preparation.js";
import { createLogger, sdpChecksum, waitForIceGatheringComplete } from "./ice-utilities.js";
import { bindMediaManager } from "./media-manager.js";
let lastProcessId = 0;
async function createRtcConnection(exchangeInfo, iceServers, callbacks) {
    const isInitator = typeof exchangeInfo === "function";
    const role = isInitator ? "initiator" : "acceptor";
    const processId = ++lastProcessId;
    const log = createLogger(`RTC:${processId}:${role}`);
    log.info(`starting connection. iceServers=${iceServers.length}`);
    const peerConnection = iceServers?.length ? new RTCPeerConnection({ iceServers }) : new RTCPeerConnection();
    // Low-level ICE / signaling / transport events — purely diagnostic.
    peerConnection.oniceconnectionstatechange = () => log.debug(`ICE connection state: ${peerConnection.iceConnectionState}`);
    peerConnection.onsignalingstatechange = () => log.debug(`signaling state: ${peerConnection.signalingState}`);
    peerConnection.onicegatheringstatechange = () => log.debug(`ICE gathering state: ${peerConnection.iceGatheringState}`);
    peerConnection.onicecandidate = (e) => log.debug(`ICE candidate: ${e.candidate ? `${e.candidate.type} ${e.candidate.protocol} ${e.candidate.address}` : "(end of candidates)"}`);
    // NOTE: peerConnection.onconnectionstatechange is set inside bindCallbacks
    // so logging + external callback happen in a single handler.
    const { getMediaState, setMediaState, setVideoTarget, setLocalVideoTarget } = bindMediaManager(peerConnection, isInitator, undefined, log);
    const { unbind, writeToChannel, whenConnectionOpened, handleDataChannel } = bindCallbacks(peerConnection, callbacks, log);
    if (isInitator) {
        const dataChannel = peerConnection.createDataChannel("data");
        handleDataChannel(dataChannel);
    }
    let id;
    const logSdp = (label, sdp) => {
        if (!sdp)
            return;
        const checksum = sdpChecksum(sdp);
        const lines = sdp.split("\r\n").length;
        log.info(`${label} sdpChecksum=${checksum} sdpLines=${lines}`);
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
        log.info(`local offer created. type=${offer.type}`);
        logSdp("local-offer", offer.sdp);
        await peerConnection.setLocalDescription(offer);
        await waitForIceGatheringComplete(peerConnection, 10000, log);
        const { answer, connectionId } = await exchangeInfo(peerConnection.localDescription);
        if (!answer?.type || !answer?.sdp) {
            log.info(`no direct answer returned; the outgoing offer has been superceeded.`);
            unbind();
            peerConnection.close();
            return null;
        }
        log.info(`remote answer received. type=${answer.type}`);
        logSdp("remote-answer", answer.sdp);
        await peerConnection.setRemoteDescription(answer);
        id = connectionId;
    }
    else {
        const { offer, sendAnswerBack } = exchangeInfo;
        log.info(`remote offer received. type=${offer?.type}`);
        logSdp("remote-offer", offer?.sdp);
        await peerConnection.setRemoteDescription(offer);
        // Chrome initialises auto-created transceivers as recvonly.
        // Explicitly set all of them to sendrecv before creating the answer
        // so both sides can send and receive audio/video.
        peerConnection.getTransceivers()
            .filter(t => t.direction !== "stopped")
            .forEach(t => { t.direction = "sendrecv"; });
        const answer = await peerConnection.createAnswer();
        if (!answer) {
            return null;
        }
        log.info(`local answer created. type=${answer?.type}`);
        logSdp("local-answer", answer.sdp);
        await peerConnection.setLocalDescription(answer);
        await waitForIceGatheringComplete(peerConnection, 10000, log);
        const connectionId = await sendAnswerBack(peerConnection.localDescription);
        if (!connectionId) {
            log.info(`local answer has not been accepted by the remote peer.`);
            unbind();
            peerConnection.close();
            return null;
        }
        id = connectionId;
        log.info(`local answer sent back. waiting for connection completion ${id}.`);
    }
    try {
        await whenConnectionOpened;
        log.info(`RTC connection connected.`);
    }
    catch (e) {
        log.error(`failed before channel open.`, e);
        unbind();
        peerConnection.close();
        throw e;
    }
    if (!window.rtcConnectionManagers) {
        window.rtcConnectionManagers = [connectionManager];
    }
    else {
        window.rtcConnectionManagers.push(connectionManager);
    }
    return connectionManager;
}
async function initiateConnectionAsync(iceServers, getAnswerAsync, onStateChangedAsync, onDataChannelMessageAsync) {
    const getAnswer = (offer) => getAnswerAsync.invokeMethodAsync("invoke", offer);
    const onStateChanged = (state) => onStateChangedAsync.invokeMethodAsync("invoke", state);
    const onDataChannelMessage = (message) => onDataChannelMessageAsync.invokeMethodAsync("invoke", message);
    const connectionManager = await createRtcConnection(getAnswer, iceServers, { onStateChanged, onDataChannelMessage });
    return connectionManager;
}
async function acceptConnectionAsync(iceServers, offer, sendAnswerBackAsync, onStateChangedAsync, onDataChannelMessageAsync) {
    const onStateChanged = (state) => onStateChangedAsync.invokeMethodAsync("invoke", state);
    const onDataChannelMessage = (message) => onDataChannelMessageAsync.invokeMethodAsync("invoke", message);
    const sendAnswerBack = (answer) => sendAnswerBackAsync.invokeMethodAsync("invoke", answer);
    const connectionManager = await createRtcConnection({ offer, sendAnswerBack }, iceServers, { onStateChanged, onDataChannelMessage });
    return connectionManager;
}
export const rtcConnectionFactory = { initiateConnectionAsync, acceptConnectionAsync };
