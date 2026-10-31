const CACHE_PREFIX = "swiftaid-guest-static-";
const CACHE_NAME = `${CACHE_PREFIX}v2`;
const OFFLINE_SHELL = "/offline.html";
const PRELOAD_PATHS = [
    "/_content/MudBlazor/MudBlazor.min.js",
    "/_content/MudBlazor/MudBlazor.min.css",
    "/_content/SwiftAid.UI/css/swiftaid-theme.css",
    "/SwiftAid.Gateway.styles.css",
    "/app.css",
    "/favicon.png",
    "/js/public-policy-bootstrap.js",
    "/js/guest-registration.js",
    "/js/guest-storage.js",
    "/_content/SwiftAid.UI/assets/swiftaid-icon.png",
    "/_content/SwiftAid.UI/js/swiftaid-shell.js",
    "/_content/SwiftAid.UI/js/swiftaid-skip-link-fallback.js"
];

self.addEventListener("install", event => {
    event.waitUntil((async () => {
        const cache = await caches.open(CACHE_NAME);
        await cache.addAll([
            OFFLINE_SHELL,
            "/_framework/blazor.webassembly.js"
        ]);

        await Promise.allSettled(PRELOAD_PATHS.map(async path => {
            const response = await fetch(new Request(path, { cache: "reload" }));
            if (response.ok) {
                await cache.put(path, response);
            }
        }));

        await self.skipWaiting();
    })());
});

self.addEventListener("activate", event => {
    event.waitUntil((async () => {
        const names = await caches.keys();
        await Promise.all(names
            .filter(name => name.startsWith(CACHE_PREFIX) && name !== CACHE_NAME)
            .map(name => caches.delete(name)));
        await self.clients.claim();
    })());
});

self.addEventListener("fetch", event => {
    const request = event.request;
    if (request.method !== "GET") return;

    const url = new URL(request.url);
    if (url.origin !== self.location.origin) return;

    if (request.mode === "navigate") {
        if (url.pathname !== "/" && url.pathname !== "/report") return;
        event.respondWith(fetch(request).catch(async () => {
            const cache = await caches.open(CACHE_NAME);
            return (await cache.match(OFFLINE_SHELL)) ?? new Response("Offline", {
                status: 503,
                headers: { "Content-Type": "text/plain; charset=utf-8" }
            });
        }));
        return;
    }

    if (!isAllowedStaticPath(url.pathname)) return;
    event.respondWith((async () => {
        const cache = await caches.open(CACHE_NAME);
        try {
            const response = await fetch(request);
            if (response.ok) {
                await cache.put(request, response.clone());
            }
            return response;
        } catch {
            return (await cache.match(request)) ?? Response.error();
        }
    })());
});

function isAllowedStaticPath(path) {
    return path.startsWith("/_framework/") ||
        path.startsWith("/_content/MudBlazor/") ||
        path === "/_content/SwiftAid.UI/css/swiftaid-theme.css" ||
        path === "/_content/SwiftAid.UI/assets/swiftaid-icon.png" ||
        path === "/_content/SwiftAid.UI/js/swiftaid-shell.js" ||
        path === "/_content/SwiftAid.UI/js/swiftaid-skip-link-fallback.js" ||
        path === "/SwiftAid.Gateway.styles.css" ||
        path === "/app.css" ||
        path === "/favicon.png" ||
        path === "/js/public-policy-bootstrap.js" ||
        path === "/js/guest-registration.js" ||
        path === "/js/guest-storage.js";
}
