import { RtcConnectionCallbacks } from "./rtc-connection";

export function bindCallbacks(connection: RTCPeerConnection, { onStateChanged, onDataChannelMessage }: RtcConnectionCallbacks) {

    let dataChannel: RTCDataChannel | null = null;

    let finishWaitingForOpening!: () => void;
    let failOpening!: () => void;
    let resolved = false;

    const timeout = setTimeout(() => {
        failOpening();
    }, 30000);

    const whenOpen = new Promise<void>((resolve, reject) => {
        finishWaitingForOpening = resolve;
        failOpening = reject;
    });

    const safeResolve = () => {
        if (resolved) return;
        resolved = true;
        clearTimeout(timeout);
        finishWaitingForOpening();
    };

    const handleDataChannel = (channel: RTCDataChannel) => {
        dataChannel = channel;
        channel.binaryType = "arraybuffer";
        channel.onmessage = (event: MessageEvent) => {
            if (!onDataChannelMessage) return;
            if (event.data instanceof ArrayBuffer) {
                const bytes = new Uint8Array(event.data);
                let binary = '';
                for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
                onDataChannelMessage(btoa(binary));
            } else {
                onDataChannelMessage(event.data as string);
            }
        };
        channel.onopen = () => {
            safeResolve();
        };
        if (channel.readyState === "open") {
            safeResolve();
        }
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

    const writeBytes = (input: Uint8Array | ArrayBuffer | string): void => {
        if (!dataChannel || dataChannel.readyState !== "open") {
            throw new Error("RTC data channel is not open.");
        }

        let payload: Uint8Array;
        if (typeof input === "string") {
            const binary = atob(input);
            payload = new Uint8Array(binary.length);
            for (let i = 0; i < binary.length; i++) payload[i] = binary.charCodeAt(i);
        } else {
            payload = input instanceof Uint8Array ? input : new Uint8Array(input);
        }
        dataChannel.send(payload as unknown as ArrayBufferView<ArrayBuffer>);
    }; 

    connection.onconnectionstatechange = () => {
        console.log("STATE:", connection.connectionState);
        onStateChanged?.(connection.connectionState);
        if (connection.connectionState === "connected") {
            finishWaitingForOpening();
        } else if (connection.connectionState === "disconnected" || connection.connectionState === "failed" || connection.connectionState === "closed") {
            failOpening();
        }
    };

    return { unbind: () => { connection.ondatachannel = null, connection.onconnectionstatechange = null; }, handleDataChannel, writeToChannel: writeBytes, whenOpen };
}