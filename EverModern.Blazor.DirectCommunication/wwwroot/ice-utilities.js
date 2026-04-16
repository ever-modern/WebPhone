export function waitForIceGatheringComplete(peerConnection, timeoutMs = 2000) {
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
