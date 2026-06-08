import { bindCallbacks } from "./callbacks-preparation.js";
import { waitForIceGatheringComplete } from "./ice-utilities.js";
import { bindMediaManager } from "./media-manager.js";
import { RtcConnectionCallbacks } from "./rtc-connection.js";

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
) {
    if (!iceServers?.length) throw new Error("At least one ICE server must be provided.");

    const isInitator = typeof exchangeInfo === "function";

    const peerConnection = new RTCPeerConnection({ iceServers });

    peerConnection.oniceconnectionstatechange = () => console.log("[RTC] ICE connection state:", peerConnection.iceConnectionState);
    peerConnection.onsignalingstatechange = () => console.log("[RTC] signaling state:", peerConnection.signalingState);
    peerConnection.onicegatheringstatechange = () => console.log("[RTC] ICE gathering state:", peerConnection.iceGatheringState);
    peerConnection.onicecandidate = (e) => console.log("[RTC] ICE candidate:", e.candidate ? `${e.candidate.type} ${e.candidate.protocol} ${e.candidate.address}` : "(end of candidates)");

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

        // Chrome initialises auto-created transceivers as recvonly.
        // Explicitly set all of them to sendrecv before creating the answer
        // so both sides can send and receive audio/video.
        peerConnection.getTransceivers()
            .filter(t => t.direction !== "stopped")
            .forEach(t => { t.direction = "sendrecv"; });

        const answer = await peerConnection.createAnswer();

        if (!answer) { return null; }

        console.log("ANSWER:", answer);
        await peerConnection.setLocalDescription(answer);

        await waitForIceGatheringComplete(peerConnection);

        await sendAnswerBack(peerConnection.localDescription!);
    }

    try {
        await whenOpen;
    } catch (e) {
        unbind();
        peerConnection.close();
        throw e;
    }

    return connectionManager;
}

function initiateConnectionAsync(
    iceServers: IceServerParameters[],
    getAnswerAsync: DotNetObjectReference,
    onStateChangedAsync: DotNetObjectReference,
    onDataChannelMessageAsync: DotNetObjectReference
) {
    const getAnswer = (offer: RTCSessionDescriptionInit) => getAnswerAsync.invokeMethodAsync<RTCSessionDescriptionInit>("invoke", offer);
    const onStateChanged = (state: RTCPeerConnectionState) => onStateChangedAsync.invokeMethodAsync("invoke", state) as Promise<void>;
    const onDataChannelMessage = (message: string) => onDataChannelMessageAsync.invokeMethodAsync("invoke", message) as Promise<void>;

    const p = createRtcConnection(getAnswer, iceServers, { onStateChanged, onDataChannelMessage });
    // Attaching .catch() here marks p as "handled" in the browser rejection tracker.
    // Returning p directly (not await p) means there is exactly one Promise object.
    // C# still receives the rejection when it IS waiting; no Uncaught (in promise) when it isn’t.
    p.catch((e: unknown) => console.warn("[RTC] initiateConnectionAsync: connection failed (C# may have already cancelled):", e));
    return p;
}

function acceptConnectionAsync(
    iceServers: IceServerParameters[],
    offer: RTCSessionDescriptionInit,
    sendAnswerBackAsync: DotNetObjectReference,
    onStateChangedAsync: DotNetObjectReference,
    onDataChannelMessageAsync: DotNetObjectReference
) {
    const onStateChanged = (state: RTCPeerConnectionState) => onStateChangedAsync.invokeMethodAsync("invoke", state) as Promise<void>;
    const onDataChannelMessage = (message: string) => onDataChannelMessageAsync.invokeMethodAsync("invoke", message) as Promise<void>;
    const sendAnswerBack = (answer: RTCSessionDescriptionInit) => sendAnswerBackAsync.invokeMethodAsync("invoke", answer) as Promise<void>;
    const p = createRtcConnection({ offer, sendAnswerBack }, iceServers, { onStateChanged, onDataChannelMessage });
    p.catch((e: unknown) => console.warn("[RTC] acceptConnectionAsync: connection failed (C# may have already cancelled):", e));
    return p;
}

export const rtcConnectionFactory = { initiateConnectionAsync, acceptConnectionAsync }; 