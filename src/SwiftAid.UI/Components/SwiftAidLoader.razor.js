const preferenceObservers = new Map();
let localLottieLoadPromise;

function getMotionPreference() {
    if (typeof window.matchMedia !== "function") {
        return { query: null, reducedMotion: true };
    }

    const query = window.matchMedia("(prefers-reduced-motion: reduce)");
    return { query, reducedMotion: query.matches };
}

export function watchMotionPreference(dotNetReference) {
    const { query, reducedMotion } = getMotionPreference();

    if (!query || typeof query.addEventListener !== "function") {
        return true;
    }

    const listener = event => {
        void dotNetReference.invokeMethodAsync("OnReducedMotionChanged", event.matches)
            .catch(() => {});
    };

    query.addEventListener("change", listener);
    preferenceObservers.set(dotNetReference, { query, listener });
    return reducedMotion;
}

export function stopWatchingMotionPreference(dotNetReference) {
    const observer = preferenceObservers.get(dotNetReference);
    if (!observer) {
        return;
    }

    observer.query.removeEventListener("change", observer.listener);
    preferenceObservers.delete(dotNetReference);
}

export function ensureLocalLottieRuntime() {
    if (window.lottie && typeof window.lottie === "object") {
        return Promise.resolve(true);
    }

    if (localLottieLoadPromise) {
        return localLottieLoadPromise;
    }

    localLottieLoadPromise = new Promise(resolve => {
        const script = document.createElement("script");
        script.type = "text/javascript";
        script.src = "./_content/Blazor.Lottie.Player/lottie.min.js";
        script.dataset.swiftaidLottieRuntime = "true";
        script.onload = () => {
            const loaded = Boolean(window.lottie && typeof window.lottie === "object");
            if (!loaded) {
                localLottieLoadPromise = undefined;
            }
            resolve(loaded);
        };
        script.onerror = () => {
            script.remove();
            localLottieLoadPromise = undefined;
            resolve(false);
        };

        document.head.appendChild(script);
    });

    return localLottieLoadPromise;
}
