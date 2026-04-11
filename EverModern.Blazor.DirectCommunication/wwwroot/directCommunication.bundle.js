"use strict";
const rtcConnectionManagerPrototype = {};
function toBytes(data) {
    if (data instanceof Uint8Array) {
        return Promise.resolve(data);
    }
    if (data instanceof ArrayBuffer) {
        return Promise.resolve(new Uint8Array(data));
    }
    if (ArrayBuffer.isView(data)) {
        return Promise.resolve(new Uint8Array(data.buffer, data.byteOffset, data.byteLength));
    }
    if (data instanceof Blob) {
        return data.arrayBuffer().then((buffer) => new Uint8Array(buffer));
    }
    return Promise.resolve(null);
}
function notifyBytes(subscribers, data) {
    void toBytes(data).then((bytes) => {
        if (!bytes) {
            return;
        }
        subscribers.forEach((subscriber) => {
            subscriber(bytes);
        });
    });
}
function waitForIceGatheringCompleteRtcMgr(connection, timeoutMs = 5000) {
    if (connection.iceGatheringState === "complete") {
        return Promise.resolve();
    }
    return new Promise((resolve) => {
        const handler = () => {
            if (connection.iceGatheringState === "complete") {
                connection.removeEventListener("icegatheringstatechange", handler);
                resolve();
            }
        };
        connection.addEventListener("icegatheringstatechange", handler);
        setTimeout(() => {
            connection.removeEventListener("icegatheringstatechange", handler);
            resolve();
        }, timeoutMs);
    });
}
function createRtcManagerConnection(stateCallback) {
    const peerConnection = new RTCPeerConnection();
    peerConnection.addTransceiver("audio", { direction: "sendrecv" });
    peerConnection.addTransceiver("video", { direction: "sendrecv" });
    let dataChannel = null;
    let localAnswer = null;
    const subscribers = new Map();
    let nextSubscriberId = 0;
    let isClosed = false;
    const wireDataChannel = (channel) => {
        dataChannel = channel;
        channel.binaryType = "arraybuffer";
        channel.onmessage = (event) => {
            notifyBytes(subscribers, event.data);
        };
    };
    const manager = Object.create(rtcConnectionManagerPrototype);
    manager.enableAudio = () => {
        const sender = peerConnection.getSenders().find((item) => item.track?.kind === "audio");
        if (sender?.track) {
            sender.track.enabled = true;
        }
    };
    manager.disableAudio = () => {
        const sender = peerConnection.getSenders().find((item) => item.track?.kind === "audio");
        if (sender?.track) {
            sender.track.enabled = false;
        }
    };
    manager.enableVideo = () => {
        const sender = peerConnection.getSenders().find((item) => item.track?.kind === "video");
        if (sender?.track) {
            sender.track.enabled = true;
        }
    };
    manager.disableVideo = () => {
        const sender = peerConnection.getSenders().find((item) => item.track?.kind === "video");
        if (sender?.track) {
            sender.track.enabled = false;
        }
    };
    manager.writeBytes = (input) => {
        if (!dataChannel || dataChannel.readyState !== "open") {
            throw new Error("RTC data channel is not open.");
        }
        const payload = input instanceof Uint8Array ? input : new Uint8Array(input);
        dataChannel.send(payload);
    };
    manager.subscribeBytes = (callback) => {
        const subscriber = typeof callback === "function"
            ? callback
            : (data) => {
                void callback.invokeMethodAsync("OnBytesReceived", data);
            };
        nextSubscriberId += 1;
        const id = nextSubscriberId;
        subscribers.set(id, subscriber);
        return id;
    };
    manager.unsubscribeBytes = (subscriptionId) => {
        subscribers.delete(subscriptionId);
    };
    manager.getLocalAnswer = () => localAnswer;
    manager.close = () => {
        if (isClosed) {
            return;
        }
        isClosed = true;
        if (dataChannel) {
            dataChannel.close();
            dataChannel = null;
        }
        peerConnection.getSenders().forEach((sender) => {
            if (sender.track) {
                sender.track.stop();
            }
        });
        peerConnection.close();
        subscribers.clear();
    };
    peerConnection.onconnectionstatechange = () => {
        void stateCallback.invokeMethodAsync("OnStateChanged", peerConnection.connectionState);
    };
    peerConnection.ondatachannel = (event) => {
        wireDataChannel(event.channel);
    };
    return {
        manager,
        peerConnection,
        wireDataChannel,
        setLocalAnswer: (description) => {
            localAnswer = description;
        }
    };
}
async function initiateConnectionAsync(dotnetCallback, onStateChanged) {
    const created = createRtcManagerConnection(onStateChanged);
    const channel = created.peerConnection.createDataChannel("primary", { ordered: true });
    created.wireDataChannel(channel);
    const offer = await created.peerConnection.createOffer();
    await created.peerConnection.setLocalDescription(offer);
    await waitForIceGatheringCompleteRtcMgr(created.peerConnection);
    const answer = await dotnetCallback.invokeMethodAsync("AcceptOfferAsync", created.peerConnection.localDescription);
    await created.peerConnection.setRemoteDescription(new RTCSessionDescription(answer));
    return created.manager;
}
async function acceptConnectionAsync(offer, onStateChanged) {
    const created = createRtcManagerConnection(onStateChanged);
    await created.peerConnection.setRemoteDescription(new RTCSessionDescription(offer));
    const answer = await created.peerConnection.createAnswer();
    await created.peerConnection.setLocalDescription(answer);
    await waitForIceGatheringCompleteRtcMgr(created.peerConnection);
    created.setLocalAnswer(created.peerConnection.localDescription);
    return created.manager;
}
window.rtcConnectionManagerInterop = {
    initiateConnectionAsync,
    acceptConnectionAsync
};
const connections = new Map();
const dotNetReferences = new Map();
const dataChannels = new Map();
const localStreams = new Map();
const remoteStreams = new Map();
const pendingRemoteAudioElements = new Map();
const mediaTransceivers = new Map();
function warn(id, message, details) {
    if (details !== undefined) {
        console.warn(`[WebRTC][${id}] ${message}`, details);
        return;
    }
    console.warn(`[WebRTC][${id}] ${message}`);
}
function toErrorMessage(error) {
    if (error instanceof Error) {
        return error.message;
    }
    return String(error);
}
function getConnection(id) {
    const connection = connections.get(id);
    if (!connection) {
        throw new Error(`No RTCPeerConnection found for id '${id}'.`);
    }
    return connection;
}
function wireDataChannel(id, channel) {
    const dotNetReference = dotNetReferences.get(id);
    if (!dotNetReference) {
        return;
    }
    dataChannels.set(id, channel);
    channel.binaryType = "arraybuffer";
    channel.onmessage = (event) => {
        const data = event.data;
        if (typeof data === "string") {
            void dotNetReference.invokeMethodAsync("OnDataChannelMessage", id, data);
            return;
        }
        if (data instanceof ArrayBuffer) {
            void dotNetReference.invokeMethodAsync("OnDataChannelBytesMessage", id, new Uint8Array(data));
            return;
        }
        if (data instanceof Blob) {
            void data.arrayBuffer().then((buffer) => {
                void dotNetReference.invokeMethodAsync("OnDataChannelBytesMessage", id, new Uint8Array(buffer));
            });
        }
    };
    channel.onopen = () => {
        void dotNetReference.invokeMethodAsync("OnDataChannelStateChanged", id, channel.readyState);
    };
    channel.onclose = () => {
        void dotNetReference.invokeMethodAsync("OnDataChannelStateChanged", id, channel.readyState);
    };
}
function waitForIceGatheringComplete(peerConnection, timeoutMs = 5000) {
    if (peerConnection.iceGatheringState === "complete") {
        return Promise.resolve();
    }
    return new Promise((resolve) => {
        const handler = () => {
            if (peerConnection.iceGatheringState === "complete") {
                peerConnection.removeEventListener("icegatheringstatechange", handler);
                resolve();
            }
        };
        peerConnection.addEventListener("icegatheringstatechange", handler);
        setTimeout(() => {
            peerConnection.removeEventListener("icegatheringstatechange", handler);
            resolve();
        }, timeoutMs);
    });
}
function buildIceServers(iceServers) {
    if (!iceServers || !Array.isArray(iceServers) || iceServers.length === 0) {
        return undefined;
    }
    return iceServers.map((server) => {
        if (typeof server === "string") {
            return { urls: server };
        }
        const rawUrls = server.urls ?? server.Urls;
        const urls = Array.isArray(rawUrls) && rawUrls.length === 1 ? rawUrls[0] : rawUrls;
        const entry = { urls: urls ?? [] };
        if (server.username ?? server.Username) {
            entry.username = server.username ?? server.Username;
        }
        if (server.credential ?? server.Credential) {
            entry.credential = server.credential ?? server.Credential;
        }
        return entry;
    });
}
async function createConnection(id, dotNetReference, iceServers) {
    if (connections.has(id)) {
        dotNetReferences.set(id, dotNetReference);
        return;
    }
    const configuration = {};
    const mappedServers = buildIceServers(iceServers);
    if (mappedServers) {
        configuration.iceServers = mappedServers;
    }
    const peerConnection = new RTCPeerConnection(configuration);
    const audioTransceiver = peerConnection.addTransceiver("audio", { direction: "sendrecv" });
    const videoTransceiver = peerConnection.addTransceiver("video", { direction: "sendrecv" });
    mediaTransceivers.set(id, { audio: audioTransceiver, video: videoTransceiver });
    peerConnection.onicecandidate = (event) => {
        if (event.candidate) {
            void dotNetReference.invokeMethodAsync("OnIceCandidate", id, event.candidate);
        }
    };
    peerConnection.onconnectionstatechange = () => {
        void dotNetReference.invokeMethodAsync("OnConnectionStateChanged", id, peerConnection.connectionState);
    };
    peerConnection.ontrack = (event) => {
        const streamFromEvent = event.streams && event.streams.length > 0
            ? event.streams[0]
            : null;
        const stream = streamFromEvent ?? remoteStreams.get(id) ?? new MediaStream();
        if (!streamFromEvent) {
            stream.addTrack(event.track);
        }
        remoteStreams.set(id, stream);
        const pendingElement = pendingRemoteAudioElements.get(id);
        if (pendingElement) {
            pendingElement.autoplay = true;
            pendingElement.srcObject = stream;
            pendingElement.muted = false;
            pendingElement.volume = 1;
            if (typeof pendingElement.play === "function") {
                void pendingElement.play().catch((error) => {
                    warn(id, "Remote audio playback was blocked (likely autoplay/user-gesture policy).", error);
                });
            }
            pendingRemoteAudioElements.delete(id);
        }
        void dotNetReference.invokeMethodAsync("OnRemoteStream", id);
    };
    peerConnection.ondatachannel = (event) => {
        wireDataChannel(id, event.channel);
    };
    connections.set(id, peerConnection);
    dotNetReferences.set(id, dotNetReference);
}
function attachRemoteStream(id, element) {
    const stream = remoteStreams.get(id);
    if (!stream) {
        if (element) {
            pendingRemoteAudioElements.set(id, element);
            return;
        }
        throw new Error(`No remote stream found for id '${id}'.`);
    }
    if (!element) {
        throw new Error("Remote audio element was not provided.");
    }
    element.autoplay = true;
    element.srcObject = stream;
    element.muted = false;
    element.volume = 1;
    if (typeof element.play === "function") {
        void element.play().catch((error) => {
            warn(id, "Remote audio playback was blocked (likely autoplay/user-gesture policy).", error);
        });
    }
}
async function startLocalStream(id, constraints) {
    if (localStreams.has(id)) {
        return localStreams.get(id);
    }
    if (!navigator?.mediaDevices?.getUserMedia) {
        throw new Error("Media devices are unavailable. Use HTTPS or localhost and allow microphone access.");
    }
    const resolvedConstraints = constraints ?? { audio: true, video: false };
    const stream = await navigator.mediaDevices.getUserMedia(resolvedConstraints);
    localStreams.set(id, stream);
    return stream;
}
function addLocalTracks(id) {
    const connection = getConnection(id);
    const transceivers = mediaTransceivers.get(id);
    const stream = localStreams.get(id);
    if (!stream) {
        throw new Error(`No local stream found for id '${id}'.`);
    }
    console.log(`[WebRTC] Adding local tracks for connection ${id}`);
    const addedTracks = [];
    stream.getTracks().forEach((track) => {
        console.log(`[WebRTC] Processing track: ${track.kind}, id: ${track.id}, enabled: ${track.enabled}`);
        const preferredSender = track.kind === "audio"
            ? transceivers?.audio.sender
            : track.kind === "video"
                ? transceivers?.video.sender
                : null;
        const transceiver = connection.getTransceivers().find((candidate) => candidate.receiver.track.kind === track.kind);
        const senderByTransceiver = transceiver?.sender ?? null;
        const senderByKind = connection.getSenders().find((sender) => sender.track?.kind === track.kind) ?? null;
        const existingSender = preferredSender ?? senderByKind ?? senderByTransceiver;
        if (existingSender) {
            console.log(`[WebRTC] Replacing track in existing sender for ${track.kind}`);
            void existingSender.replaceTrack(track).then(() => {
                console.log(`[WebRTC] Successfully replaced ${track.kind} track`);
            }).catch((error) => {
                console.error(`[WebRTC] Failed to replace ${track.kind} track:`, error);
            });
            addedTracks.push(track.kind);
        }
        else {
            console.log(`[WebRTC] Adding new sender for ${track.kind} track`);
            if (connection.localDescription || connection.remoteDescription) {
                warn(id, `Adding ${track.kind} track after SDP negotiation. A renegotiation may be required for remote audio to work.`);
            }
            const sender = connection.addTrack(track, stream);
            console.log("[WebRTC] Added sender:", sender);
            addedTracks.push(track.kind);
        }
    });
    console.log(`[WebRTC] Processed tracks: ${addedTracks.join(", ")}`);
    console.log(`[WebRTC] Total senders on connection: ${connection.getSenders().length}`);
    connection.getSenders().forEach((sender, index) => {
        console.log(`[WebRTC] Sender ${index}: track=${sender.track?.kind || "none"}, trackId=${sender.track?.id || "none"}, enabled=${sender.track?.enabled || "n/a"}`);
    });
}
function createDataChannel(id, label, options) {
    const connection = getConnection(id);
    const channel = connection.createDataChannel(label, options ?? undefined);
    wireDataChannel(id, channel);
}
async function createOffer(id) {
    const connection = getConnection(id);
    const offer = await connection.createOffer();
    await connection.setLocalDescription(offer);
    await waitForIceGatheringComplete(connection);
    return connection.localDescription;
}
async function createAnswer(id) {
    const connection = getConnection(id);
    const answer = await connection.createAnswer();
    await connection.setLocalDescription(answer);
    await waitForIceGatheringComplete(connection);
    return connection.localDescription;
}
async function setRemoteDescription(id, description) {
    const connection = getConnection(id);
    const rtcDescription = new RTCSessionDescription(description);
    await connection.setRemoteDescription(rtcDescription);
    const remoteAudioTracks = remoteStreams.get(id)?.getAudioTracks() ?? [];
    if (connection.iceConnectionState === "connected" && remoteAudioTracks.length === 0) {
        warn(id, "ICE is connected, but no remote audio tracks are present yet. This usually means tracks were not negotiated or were added too late without renegotiation.");
    }
}
async function addIceCandidate(id, candidate) {
    const connection = getConnection(id);
    if (!candidate) {
        return;
    }
    await connection.addIceCandidate(new RTCIceCandidate(candidate));
}
async function waitForDataChannelOpen(channel, timeoutMs = 5000) {
    if (channel.readyState === "open") {
        return;
    }
    await new Promise((resolve, reject) => {
        const timeout = setTimeout(() => {
            cleanup();
            reject(new Error("RTCDataChannel open timeout."));
        }, timeoutMs);
        const onOpen = () => {
            cleanup();
            resolve();
        };
        const onClose = () => {
            cleanup();
            reject(new Error("RTCDataChannel closed before opening."));
        };
        const cleanup = () => {
            clearTimeout(timeout);
            channel.removeEventListener("open", onOpen);
            channel.removeEventListener("close", onClose);
        };
        channel.addEventListener("open", onOpen);
        channel.addEventListener("close", onClose);
    });
}
async function sendData(id, message) {
    const channel = dataChannels.get(id);
    if (!channel) {
        console.warn(`sendData: no data channel for id '${id}', dropping message.`);
        return;
    }
    try {
        await waitForDataChannelOpen(channel);
    }
    catch (error) {
        console.warn(`sendData: data channel not ready for id '${id}': ${toErrorMessage(error)}`);
        return;
    }
    if (channel.readyState !== "open") {
        console.warn(`sendData: channel not open for id '${id}', dropping message.`);
        return;
    }
    try {
        channel.send(message);
    }
    catch (error) {
        console.warn(`sendData: send failed for id '${id}': ${toErrorMessage(error)}`);
    }
}
function stopLocalStream(id) {
    const stream = localStreams.get(id);
    if (!stream) {
        return;
    }
    stream.getTracks().forEach((track) => track.stop());
    localStreams.delete(id);
}
function closeConnection(id) {
    const channel = dataChannels.get(id);
    if (channel) {
        channel.close();
        dataChannels.delete(id);
    }
    const connection = connections.get(id);
    if (connection) {
        connection.close();
        connections.delete(id);
    }
    dotNetReferences.delete(id);
    remoteStreams.delete(id);
    pendingRemoteAudioElements.delete(id);
    mediaTransceivers.delete(id);
    stopLocalStream(id);
}
async function copyToClipboard(text) {
    if (navigator?.clipboard?.writeText) {
        await navigator.clipboard.writeText(text);
        return true;
    }
    const textarea = document.createElement("textarea");
    textarea.value = text;
    textarea.style.position = "fixed";
    textarea.style.opacity = "0";
    document.body.appendChild(textarea);
    textarea.focus();
    textarea.select();
    let success = false;
    try {
        success = document.execCommand("copy");
    }
    catch {
        success = false;
    }
    document.body.removeChild(textarea);
    return success;
}
window.webrtcInterop = {
    createConnection,
    startLocalStream,
    addLocalTracks,
    createDataChannel,
    createOffer,
    createAnswer,
    setRemoteDescription,
    addIceCandidate,
    sendData,
    stopLocalStream,
    closeConnection,
    copyToClipboard,
    attachRemoteStream
};
