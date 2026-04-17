import { bindCallbacks } from "./callbacks-preparation.js";
import { waitForIceGatheringComplete } from "./ice-utilities.js";
import { bindMediaManager } from "./media-manager.js";
async function createRtcConnection(exchangeInfo, iceServers, callbacks) {
    if (!iceServers?.length)
        throw new Error("At least one ICE server must be provided.");
    const isInitator = typeof exchangeInfo === "function";
    const peerConnection = new RTCPeerConnection({ iceServers });
    peerConnection.oniceconnectionstatechange = () => console.log("ICE:", peerConnection.iceConnectionState);
    peerConnection.onsignalingstatechange = () => console.log("SIGNAL:", peerConnection.signalingState);
    const { getMediaState, setMediaState } = bindMediaManager(peerConnection);
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
        setMediaState
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
        const answer = await peerConnection.createAnswer();
        console.log("ANSWER:", answer);
        await peerConnection.setLocalDescription(answer);
        await waitForIceGatheringComplete(peerConnection);
        await sendAnswerBack(peerConnection.localDescription);
    }
    await whenOpen;
    return connectionManager;
}
async function initiateConnectionAsync(iceServers, getAnswerAsync, onStateChangedAsync, onDataChannelMessageAsync) {
    const getAnswer = (offer) => getAnswerAsync.invokeMethodAsync("invoke", offer);
    const onStateChanged = (state) => onStateChangedAsync.invokeMethodAsync("invoke", state);
    const onDataChannelMessage = (message) => onDataChannelMessageAsync.invokeMethodAsync("invoke", message);
    const result = await createRtcConnection(getAnswer, iceServers, { onStateChanged, onDataChannelMessage });
    return result;
}
async function acceptConnectionAsync(iceServers, offer, sendAnswerBackAsync, onStateChangedAsync, onDataChannelMessageAsync) {
    const onStateChanged = (state) => onStateChangedAsync.invokeMethodAsync("invoke", state);
    const onDataChannelMessage = (message) => onDataChannelMessageAsync.invokeMethodAsync("invoke", message);
    const sendAnswerBack = (answer) => sendAnswerBackAsync.invokeMethodAsync("invoke", answer);
    const result = await createRtcConnection({ offer, sendAnswerBack }, iceServers, { onStateChanged, onDataChannelMessage });
    return result;
}
export const rtcConnectionFactory = { initiateConnectionAsync, acceptConnectionAsync };
