import { bindCallbacks } from "./callbacks-preparation.js";
import { waitForIceGatheringComplete } from "./ice-utilities.js";
async function createRtcConnection(exchangeInfo, iceServers, callbacks) {
    if (!iceServers?.length)
        throw new Error("At least one ICE server must be provided.");
    const isInitator = typeof exchangeInfo === "function";
    const peerConnection = new RTCPeerConnection({ iceServers });
    const { unbind, writeToChannel, whenOpen, handleDataChannel } = bindCallbacks(peerConnection, callbacks);
    if (isInitator) {
        const dataChannel = peerConnection.createDataChannel("data");
        handleDataChannel(dataChannel);
    }
    const agent = {
        close: () => {
            unbind();
            peerConnection.close();
        }, getState: () => peerConnection.connectionState,
        writeToChannel
    };
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
    const audioElement = createAudioElement();
    stream.getTracks().forEach((track) => peerConnection.addTrack(track, stream));
    if (isInitator) {
        const offer = await peerConnection.createOffer();
        await peerConnection.setLocalDescription(offer);
        await waitForIceGatheringComplete(peerConnection);
        const answer = await exchangeInfo(offer);
        await peerConnection.setRemoteDescription(answer);
    }
    else {
        const { offer, sendAnswerBack } = exchangeInfo;
        await peerConnection.setRemoteDescription(offer);
        const answer = await peerConnection.createAnswer();
        await peerConnection.setLocalDescription(answer);
        await waitForIceGatheringComplete(peerConnection);
        await sendAnswerBack(answer);
    }
    peerConnection.ontrack = (event) => {
        const stream = event.streams[0];
        if (stream) {
            audioElement.srcObject = stream;
            audioElement.play()?.catch(() => { });
            const localStream = stream;
        }
    };
    await whenOpen;
    return agent;
}
function createAudioElement() {
    const remoteAudioElement = document.createElement("audio");
    remoteAudioElement.autoplay = true;
    remoteAudioElement.setAttribute("playsinline", "true");
    remoteAudioElement.style.display = "none";
    document.body.appendChild(remoteAudioElement);
    return remoteAudioElement;
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
