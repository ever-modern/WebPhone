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
                    const a = 5599;
                }
            };
        },
        invoke: async (event) => {
            for (const callback of callbacks) {
                try {
                    await callback(event);
                }
                catch (error) {
                    console.error("Error invoking callback:", error);
                }
            }
        }
    };
}
