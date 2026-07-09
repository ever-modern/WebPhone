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
export function bindMediaManager(connection, isInitiator, videoContainer, log) {
    const audioElement = createAudioElement();
    const videoElement = videoContainer ? createVideoElement(videoContainer) : null;
    let localAudioTrack = null;
    let localVideoTrack = null;
    let remoteVideoStream = null;
    let activeVideoTarget = null;
    let localVideoTarget = null;
    connection.ontrack = (event) => {
        const { kind, id, readyState } = event.track;
        log?.(`[MEDIA] ontrack kind=${kind} id=${id} readyState=${readyState} streams=${event.streams.length}`);
        const remoteStream = event.streams[0] ?? new MediaStream([event.track]);
        log?.(`[MEDIA] stream id=${remoteStream.id} active=${remoteStream.active}`);
        if (event.track.kind === "audio") {
            audioElement.srcObject = remoteStream;
            log?.(`[MEDIA] audio element srcObject set, paused=${audioElement.paused} muted=${audioElement.muted}`);
            audioElement.play()
                .then(() => log?.("[MEDIA] audio play() resolved"))
                .catch((err) => log?.(`[MEDIA] audio play() rejected: ${err}`));
        }
        if (event.track.kind === "video") {
            // Use a video-only stream; event.streams[0] may contain audio too which
            // would trip Chrome's autoplay policy and block play() on the video element.
            const videoOnlyStream = new MediaStream([event.track]);
            remoteVideoStream = videoOnlyStream;
            log?.("[VIDEO] ontrack"
                + " track.id=" + event.track.id
                + " readyState=" + event.track.readyState
                + " muted=" + event.track.muted
                + " enabled=" + event.track.enabled
                + " streams.len=" + event.streams.length
                + " newStream.id=" + videoOnlyStream.id
                + " activeVideoTarget=" + (activeVideoTarget ? "SET(" + activeVideoTarget.tagName + ")" : "null"));
            if (activeVideoTarget) {
                activeVideoTarget.srcObject = videoOnlyStream;
                activeVideoTarget.play()
                    .then(() => log?.("[VIDEO] ontrack → play() resolved on existing target"))
                    .catch((err) => log?.(`[VIDEO] ontrack → play() rejected: ${err}`));
            }
            else {
                log?.("[VIDEO] ontrack: no activeVideoTarget yet — stream stored for later setVideoTarget call");
            }
        }
        event.track.onmute = () => log?.(`[MEDIA] track muted kind=${kind} id=${id}`);
        event.track.onunmute = () => log?.(`[MEDIA] track unmuted kind=${kind} id=${id}`);
        event.track.onended = () => log?.(`[MEDIA] track ended kind=${kind} id=${id}`);
    };
    // Initiator adds transceivers so they appear in the offer SDP.
    // Acceptor must NOT pre-add them: Chrome initialises auto-created transceivers
    // as recvonly, and pre-adding sendrecv ones causes a direction conflict in the answer.
    let cachedAudioTransceiver = null;
    let cachedVideoTransceiver = null;
    if (isInitiator) {
        cachedAudioTransceiver = connection.addTransceiver("audio", { direction: "sendrecv" });
        cachedVideoTransceiver = connection.addTransceiver("video", { direction: "sendrecv" });
        log?.(`[MEDIA] transceivers added (initiator) audio mid=${cachedAudioTransceiver.mid} video mid=${cachedVideoTransceiver.mid}`);
    }
    else {
        log?.("[MEDIA] acceptor - transceivers resolved after SDP exchange");
    }
    const getAudioTransceiver = () => {
        if (cachedAudioTransceiver)
            return cachedAudioTransceiver;
        cachedAudioTransceiver = connection.getTransceivers().find(t => t.receiver.track?.kind === "audio" && t.direction !== "stopped") ?? null;
        log?.(`[MEDIA] audio transceiver resolved: ${cachedAudioTransceiver
            ? "direction=" + cachedAudioTransceiver.direction + " currentDirection=" + cachedAudioTransceiver.currentDirection + " mid=" + cachedAudioTransceiver.mid
            : "NOT FOUND"}`);
        return cachedAudioTransceiver;
    };
    const getVideoTransceiver = () => {
        if (cachedVideoTransceiver) {
            log?.(`[VIDEO] getVideoTransceiver: cached direction=${cachedVideoTransceiver.direction}`
                + ` currentDirection=${cachedVideoTransceiver.currentDirection}`
                + ` stopped=${cachedVideoTransceiver.direction === "stopped"}`);
            return cachedVideoTransceiver;
        }
        const all = connection.getTransceivers();
        log?.(`[VIDEO] getVideoTransceiver: searching ${all.length} transceivers: `
            + all.map(t => (t.receiver.track?.kind ?? "null")
                + `(${t.direction}/${t.currentDirection})`).join(", "));
        cachedVideoTransceiver = all.find(t => t.receiver.track?.kind === "video" && t.direction !== "stopped") ?? null;
        log?.(`[VIDEO] getVideoTransceiver: result=${cachedVideoTransceiver
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
            log?.(`[MEDIA] setMediaState: currentState=${JSON.stringify(currentState)} → new=${JSON.stringify(state)}`);
            log?.(`[MEDIA] audio el: paused=${audioElement.paused} muted=${audioElement.muted} srcObject=${audioElement.srcObject ? "set" : "null"} readyState=${audioElement.readyState}`);
            const { audio, video } = state;
            const { outputEnabled: audioOutput, inputEnabled: audioInput } = audio;
            const { outputEnabled: videoOutput, inputEnabled: videoInput } = video;
            const aTransceiver = getAudioTransceiver();
            log?.(`[MEDIA] audio transceiver: ${aTransceiver
                ? "direction=" + aTransceiver.direction + " currentDirection=" + aTransceiver.currentDirection + " mid=" + aTransceiver.mid
                : "NOT FOUND"}`);
            if (aTransceiver) {
                if (audioInput && !currentState.audio.inputEnabled) {
                    log?.("[MEDIA] acquiring microphone...");
                    const track = await navigator.mediaDevices
                        .getUserMedia({ audio: true, video: false })
                        .then(stream => stream.getAudioTracks()[0]);
                    localAudioTrack = track;
                    log?.(`[MEDIA] mic acquired id=${track.id} readyState=${track.readyState}`);
                    await aTransceiver.sender.replaceTrack(track);
                    log?.(`[MEDIA] sender track replaced direction=${aTransceiver.direction} currentDirection=${aTransceiver.currentDirection}`);
                }
                else if (!audioInput && currentState.audio.inputEnabled) {
                    log?.("[MEDIA] stopping microphone");
                    await aTransceiver.sender.replaceTrack(null);
                    localAudioTrack?.stop();
                    localAudioTrack = null;
                }
            }
            audioElement.muted = audioOutput !== true;
            log?.(`[MEDIA] audio muted=${audioElement.muted} paused=${audioElement.paused}`);
            if (!audioElement.muted && audioElement.paused) {
                log?.("[MEDIA] unpausing audio after unmute");
                audioElement.play()
                    .then(() => log?.("[MEDIA] resume play() resolved"))
                    .catch((err) => log?.(`[MEDIA] resume play() rejected: ${err}`));
            }
            if (videoElement) {
                videoElement.muted = videoOutput !== true;
            }
            const vTransceiver = getVideoTransceiver();
            log?.(`[VIDEO] setMediaState: vTransceiver=${vTransceiver ? "ok" : "NULL"}`
                + ` videoInput=${videoInput}`
                + ` currentVideoInputEnabled=${currentState.video.inputEnabled}`);
            if (vTransceiver) {
                if (videoInput && !currentState.video.inputEnabled) {
                    let acquired = false;
                    for (let attempt = 1; attempt <= 3 && !acquired; attempt++) {
                        try {
                            if (attempt > 1) {
                                log?.(`[VIDEO] setMediaState: retry ${attempt}/3 — waiting 2 s before re-requesting camera...`);
                                await new Promise(r => setTimeout(r, 2000));
                            }
                            else {
                                log?.("[VIDEO] setMediaState: calling getUserMedia({video:true})...");
                            }
                            const stream = await navigator.mediaDevices.getUserMedia({ audio: false, video: true });
                            const track = stream.getVideoTracks()[0];
                            if (!track) {
                                log?.("[VIDEO] setMediaState: getUserMedia succeeded but returned 0 video tracks!");
                            }
                            else {
                                localVideoTrack = track;
                                log?.(`[VIDEO] setMediaState: camera acquired id=${track.id}`
                                    + ` readyState=${track.readyState} enabled=${track.enabled}`);
                                await vTransceiver.sender.replaceTrack(track);
                                log?.("[VIDEO] setMediaState: video sender replaceTrack completed");
                                // Mirror local camera in the self-view PiP element
                                if (localVideoTarget) {
                                    localVideoTarget.srcObject = new MediaStream([track]);
                                    localVideoTarget.play().catch((e) => log?.(`[VIDEO] local play() rejected: ${e}`));
                                }
                                acquired = true;
                            }
                        }
                        catch (err) {
                            const msg = err instanceof Error ? `${err.name}: ${err.message}` : String(err);
                            if (attempt < 3) {
                                log?.(`[VIDEO] setMediaState: camera failed (attempt ${attempt}/3): ${msg}`);
                            }
                            else {
                                log?.(`[VIDEO] setMediaState: camera acquisition FAILED after 3 attempts: ${msg}`);
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
                    log?.("[VIDEO] setMediaState: camera stopped");
                }
                else {
                    log?.("[VIDEO] setMediaState: video path skipped (no state change needed)");
                }
            }
            currentState = state;
            log?.("[MEDIA] setMediaState complete");
        },
        getMediaState: () => ({
            ...currentState
        }),
        setVideoTarget: (element) => {
            const isValidEl = element instanceof HTMLVideoElement;
            log?.("[VIDEO] setVideoTarget called"
                + " element=" + (element ? (isValidEl ? "HTMLVideoElement" : "INVALID(" + element?.tagName + ")") : "null")
                + " remoteVideoStream=" + (remoteVideoStream ? "id=" + remoteVideoStream.id + " tracks=" + remoteVideoStream.getTracks().length : "null"));
            activeVideoTarget = isValidEl ? element : null;
            if (activeVideoTarget && remoteVideoStream) {
                activeVideoTarget.srcObject = remoteVideoStream;
                log?.("[VIDEO] srcObject assigned — calling play()");
                activeVideoTarget.play()
                    .then(() => log?.("[VIDEO] play() resolved — video should be visible"))
                    .catch((err) => log?.(`[VIDEO] play() rejected: ${err}`));
            }
            else {
                log?.("[VIDEO] setVideoTarget: no srcObject assigned"
                    + " (activeVideoTarget=" + (activeVideoTarget ? "ok" : "null")
                    + " remoteVideoStream=" + (remoteVideoStream ? "ok" : "null") + ")");
            }
        },
        setLocalVideoTarget: (element) => {
            localVideoTarget = element instanceof HTMLVideoElement ? element : null;
            if (localVideoTarget && localVideoTrack) {
                localVideoTarget.srcObject = new MediaStream([localVideoTrack]);
                localVideoTarget.play().catch((e) => log?.(`[VIDEO] local play() rejected: ${e}`));
            }
        }
    };
    return manager;
}
