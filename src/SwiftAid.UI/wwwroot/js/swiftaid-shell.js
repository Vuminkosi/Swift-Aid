let skipLink;
let skipLinkFocusHandler;

export function attachSkipLinkFocus(target) {
    if (!target) {
        return;
    }

    // A classic shared-library script may already provide this focus enhancement.
    // Let that module-independent handler own the click to avoid duplicate focus events.
    if (document.documentElement.dataset.swiftaidSkipFocusBound === "true") {
        return;
    }

    detachSkipLinkFocus();
    skipLink = document.querySelector(".swiftaid-app-shell__skip-link");
    if (!skipLink) {
        return;
    }

    skipLinkFocusHandler = event => {
        const urlBeforeClick = new URL(window.location.href);
        if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
            return;
        }

        // The shared shell persists while Blazor changes routes, so resolve its current href per activation.
        const destinationUrl = new URL(skipLink.href, urlBeforeClick);
        if (destinationUrl.origin !== urlBeforeClick.origin ||
            destinationUrl.pathname !== urlBeforeClick.pathname ||
            destinationUrl.search !== urlBeforeClick.search ||
            !destinationUrl.hash) {
            return;
        }

        window.requestAnimationFrame(() => {
            const currentUrl = new URL(window.location.href);
            const blazorHandledSamePageFragment = event.defaultPrevented &&
                currentUrl.origin === destinationUrl.origin &&
                currentUrl.pathname === destinationUrl.pathname &&
                currentUrl.search === destinationUrl.search &&
                currentUrl.hash === destinationUrl.hash;

            if ((event.defaultPrevented && !blazorHandledSamePageFragment) || !target.isConnected) {
                return;
            }

            target.focus({ preventScroll: true });
            target.scrollIntoView({ block: "start" });
        });
    };

    skipLink.addEventListener("click", skipLinkFocusHandler);
}

export function detachSkipLinkFocus() {
    if (skipLink && skipLinkFocusHandler) {
        skipLink.removeEventListener("click", skipLinkFocusHandler);
    }

    skipLink = undefined;
    skipLinkFocusHandler = undefined;
}
