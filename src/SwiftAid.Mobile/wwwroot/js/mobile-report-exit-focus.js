const activeDialogs = new WeakMap();

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

    deactivate(dialog);

    const previousFocus = document.activeElement instanceof HTMLElement
        && document.activeElement !== document.body
        && document.activeElement !== document.documentElement
        ? document.activeElement
        : null;

    const handleKeyDown = event => {
        if (event.key !== "Tab") {
            return;
        }

        const focusableElements = Array.from(dialog.querySelectorAll(focusableSelector))
            .filter(element => element instanceof HTMLElement
                && element.getAttribute("aria-hidden") !== "true"
                && element.getAttribute("aria-disabled") !== "true"
                && element.getClientRects().length > 0);

        if (focusableElements.length === 0) {
            event.preventDefault();
            dialog.focus({ preventScroll: true });
            return;
        }

        const first = focusableElements[0];
        const last = focusableElements[focusableElements.length - 1];
        const activeElement = document.activeElement;
        const focusIsOnDialog = activeElement === dialog;
        const targetIsOutsideDialog = !(activeElement instanceof HTMLElement)
            || !dialog.contains(activeElement);

        if (event.shiftKey && (activeElement === first || focusIsOnDialog || targetIsOutsideDialog)) {
            event.preventDefault();
            last.focus({ preventScroll: true });
        } else if (!event.shiftKey && (activeElement === last || focusIsOnDialog || targetIsOutsideDialog)) {
            event.preventDefault();
            first.focus({ preventScroll: true });
        }
    };

    dialog.addEventListener("keydown", handleKeyDown, true);
    activeDialogs.set(dialog, { previousFocus, handleKeyDown });
    initialFocus.focus({ preventScroll: true });
}

export function focusDialog(dialog) {
    if (dialog instanceof HTMLElement) {
        dialog.focus({ preventScroll: true });
    }
}

export function deactivate(dialog) {
    const activeDialog = activeDialogs.get(dialog);
    if (!activeDialog) {
        return;
    }

    dialog.removeEventListener("keydown", activeDialog.handleKeyDown, true);
    activeDialogs.delete(dialog);

    if (activeDialog.previousFocus?.isConnected) {
        activeDialog.previousFocus.focus({ preventScroll: true });
    }
}
