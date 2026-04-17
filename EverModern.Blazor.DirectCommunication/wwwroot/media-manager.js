function createAudioElement() {
    const remoteAudioElement = document.createElement("audio");
    remoteAudioElement.autoplay = true;
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
export function bindMediaManager(connection, videoContainer) {
    const audioElement = createAudioElement();
    const videoElement = videoContainer ? createVideoElement(videoContainer) : null;
    connection.ontrack = (event) => {
        const remoteStream = event.streams[0] ?? new MediaStream([event.track]);
        if (event.track.kind === "audio") {
            audioElement.srcObject = remoteStream;
            audioElement.play().catch(() => { });
        }
        if (event.track.kind === "video" && videoElement) {
            videoElement.srcObject = remoteStream;
            videoElement.play().catch(() => { });
        }
    };
    const audioTransceiver = connection.addTransceiver("audio", { direction: "sendrecv" });
    const videoTransceiver = connection.addTransceiver("video", { direction: "sendrecv" });
    let currentState = {
        audio: { outputEnabled: false, inputEnabled: false },
        video: { outputEnabled: false, inputEnabled: false }
    };
    const manager = {
        setMediaState: async (state) => {
            const { audio, video } = state;
            const { outputEnabled: audioOutput, inputEnabled: audioInput } = audio;
            const { outputEnabled: videoOutput, inputEnabled: videoInput } = video;
            if (audioInput && !currentState.audio.inputEnabled) {
                const localAudioTrack = await navigator.mediaDevices
                    .getUserMedia({ audio: true, video: false })
                    .then(stream => stream.getAudioTracks()[0]);
                await audioTransceiver.sender.replaceTrack(localAudioTrack);
            }
            else if (audioInput !== true && currentState.audio.inputEnabled) {
                await audioTransceiver.sender.replaceTrack(null);
            }
            audioElement.muted = audioOutput !== true;
            if (videoElement) {
                videoElement.muted = videoOutput !== true;
            }
            if (videoInput && !currentState.video.inputEnabled) {
                const localVideoTrack = await navigator.mediaDevices
                    .getUserMedia({ audio: false, video: true })
                    .then(stream => stream.getVideoTracks()[0]);
                await videoTransceiver.sender.replaceTrack(localVideoTrack);
            }
            else if (videoInput !== true && currentState.video.inputEnabled) {
                await videoTransceiver.sender.replaceTrack(null);
            }
            currentState = state;
        },
        getMediaState: () => ({
            ...currentState
        })
    };
    return manager;
}
