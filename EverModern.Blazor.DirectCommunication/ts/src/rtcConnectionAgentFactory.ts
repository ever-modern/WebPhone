import { RtcConnectionCallbacks, RtcConnectionAgent } from "./rtcConnectionAgent";
import { waitForIceGatheringComplete } from "./waitForIceGatheringComplete";

type IceServerParameters = {
    urls: string[];
    username?: string;
    credential?: string;
};

type DotNetObjectReference = {
    invokeMethodAsync<T = unknown>(methodName: string, ...args: unknown[]): Promise<T>;
};

function bindCallbacks(connection: RTCPeerConnection, { onStateChanged, onDataChannelMessage }: RtcConnectionCallbacks): () => void {

    connection.ondatachannel = (event) => {
        const channel = event.channel;
        if (!channel) {
            return;
        }
        channel.onmessage = (event: MessageEvent) => {
            onDataChannelMessage?.(event.data);
        };
    }

    connection.onconnectionstatechange = () => {
        onStateChanged?.(connection.connectionState);
    };

    return () => { connection.ondatachannel = null, connection.onconnectionstatechange = null };
}

type ConnectionDescriptionParameter = ((offer: RTCSessionDescriptionInit) => Promise<RTCSessionDescriptionInit>) | RTCSessionDescriptionInit;

async function createRtcConnectionAgent(
    description: ConnectionDescriptionParameter,
    iceServers: RTCIceServer[],
    callbacks: RtcConnectionCallbacks
): Promise<RtcConnectionAgent> {
    if (!iceServers?.length) throw new Error("At least one ICE server must be provided.");

    const peerConnection = new RTCPeerConnection({ iceServers });

    const unbindCallbacks = bindCallbacks(peerConnection, callbacks);

    const agent: RtcConnectionAgent = {
        close: () => {
            unbindCallbacks();
            peerConnection.close();
        }, state: () => peerConnection.connectionState
    };

    const stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });

    const audioElement = createAudioElement();

    stream.getTracks().forEach((track) =>
        peerConnection.addTrack(track, stream)
    );

    if (typeof description === "function") {
        const offer = await peerConnection.createOffer();
        await peerConnection.setLocalDescription(offer);

        await waitForIceGatheringComplete(peerConnection, 2000);

        const answer = await description(offer);
        await peerConnection.setRemoteDescription(answer);
    }
    else {
        await peerConnection.setRemoteDescription(description);
        const answer = await peerConnection.createAnswer();
        await peerConnection.setLocalDescription(answer);

        await waitForIceGatheringComplete(peerConnection, 2000);
    }

    peerConnection.ontrack = (event) => {
        const stream = event.streams[0];
        if (stream) {
            audioElement.srcObject = stream;
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
): Promise<RtcConnectionAgent> {
    const getAnswer = (offer: RTCSessionDescriptionInit) => getAnswerAsync.invokeMethodAsync<RTCSessionDescriptionInit>("invoke", offer);
    const onStateChanged = (state: RTCPeerConnectionState) => onStateChangedAsync.invokeMethodAsync("invoke", state) as Promise<void>;
    const onDataChannelMessage = (message: string) => onDataChannelMessageAsync.invokeMethodAsync("invoke", message) as Promise<void>;

    const result = await createRtcConnectionAgent(getAnswer, iceServers, { onStateChanged, onDataChannelMessage });

    return result;
}

async function acceptConnectionAsync(
    iceServers: IceServerParameters[],
    offer: RTCSessionDescriptionInit,
    onStateChangedAsync: DotNetObjectReference,
    onDataChannelMessageAsync: DotNetObjectReference
): Promise<RtcConnectionAgent> {
    const onStateChanged = (state: RTCPeerConnectionState) => onStateChangedAsync.invokeMethodAsync("invoke", state) as Promise<void>;
    const onDataChannelMessage = (message: string) => onDataChannelMessageAsync.invokeMethodAsync("invoke", message) as Promise<void>;
    const result = await createRtcConnectionAgent(offer, iceServers, { onStateChanged, onDataChannelMessage });
    return result;
}

const rtcConnectionFactory = { initiateConnectionAsync, acceptConnectionAsync };

(window as any).rtcConnectionFactory = rtcConnectionFactory;