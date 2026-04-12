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
    const remoteAudioElement = document.createElement("audio");
    remoteAudioElement.autoplay = true;
    remoteAudioElement.setAttribute("playsinline", "true");
    remoteAudioElement.style.display = "none";
    document.body.appendChild(remoteAudioElement);
    const localTracks = new Map();
    const remoteAudioStream = new MediaStream();
    remoteAudioElement.srcObject = remoteAudioStream;
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
    const getSenderForKind = (kind) => {
        const transceiver = peerConnection.getTransceivers().find((item) => item.receiver.track?.kind === kind);
        if (transceiver) {
            return transceiver.sender;
        }
        return peerConnection.getSenders().find((sender) => sender.track?.kind === kind) ?? null;
    };
    const ensureInputTrackAsync = async (kind) => {
        if (localTracks.has(kind)) {
            return;
        }
        if (!navigator.mediaDevices?.getUserMedia) {
            throw new Error("The browser does not support media capture.");
        }
        const stream = await navigator.mediaDevices.getUserMedia(kind === "audio"
            ? { audio: true, video: false }
            : { audio: false, video: true });
        const track = stream.getTracks().find((candidate) => candidate.kind === kind);
        if (!track) {
            throw new Error(`No ${kind} input track is available.`);
        }
        localTracks.set(kind, track);
        const sender = getSenderForKind(kind);
        if (sender) {
            await sender.replaceTrack(track);
            return;
        }
        peerConnection.addTrack(track, stream);
    };
    const setInputEnabled = async (kind, enabled) => {
        if (enabled) {
            await ensureInputTrackAsync(kind);
        }
        peerConnection.getSenders()
            .filter((sender) => sender.track?.kind === kind)
            .forEach((sender) => {
            if (sender.track) {
                sender.track.enabled = enabled;
            }
        });
    };
    const setOutputEnabled = async (kind, enabled) => {
        peerConnection.getReceivers()
            .filter((receiver) => receiver.track?.kind === kind)
            .forEach((receiver) => {
            if (receiver.track) {
                receiver.track.enabled = enabled;
            }
        });
        if (kind === "audio") {
            remoteAudioElement.muted = !enabled;
            if (enabled) {
                void remoteAudioElement.play().catch(() => undefined);
            }
        }
    };
    const getInputState = (kind) => {
        const tracks = peerConnection.getSenders()
            .filter((sender) => sender.track?.kind === kind)
            .map((sender) => sender.track)
            .filter((track) => !!track);
        return tracks.length > 0 && tracks.every((track) => track.enabled);
    };
    const getOutputState = (kind) => {
        const tracks = peerConnection.getReceivers()
            .filter((receiver) => receiver.track?.kind === kind)
            .map((receiver) => receiver.track)
            .filter((track) => !!track);
        return tracks.length > 0 && tracks.every((track) => track.enabled);
    };
    manager.enableAudioInput = async () => {
        await setInputEnabled("audio", true);
    };
    manager.disableAudioInput = async () => {
        await setInputEnabled("audio", false);
    };
    manager.enableAudioOutput = async () => {
        await setOutputEnabled("audio", true);
    };
    manager.disableAudioOutput = async () => {
        await setOutputEnabled("audio", false);
    };
    manager.enableVideoInput = async () => {
        await setInputEnabled("video", true);
    };
    manager.disableVideoInput = async () => {
        await setInputEnabled("video", false);
    };
    manager.enableVideoOutput = async () => {
        await setOutputEnabled("video", true);
    };
    manager.disableVideoOutput = async () => {
        await setOutputEnabled("video", false);
    };
    manager.getMediaExchangeState = () => {
        return {
            audio: {
                input: getInputState("audio"),
                output: getOutputState("audio")
            },
            video: {
                input: getInputState("video"),
                output: getOutputState("video")
            }
        };
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
        localTracks.forEach((track) => {
            track.stop();
        });
        localTracks.clear();
        remoteAudioStream.getTracks().forEach((track) => {
            remoteAudioStream.removeTrack(track);
            track.stop();
        });
        remoteAudioElement.srcObject = null;
        remoteAudioElement.remove();
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
    peerConnection.ontrack = (event) => {
        if (event.track.kind !== "audio") {
            return;
        }
        const trackExists = remoteAudioStream.getTracks().some((knownTrack) => knownTrack.id === event.track.id);
        if (!trackExists) {
            remoteAudioStream.addTrack(event.track);
        }
        void remoteAudioElement.play().catch(() => undefined);
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
