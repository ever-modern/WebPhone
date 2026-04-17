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
export function bindMediaManager(connection, isInitiator, videoContainer) {
    const audioElement = createAudioElement();
    const videoElement = videoContainer ? createVideoElement(videoContainer) : null;
    let localAudioTrack = null;
    let localVideoTrack = null;
    connection.ontrack = (event) => {
        const { kind, id, readyState } = event.track;
        console.log("[MEDIA] ontrack kind=" + kind + " id=" + id + " readyState=" + readyState + " streams=" + event.streams.length);
        const remoteStream = event.streams[0] ?? new MediaStream([event.track]);
        console.log("[MEDIA] stream id=" + remoteStream.id + " active=" + remoteStream.active);
        if (event.track.kind === "audio") {
            audioElement.srcObject = remoteStream;
            console.log("[MEDIA] audio element srcObject set, paused=" + audioElement.paused + " muted=" + audioElement.muted);
            audioElement.play()
                .then(() => console.log("[MEDIA] audio play() resolved"))
                .catch((err) => console.warn("[MEDIA] audio play() rejected:", err));
        }
        if (event.track.kind === "video" && videoElement) {
            videoElement.srcObject = remoteStream;
            videoElement.play().catch((err) => console.warn("[MEDIA] video play() rejected:", err));
        }
        event.track.onmute = () => console.log("[MEDIA] track muted kind=" + kind + " id=" + id);
        event.track.onunmute = () => console.log("[MEDIA] track unmuted kind=" + kind + " id=" + id);
        event.track.onended = () => console.log("[MEDIA] track ended kind=" + kind + " id=" + id);
    };
    // Initiator adds transceivers so they appear in the offer SDP.
    // Acceptor must NOT pre-add them: Chrome initialises auto-created transceivers
    // as recvonly, and pre-adding sendrecv ones causes a direction conflict in the answer.
    let cachedAudioTransceiver = null;
    let cachedVideoTransceiver = null;
    if (isInitiator) {
        cachedAudioTransceiver = connection.addTransceiver("audio", { direction: "sendrecv" });
        cachedVideoTransceiver = connection.addTransceiver("video", { direction: "sendrecv" });
        console.log("[MEDIA] transceivers added (initiator) audio mid=" + cachedAudioTransceiver.mid + " video mid=" + cachedVideoTransceiver.mid);
    }
    else {
        console.log("[MEDIA] acceptor - transceivers resolved after SDP exchange");
    }
    const getAudioTransceiver = () => {
        if (cachedAudioTransceiver)
            return cachedAudioTransceiver;
        cachedAudioTransceiver = connection.getTransceivers().find(t => t.receiver.track?.kind === "audio" && t.direction !== "stopped") ?? null;
        console.log("[MEDIA] audio transceiver resolved: " + (cachedAudioTransceiver
            ? "direction=" + cachedAudioTransceiver.direction + " currentDirection=" + cachedAudioTransceiver.currentDirection + " mid=" + cachedAudioTransceiver.mid
            : "NOT FOUND"));
        return cachedAudioTransceiver;
    };
    const getVideoTransceiver = () => {
        if (cachedVideoTransceiver)
            return cachedVideoTransceiver;
        cachedVideoTransceiver = connection.getTransceivers().find(t => t.receiver.track?.kind === "video" && t.direction !== "stopped") ?? null;
        return cachedVideoTransceiver;
    };
    let currentState = {
        audio: { outputEnabled: false, inputEnabled: false },
        video: { outputEnabled: false, inputEnabled: false }
    };
    const manager = {
        setMediaState: async (state) => {
            console.log("[MEDIA] setMediaState: " + JSON.stringify(state));
            console.log("[MEDIA] audio el: paused=" + audioElement.paused + " muted=" + audioElement.muted + " srcObject=" + (audioElement.srcObject ? "set" : "null") + " readyState=" + audioElement.readyState);
            const { audio, video } = state;
            const { outputEnabled: audioOutput, inputEnabled: audioInput } = audio;
            const { outputEnabled: videoOutput, inputEnabled: videoInput } = video;
            const aTransceiver = getAudioTransceiver();
            console.log("[MEDIA] audio transceiver: " + (aTransceiver
                ? "direction=" + aTransceiver.direction + " currentDirection=" + aTransceiver.currentDirection + " mid=" + aTransceiver.mid
                : "NOT FOUND"));
            if (aTransceiver) {
                if (audioInput && !currentState.audio.inputEnabled) {
                    console.log("[MEDIA] acquiring microphone...");
                    const track = await navigator.mediaDevices
                        .getUserMedia({ audio: true, video: false })
                        .then(stream => stream.getAudioTracks()[0]);
                    localAudioTrack = track;
                    console.log("[MEDIA] mic acquired id=" + track.id + " readyState=" + track.readyState);
                    await aTransceiver.sender.replaceTrack(track);
                    console.log("[MEDIA] sender track replaced direction=" + aTransceiver.direction + " currentDirection=" + aTransceiver.currentDirection);
                }
                else if (!audioInput && currentState.audio.inputEnabled) {
                    console.log("[MEDIA] stopping microphone");
                    await aTransceiver.sender.replaceTrack(null);
                    localAudioTrack?.stop();
                    localAudioTrack = null;
                }
            }
            audioElement.muted = audioOutput !== true;
            console.log("[MEDIA] audio muted=" + audioElement.muted + " paused=" + audioElement.paused);
            if (!audioElement.muted && audioElement.paused) {
                console.log("[MEDIA] unpausing audio after unmute");
                audioElement.play()
                    .then(() => console.log("[MEDIA] resume play() resolved"))
                    .catch((err) => console.warn("[MEDIA] resume play() rejected:", err));
            }
            if (videoElement) {
                videoElement.muted = videoOutput !== true;
            }
            const vTransceiver = getVideoTransceiver();
            if (vTransceiver) {
                if (videoInput && !currentState.video.inputEnabled) {
                    const track = await navigator.mediaDevices
                        .getUserMedia({ audio: false, video: true })
                        .then(stream => stream.getVideoTracks()[0]);
                    localVideoTrack = track;
                    await vTransceiver.sender.replaceTrack(track);
                }
                else if (!videoInput && currentState.video.inputEnabled) {
                    await vTransceiver.sender.replaceTrack(null);
                    localVideoTrack?.stop();
                    localVideoTrack = null;
                }
            }
            currentState = state;
            console.log("[MEDIA] setMediaState complete");
        },
        getMediaState: () => ({
            ...currentState
        })
    };
    return manager;
}
