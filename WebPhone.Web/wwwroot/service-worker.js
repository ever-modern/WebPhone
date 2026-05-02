// In development, always fetch from the network and do not enable offline support.
// This is because caching would make development more difficult (changes would not
// be reflected on the first load after each change).
self.addEventListener('fetch', event => {
    event.respondWith((async () => {
        try {
            return await fetch(event.request);
        } catch {
            return new Response('', { status: 503, statusText: 'Service Unavailable' });
        }
    })());
});

function parsePushPayload(event) {
    const raw = event.data?.text() || "";
    if (!raw) return { title: "WebPhone", body: "You have a new notification.", data: {} };

    try {
        const payload = JSON.parse(raw);
        if (payload?.type === "chat") {
            return {
                title: `Message from ${payload.from || "Unknown"}`,
                body: payload.text || "",
                data: { url: "/", ...payload }
            };
        }
        return {
            title: payload?.title || "WebPhone",
            body: payload?.body || raw,
            data: { url: "/", ...payload }
        };
    } catch {
        return { title: "WebPhone", body: raw, data: { url: "/" } };
    }
}

self.addEventListener("push", event => {
    event.waitUntil((async () => {
        const payload = parsePushPayload(event);
        const clients = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
        const hasFocusedClient = clients.some(c => c.focused);

        if (!hasFocusedClient) {
            await self.registration.showNotification(payload.title, {
                body: payload.body,
                data: payload.data,
                tag: payload.data?.type === "chat" ? `chat-${payload.data?.from || "unknown"}` : undefined
            });
        }
    })());
});

self.addEventListener("notificationclick", event => {
    event.notification.close();
    const targetUrl = event.notification?.data?.url || "/";
    event.waitUntil((async () => {
        const clients = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
        const existing = clients.find(c => c.url.includes(self.location.origin));
        if (existing) {
            await existing.focus();
            return;
        }
        await self.clients.openWindow(targetUrl);
    })());
});
