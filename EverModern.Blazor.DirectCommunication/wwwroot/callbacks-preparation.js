export function bindCallbacks(connection, { onStateChanged, onDataChannelMessage }) {
    let dataChannel = null;
    let finishWaitingForOpening;
    let failOpening;
    let resolved = false;
    const timeout = setTimeout(() => {
        failOpening(new Error("Connection timed out after 30 s"));
    }, 30000);
    const whenOpen = new Promise((resolve, reject) => {
        finishWaitingForOpening = resolve;
        failOpening = (reason) => reject(reason instanceof Error ? reason : new Error(reason !== undefined ? String(reason) : "Connection failed to open"));
    });
    const safeResolve = () => {
        if (resolved)
            return;
        resolved = true;
        clearTimeout(timeout);
        finishWaitingForOpening();
    };
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
            console.log("[RTC] data channel opened");
            safeResolve();
        };
        if (channel.readyState === "open") {
            console.log("[RTC] data channel already open at handleDataChannel time");
            safeResolve();
        }
        channel.onerror = (e) => { console.warn("[RTC] data channel error:", e); failOpening(); };
        channel.onclose = () => {
            console.log("[RTC] data channel closed, readyState:", channel.readyState);
            if (channel.readyState !== "open") {
                failOpening();
                if (onStateChanged)
                    onStateChanged("closed");
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
    connection.onconnectionstatechange = () => {
        console.log("[RTC] peer connection state:", connection.connectionState);
        onStateChanged?.(connection.connectionState);
        if (connection.connectionState === "connected") {
            console.log("[RTC] whenOpen resolving (connected)");
            finishWaitingForOpening();
        }
        else if (connection.connectionState === "disconnected" || connection.connectionState === "failed" || connection.connectionState === "closed") {
            console.warn("[RTC] whenOpen rejecting, state:", connection.connectionState);
            failOpening();
        }
    };
    return { unbind: () => { connection.ondatachannel = null, connection.onconnectionstatechange = null; }, handleDataChannel, writeToChannel: writeBytes, whenOpen };
}
