export type Logger = {
    info: (message: string, ...args: unknown[]) => void;
    error: (message: string, ...args: unknown[]) => void;
    warning: (message: string, ...args: unknown[]) => void;
    debug: (message: string, ...args: unknown[]) => void;
}

export function createLogger(prefix: string): Logger {
    return {
        info: (message: string, ...args: unknown[]) => console.info(`[${prefix}] ${message}`, ...args),
        error: (message: string, ...args: unknown[]) => console.error(`[${prefix}] ${message}`, ...args),
        warning: (message: string, ...args: unknown[]) => console.warn(`[${prefix}] ${message}`, ...args),
        debug: (message: string, ...args: unknown[]) => console.debug(`[${prefix}] ${message}`, ...args)
    };
}

/** DJB2 hash — deterministic across all browsers and platforms. */
export function sdpChecksum(sdp: string): string { 
    let hash = 5381;
    for (let i = 0; i < sdp.length; i++) {
        hash = ((hash << 5) + hash + sdp.charCodeAt(i)) | 0;
    }
    return (hash >>> 0).toString(16).padStart(8, "0");
}

export function waitForIceGatheringComplete(
    peerConnection: RTCPeerConnection, 
    timeoutMs = 10000,
    logger?: Logger): Promise<void> {
    if (peerConnection.iceGatheringState === "complete") {
        logger?.debug("ICE gathering already complete");
        return Promise.resolve();
    }

    return new Promise<void>((resolve) => {
        const handler = () => {
            if (peerConnection.iceGatheringState === "complete") {
                logger?.info("ICE gathering completed naturally");
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
            logger?.warning(`ICE gathering timed out after ${timeoutMs}ms, state is '${peerConnection.iceGatheringState}', using partial candidates`);
            resolve();
        }, timeoutMs);
    });
}
