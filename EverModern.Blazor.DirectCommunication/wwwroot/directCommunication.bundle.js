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
function createRtcManagerConnection(stateCallback, iceServers) {
    const peerConnection = new RTCPeerConnection({ iceServers: iceServers ?? [] });
    const transceivers = {
        audio: peerConnection.addTransceiver("audio", { direction: "sendrecv" }),
        video: peerConnection.addTransceiver("video", { direction: "sendrecv" })
    };
    const remoteAudioElement = document.createElement("audio");
    remoteAudioElement.autoplay = true;
    remoteAudioElement.setAttribute("playsinline", "true");
    remoteAudioElement.style.display = "none";
    document.body.appendChild(remoteAudioElement);
    const remoteVideoElement = document.createElement("video");
    remoteVideoElement.autoplay = true;
    remoteVideoElement.setAttribute("playsinline", "true");
    remoteVideoElement.style.display = "none";
    document.body.appendChild(remoteVideoElement);
    const localTracks = new Map();
    const remoteAudioStream = new MediaStream();
    const remoteVideoStream = new MediaStream();
    remoteAudioElement.srcObject = remoteAudioStream;
    remoteVideoElement.srcObject = remoteVideoStream;
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
        const transceiver = transceivers[kind];
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
            if (sender.track) {
                sender.track.enabled = true;
            }
            console.info("[rtcConnectionManager] replaced sender track", {
                kind,
                senderTrackKind: sender.track?.kind,
                senderTrackState: sender.track?.readyState,
                senderTrackEnabled: sender.track?.enabled
            });
            return;
        }
    };
    const setInputEnabled = async (kind, enabled) => {
        if (enabled) {
            await ensureInputTrackAsync(kind);
        }
        const senders = peerConnection.getSenders()
            .filter((sender) => sender.track?.kind === kind);
        for (const sender of senders) {
            if (sender.track) {
                sender.track.enabled = enabled;
            }
            if (enabled) {
                try {
                    const parameters = sender.getParameters();
                    if (!parameters.encodings || parameters.encodings.length === 0) {
                        parameters.encodings = [{}];
                    }
                    parameters.encodings.forEach((encoding) => {
                        encoding.active = true;
                    });
                    await sender.setParameters(parameters);
                }
                catch {
                }
            }
        }
    };
    const setOutputEnabled = async (kind, enabled) => {
        if (kind === "audio") {
            remoteAudioElement.muted = !enabled;
            if (enabled) {
                void remoteAudioElement.play().catch(() => undefined);
            }
        }
        else if (kind === "video") {
            if (enabled) {
                void remoteVideoElement.play().catch(() => undefined);
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
        console.info("[rtcConnectionManager] audio input ENABLED");
    };
    manager.disableAudioInput = async () => {
        await setInputEnabled("audio", false);
        console.info("[rtcConnectionManager] audio input DISABLED");
    };
    manager.enableAudioOutput = async () => {
        await setOutputEnabled("audio", true);
        console.info("[rtcConnectionManager] audio output ENABLED");
    };
    manager.disableAudioOutput = async () => {
        await setOutputEnabled("audio", false);
        console.info("[rtcConnectionManager] audio output DISABLED");
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
            track.stop();
        });
        remoteAudioStream.getTracks().forEach((track) => remoteAudioStream.removeTrack(track));
        remoteVideoStream.getTracks().forEach((track) => {
            track.stop();
        });
        remoteVideoStream.getTracks().forEach((track) => remoteVideoStream.removeTrack(track));
        remoteAudioElement.srcObject = null;
        remoteAudioElement.remove();
        remoteVideoElement.srcObject = null;
        remoteVideoElement.remove();
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
        const track = event.track;
        if (!track) {
            return;
        }
        // Determine which stream and element to use based on track kind
        let targetStream;
        let targetElement;
        if (track.kind === "audio") {
            targetStream = remoteAudioStream;
            targetElement = remoteAudioElement;
        }
        else if (track.kind === "video") {
            targetStream = remoteVideoStream;
            targetElement = remoteVideoElement;
        }
        else {
            return;
        }
        // Add the track to our managed stream
        const existingTracks = targetStream.getTracks().filter((t) => t.id === track.id);
        if (existingTracks.length === 0) {
            targetStream.addTrack(track);
        }
        // Update the element's srcObject if not already set
        if (targetElement.srcObject !== targetStream) {
            targetElement.srcObject = targetStream;
        }
        // Attempt to play the element
        void targetElement.play().catch(() => undefined);
        // Handle track ended event
        track.onended = () => {
            const streams = event.streams || [];
            streams.forEach((stream) => {
                const streamTrack = stream.getTracks().find((t) => t.id === track.id);
                if (streamTrack) {
                    stream.removeTrack(streamTrack);
                }
            });
        };
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
async function initiateConnectionAsync(dotnetCallback, onStateChanged, iceServers) {
    const created = createRtcManagerConnection(onStateChanged, iceServers);
    created.peerConnection.getTransceivers().forEach((transceiver) => {
        transceiver.direction = "sendrecv";
    });
    const channel = created.peerConnection.createDataChannel("primary", { ordered: true });
    created.wireDataChannel(channel);
    const offer = await created.peerConnection.createOffer({
        offerToReceiveAudio: true,
        offerToReceiveVideo: true
    });
    await created.peerConnection.setLocalDescription(offer);
    await waitForIceGatheringCompleteRtcMgr(created.peerConnection);
    const answer = await dotnetCallback.invokeMethodAsync("AcceptOfferAsync", created.peerConnection.localDescription);
    await created.peerConnection.setRemoteDescription(new RTCSessionDescription(answer));
    return created.manager;
}
async function acceptConnectionAsync(offer, onStateChanged, iceServers) {
    const created = createRtcManagerConnection(onStateChanged, iceServers);
    await created.peerConnection.setRemoteDescription(new RTCSessionDescription(offer));
    created.peerConnection.getTransceivers().forEach((transceiver) => {
        transceiver.direction = "sendrecv";
    });
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
