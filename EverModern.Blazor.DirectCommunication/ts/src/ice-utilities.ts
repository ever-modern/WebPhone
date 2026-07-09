export function waitForIceGatheringComplete(
    peerConnection: RTCPeerConnection,
    timeoutMs = 10000,
    log?: (message: string) => void): Promise<void> {
    if (peerConnection.iceGatheringState === "complete") {
        log?.("[ICE] gathering already complete");
        return Promise.resolve();
    }

    return new Promise<void>((resolve) => {
        const handler = () => {
            if (peerConnection.iceGatheringState === "complete") {
                log?.("[ICE] gathering completed naturally");
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
            log?.(`[ICE] gathering timed out after ${timeoutMs}ms, state is '${peerConnection.iceGatheringState}', using partial candidates`);
            resolve();
        }, timeoutMs);
    });
}
