import { RtcConnectionCallbacks } from "./rtc-connection";

export function bindCallbacks(connection: RTCPeerConnection, { onStateChanged, onDataChannelMessage }: RtcConnectionCallbacks) {

    let dataChannel: RTCDataChannel | null = null;

    let finishWaitingForOpening!: () => void;
    let failOpening!: () => void;

    const timeout = setTimeout(() => {
        failOpening();
    }, 30000);

    const whenOpen = new Promise<void>((resolve, reject) => {
        finishWaitingForOpening = resolve;
        failOpening = reject;
    });

    const handleDataChannel = (channel: RTCDataChannel) => {
        dataChannel = channel;
        channel.onmessage = (event: MessageEvent) => {
            onDataChannelMessage?.(event.data);
        };
        channel.onopen = () => {
            clearTimeout(timeout);
            finishWaitingForOpening();
        };
        channel.onerror = () => failOpening();
        channel.onclose = () => {
            if (channel.readyState !== "open") {
                failOpening();
            }
        };
    }

    connection.ondatachannel = (event) => {
        if (event.channel) {
            handleDataChannel(event.channel);
        }
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
        if (connection.connectionState === "connected") {
            finishWaitingForOpening();
        } else if (connection.connectionState === "disconnected" || connection.connectionState === "failed" || connection.connectionState === "closed") {
            failOpening();
        }
    };

    return { unbind: () => { connection.ondatachannel = null, connection.onconnectionstatechange = null; }, handleDataChannel, writeToChannel: writeBytes, whenOpen };
}
