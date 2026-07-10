const PromiseResolutionSource = (timeoutMs = 0) => {
    let resolve;
    let reject;
    let resolved = false;
    const promise = new Promise((res, rej) => { resolve = (value) => { if (!resolved) {
        resolved = true;
        res(value);
    } }; reject = (reason) => { if (!resolved) {
        resolved = true;
        rej(reason);
    } }; });
    if (timeoutMs > 0) {
        setTimeout(() => {
            reject(new Error("Promise hasn't been resolved within deadline."));
        }, timeoutMs);
    }
    const result = { ...promise, resolve, reject };
    return result;
};
export function bindCallbacks(connection, { onStateChanged, onDataChannelMessage }, logger) {
    let dataChannel = null;
    const connectionOpened = PromiseResolutionSource(300_000);
    const channelOpened = PromiseResolutionSource(300_000);
    /** Called on every connection-state transition. Handles
     *  logging, external callback, and open/closed detection. */
    const reactToState = () => {
        logger?.info(`connection state: ${connection.connectionState}`);
        onStateChanged?.(connection.connectionState);
        if (connection.connectionState === "connected") {
            logger?.info("whenOpen resolving (connected)");
            connectionOpened.resolve();
        }
        else if (connection.connectionState === "disconnected"
            || connection.connectionState === "failed"
            || connection.connectionState === "closed") {
            logger?.warning(`whenOpen rejecting, state: ${connection.connectionState}`);
            connectionOpened.reject(new Error(`Connection state is ${connection.connectionState}`));
        }
    };
    // ── Both state-change logging and the callback are wired through this handler ──
    connection.onconnectionstatechange = () => reactToState();
    const handleDataChannel = (channel) => {
        dataChannel = channel;
        channel.binaryType = "arraybuffer";
        channel.onmessage = (event) => {
            if (!onDataChannelMessage)
                return;
            if (event.data instanceof ArrayBuffer) {
                const bytes = new Uint8Array(event.data);
                let binary = '';
                for (let i = 0; i < bytes.length; i++)
                    binary += String.fromCharCode(bytes[i]);
                onDataChannelMessage(btoa(binary));
            }
            else {
                onDataChannelMessage(event.data);
            }
        };
        channel.onopen = () => {
            logger?.debug("data channel opened");
            channelOpened.resolve();
        };
        if (channel.readyState === "open") {
            logger?.debug("data channel already open at handleDataChannel time");
            channelOpened.resolve();
        }
        channel.onerror = (e) => {
            logger?.error(`data channel error: ${e}`);
            connectionOpened.reject(e);
        };
        channel.onclose = () => {
            logger?.info(`data channel closed, readyState: ${channel.readyState}`);
            if (channel.readyState !== "open") {
                channelOpened.reject(new Error("Channel is closed. Connection has not been established."));
                reactToState();
            }
        };
    };
    connection.ondatachannel = (event) => {
        if (event.channel) {
            handleDataChannel(event.channel);
        }
    };
    const writeBytes = async (input) => {
        await channelOpened;
        if (!dataChannel || dataChannel.readyState !== "open") {
            return false;
        }
        let payload;
        if (typeof input === "string") {
            const binary = atob(input);
            payload = new Uint8Array(binary.length);
            for (let i = 0; i < binary.length; i++)
                payload[i] = binary.charCodeAt(i);
        }
        else {
            payload = input instanceof Uint8Array ? input : new Uint8Array(input);
        }
        dataChannel.send(payload);
        return true;
    };
    reactToState();
    return {
        unbind: () => {
            connection.ondatachannel = null;
            connection.onconnectionstatechange = null;
        },
        handleDataChannel,
        writeToChannel: writeBytes,
        whenConnectionOpened: connectionOpened
    };
}
