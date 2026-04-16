export type RtcConnectionCallbacks = {
    onStateChanged?: SubscriptionParameter<RTCPeerConnectionState>;
    onDataChannelMessage?: SubscriptionParameter<string>;
}

export type RtcConnectionManager = {
    close?: () => void;
    getState: () => RTCPeerConnectionState;
};

type SubscriptionParameter<T> = (event: T) => Promise<void>;

export function createEventSource<T>() {
    const callbacks: ((event: T) => Promise<void>)[] = [];

    return {
        subscribe: (callback: (event: T) => Promise<void>) => {
            callbacks.push(callback);
            return {
                finish: () => {
                    const index = callbacks.indexOf(callback);
                    if (index !== -1) {
                        callbacks.splice(index, 1);
                    }
                }
            };
        },

        invoke: async (event: T) => {
            for (const callback of callbacks) {
                try {
                    await callback(event);
                } catch (error) {
                    console.error("Error invoking callback:", error);
                }
            }
        }
    }
}