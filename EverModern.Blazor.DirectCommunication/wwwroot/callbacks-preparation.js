export function bindCallbacks(connection, { onStateChanged, onDataChannelMessage }) {
    let dataChannel = null;
    let finishWaitingForOpening;
    let failOpening;
    const timeout = setTimeout(() => {
        failOpening();
    }, 30000);
    const whenOpen = new Promise((resolve, reject) => {
        finishWaitingForOpening = resolve;
        failOpening = reject;
    });
    const handleDataChannel = (channel) => {
        dataChannel = channel;
        channel.onmessage = (event) => {
            onDataChannelMessage?.(event.data);
        };
        channel.onopen = () => {
            clearTimeout(timeout);
            finishWaitingForOpening();
        };
        if (channel.readyState === "open") {
            clearTimeout(timeout);
            finishWaitingForOpening();
        }
        channel.onerror = () => failOpening();
        channel.onclose = () => {
            if (channel.readyState !== "open") {
                failOpening();
            }
        };
    };
    connection.ondatachannel = (event) => {
        if (event.channel) {
            handleDataChannel(event.channel);
        }
    };
    const writeBytes = (input) => {
        if (!dataChannel || dataChannel.readyState !== "open") {
            throw new Error("RTC data channel is not open.");
        }
        const payload = input instanceof Uint8Array ? input : new Uint8Array(input);
        dataChannel.send(payload);
    };
    connection.onconnectionstatechange = () => {
        onStateChanged?.(connection.connectionState);
        if (connection.connectionState === "connected") {
            finishWaitingForOpening();
        }
        else if (connection.connectionState === "disconnected" || connection.connectionState === "failed" || connection.connectionState === "closed") {
            failOpening();
        }
    };
    return { unbind: () => { connection.ondatachannel = null, connection.onconnectionstatechange = null; }, handleDataChannel, writeToChannel: writeBytes, whenOpen };
}
