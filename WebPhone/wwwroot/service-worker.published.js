// Caution! Be sure you understand the caveats before publishing an application with
// offline support. See https://aka.ms/blazor-offline-considerations

self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));
self.addEventListener('push', event => event.waitUntil(onPush(event)));
self.addEventListener('message', event => {
    if (event.data === 'SKIP_WAITING') {
        self.skipWaiting();
    }
});

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [ /\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.webmanifest$/ ];
const offlineAssetsExclude = [ /^service-worker\.js$/ ];

// Replace with your base path if you are hosting on a subfolder. Ensure there is a trailing '/'.
const base = "/";
const baseUrl = new URL(base, self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

async function onInstall(event) {
    console.info('Service worker: Install');

    // Fetch and cache all matching items from the assets manifest
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
    await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));

    // Skip waiting to activate immediately
    self.skipWaiting();
}

async function onActivate(event) {
    console.info('Service worker: Activate');

    // Delete unused caches
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));

    // Claim all clients immediately
    await self.clients.claim();

    // Notify all clients that a new version is active
    const allClients = await self.clients.matchAll({ type: 'window' });
    allClients.forEach(client => {
        client.postMessage({ type: 'SERVICE_WORKER_UPDATED' });
    });
}

async function onFetch(event) {
    let cachedResponse = null;
    if (event.request.method === 'GET') {
        // For all navigation requests, try to serve index.html from cache,
        // unless that request is for an offline resource.
        // If you need some URLs to be server-rendered, edit the following check to exclude those URLs
        const shouldServeIndexHtml = event.request.mode === 'navigate'
            && !manifestUrlList.some(url => url === event.request.url);

        const request = shouldServeIndexHtml ? 'index.html' : event.request;
        const cache = await caches.open(cacheName);
        cachedResponse = await cache.match(request);
    }

    if (cachedResponse) {
        return cachedResponse;
    }

    try {
        return await fetch(event.request);
    } catch {
        // Never reject fetch event promise: return offline shell for navigations.
        if (event.request.mode === 'navigate') {
            const cache = await caches.open(cacheName);
            const offlineShell = await cache.match('index.html');
            if (offlineShell) return offlineShell;
        }

        return new Response('', { status: 503, statusText: 'Service Unavailable' });
    }
}

async function onPush(event) {
    const raw = event.data?.text() || '';
    let title = 'WebPhone';
    let body = 'You have a new notification.';
    let data = { url: '/' };

    if (raw) {
        try {
            const payload = JSON.parse(raw);
            if (payload?.type === 'chat') {
                title = `Message from ${payload.from || 'Unknown'}`;
                body = payload.text || '';
                data = { url: '/', ...payload };
            } else {
                title = payload?.title || 'WebPhone';
                body = payload?.body || raw;
                data = { url: '/', ...payload };
            }
        } catch {
            body = raw;
        }
    }

    const clients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const hasFocusedClient = clients.some(c => c.focused);
    if (!hasFocusedClient) {
        await self.registration.showNotification(title, {
            body,
            data,
            tag: data?.type === 'chat' ? `chat-${data?.from || 'unknown'}` : undefined
        });
    }
}

self.addEventListener('notificationclick', event => {
    event.notification.close();
    const targetUrl = event.notification?.data?.url || '/';

    event.waitUntil((async () => {
        const clients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        const existing = clients.find(c => c.url.includes(self.location.origin));
        if (existing) {
            await existing.focus();
            return;
        }
        await self.clients.openWindow(targetUrl);
    })());
});
