(() => {
    const preferenceKey = "swiftaid-theme";
    const legacyPreferenceKey = "swiftaid.theme.preference";
    const root = document.documentElement;
    const systemPrefersDark = window.matchMedia?.("(prefers-color-scheme: dark)")?.matches === true;
    const systemPrefersReducedMotion = window.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches === true;
    let preference = "system";
    try {
        let stored = window.localStorage.getItem(preferenceKey);
        if (stored !== "light" && stored !== "dark" && stored !== "system") {
            stored = window.localStorage.getItem(legacyPreferenceKey);
            if (stored === "light" || stored === "dark" || stored === "system") {
                window.localStorage.setItem(preferenceKey, stored);
            }
        }
        if (stored === "light" || stored === "dark") {
            preference = stored;
        }
    } catch {
        // Use the system preference when browser storage is unavailable.
    }

    root.dataset.theme = "agentic";
    root.dataset.colorMode = preference === "dark"
        || (preference === "system" && systemPrefersDark)
        ? "dark"
        : "light";

    try {
        const motion = window.localStorage.getItem("swiftaid-motion");
        if (motion === "reduced" || systemPrefersReducedMotion) {
            root.setAttribute("data-motion", "reduced");
        }

        const textScale = window.localStorage.getItem("swiftaid-text-scale");
        if (textScale === "large") {
            root.style.fontSize = "112.5%";
            root.setAttribute("data-text-scale", "large");
        } else if (textScale === "extra-large") {
            root.style.fontSize = "125%";
            root.setAttribute("data-text-scale", "extra-large");
        }
    } catch {
        // Keep default sizing and device motion settings when storage is unavailable.
    }
})();
