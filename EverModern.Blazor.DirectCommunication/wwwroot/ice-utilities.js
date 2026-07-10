export function createLogger(prefix) {
    return {
        info: (message, ...args) => console.info(`[${prefix}] ${message}`, ...args),
        error: (message, ...args) => console.error(`[${prefix}] ${message}`, ...args),
        warning: (message, ...args) => console.warn(`[${prefix}] ${message}`, ...args),
        debug: (message, ...args) => console.debug(`[${prefix}] ${message}`, ...args)
    };
}
/** DJB2 hash — deterministic across all browsers and platforms. */
export function sdpChecksum(sdp) {
    let hash = 5381;
    for (let i = 0; i < sdp.length; i++) {
        hash = ((hash << 5) + hash + sdp.charCodeAt(i)) | 0;
    }
    return (hash >>> 0).toString(16).padStart(8, "0");
}
export function waitForIceGatheringComplete(peerConnection, timeoutMs = 10000, logger) {
    if (peerConnection.iceGatheringState === "complete") {
        logger?.debug("ICE gathering already complete");
        return Promise.resolve();
    }
    return new Promise((resolve) => {
        const handler = () => {
            if (peerConnection.iceGatheringState === "complete") {
                logger?.info("ICE gathering completed naturally");
                peerConnection.removeEventListener("icegatheringstatechange", handler);
                resolve();
            }
        };
        peerConnection.addEventListener("icegatheringstatechange", handler);
        setTimeout(() => {
            peerConnection.removeEventListener("icegatheringstatechange", handler);
            logger?.warning(`ICE gathering timed out after ${timeoutMs}ms, state is '${peerConnection.iceGatheringState}', using partial candidates`);
            resolve();
        }, timeoutMs);
    });
}
