type DotNetReference = {
    invokeMethodAsync(methodName: string, ...args: unknown[]): Promise<unknown>;
};

type IceServerInput =
    | string
    | {
        urls?: string | string[];
        Urls?: string | string[];
        username?: string;
        Username?: string;
        credential?: string;
        Credential?: string;
    };

type SendDataPayload = string | Blob | BufferSource;

interface WebRtcInteropApi {
    createConnection: (id: string, dotNetReference: DotNetReference, iceServers?: IceServerInput[]) => Promise<void>;
    startLocalStream: (id: string, constraints?: MediaStreamConstraints) => Promise<MediaStream>;
    addLocalTracks: (id: string) => Promise<void>;
    createDataChannel: (id: string, label: string, options?: RTCDataChannelInit) => void;
    createOffer: (id: string) => Promise<RTCSessionDescription>;
    createAnswer: (id: string) => Promise<RTCSessionDescription>;
    setRemoteDescription: (id: string, description: RTCSessionDescriptionInit) => Promise<void>;
    addIceCandidate: (id: string, candidate?: RTCIceCandidateInit | null) => Promise<void>;
    sendData: (id: string, message: SendDataPayload) => Promise<void>;
    stopLocalStream: (id: string) => void;
    closeConnection: (id: string) => void;
    copyToClipboard: (text: string) => Promise<boolean>;
    attachRemoteStream: (id: string, element: HTMLAudioElement) => void;
}

interface Window {
    webrtcInterop: WebRtcInteropApi;
}

const connections = new Map<string, RTCPeerConnection>();
const dotNetReferences = new Map<string, DotNetReference>();
const dataChannels = new Map<string, RTCDataChannel>();
const localStreams = new Map<string, MediaStream>();
const remoteStreams = new Map<string, MediaStream>();
const pendingRemoteAudioElements = new Map<string, HTMLAudioElement>();
const mediaTransceivers = new Map<
    string,
    { audio: RTCRtpTransceiver; video: RTCRtpTransceiver }
>();

function warn(id: string, message: string, details?: unknown): void {
    if (details !== undefined) {
        console.warn(`[WebRTC][${id}] ${message}`, details);
        return;
    }

    console.warn(`[WebRTC][${id}] ${message}`);
}

function toErrorMessage(error: unknown): string {
    if (error instanceof Error) {
        return error.message;
    }

    return String(error);
}

function getConnection(id: string): RTCPeerConnection {
    const connection = connections.get(id);
    if (!connection) {
        throw new Error(`No RTCPeerConnection found for id '${id}'.`);
    }

    return connection;
}

function wireDataChannel(id: string, channel: RTCDataChannel): void {
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

function waitForIceGatheringComplete(peerConnection: RTCPeerConnection, timeoutMs = 5000): Promise<void> {
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

function buildIceServers(iceServers?: IceServerInput[]): RTCIceServer[] | undefined {
    if (!iceServers || !Array.isArray(iceServers) || iceServers.length === 0) {
        return undefined;
    }

    return iceServers.map((server) => {
        if (typeof server === "string") {
            return { urls: server }
                ;
        }

        const rawUrls = server.urls ?? server.Urls;
        const urls = Array.isArray(rawUrls) && rawUrls.length === 1 ? rawUrls[0] : rawUrls;

        const entry: RTCIceServer = { urls: urls ?? [] }
            ;
        if (server.username ?? server.Username) {
            entry.username = server.username ?? server.Username;
        }

        if (server.credential ?? server.Credential) {
            entry.credential = server.credential ?? server.Credential;
        }

        return entry;
    });
}

async function createConnection(id: string, dotNetReference: DotNetReference, iceServers?: IceServerInput[]): Promise<void> {
    if (connections.has(id)) {
        dotNetReferences.set(id, dotNetReference);
        return;
    }

    const configuration: RTCConfiguration = {}
        ;
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
    }
        ;

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
                void pendingElement.play().catch((error: unknown) => {
                    warn(id, "Remote audio playback was blocked (likely autoplay/user-gesture policy).", error);
                });
            }

            pendingRemoteAudioElements.delete(id);
        }

        void dotNetReference.invokeMethodAsync("OnRemoteStream", id);
    }
        ;

    peerConnection.ondatachannel = (event) => {
        wireDataChannel(id, event.channel);
    }
        ;

    connections.set(id, peerConnection);
    dotNetReferences.set(id, dotNetReference);
}

function attachRemoteStream(id: string, element: HTMLAudioElement): void {
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
        void element.play().catch((error: unknown) => {
            warn(id, "Remote audio playback was blocked (likely autoplay/user-gesture policy).", error);
        });
    }
}

async function startLocalStream(id: string, constraints?: MediaStreamConstraints): Promise<MediaStream> {
    if (localStreams.has(id)) {
        return localStreams.get(id)!;
    }

    if (!navigator?.mediaDevices?.getUserMedia) {
        throw new Error("Media devices are unavailable. Use HTTPS or localhost and allow microphone access.");
    }

    const resolvedConstraints = constraints ?? { audio: true, video: false }
        ;
    const stream = await navigator.mediaDevices.getUserMedia(resolvedConstraints);
    localStreams.set(id, stream);
    return stream;
}

async function addLocalTracks(id: string): Promise<void> {
    const connection = getConnection(id);
    const transceivers = mediaTransceivers.get(id);
    const stream = localStreams.get(id);
    if (!stream) {
        throw new Error(`No local stream found for id '${id}'.`);
    }

    console.log(`[WebRTC] Adding local tracks for connection ${id}`);
    const addedTracks: string[] = [];
    for (const track of stream.getTracks()) {
        console.log(`[WebRTC] Processing track: ${track.kind}, id: ${track.id}, enabled: ${track.enabled}`);

        const preferredSender =
            track.kind === "audio"
                ? transceivers?.audio.sender
                : track.kind === "video"
                    ? transceivers?.video.sender
                    : null;

        const transceiver = connection.getTransceivers().find((candidate) =>
            candidate.receiver.track.kind === track.kind
        );
        const senderByTransceiver = transceiver?.sender ?? null;
        const senderByKind = connection.getSenders().find((sender) => sender.track?.kind === track.kind) ?? null;
        const existingSender = preferredSender ?? senderByKind ?? senderByTransceiver;

        if (existingSender) {
            console.log(`[WebRTC] Replacing track in existing sender for ${track.kind}`);
            try {
                await existingSender.replaceTrack(track);
                track.enabled = true;

                const parameters = existingSender.getParameters();
                if (!parameters.encodings || parameters.encodings.length === 0) {
                    parameters.encodings = [{}];
                }

                parameters.encodings.forEach((encoding) => {
                    encoding.active = true;
                });

                await existingSender.setParameters(parameters);

                console.log(`[WebRTC] Successfully replaced ${track.kind} track`, {
                    senderTrackKind: existingSender.track?.kind,
                    senderTrackId: existingSender.track?.id,
                    senderTrackEnabled: existingSender.track?.enabled,
                    senderTrackState: existingSender.track?.readyState
                });
            } catch (error: unknown) {
                console.error(`[WebRTC] Failed to replace ${track.kind} track:`, error);
            }
            addedTracks.push(track.kind);
        } else {
            console.log(`[WebRTC] Adding new sender for ${track.kind}
    track`);
            if (connection.localDescription || connection.remoteDescription) {
                warn(
                    id,
                    `Adding ${track.kind}
        track after SDP negotiation. A renegotiation may be required for remote audio to work.`
                );
            }

            const sender = connection.addTrack(track, stream);
            console.log("[WebRTC] Added sender:", sender);
            addedTracks.push(track.kind);
        }
    }

    console.log(`[WebRTC] Processed tracks: ${addedTracks.join(", ")}`);
    console.log(`[WebRTC] Total senders on connection: ${connection.getSenders().length}`);
    connection.getSenders().forEach((sender, index) => {
        console.log(`[WebRTC] Sender ${index}: track =${sender.track?.kind || "none"}, trackId =${sender.track?.id || "none"}, enabled =${sender.track?.enabled || "n/a"}`);
    });
}

function createDataChannel(id: string, label: string, options?: RTCDataChannelInit): void {
    const connection = getConnection(id);
    const channel = connection.createDataChannel(label, options ?? undefined);
    wireDataChannel(id, channel);
}

async function createOffer(id: string): Promise<RTCSessionDescription> {
    const connection = getConnection(id);
    connection.getTransceivers().forEach((transceiver) => {
        transceiver.direction = "sendrecv";
    });
    const offer = await connection.createOffer();
    await connection.setLocalDescription(offer);
    await waitForIceGatheringComplete(connection);
    return connection.localDescription!;
}

async function createAnswer(id: string): Promise<RTCSessionDescription> {
    const connection = getConnection(id);
    connection.getTransceivers().forEach((transceiver) => {
        transceiver.direction = "sendrecv";
    });
    const answer = await connection.createAnswer();
    await connection.setLocalDescription(answer);
    await waitForIceGatheringComplete(connection);
    return connection.localDescription!;
}

async function setRemoteDescription(id: string, description: RTCSessionDescriptionInit): Promise<void> {
    const connection = getConnection(id);
    const rtcDescription = new RTCSessionDescription(description);
    await connection.setRemoteDescription(rtcDescription);

    const remoteAudioTracks = remoteStreams.get(id)?.getAudioTracks() ?? [];
    if (connection.iceConnectionState === "connected" && remoteAudioTracks.length === 0) {
        warn(
            id,
            "ICE is connected, but no remote audio tracks are present yet. This usually means tracks were not negotiated or were added too late without renegotiation."
        );
    }
}

async function addIceCandidate(id: string, candidate?: RTCIceCandidateInit | null): Promise<void> {
    const connection = getConnection(id);
    if (!candidate) {
        return;
    }

    await connection.addIceCandidate(new RTCIceCandidate(candidate));
}

async function waitForDataChannelOpen(channel: RTCDataChannel, timeoutMs = 5000): Promise<void> {
    if (channel.readyState === "open") {
        return;
    }

    await new Promise<void>((resolve, reject) => {
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

async function sendData(id: string, message: SendDataPayload): Promise<void> {
    const channel = dataChannels.get(id);
    if (!channel) {
        console.warn(`sendData: no data channel for id '${id}', dropping message.`);
        return;
    }

    try {
        await waitForDataChannelOpen(channel);
    }
    catch (error: unknown) {
        console.warn(`sendData: data channel not ready for id '${id}': ${toErrorMessage(error)}`);
        return;
    }

    if (channel.readyState !== "open") {
        console.warn(`sendData: channel not open for id '${id}', dropping message.`);
        return;
    }

    try {
        channel.send(message as any);
    }
    catch (error: unknown) {
        console.warn(`sendData: send failed for id '${id}': ${toErrorMessage(error)}`);
    }
}

function stopLocalStream(id: string): void {
    const stream = localStreams.get(id);
    if (!stream) {
        return;
    }

    stream.getTracks().forEach((track) => track.stop());
    localStreams.delete(id);
}

function closeConnection(id: string): void {
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

async function copyToClipboard(text: string): Promise<boolean> {
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
}
    ;
