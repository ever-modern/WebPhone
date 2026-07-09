export function createEventSource() {
    const callbacks = [];
    return {
        subscribe: (callback) => {
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
        invoke: async (event) => {
            for (const callback of callbacks) {
                try {
                    await callback(event);
                }
                catch (error) {
                    // Error invoking callback — intentionally silenced to avoid
                    // breaking the invocation chain for other subscribers.
                }
            }
        }
    };
}
