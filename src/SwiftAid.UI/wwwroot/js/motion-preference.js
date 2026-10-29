const storageKey = "swiftaid-motion";

function storedPreference() {
    try {
        const stored = window.localStorage.getItem(storageKey);
        if (stored === "reduced" || stored === "system") {
            return stored;
        }
    } catch {
        // Private browsing or disabled storage: follow the device setting.
    }
    return "system";
}

function systemPrefersReduced() {
    return typeof window.matchMedia === "function" &&
        window.matchMedia("(prefers-reduced-motion: reduce)").matches;
}

function effectiveReduced(preference) {
    return preference === "reduced" || (preference === "system" && systemPrefersReduced());
}

function apply(reduced) {
    if (reduced) {
        document.documentElement.setAttribute("data-motion", "reduced");
    } else {
        document.documentElement.removeAttribute("data-motion");
    }
}

export function getMotion() {
    const preference = storedPreference();
    apply(effectiveReduced(preference));
    return preference;
}

export function setMotion(preference) {
    const next = preference === "reduced" ? "reduced" : "system";
    try {
        window.localStorage.setItem(storageKey, next);
    } catch {
        // Keep the in-memory choice working even when storage is unavailable.
    }
    const reduced = effectiveReduced(next);
    apply(reduced);
    return reduced;
}

export function watchMotion(dotNet) {
    if (!dotNet || typeof window.matchMedia !== "function") {
        return;
    }
    const query = window.matchMedia("(prefers-reduced-motion: reduce)");
    const handler = () => {
        if (storedPreference() !== "system") {
            return;
        }
        const reduced = query.matches;
        apply(reduced);
        dotNet.invokeMethodAsync("OnSystemMotionChanged", reduced);
    };
    if (typeof query.addEventListener === "function") {
        query.addEventListener("change", handler);
    }
}
