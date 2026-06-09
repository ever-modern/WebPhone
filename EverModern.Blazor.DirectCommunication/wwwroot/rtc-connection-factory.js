import { bindCallbacks } from "./callbacks-preparation.js";
import { waitForIceGatheringComplete } from "./ice-utilities.js";
import { bindMediaManager } from "./media-manager.js";
async function createRtcConnection(exchangeInfo, iceServers, callbacks) {
    if (!iceServers?.length)
        throw new Error("At least one ICE server must be provided.");
    const isInitator = typeof exchangeInfo === "function";
    const role = isInitator ? "initiator" : "acceptor";
    console.log(`[RTC] starting connection as ${role}. iceServers=${iceServers.length}`);
    const peerConnection = new RTCPeerConnection({ iceServers });
    peerConnection.onconnectionstatechange = () => console.log(`[RTC][${role}] connection state:`, peerConnection.connectionState);
    peerConnection.oniceconnectionstatechange = () => console.log(`[RTC][${role}] ICE connection state:`, peerConnection.iceConnectionState);
    peerConnection.onsignalingstatechange = () => console.log(`[RTC][${role}] signaling state:`, peerConnection.signalingState);
    peerConnection.onicegatheringstatechange = () => console.log(`[RTC][${role}] ICE gathering state:`, peerConnection.iceGatheringState);
    peerConnection.onicecandidate = (e) => console.log(`[RTC][${role}] ICE candidate:`, e.candidate ? `${e.candidate.type} ${e.candidate.protocol} ${e.candidate.address}` : "(end of candidates)");
    const { getMediaState, setMediaState, setVideoTarget, setLocalVideoTarget } = bindMediaManager(peerConnection, isInitator);
    const { unbind, writeToChannel, whenOpen, handleDataChannel } = bindCallbacks(peerConnection, callbacks);
    if (isInitator) {
        const dataChannel = peerConnection.createDataChannel("data");
        handleDataChannel(dataChannel);
    }
    const connectionManager = {
        close: () => {
            unbind();
            peerConnection.close();
        }, getState: () => peerConnection.connectionState,
        writeToChannel,
        getMediaState,
        setMediaState,
        setVideoTarget,
        setLocalVideoTarget
    };
    if (isInitator) {
        const offer = await peerConnection.createOffer();
        console.log(`[RTC][${role}] local offer created. type=${offer.type}, hasSdp=${Boolean(offer.sdp)}`);
        await peerConnection.setLocalDescription(offer);
        await waitForIceGatheringComplete(peerConnection);
        const answer = await exchangeInfo(peerConnection.localDescription);
        console.log(`[RTC][${role}] remote answer received. type=${answer?.type}, hasSdp=${Boolean(answer?.sdp)}`);
        if (!answer?.type || !answer?.sdp) {
            console.log(`[RTC][${role}] no direct answer returned; likely switching to counter-offer flow.`);
            unbind();
            peerConnection.close();
            return null;
        }
        await peerConnection.setRemoteDescription(answer);
    }
    else {
        const { offer, sendAnswerBack } = exchangeInfo;
        console.log(`[RTC][${role}] remote offer received. type=${offer?.type}, hasSdp=${Boolean(offer?.sdp)}`);
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
        console.log(`[RTC][${role}] local answer created. type=${answer?.type}, hasSdp=${Boolean(answer?.sdp)}`);
        await peerConnection.setLocalDescription(answer);
        await waitForIceGatheringComplete(peerConnection);
        await sendAnswerBack(peerConnection.localDescription);
        console.log(`[RTC][${role}] local answer sent back.`);
    }
    try {
        await whenOpen;
        console.log(`[RTC][${role}] data channel open.`);
    }
    catch (e) {
        console.error(`[RTC][${role}] failed before channel open.`, e);
        unbind();
        peerConnection.close();
        throw e;
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
