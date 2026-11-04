(function () {
    "use strict";

    const root = document.documentElement;
    if (root.dataset.swiftaidSkipFocusBound === "true") {
        return;
    }

    root.dataset.swiftaidSkipFocusBound = "true";

    document.addEventListener("click", function (event) {
        const link = event.target instanceof Element
            ? event.target.closest(".swiftaid-app-shell__skip-link")
            : null;

        if (!link || event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
            return;
        }

        let destination;
        try {
            destination = new URL(link.href, window.location.href);
        } catch {
            return;
        }

        if (destination.origin !== window.location.origin ||
            destination.pathname !== window.location.pathname ||
            destination.search !== window.location.search ||
            !destination.hash) {
            return;
        }

        const targetId = decodeURIComponent(destination.hash.slice(1));
        window.setTimeout(function () {
            const focusTarget = document.getElementById(targetId);
            if (!focusTarget || !focusTarget.isConnected) {
                return;
            }

            focusTarget.focus({ preventScroll: true });
            focusTarget.scrollIntoView({ block: "start" });
        }, 0);
    });
})();
