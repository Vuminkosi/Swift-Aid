export async function registerServiceWorker() {
    if (!("serviceWorker" in navigator)) {
        return;
    }

    try {
        await navigator.serviceWorker.register("/sw.js", { scope: "/" });
    } catch {
        // Offline navigation is optional; the active report page remains available.
    }
}
