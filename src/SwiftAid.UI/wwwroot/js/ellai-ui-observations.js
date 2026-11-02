// Trusted presentation bindings. No user-entered values, text, URLs or selectors
// supplied by the model are collected or executed.
// Components own their semantic identity. DOM presence is presentation evidence,
// never authorization; the Gateway filters these IDs against trusted descriptors.
const semanticId = /^[a-z][a-z0-9.-]{0,119}$/;

// Value-only snapshots, keyed by a disposable component instance token.
// No .NET component references, callbacks, field values or labels are retained.
const components = new Map();
export function updateComponent(instanceId, snapshot) {
    if (typeof instanceId !== 'string' || instanceId.length > 120 || !instanceId ||
        !semanticId.test(snapshot?.componentId ?? '') || !Array.isArray(snapshot.controls) ||
        snapshot.controls.length > 20) return false;
    if (!components.has(instanceId) && components.size >= 128) return false;
    const controls = snapshot.controls.map(control => ({ controlId: control.controlId, state: control.state }));
    if (controls.some(control => !semanticId.test(control.controlId ?? '') ||
        !Number.isInteger(control.state) || control.state < 0 || control.state > 6)) return false;
    components.set(instanceId, { path: location.pathname, controls });
    return true;
}

export function removeComponent(instanceId) {
    components.delete(instanceId);
}

let viewInstanceId = crypto.randomUUID();
let previousPath = location.pathname;
let contextStamp = crypto.randomUUID();
let previousState = null;

export function collect(requestedControls = null) {
    // The host supplies registered IDs for the active feature, never selectors.
    // Invalid filters return no targets rather than silently broadening the snapshot.
    const requested = requestedControls === null ? null : new Set(
        Array.isArray(requestedControls) && requestedControls.length <= 20 &&
        requestedControls.every(id => semanticId.test(id ?? '')) ? requestedControls : []);
    if (location.pathname !== previousPath) {
        previousPath = location.pathname;
        viewInstanceId = crypto.randomUUID();
        contextStamp = crypto.randomUUID();
    }
    const controlStates = [];
    const registrations = new Map();
    for (const element of document.querySelectorAll('[data-ellai-control]')) {
        const controlId = element.getAttribute('data-ellai-control');
        if (!semanticId.test(controlId ?? '') || (requested !== null && !requested.has(controlId))) continue;
        if (!registrations.has(controlId)) registrations.set(controlId, []);
        registrations.get(controlId).push(element);
    }
    // Bound the wire snapshot, retaining every duplicate for ambiguity detection.
    for (const [controlId, elements] of [...registrations.entries()].sort(([a], [b]) =>
        Number(a.startsWith('navigation.')) - Number(b.startsWith('navigation.')) || a.localeCompare(b)).slice(0, 20)) {
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
        const semanticStates = [...components.values()]
            .filter(component => component.path === location.pathname)
            .flatMap(component => component.controls.filter(control => control.controlId === controlId));
        if (semanticStates.length > 1) state = 5; // Multiple owners are ambiguous, never actionable.
        else if (state === 4 && semanticStates.length === 1) state = semanticStates[0].state;
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
