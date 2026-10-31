const preferenceKey = "swiftaid-theme";
const legacyPreferenceKey = "swiftaid.theme.preference";
const motionPreferenceKey = "swiftaid-motion";
const textScalePreferenceKey = "swiftaid-text-scale";
const localPreferencesChangedEvent = "swiftaid-local-preferences-changed";
let colorSchemeQuery;
let colorSchemeChanged;
let reducedMotionQuery;
let reducedMotionChanged;
let storageChanged;
let preferenceChanged;
let usesModernMediaQueryListener;
let providerReference;
let activePreference = "system";

function readPreference() {
    try {
        let preference = window.localStorage.getItem(preferenceKey);
        if (preference !== "light" && preference !== "dark" && preference !== "system") {
            preference = window.localStorage.getItem(legacyPreferenceKey);
            if (preference === "light" || preference === "dark" || preference === "system") {
                window.localStorage.setItem(preferenceKey, preference);
            }
        }
        return preference === "light" || preference === "dark" || preference === "system" ? preference : "system";
    } catch {
        return "system";
    }
}

function applyPreference(preference) {
    activePreference = preference;
    const isDark = preference === "dark"
        || (preference === "system" && colorSchemeQuery?.matches === true);
    document.documentElement.dataset.theme = "agentic";
    document.documentElement.dataset.colorMode = isDark ? "dark" : "light";
    applyLocalPreferences();
    return isDark;
}

function applyLocalPreferences() {
    const root = document.documentElement;
    try {
        const motion = window.localStorage.getItem(motionPreferenceKey);
        const systemPrefersReducedMotion = reducedMotionQuery?.matches === true
            || window.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches === true;
        if (motion === "reduced" || (motion !== "reduced" && systemPrefersReducedMotion)) {
            root.setAttribute("data-motion", "reduced");
        } else {
            root.removeAttribute("data-motion");
        }

        const textScale = window.localStorage.getItem(textScalePreferenceKey);
        if (textScale === "large") {
            root.style.fontSize = "112.5%";
            root.setAttribute("data-text-scale", "large");
        } else if (textScale === "extra-large") {
            root.style.fontSize = "125%";
            root.setAttribute("data-text-scale", "extra-large");
        } else {
            root.style.removeProperty("font-size");
            root.removeAttribute("data-text-scale");
        }
    } catch {
        // Keep the page usable with the browser and device defaults when storage is unavailable.
    }
}

function notifyProvider() {
    if (providerReference) {
        void providerReference.invokeMethodAsync("SetThemeState", activePreference, applyPreference(activePreference)).catch(() => {
            // The provider can be disposed while a preference change is being delivered.
        });
    }
}

export function getThemePreference() {
    return readPreference();
}

export function watchSystemTheme(dotNetReference) {
    providerReference = dotNetReference;
    colorSchemeQuery = window.matchMedia("(prefers-color-scheme: dark)");
    colorSchemeChanged = () => {
        if (activePreference === "system") {
            applyPreference("system");
            notifyProvider();
        }
    };
    reducedMotionQuery = window.matchMedia?.("(prefers-reduced-motion: reduce)");
    reducedMotionChanged = () => applyLocalPreferences();

    activePreference = readPreference();
    usesModernMediaQueryListener = typeof colorSchemeQuery.addEventListener === "function";
    if (usesModernMediaQueryListener) {
        colorSchemeQuery.addEventListener("change", colorSchemeChanged);
    } else {
        colorSchemeQuery.addListener(colorSchemeChanged);
    }
    if (reducedMotionQuery) {
        if (typeof reducedMotionQuery.addEventListener === "function") {
            reducedMotionQuery.addEventListener("change", reducedMotionChanged);
        } else {
            reducedMotionQuery.addListener(reducedMotionChanged);
        }
    }

    storageChanged = (event) => {
        if ([preferenceKey, legacyPreferenceKey, motionPreferenceKey, textScalePreferenceKey, null].includes(event.key)) {
            activePreference = readPreference();
            applyPreference(activePreference);
            notifyProvider();
        }
    };
    window.addEventListener("storage", storageChanged);
    preferenceChanged = () => {
        activePreference = readPreference();
        applyPreference(activePreference);
        notifyProvider();
    };
    window.addEventListener("swiftaid-theme-preference-changed", preferenceChanged);
    window.addEventListener(localPreferencesChangedEvent, preferenceChanged);
    return applyPreference(activePreference);
}

export function setThemePreference(preference) {
    let selected = preference === "light" || preference === "dark" ? preference : "system";
    try {
        if (selected === "system") {
            window.localStorage.removeItem(preferenceKey);
            window.localStorage.removeItem(legacyPreferenceKey);
        } else {
            window.localStorage.setItem(preferenceKey, selected);
            window.localStorage.removeItem(legacyPreferenceKey);
        }
    } catch {
        // Keep the app usable with the device setting when storage is unavailable.
        selected = "system";
    }

    activePreference = selected;
    notifyProvider();
    return selected;
}

export function stopWatchingSystemTheme() {
    if (colorSchemeQuery && colorSchemeChanged) {
        if (usesModernMediaQueryListener) {
            colorSchemeQuery.removeEventListener("change", colorSchemeChanged);
        } else {
            colorSchemeQuery.removeListener(colorSchemeChanged);
        }
    }
    if (reducedMotionQuery && reducedMotionChanged) {
        if (typeof reducedMotionQuery.removeEventListener === "function") {
            reducedMotionQuery.removeEventListener("change", reducedMotionChanged);
        } else {
            reducedMotionQuery.removeListener(reducedMotionChanged);
        }
    }
    if (storageChanged) {
        window.removeEventListener("storage", storageChanged);
    }
    if (preferenceChanged) {
        window.removeEventListener("swiftaid-theme-preference-changed", preferenceChanged);
        window.removeEventListener(localPreferencesChangedEvent, preferenceChanged);
    }

    colorSchemeQuery = undefined;
    colorSchemeChanged = undefined;
    reducedMotionQuery = undefined;
    reducedMotionChanged = undefined;
    storageChanged = undefined;
    preferenceChanged = undefined;
    usesModernMediaQueryListener = undefined;
    providerReference = undefined;
}
