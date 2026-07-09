import { bindCallbacks } from "./callbacks-preparation.js";
import { waitForIceGatheringComplete } from "./ice-utilities.js";
import { bindMediaManager } from "./media-manager.js";
let lastProcessId = 0;
async function createRtcConnection(exchangeInfo, iceServers, callbacks) {
    const isInitator = typeof exchangeInfo === "function";
    const role = isInitator ? "initiator" : "acceptor";
    const processId = ++lastProcessId;
    const log = (message) => console.log(`[RTC][${processId}][${role}] ${message}`);
    const logError = (message, error) => console.error(`[RTC][${processId}][${role}] ${message}`, error);
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
    let id;
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
        const { answer, connectionId } = await exchangeInfo(peerConnection.localDescription);
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
        if (!answer) {
            return null;
        }
        log(`local answer created. type=${answer?.type}, hasSdp=${Boolean(answer?.sdp)}`);
        await peerConnection.setLocalDescription(answer);
        await waitForIceGatheringComplete(peerConnection, 10000, log);
        const connectionId = await sendAnswerBack(peerConnection.localDescription);
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
    }
    catch (e) {
        logError(`failed before channel open.`, e);
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
