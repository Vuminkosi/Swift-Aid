// Trusted presentation bindings. No user-entered values, text, URLs or selectors
// supplied by the model are collected or executed.
// Components own their semantic identity. DOM presence is presentation evidence,
// never authorization; the Gateway filters these IDs against trusted descriptors.
const semanticId = /^[a-z][a-z0-9.-]{0,119}$/;

function isVisible(element) {
    const style = getComputedStyle(element);
    return element.getClientRects().length > 0 && style.visibility === "visible" &&
        (!element.checkVisibility || element.checkVisibility({ opacityProperty: true, visibilityProperty: true })) &&
        !element.closest('[hidden], [aria-hidden="true"], [inert]');
}

function isDisabled(element) {
    return element.matches(':disabled') || element.closest('[disabled], [aria-disabled="true"]') !== null;
}

// Value-only snapshots, keyed by a disposable component instance token.
// No .NET component references, callbacks, field values or labels are retained.
const components = new Map();
export function invalidateComponent(instanceId) {
    const previous = components.get(instanceId);
    if (!previous) return;
    components.set(instanceId, { ...previous, validationInputIds: [], inputStates: [], controls: previous.controls.map(control => ({
        controlId: control.controlId, state: 0, completionAccepted: false
    })) });
}

export function updateComponent(instanceId, snapshot) {
    if (typeof instanceId !== 'string' || instanceId.length > 120 || !instanceId) return false;
    if (!semanticId.test(snapshot?.componentId ?? '') || !Array.isArray(snapshot.controls) ||
        snapshot.controls.length > 20) {
        invalidateComponent(instanceId);
        return false;
    }
    if (!components.has(instanceId) && components.size >= 128) return false;
    const controls = snapshot.controls.map(control => ({ controlId: control?.controlId, state: control?.state, completionAccepted: control?.completionAccepted === true }));
    if (controls.some(control => !semanticId.test(control.controlId ?? '') ||
        !Number.isInteger(control.state) || control.state < 0 || control.state > 6)) {
        invalidateComponent(instanceId);
        return false;
    }
    const validationInputIds = snapshot.validationIssues ?? [];
    if (!Array.isArray(validationInputIds) || validationInputIds.length > 20 ||
        validationInputIds.some(issue => !semanticId.test(issue?.inputId ?? '')) ||
        new Set(validationInputIds.map(issue => issue.inputId)).size !== validationInputIds.length) {
        invalidateComponent(instanceId);
        return false;
    }
    const inputStates = snapshot.inputStates ?? [];
    if (!Array.isArray(inputStates) || inputStates.length > 20 || inputStates.some(input =>
        !semanticId.test(input?.inputId ?? '') || !controls.some(control => control.controlId === input?.controlId) ||
        !Number.isInteger(input?.presence) || input.presence < 0 || input.presence > 4) ||
        new Set(inputStates.map(input => `${input.controlId}:${input.inputId}`)).size !== inputStates.length) {
        invalidateComponent(instanceId);
        return false;
    }
    components.set(instanceId, { path: location.pathname, controls,
        inputStates: inputStates.map(({ controlId, inputId, presence }) => ({ controlId, inputId, presence })),
        validationInputIds: validationInputIds.map(issue => issue.inputId) });
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
        const visibleElements = elements.filter(isVisible);
        let state = 4; // UiObservationState.Ready
        if (visibleElements.length > 1) state = 5; // Multiple visible targets are ambiguous.
        else if (visibleElements.length === 0) state = 2; // Hidden duplicates do not block a visible responsive copy.
        else if (isDisabled(visibleElements[0])) state = 3; // Disabled
        const semanticStates = [...components.values()]
            .filter(component => component.path === location.pathname)
            .flatMap(component => component.controls.filter(control => control.controlId === controlId));
        if (semanticStates.length > 1) state = 5; // Multiple owners are ambiguous, never actionable.
        else if (state === 4 && semanticStates.length === 1) state = semanticStates[0].state;
        controlStates.push({ controlId, state });
    }
    // Validation evidence belongs to the mounted component, even after its wizard
    // moves past the validated field. It is never evidence of a server mutation.
    const completionOwners = new Map();
    for (const component of components.values()) {
        if (component.path !== location.pathname) continue;
        for (const control of component.controls) {
            if (requested !== null && !requested.has(control.controlId)) continue;
            const owners = completionOwners.get(control.controlId) ?? [];
            owners.push(control.completionAccepted);
            completionOwners.set(control.controlId, owners);
        }
    }
    const completedControlIds = [...completionOwners.entries()]
        .filter(([, owners]) => owners.length === 1 && owners[0])
        .map(([id]) => id).sort().slice(0, 20);
    const validationOwners = new Map();
    for (const component of components.values()) {
        if (component.path !== location.pathname) continue;
        if (requested !== null && !component.controls.some(control => requested.has(control.controlId))) continue;
        for (const id of component.validationInputIds ?? [])
            validationOwners.set(id, (validationOwners.get(id) ?? 0) + 1);
    }
    const validationInputIds = [...validationOwners.entries()]
        .filter(([, owners]) => owners === 1).map(([id]) => id).slice(0, 20);
    const inputOwners = new Map();
    for (const component of components.values()) {
        if (component.path !== location.pathname) continue;
        for (const input of component.inputStates ?? []) {
            if (!controlStates.some(control => control.controlId === input.controlId && control.state === 4)) continue;
            const key = `${input.controlId}:${input.inputId}`;
            const owners = inputOwners.get(key) ?? [];
            owners.push(input);
            inputOwners.set(key, owners);
        }
    }
    const inputStates = [...inputOwners.values()].filter(owners => owners.length === 1).map(owners => owners[0]).slice(0, 20);
    const stateKey = JSON.stringify({ controlStates, completedControlIds, validationInputIds, inputStates });
    if (stateKey !== previousState) {
        previousState = stateKey;
        contextStamp = crypto.randomUUID();
    }
    return { viewInstanceId, contextStamp, platform: "web",
        mountedControlIds: controlStates.map(c => c.controlId), controlStates, completedControlIds, validationInputIds, inputStates };
}
