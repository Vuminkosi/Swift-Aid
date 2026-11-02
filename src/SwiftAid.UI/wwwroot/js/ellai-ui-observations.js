// Trusted presentation bindings. No user-entered values, text, URLs or selectors
// supplied by the model are collected or executed.
// Components own their semantic identity. DOM presence is presentation evidence,
// never authorization; the Gateway filters these IDs against trusted descriptors.
const semanticId = /^[a-z][a-z0-9.-]{0,119}$/;

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
    const registrations = new Map();
    for (const element of document.querySelectorAll('[data-ellai-control]')) {
        const controlId = element.getAttribute('data-ellai-control');
        if (!semanticId.test(controlId ?? '')) continue;
        if (!registrations.has(controlId)) registrations.set(controlId, []);
        registrations.get(controlId).push(element);
    }
    // Bound the wire snapshot, retaining every duplicate for ambiguity detection.
    for (const [controlId, elements] of [...registrations.entries()].sort(([a], [b]) => a.localeCompare(b)).slice(0, 20)) {
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
