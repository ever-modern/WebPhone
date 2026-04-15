import { createEventSource, RtcConnectionAgent, RtcConnectionCallbacks } from "./RtcConnectionAgent.1";

type IceServerParameters =
    | string
    | {
        urls?: string[];
        username?: string;
        credential?: string;
    };

type DotNetObjectReference = {
    invokeMethodAsync<T = unknown>(methodName: string, ...args: unknown[]): Promise<T>;
};


function buildIceServers(
    iceServers: IceServerParameters[]
): RTCIceServer[] {
    const result = iceServers.map((server) => {
        if (typeof server === "string") {
            return { urls: server };
        }

        return {
            urls: server.urls,
            username: server.username,
            credential: server.credential
        };
    });

    return result;
}

function createCallbacks(connection: RTCPeerConnection): RtcConnectionCallbacks {

    const onStateChanged = createEventSource<RTCPeerConnectionState>();
    const onDataChannelMessage = createEventSource<string>();


    connection.ondatachannel = (event) => {
        const channel = event.channel;
        if (!channel) {
            return;
        }
        channel.onmessage = (event: MessageEvent) => {
            onDataChannelMessage.invoke(event.data);
        };
    }

    connection.onconnectionstatechange = () => {
        onStateChanged.invoke(connection.connectionState);
    };

    return { onStateChanged: onStateChanged.subscribe, onDataChannelMessage: onDataChannelMessage.subscribe };
}

async function createRtcConnectionAgent(
    stateCallback: DotNetObjectReference,
    iceServers: RTCIceServer[]
): Promise<RtcConnectionAgent> {
    if (!iceServers?.length) throw new Error("At least one ICE server must be provided.");

    const peerConnection = new RTCPeerConnection({ iceServers });

    const agent: RtcConnectionAgent = { ...createCallbacks(peerConnection), close: () => peerConnection.close() };

    let localStream: MediaStream | undefined = undefined;
    let remoteStream: MediaStream | undefined = undefined;

    peerConnection.ontrack = (event) => {
        const stream = event.streams[0];
        if (stream) {
            remoteStream = stream;
        }
    };

    return agent;
}

async function initiateConnectionAsync(
    getAnswerAsync: DotNetObjectReference,
    iceServers?: IceServerParameters[]
): Promise<RtcConnectionAgent> {

}

async function acceptConnectionAsync(
    offer: RTCSessionDescriptionInit,
    iceServers?: IceServerParameters[]
): Promise<RtcConnectionAgent> {

}

const RtcConnectionFactory = { initiateConnectionAsync, acceptConnectionAsync };

(window as any).rtcConnectionFactory = RtcConnectionFactory;