const storageKey = "swiftaid-theme";

function storedPreference() {
    try {
        const stored = window.localStorage.getItem(storageKey);
        if (stored === "light" || stored === "dark" || stored === "system") {
            return stored;
        }
    } catch {
        // Private browsing or disabled storage: fall back to the device setting.
    }
    return "system";
}

function effectiveTheme(preference) {
    if (preference === "light" || preference === "dark") {
        return preference;
    }
    return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

function apply(theme) {
    document.documentElement.setAttribute("data-theme", theme);
}

export function getTheme() {
    const preference = storedPreference();
    apply(effectiveTheme(preference));
    return preference;
}

export function setTheme(preference) {
    const next = preference === "light" || preference === "dark" || preference === "system"
        ? preference
        : "system";
    try {
        window.localStorage.setItem(storageKey, next);
    } catch {
        // Keep the in-memory choice working even when storage is unavailable.
    }
    const effective = effectiveTheme(next);
    apply(effective);
    return effective;
}

export function watchSystemTheme(dotNet) {
    if (!dotNet || typeof window.matchMedia !== "function") {
        return;
    }
    const query = window.matchMedia("(prefers-color-scheme: dark)");
    const handler = () => {
        if (storedPreference() !== "system") {
            return;
        }
        const effective = query.matches ? "dark" : "light";
        apply(effective);
        dotNet.invokeMethodAsync("OnSystemThemeChanged", effective);
    };
    if (typeof query.addEventListener === "function") {
        query.addEventListener("change", handler);
    }
}
