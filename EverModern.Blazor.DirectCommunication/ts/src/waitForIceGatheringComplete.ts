export function waitForIceGatheringComplete(
    peerConnection: RTCPeerConnection,
    timeoutMs = 2000): Promise<void> {
    if (peerConnection.iceGatheringState === "complete") {
        return Promise.resolve();
    }

    return new Promise<void>((resolve) => {
        const handler = () => {
            if (peerConnection.iceGatheringState === "complete") {
                peerConnection.removeEventListener(
                    "icegatheringstatechange",
                    handler
                );
                resolve();
            }
        };

        peerConnection.addEventListener("icegatheringstatechange", handler);

        setTimeout(() => {
            peerConnection.removeEventListener(
                "icegatheringstatechange",
                handler
            );
            resolve();
        }, timeoutMs);
    });
}
