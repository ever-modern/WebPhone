"use strict";
async function registerPush(vapid) {
    if (!vapid || !navigator?.serviceWorker || !window?.PushManager) {
        return null;
    }
    try {
        const registration = await navigator.serviceWorker.register("/service-worker.js");
        if (typeof Notification !== "undefined" && Notification.permission === "default") {
            await Notification.requestPermission();
        }
        if (typeof Notification !== "undefined" && Notification.permission !== "granted") {
            return null;
        }
        let subscription = await registration.pushManager.getSubscription();
        if (!subscription) {
            subscription = await registration.pushManager.subscribe({
                userVisibleOnly: true,
                applicationServerKey: urlBase64ToUint8Array(vapid)
            });
        }
        return JSON.stringify(subscription);
    }
    catch {
        return null;
    }
}
// Helper
function urlBase64ToUint8Array(base64String) {
    const padding = "=".repeat((4 - base64String.length % 4) % 4);
    const base64 = (base64String + padding)
        .replace(/-/g, "+")
        .replace(/_/g, "/");
    const rawData = window.atob(base64);
    return Uint8Array.from([...rawData].map(char => char.charCodeAt(0)));
}
window.registerPush = registerPush;
