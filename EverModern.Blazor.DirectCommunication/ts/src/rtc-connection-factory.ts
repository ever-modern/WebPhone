import { bindCallbacks } from "./callbacks-preparation.js";
import { waitForIceGatheringComplete } from "./ice-utilities.js";
import { RtcConnectionCallbacks, RtcConnectionManager } from "./rtc-connection.js";

type IceServerParameters = {
    urls: string[];
    username?: string;
    credential?: string;
};

type DotNetObjectReference = {
    invokeMethodAsync<T = unknown>(methodName: string, ...args: unknown[]): Promise<T>;
};

type OfferAnswerExchange = {
    offer: RTCSessionDescriptionInit;
    sendAnswerBack: (answer: RTCSessionDescriptionInit) => Promise<void>;
}

type ConnectionDescriptionExchangeInfo = ((offer: RTCSessionDescriptionInit) => Promise<RTCSessionDescriptionInit>) | OfferAnswerExchange;

async function createRtcConnection(
    exchangeInfo: ConnectionDescriptionExchangeInfo,
    iceServers: RTCIceServer[],
    callbacks: RtcConnectionCallbacks
): Promise<RtcConnectionManager> {
    if (!iceServers?.length) throw new Error("At least one ICE server must be provided.");
     
    const isInitator = typeof exchangeInfo === "function";
    
    const peerConnection = new RTCPeerConnection({ iceServers });

    peerConnection.oniceconnectionstatechange = () => console.log("ICE:", peerConnection.iceConnectionState);
    peerConnection.onsignalingstatechange = () => console.log("SIGNAL:", peerConnection.signalingState);

    const { unbind, writeToChannel, whenOpen, handleDataChannel } = bindCallbacks(peerConnection, callbacks);

    if (isInitator) {
        const dataChannel = peerConnection.createDataChannel("data");
        handleDataChannel(dataChannel);
    }

    const agent: RtcConnectionManager = {
        close: () => {
            unbind();
            peerConnection.close();
        }, getState: () => peerConnection.connectionState,
        writeToChannel
    };

    const stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });

    const audioElement = createAudioElement();

    stream.getTracks().forEach((track) =>
        peerConnection.addTrack(track, stream)
    );

    peerConnection.ontrack = (event) => {
        const remoteStream = event.streams[0] ?? new MediaStream([event.track]);
        audioElement.srcObject = remoteStream;
        audioElement.play().catch(() => { });
    };

    if (isInitator) {
        const offer = await peerConnection.createOffer();
        console.log("OFFER:", offer);
        await peerConnection.setLocalDescription(offer);

        await waitForIceGatheringComplete(peerConnection);

        const answer = await exchangeInfo(peerConnection.localDescription!);
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

        await sendAnswerBack(peerConnection.localDescription!);
    }

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

async function initiateConnectionAsync(
    iceServers: IceServerParameters[],
    getAnswerAsync: DotNetObjectReference,
    onStateChangedAsync: DotNetObjectReference,
    onDataChannelMessageAsync: DotNetObjectReference
): Promise<RtcConnectionManager> {
    const getAnswer = (offer: RTCSessionDescriptionInit) => getAnswerAsync.invokeMethodAsync<RTCSessionDescriptionInit>("invoke", offer);
    const onStateChanged = (state: RTCPeerConnectionState) => onStateChangedAsync.invokeMethodAsync("invoke", state) as Promise<void>;
    const onDataChannelMessage = (message: string) => onDataChannelMessageAsync.invokeMethodAsync("invoke", message) as Promise<void>;

    const result = await createRtcConnection(getAnswer, iceServers, { onStateChanged, onDataChannelMessage });

    return result;
}

async function acceptConnectionAsync(
    iceServers: IceServerParameters[],
    offer: RTCSessionDescriptionInit,
    sendAnswerBackAsync: DotNetObjectReference,
    onStateChangedAsync: DotNetObjectReference,
    onDataChannelMessageAsync: DotNetObjectReference
): Promise<RtcConnectionManager> {
    const onStateChanged = (state: RTCPeerConnectionState) => onStateChangedAsync.invokeMethodAsync("invoke", state) as Promise<void>;
    const onDataChannelMessage = (message: string) => onDataChannelMessageAsync.invokeMethodAsync("invoke", message) as Promise<void>;
    const sendAnswerBack = (answer: RTCSessionDescriptionInit) => sendAnswerBackAsync.invokeMethodAsync("invoke", answer) as Promise<void>;
    const result = await createRtcConnection({ offer, sendAnswerBack }, iceServers, { onStateChanged, onDataChannelMessage });
    return result;
}

export const rtcConnectionFactory = { initiateConnectionAsync, acceptConnectionAsync }; 