function createAudioElement() {
    const remoteAudioElement = document.createElement("audio");
    remoteAudioElement.autoplay = true;
    remoteAudioElement.muted = true;
    remoteAudioElement.setAttribute("playsinline", "true");
    remoteAudioElement.style.display = "none";
    document.body.appendChild(remoteAudioElement);
    return remoteAudioElement;
}
function createVideoElement(videoContainer) {
    const remoteVideoElement = document.createElement("video");
    remoteVideoElement.autoplay = true;
    remoteVideoElement.setAttribute("playsinline", "true");
    remoteVideoElement.style.display = "none";
    videoContainer.appendChild(remoteVideoElement);
    return remoteVideoElement;
}
export function bindMediaManager(connection, isInitiator, videoContainer, logger) {
    const audioElement = createAudioElement();
    const videoElement = videoContainer ? createVideoElement(videoContainer) : null;
    let localAudioTrack = null;
    let localVideoTrack = null;
    let remoteVideoStream = null;
    let activeVideoTarget = null;
    let localVideoTarget = null;
    connection.ontrack = (event) => {
        const { kind, id, readyState } = event.track;
        logger?.debug(`ontrack kind=${kind} id=${id} readyState=${readyState} streams=${event.streams.length}`);
        const remoteStream = event.streams[0] ?? new MediaStream([event.track]);
        logger?.debug(`stream id=${remoteStream.id} active=${remoteStream.active}`);
        if (event.track.kind === "audio") {
            audioElement.srcObject = remoteStream;
            logger?.debug(`audio element srcObject set, paused=${audioElement.paused} muted=${audioElement.muted}`);
            audioElement.play()
                .then(() => logger?.debug("audio play() resolved"))
                .catch((err) => logger?.warning(`audio play() rejected: ${err}`));
        }
        if (event.track.kind === "video") {
            // Use a video-only stream; event.streams[0] may contain audio too which
            // would trip Chrome's autoplay policy and block play() on the video element.
            const videoOnlyStream = new MediaStream([event.track]);
            remoteVideoStream = videoOnlyStream;
            logger?.debug(`ontrack video: track.id=${event.track.id}` +
                ` readyState=${event.track.readyState}` +
                ` muted=${event.track.muted}` +
                ` enabled=${event.track.enabled}` +
                ` streams.len=${event.streams.length}` +
                ` newStream.id=${videoOnlyStream.id}` +
                ` activeVideoTarget=${activeVideoTarget ? "SET(" + activeVideoTarget.tagName + ")" : "null"}`);
            if (activeVideoTarget) {
                activeVideoTarget.srcObject = videoOnlyStream;
                activeVideoTarget.play()
                    .then(() => logger?.debug("ontrack → play() resolved on existing target"))
                    .catch((err) => logger?.warning(`ontrack → play() rejected: ${err}`));
            }
            else {
                logger?.debug("ontrack: no activeVideoTarget yet — stream stored for later setVideoTarget call");
            }
        }
        event.track.onmute = () => logger?.debug(`track muted kind=${kind} id=${id}`);
        event.track.onunmute = () => logger?.debug(`track unmuted kind=${kind} id=${id}`);
        event.track.onended = () => logger?.info(`track ended kind=${kind} id=${id}`);
    };
    // Initiator adds transceivers so they appear in the offer SDP.
    // Acceptor must NOT pre-add them: Chrome initialises auto-created transceivers
    // as recvonly, and pre-adding sendrecv ones causes a direction conflict in the answer.
    let cachedAudioTransceiver = null;
    let cachedVideoTransceiver = null;
    if (isInitiator) {
        cachedAudioTransceiver = connection.addTransceiver("audio", { direction: "sendrecv" });
        cachedVideoTransceiver = connection.addTransceiver("video", { direction: "sendrecv" });
        logger?.debug(`transceivers added (initiator) audio mid=${cachedAudioTransceiver.mid} video mid=${cachedVideoTransceiver.mid}`);
    }
    else {
        logger?.debug("acceptor — transceivers resolved after SDP exchange");
    }
    const getAudioTransceiver = () => {
        if (cachedAudioTransceiver)
            return cachedAudioTransceiver;
        cachedAudioTransceiver = connection.getTransceivers().find(t => t.receiver.track?.kind === "audio" && t.direction !== "stopped") ?? null;
        logger?.debug(`audio transceiver resolved: ${cachedAudioTransceiver
            ? "direction=" + cachedAudioTransceiver.direction + " currentDirection=" + cachedAudioTransceiver.currentDirection + " mid=" + cachedAudioTransceiver.mid
            : "NOT FOUND"}`);
        return cachedAudioTransceiver;
    };
    const getVideoTransceiver = () => {
        if (cachedVideoTransceiver) {
            logger?.debug(`getVideoTransceiver: cached direction=${cachedVideoTransceiver.direction}`
                + ` currentDirection=${cachedVideoTransceiver.currentDirection}`
                + ` stopped=${cachedVideoTransceiver.direction === "stopped"}`);
            return cachedVideoTransceiver;
        }
        const all = connection.getTransceivers();
        logger?.debug(`getVideoTransceiver: searching ${all.length} transceivers: `
            + all.map(t => (t.receiver.track?.kind ?? "null")
                + `(${t.direction}/${t.currentDirection})`).join(", "));
        cachedVideoTransceiver = all.find(t => t.receiver.track?.kind === "video" && t.direction !== "stopped") ?? null;
        logger?.debug(`getVideoTransceiver: result=${cachedVideoTransceiver
            ? "FOUND direction=" + cachedVideoTransceiver.direction + " currentDirection=" + cachedVideoTransceiver.currentDirection
            : "NOT FOUND"}`);
        return cachedVideoTransceiver;
    };
    let currentState = {
        audio: { outputEnabled: false, inputEnabled: false },
        video: { outputEnabled: false, inputEnabled: false }
    };
    const manager = {
        setMediaState: async (state) => {
            logger?.debug(`setMediaState: currentState=${JSON.stringify(currentState)} → new=${JSON.stringify(state)}`);
            logger?.debug(`audio el: paused=${audioElement.paused} muted=${audioElement.muted} srcObject=${audioElement.srcObject ? "set" : "null"} readyState=${audioElement.readyState}`);
            const { audio, video } = state;
            const { outputEnabled: audioOutput, inputEnabled: audioInput } = audio;
            const { outputEnabled: videoOutput, inputEnabled: videoInput } = video;
            const aTransceiver = getAudioTransceiver();
            logger?.debug(`audio transceiver: ${aTransceiver
                ? "direction=" + aTransceiver.direction + " currentDirection=" + aTransceiver.currentDirection + " mid=" + aTransceiver.mid
                : "NOT FOUND"}`);
            if (aTransceiver) {
                if (audioInput && !currentState.audio.inputEnabled) {
                    logger?.info("acquiring microphone...");
                    const track = await navigator.mediaDevices
                        .getUserMedia({ audio: true, video: false })
                        .then(stream => stream.getAudioTracks()[0]);
                    localAudioTrack = track;
                    logger?.info(`mic acquired id=${track.id} readyState=${track.readyState}`);
                    await aTransceiver.sender.replaceTrack(track);
                    logger?.info(`sender track replaced direction=${aTransceiver.direction} currentDirection=${aTransceiver.currentDirection}`);
                }
                else if (!audioInput && currentState.audio.inputEnabled) {
                    logger?.info("stopping microphone");
                    await aTransceiver.sender.replaceTrack(null);
                    localAudioTrack?.stop();
                    localAudioTrack = null;
                }
            }
            audioElement.muted = audioOutput !== true;
            logger?.debug(`audio muted=${audioElement.muted} paused=${audioElement.paused}`);
            if (!audioElement.muted && audioElement.paused) {
                logger?.debug("unpausing audio after unmute");
                audioElement.play()
                    .then(() => logger?.debug("resume play() resolved"))
                    .catch((err) => logger?.warning(`resume play() rejected: ${err}`));
            }
            if (videoElement) {
                videoElement.muted = videoOutput !== true;
            }
            const vTransceiver = getVideoTransceiver();
            logger?.debug(`setMediaState video: vTransceiver=${vTransceiver ? "ok" : "NULL"}`
                + ` videoInput=${videoInput}`
                + ` currentVideoInputEnabled=${currentState.video.inputEnabled}`);
            if (vTransceiver) {
                if (videoInput && !currentState.video.inputEnabled) {
                    let acquired = false;
                    for (let attempt = 1; attempt <= 3 && !acquired; attempt++) {
                        try {
                            if (attempt > 1) {
                                logger?.debug(`setMediaState: retry ${attempt}/3 — waiting 2 s before re-requesting camera...`);
                                await new Promise(r => setTimeout(r, 2000));
                            }
                            else {
                                logger?.info("calling getUserMedia({video:true})...");
                            }
                            const stream = await navigator.mediaDevices.getUserMedia({ audio: false, video: true });
                            const track = stream.getVideoTracks()[0];
                            if (!track) {
                                logger?.error("getUserMedia succeeded but returned 0 video tracks!");
                            }
                            else {
                                localVideoTrack = track;
                                logger?.info(`camera acquired id=${track.id}`
                                    + ` readyState=${track.readyState} enabled=${track.enabled}`);
                                await vTransceiver.sender.replaceTrack(track);
                                logger?.info("video sender replaceTrack completed");
                                // Mirror local camera in the self-view PiP element
                                if (localVideoTarget) {
                                    localVideoTarget.srcObject = new MediaStream([track]);
                                    localVideoTarget.play().catch((e) => logger?.warning(`local play() rejected: ${e}`));
                                }
                                acquired = true;
                            }
                        }
                        catch (err) {
                            const msg = err instanceof Error ? `${err.name}: ${err.message}` : String(err);
                            if (attempt < 3) {
                                logger?.warning(`camera failed (attempt ${attempt}/3): ${msg}`);
                            }
                            else {
                                logger?.error(`camera acquisition FAILED after 3 attempts: ${msg}`);
                            }
                        }
                    }
                }
                else if (!videoInput && currentState.video.inputEnabled) {
                    await vTransceiver.sender.replaceTrack(null);
                    localVideoTrack?.stop();
                    localVideoTrack = null;
                    if (localVideoTarget)
                        localVideoTarget.srcObject = null;
                    logger?.info("camera stopped");
                }
                else {
                    logger?.debug("video path skipped (no state change needed)");
                }
            }
            currentState = state;
            logger?.debug("setMediaState complete");
        },
        getMediaState: () => ({
            ...currentState
        }),
        setVideoTarget: (element) => {
            const isValidEl = element instanceof HTMLVideoElement;
            logger?.debug(`setVideoTarget called` +
                ` element=${element ? (isValidEl ? "HTMLVideoElement" : "INVALID(" + element?.tagName + ")") : "null"}` +
                ` remoteVideoStream=${remoteVideoStream ? "id=" + remoteVideoStream.id + " tracks=" + remoteVideoStream.getTracks().length : "null"}`);
            activeVideoTarget = isValidEl ? element : null;
            if (activeVideoTarget && remoteVideoStream) {
                activeVideoTarget.srcObject = remoteVideoStream;
                logger?.debug("srcObject assigned — calling play()");
                activeVideoTarget.play()
                    .then(() => logger?.debug("play() resolved — video should be visible"))
                    .catch((err) => logger?.warning(`play() rejected: ${err}`));
            }
            else {
                logger?.debug(`setVideoTarget: no srcObject assigned` +
                    ` (activeVideoTarget=${activeVideoTarget ? "ok" : "null"}` +
                    ` remoteVideoStream=${remoteVideoStream ? "ok" : "null"})`);
            }
        },
        setLocalVideoTarget: (element) => {
            localVideoTarget = element instanceof HTMLVideoElement ? element : null;
            if (localVideoTarget && localVideoTrack) {
                localVideoTarget.srcObject = new MediaStream([localVideoTrack]);
                localVideoTarget.play().catch((e) => logger?.warning(`local play() rejected: ${e}`));
            }
        }
    };
    return manager;
}
