// In development, always fetch from the network and do not enable offline support.
// This is because caching would make development more difficult (changes would not
// be reflected on the first load after each change).
self.addEventListener('fetch', () => { });

self.addEventListener("push", event => {
    const data = event.data?.text() || "No payload";

    console.log("Push received:", data);

    // Optional: show notification
    event.waitUntil(
        self.registration.showNotification("Push Message", {
            body: data
        })
    );
});