let colorSchemeQuery;
let colorSchemeChanged;
let usesModernMediaQueryListener;

export function watchSystemTheme(dotNetReference) {
    colorSchemeQuery = window.matchMedia("(prefers-color-scheme: dark)");
    colorSchemeChanged = (event) => {
        const theme = event.matches ? "dark" : "light";
        document.documentElement.dataset.theme = theme;
        void dotNetReference.invokeMethodAsync("SetSystemTheme", event.matches).catch(() => {
            // The component can be disposed while a preference change is being delivered.
        });
    };

    document.documentElement.dataset.theme = colorSchemeQuery.matches ? "dark" : "light";
    usesModernMediaQueryListener = typeof colorSchemeQuery.addEventListener === "function";
    if (usesModernMediaQueryListener) {
        colorSchemeQuery.addEventListener("change", colorSchemeChanged);
    } else {
        colorSchemeQuery.addListener(colorSchemeChanged);
    }
    return colorSchemeQuery.matches;
}

export function stopWatchingSystemTheme() {
    if (colorSchemeQuery && colorSchemeChanged) {
        if (usesModernMediaQueryListener) {
            colorSchemeQuery.removeEventListener("change", colorSchemeChanged);
        } else {
            colorSchemeQuery.removeListener(colorSchemeChanged);
        }
    }

    colorSchemeQuery = undefined;
    colorSchemeChanged = undefined;
    usesModernMediaQueryListener = undefined;
}
