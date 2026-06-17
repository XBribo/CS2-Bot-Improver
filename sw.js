const CACHE_NAME = "cs2rec-converter-v1";
const APP_SHELL = [
  "./",
  "./index.html",
  "./manifest.webmanifest",
  "./assets/app.js",
  "./assets/converter-worker.js",
  "./assets/cs2rec-writer.js",
  "./assets/styles.css",
  "./assets/icon.svg",
  "./vendor/demoparser/demoparser2.js",
  "./vendor/demoparser/demoparser2_bg.wasm"
];

self.addEventListener("install", event => {
  event.waitUntil(caches.open(CACHE_NAME).then(cache => cache.addAll(APP_SHELL)));
  self.skipWaiting();
});

self.addEventListener("activate", event => {
  event.waitUntil((async () => {
    const keys = await caches.keys();
    await Promise.all(keys.filter(key => key !== CACHE_NAME).map(key => caches.delete(key)));
    await self.clients.claim();
  })());
});

self.addEventListener("fetch", event => {
  if (event.request.method !== "GET") {
    return;
  }

  event.respondWith((async () => {
    const cache = await caches.open(CACHE_NAME);
    const cached = await cache.match(event.request);
    if (cached) {
      return cached;
    }

    const response = await fetch(event.request);
    if (response.ok) {
      cache.put(event.request, response.clone()).catch(() => {});
    }
    return response;
  })());
});
