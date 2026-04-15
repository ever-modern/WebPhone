type RtcMgrDotNetReference = {
    invokeMethodAsync<T = unknown>(methodName: string, ...args: unknown[]): Promise<T>;
};

type ByteSubscriber = (data: Uint8Array) => void;

type MediaDirectionState = {
    input: boolean;
    output: boolean;
};

type MediaExchangeState = {
    audio: MediaDirectionState;
    video: MediaDirectionState;
};

type RtcConnectionManager = {
    enableAudioInput(): Promise<void>;
    disableAudioInput(): Promise<void>;
    enableAudioOutput(): Promise<void>;
    disableAudioOutput(): Promise<void>;
    enableVideoInput(): Promise<void>;
    disableVideoInput(): Promise<void>;
    enableVideoOutput(): Promise<void>;
    disableVideoOutput(): Promise<void>;
    getMediaExchangeState(): MediaExchangeState;
    writeBytes(input: Uint8Array | ArrayBuffer): void;
    subscribeBytes(callback: ByteSubscriber | RtcMgrDotNetReference): number;
    unsubscribeBytes(subscriptionId: number): void;
    getLocalAnswer(): RTCSessionDescription | null;
    close(): void;
};

type RtcConnectionManagerFactory = {
    initiateConnectionAsync(
        dotnetCallback: RtcMgrDotNetReference,
        onStateChanged: RtcMgrDotNetReference,
        iceServers?: RTCIceServer[]
    ): Promise<RtcConnectionManager>;
    acceptConnectionAsync(
        offer: RTCSessionDescriptionInit,
        onStateChanged: RtcMgrDotNetReference,
        iceServers?: RTCIceServer[]
    ): Promise<RtcConnectionManager>;
};

interface Window {
    rtcConnectionManagerInterop: RtcConnectionManagerFactory;
}

const rtcConnectionManagerPrototype: Record<string, unknown> = {};

function toBytes(data: unknown): Promise<Uint8Array | null> {
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

function notifyBytes(subscribers: Map<number, ByteSubscriber>, data: unknown): void {
    void toBytes(data).then((bytes) => {
        if (!bytes) {
            return;
        }

        subscribers.forEach((subscriber) => {
            subscriber(bytes);
        });
    });
}

function waitForIceGatheringCompleteRtcMgr(connection: RTCPeerConnection, timeoutMs = 5000): Promise<void> {
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

type CreatedConnection = {
    manager: RtcConnectionManager;
    peerConnection: RTCPeerConnection;
    wireDataChannel(channel: RTCDataChannel): void;
    setLocalAnswer(description: RTCSessionDescription | null): void;
};

function createRtcManagerConnection(
    stateCallback: RtcMgrDotNetReference,
    iceServers?: RTCIceServer[]
): CreatedConnection {
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

    const localTracks = new Map<"audio" | "video", MediaStreamTrack>();
    const remoteAudioStream = new MediaStream();
    const remoteVideoStream = new MediaStream();
    remoteAudioElement.srcObject = remoteAudioStream;
    remoteVideoElement.srcObject = remoteVideoStream;

    let dataChannel: RTCDataChannel | null = null;
    let localAnswer: RTCSessionDescription | null = null;
    const subscribers = new Map<number, ByteSubscriber>();
    let nextSubscriberId = 0;
    let isClosed = false;

    const wireDataChannel = (channel: RTCDataChannel): void => {
        dataChannel = channel;
        channel.binaryType = "arraybuffer";
        channel.onmessage = (event) => {
            notifyBytes(subscribers, event.data);
        };
    };

    const manager = Object.create(rtcConnectionManagerPrototype) as RtcConnectionManager;

    const attemptPlay = (element: HTMLMediaElement, source: string): void => {
        void element.play().then(() => {
            console.info("[rtcConnectionManager] media play started", {
                source,
                currentTime: element.currentTime,
                paused: element.paused,
                readyState: element.readyState
            });
        }).catch((error: unknown) => {
            console.warn("[rtcConnectionManager] media play blocked/failed", {
                source,
                error: error instanceof Error ? error.message : String(error),
                paused: element.paused,
                readyState: element.readyState
            });
        });
    };

    const getSenderForKind = (kind: "audio" | "video"): RTCRtpSender | null => {
        const transceiver = transceivers[kind];
        if (transceiver) {
            return transceiver.sender;
        }

        return peerConnection.getSenders().find((sender) => sender.track?.kind === kind) ?? null;
    };

    const ensureInputTrackAsync = async (kind: "audio" | "video"): Promise<void> => {
        if (localTracks.has(kind)) {
            return;
        }

        if (!navigator.mediaDevices?.getUserMedia) {
            throw new Error("The browser does not support media capture.");
        }

        const stream = await navigator.mediaDevices.getUserMedia(
            kind === "audio"
                ? { audio: true, video: false }
                : { audio: false, video: true }
        );

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

    const setInputEnabled = async (kind: "audio" | "video", enabled: boolean): Promise<void> => {
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

    const setOutputEnabled = async (kind: "audio" | "video", enabled: boolean): Promise<void> => {
        if (kind === "audio") {
            remoteAudioElement.muted = !enabled;
            if (enabled) {
                attemptPlay(remoteAudioElement, "enableAudioOutput");
            }
        } else if (kind === "video") {
            if (enabled) {
                attemptPlay(remoteVideoElement, "enableVideoOutput");
            }
        }
    };

    const getInputState = (kind: "audio" | "video"): boolean => {
        const tracks = peerConnection.getSenders()
            .filter((sender) => sender.track?.kind === kind)
            .map((sender) => sender.track)
            .filter((track): track is MediaStreamTrack => !!track);

        return tracks.length > 0 && tracks.every((track) => track.enabled);
    };

    const getOutputState = (kind: "audio" | "video"): boolean => {
        const tracks = peerConnection.getReceivers()
            .filter((receiver) => receiver.track?.kind === kind)
            .map((receiver) => receiver.track)
            .filter((track): track is MediaStreamTrack => !!track);

        return tracks.length > 0 && tracks.every((track) => track.enabled);
    };

    manager.enableAudioInput = async (): Promise<void> => {
        await setInputEnabled("audio", true);
        console.info("[rtcConnectionManager] audio input ENABLED");
    };

    manager.disableAudioInput = async (): Promise<void> => {
        await setInputEnabled("audio", false);
        console.info("[rtcConnectionManager] audio input DISABLED");
    };

    manager.enableAudioOutput = async (): Promise<void> => {
        await setOutputEnabled("audio", true);
        console.info("[rtcConnectionManager] audio output ENABLED");
    };

    manager.disableAudioOutput = async (): Promise<void> => {
        await setOutputEnabled("audio", false);
        console.info("[rtcConnectionManager] audio output DISABLED");
    };

    manager.enableVideoInput = async (): Promise<void> => {
        await setInputEnabled("video", true);
    };

    manager.disableVideoInput = async (): Promise<void> => {
        await setInputEnabled("video", false);
    };

    manager.enableVideoOutput = async (): Promise<void> => {
        await setOutputEnabled("video", true);
    };

    manager.disableVideoOutput = async (): Promise<void> => {
        await setOutputEnabled("video", false);
    };

    manager.getMediaExchangeState = (): MediaExchangeState => {
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

    manager.writeBytes = (input: Uint8Array | ArrayBuffer): void => {
        if (!dataChannel || dataChannel.readyState !== "open") {
            throw new Error("RTC data channel is not open.");
        }

        const payload = input instanceof Uint8Array ? input : new Uint8Array(input);
        dataChannel.send(payload as any);
    };

    manager.subscribeBytes = (callback: ByteSubscriber | RtcMgrDotNetReference): number => {
        const subscriber: ByteSubscriber = typeof callback === "function"
            ? callback
            : (data) => {
                void callback.invokeMethodAsync("OnBytesReceived", data);
            };

        nextSubscriberId += 1;
        const id = nextSubscriberId;
        subscribers.set(id, subscriber);
        return id;
    };

    manager.unsubscribeBytes = (subscriptionId: number): void => {
        subscribers.delete(subscriptionId);
    };

    manager.getLocalAnswer = (): RTCSessionDescription | null => localAnswer;

    manager.close = (): void => {
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
        let targetStream: MediaStream;
        let targetElement: HTMLAudioElement | HTMLVideoElement;

        if (track.kind === "audio") {
            targetStream = remoteAudioStream;
            targetElement = remoteAudioElement;
        } else if (track.kind === "video") {
            targetStream = remoteVideoStream;
            targetElement = remoteVideoElement;
        } else {
            return;
        }

        const incomingStream = event.streams && event.streams.length > 0 ? event.streams[0] : null;

        // Add the track to our managed stream when event stream is not provided.
        if (!incomingStream) {
            const existingTracks = targetStream.getTracks().filter((t) => t.id === track.id);
            if (existingTracks.length === 0) {
                targetStream.addTrack(track);
            }
        }

        const streamToAttach = incomingStream ?? targetStream;

        // Update the element's srcObject if not already set
        if (targetElement.srcObject !== streamToAttach) {
            targetElement.srcObject = streamToAttach;
        }

        // Attempt to play now and when real media starts flowing.
        attemptPlay(targetElement, "ontrack");
        track.onunmute = () => {
            attemptPlay(targetElement, "track.onunmute");
        };

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
        setLocalAnswer: (description: RTCSessionDescription | null): void => {
            localAnswer = description;
        }
    };
}

async function initiateConnectionAsync(
    dotnetCallback: RtcMgrDotNetReference,
    onStateChanged: RtcMgrDotNetReference,
    iceServers?: RTCIceServer[]
): Promise<RtcConnectionManager> {
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

    const answer = await dotnetCallback.invokeMethodAsync<RTCSessionDescriptionInit>(
        "AcceptOfferAsync",
        created.peerConnection.localDescription
    );

    await created.peerConnection.setRemoteDescription(new RTCSessionDescription(answer));
    return created.manager;
}

async function acceptConnectionAsync(
    offer: RTCSessionDescriptionInit,
    onStateChanged: RtcMgrDotNetReference,
    iceServers?: RTCIceServer[]
): Promise<RtcConnectionManager> {
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
