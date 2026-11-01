const bindings = new WeakMap();

export function attach(root, receiver) {
    detach(root);
    let pending = false;
    const listener = event => {
        const input = event.target;
        if (!input?.matches?.('.ellai-composer__field-wrap textarea, .ellai-composer__field-wrap input') ||
            event.key !== 'Enter' || event.shiftKey || event.isComposing || event.keyCode === 229) return;
        event.preventDefault();
        event.stopPropagation();
        if (pending || event.repeat || input.disabled || !input.value.trim()) return;
        pending = true;
        // Send the DOM value with the gesture; server-side input events may still
        // be in flight. The component retains the authoritative CanSend gate.
        receiver.invokeMethodAsync('SubmitKeyboardMessage', input.value)
            .catch(() => {})
            .finally(() => { pending = false; });
    };
    root.addEventListener('keydown', listener);
    bindings.set(root, listener);
}

export function detach(root) {
    const listener = bindings.get(root);
    if (listener) root.removeEventListener('keydown', listener);
    bindings.delete(root);
}
