import { bindCallbacks } from "./callbacks-preparation.js";
import { waitForIceGatheringComplete } from "./ice-utilities.js";
import { bindMediaManager } from "./media-manager.js";
async function createRtcConnection(exchangeInfo, iceServers, callbacks) {
    if (!iceServers?.length)
        throw new Error("At least one ICE server must be provided.");
    const isInitator = typeof exchangeInfo === "function";
    const peerConnection = new RTCPeerConnection({ iceServers });
    peerConnection.oniceconnectionstatechange = () => console.log("[RTC] ICE connection state:", peerConnection.iceConnectionState);
    peerConnection.onsignalingstatechange = () => console.log("[RTC] signaling state:", peerConnection.signalingState);
    peerConnection.onicegatheringstatechange = () => console.log("[RTC] ICE gathering state:", peerConnection.iceGatheringState);
    peerConnection.onicecandidate = (e) => console.log("[RTC] ICE candidate:", e.candidate ? `${e.candidate.type} ${e.candidate.protocol} ${e.candidate.address}` : "(end of candidates)");
    const { getMediaState, setMediaState, setVideoTarget } = bindMediaManager(peerConnection, isInitator);
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
        setVideoTarget
    };
    if (isInitator) {
        const offer = await peerConnection.createOffer();
        console.log("OFFER:", offer);
        await peerConnection.setLocalDescription(offer);
        await waitForIceGatheringComplete(peerConnection);
        const answer = await exchangeInfo(peerConnection.localDescription);
        console.log("ANSWER:", answer);
        await peerConnection.setRemoteDescription(answer);
    }
    else {
        const { offer, sendAnswerBack } = exchangeInfo;
        console.log("OFFER:", offer);
        await peerConnection.setRemoteDescription(offer);
        // Chrome initialises auto-created transceivers as recvonly.
        // Explicitly set all of them to sendrecv before creating the answer
        // so both sides can send and receive audio/video.
        peerConnection.getTransceivers()
            .filter(t => t.direction !== "stopped")
            .forEach(t => { t.direction = "sendrecv"; });
        const answer = await peerConnection.createAnswer();
        console.log("ANSWER:", answer);
        await peerConnection.setLocalDescription(answer);
        await waitForIceGatheringComplete(peerConnection);
        await sendAnswerBack(peerConnection.localDescription);
    }
    try {
        await whenOpen;
    }
    catch (e) {
        unbind();
        peerConnection.close();
        throw e;
    }
    return connectionManager;
}
function initiateConnectionAsync(iceServers, getAnswerAsync, onStateChangedAsync, onDataChannelMessageAsync) {
    const getAnswer = (offer) => getAnswerAsync.invokeMethodAsync("invoke", offer);
    const onStateChanged = (state) => onStateChangedAsync.invokeMethodAsync("invoke", state);
    const onDataChannelMessage = (message) => onDataChannelMessageAsync.invokeMethodAsync("invoke", message);
    const p = createRtcConnection(getAnswer, iceServers, { onStateChanged, onDataChannelMessage });
    // Attaching .catch() here marks p as "handled" in the browser rejection tracker.
    // Returning p directly (not await p) means there is exactly one Promise object.
    // C# still receives the rejection when it IS waiting; no Uncaught (in promise) when it isn’t.
    p.catch((e) => console.warn("[RTC] initiateConnectionAsync: connection failed (C# may have already cancelled):", e));
    return p;
}
function acceptConnectionAsync(iceServers, offer, sendAnswerBackAsync, onStateChangedAsync, onDataChannelMessageAsync) {
    const onStateChanged = (state) => onStateChangedAsync.invokeMethodAsync("invoke", state);
    const onDataChannelMessage = (message) => onDataChannelMessageAsync.invokeMethodAsync("invoke", message);
    const sendAnswerBack = (answer) => sendAnswerBackAsync.invokeMethodAsync("invoke", answer);
    const p = createRtcConnection({ offer, sendAnswerBack }, iceServers, { onStateChanged, onDataChannelMessage });
    p.catch((e) => console.warn("[RTC] acceptConnectionAsync: connection failed (C# may have already cancelled):", e));
    return p;
}
export const rtcConnectionFactory = { initiateConnectionAsync, acceptConnectionAsync };
