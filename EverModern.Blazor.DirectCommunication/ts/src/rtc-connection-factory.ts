import { waitForIceGatheringComplete } from "./ice-utilities";
import { RtcConnectionCallbacks, RtcConnectionManager } from "./rtc-connection";

type IceServerParameters = {
    urls: string[];
    username?: string;
    credential?: string;
};

type DotNetObjectReference = {
    invokeMethodAsync<T = unknown>(methodName: string, ...args: unknown[]): Promise<T>;
};

function bindCallbacks(connection: RTCPeerConnection, { onStateChanged, onDataChannelMessage }: RtcConnectionCallbacks) {

    let dataChannel: RTCDataChannel | null = null;

    connection.ondatachannel = (event) => {
        const channel = event.channel;
        if (!channel) {
            return;
        }
        dataChannel = channel;
        channel.onmessage = (event: MessageEvent) => {
            onDataChannelMessage?.(event.data);
        };
    }

    const writeBytes = (input: Uint8Array | ArrayBuffer): void => {
        if (!dataChannel || dataChannel.readyState !== "open") {
            throw new Error("RTC data channel is not open.");
        }

        const payload = input instanceof Uint8Array ? input : new Uint8Array(input);
        dataChannel.send(payload as any);
    };

    connection.onconnectionstatechange = () => {
        onStateChanged?.(connection.connectionState);
    };

    return { unbind: () => { connection.ondatachannel = null, connection.onconnectionstatechange = null }, writeToChannel: writeBytes };
}

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

    const peerConnection = new RTCPeerConnection({ iceServers });

    const { unbind, writeToChannel } = bindCallbacks(peerConnection, callbacks);

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

    if (typeof exchangeInfo === "function") {
        peerConnection.createDataChannel("data");
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
    }

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

const rtcConnectionFactory = { initiateConnectionAsync, acceptConnectionAsync };

(window as any).rtcConnectionFactory = rtcConnectionFactory;