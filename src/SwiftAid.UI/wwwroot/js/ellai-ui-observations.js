// Trusted presentation bindings. No user-entered values, text, URLs or selectors
// supplied by the model are collected or executed.
const reportBindings = [
    ["report.entry", "guided-report-title"],
    ["report.subject", "report-type-title"],
    ["report.narrative", "report-details-title"],
    ["report.time-location", "guided-report-country"],
    ["report.voice-note", "public-report-voice-title"],
    ["report.review", "report-review-title"],
    ["report.confirmations", "report-review-title"],
    ["report.submit", "report-submit"],
    ["report.receipt", "public-receipt-title"]
];

let viewInstanceId = crypto.randomUUID();
let previousPath = location.pathname;
let contextStamp = crypto.randomUUID();
let previousState = null;

export function collect() {
    if (location.pathname !== previousPath) {
        previousPath = location.pathname;
        viewInstanceId = crypto.randomUUID();
        contextStamp = crypto.randomUUID();
    }
    const controlStates = [];
    for (const [controlId, id] of reportBindings) {
        const elements = document.querySelectorAll(`[id="${id}"]`);
        if (!elements.length) continue;
        let state = 4; // UiObservationState.Ready
        if (elements.length !== 1) state = 5; // UiObservationState.Ambiguous
        else {
            const element = elements[0];
            const style = getComputedStyle(element);
            if (!element.getClientRects().length || style.visibility !== "visible" ||
                (element.checkVisibility && !element.checkVisibility({ opacityProperty: true, visibilityProperty: true })) ||
                element.closest('[hidden], [aria-hidden="true"], [inert]')) state = 2; // Hidden
            else if (element.matches(':disabled') ||
                element.closest('[disabled], [aria-disabled="true"]')) state = 3; // Disabled
        }
        controlStates.push({ controlId, state });
    }
    const stateKey = JSON.stringify(controlStates);
    if (stateKey !== previousState) {
        previousState = stateKey;
        contextStamp = crypto.randomUUID();
    }
    return { viewInstanceId, contextStamp, platform: "web",
        mountedControlIds: controlStates.map(c => c.controlId), controlStates };
}
