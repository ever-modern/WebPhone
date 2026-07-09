export function waitForIceGatheringComplete(peerConnection, timeoutMs = 10000, log) {
    if (peerConnection.iceGatheringState === "complete") {
        log?.("[ICE] gathering already complete");
        return Promise.resolve();
    }
    return new Promise((resolve) => {
        const handler = () => {
            if (peerConnection.iceGatheringState === "complete") {
                log?.("[ICE] gathering completed naturally");
                peerConnection.removeEventListener("icegatheringstatechange", handler);
                resolve();
            }
        };
        peerConnection.addEventListener("icegatheringstatechange", handler);
        setTimeout(() => {
            peerConnection.removeEventListener("icegatheringstatechange", handler);
            log?.(`[ICE] gathering timed out after ${timeoutMs}ms, state is '${peerConnection.iceGatheringState}', using partial candidates`);
            resolve();
        }, timeoutMs);
    });
}
