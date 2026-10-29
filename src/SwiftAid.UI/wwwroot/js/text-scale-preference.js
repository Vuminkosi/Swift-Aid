const storageKey = "swiftaid-text-scale";

// Applies in-app text scaling beyond the device setting without touching
// shared stylesheets: the root font size drives MudBlazor's rem-based text.
function apply(scale) {
    const root = document.documentElement;
    if (!root || !root.style) {
        return;
    }
    if (scale === "large") {
        root.style.fontSize = "112.5%";
    } else if (scale === "extra-large") {
        root.style.fontSize = "125%";
    } else {
        // "system" and "standard" both render at the default size;
        // the stored choice only records whether the user overrode the device.
        root.style.removeProperty("font-size");
    }
    try {
        if (scale === "large" || scale === "extra-large") {
            root.setAttribute("data-text-scale", scale);
        } else {
            root.removeAttribute("data-text-scale");
        }
    } catch {
        // Attribute reflection is a nicety; the inline size already applied.
    }
}

function storedPreference() {
    try {
        const stored = window.localStorage.getItem(storageKey);
        if (stored === "system" || stored === "standard" || stored === "large" || stored === "extra-large") {
            return stored;
        }
    } catch {
        // Private browsing or disabled storage: follow the device setting.
    }
    return "system";
}

export function getTextScale() {
    const preference = storedPreference();
    apply(preference);
    return preference;
}

export function setTextScale(preference) {
    const next = preference === "standard" || preference === "large" || preference === "extra-large"
        ? preference
        : "system";
    try {
        window.localStorage.setItem(storageKey, next);
    } catch {
        // Keep the in-memory choice working even when storage is unavailable.
    }
    apply(next);
    return next;
}
