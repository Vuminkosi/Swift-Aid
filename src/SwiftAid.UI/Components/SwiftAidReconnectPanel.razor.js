let activeTrap;

const focusableSelector = [
    "a[href]",
    "area[href]",
    "button:not([disabled])",
    "input:not([disabled]):not([type='hidden'])",
    "select:not([disabled])",
    "textarea:not([disabled])",
    "[contenteditable='true']",
    "[tabindex]:not([tabindex='-1'])"
].join(",");

export function activate(dialog, initialFocus) {
    if (!(dialog instanceof HTMLElement) || !(initialFocus instanceof HTMLElement)) {
        return;
    }

    deactivate();

    const previousFocus = document.activeElement instanceof HTMLElement
        && document.activeElement !== document.body
        && document.activeElement !== document.documentElement
        ? document.activeElement
        : null;

    const handleKeyDown = event => {
        if (event.key === "Escape") {
            event.preventDefault();
            event.stopPropagation();
            return;
        }

        if (event.key !== "Tab") {
            return;
        }

        const focusableElements = Array.from(dialog.querySelectorAll(focusableSelector))
            .filter(element => element instanceof HTMLElement
                && element.getAttribute("aria-hidden") !== "true"
                && element.getClientRects().length > 0);

        if (focusableElements.length === 0) {
            event.preventDefault();
            initialFocus.focus({ preventScroll: true });
            return;
        }

        const first = focusableElements[0];
        const last = focusableElements[focusableElements.length - 1];
        const targetIsOutsideDialog = !dialog.contains(event.target);

        if (event.shiftKey && (event.target === first || targetIsOutsideDialog)) {
            event.preventDefault();
            last.focus({ preventScroll: true });
        } else if (!event.shiftKey && (event.target === last || targetIsOutsideDialog)) {
            event.preventDefault();
            first.focus({ preventScroll: true });
        }
    };

    activeTrap = { dialog, initialFocus, previousFocus, handleKeyDown };
    document.addEventListener("keydown", handleKeyDown, true);
    initialFocus.focus({ preventScroll: true });
}

export function deactivate() {
    if (!activeTrap) {
        return;
    }

    const { handleKeyDown, previousFocus } = activeTrap;
    document.removeEventListener("keydown", handleKeyDown, true);
    activeTrap = undefined;

    if (previousFocus?.isConnected) {
        previousFocus.focus({ preventScroll: true });
    }
}
