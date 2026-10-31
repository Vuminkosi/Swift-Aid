let interactPromise;
const bindings = new WeakMap();

function clamp(value, maximum) {
    return Math.max(0, Math.min(value, Math.max(0, maximum)));
}

function applyPosition(root, x, y) {
    x = clamp(x, window.innerWidth - root.offsetWidth);
    y = clamp(y, window.innerHeight - root.offsetHeight);
    root.style.insetInlineEnd = "auto";
    root.style.insetBlockEnd = "auto";
    root.style.left = `${x}px`;
    root.style.top = `${y}px`;
    return { x, y };
}

function loadInteract() {
    if (globalThis.interact) {
        return Promise.resolve(globalThis.interact);
    }
    if (interactPromise) {
        return interactPromise;
    }

    interactPromise = new Promise((resolve, reject) => {
        const script = document.createElement("script");
        script.src = new URL("../vendor/interactjs/interact.min.js", import.meta.url).href;
        script.async = true;
        script.onload = () => globalThis.interact
            ? resolve(globalThis.interact)
            : reject(new Error("interact.js loaded without its browser API."));
        script.onerror = () => reject(new Error("interact.js could not be loaded."));
        document.head.append(script);
    });

    return interactPromise;
}

export async function attach(root, dotnet) {
    detach(root);
    const interact = await loadInteract();
    let x = 0;
    let y = 0;

    const interactable = interact(root).draggable({
        allowFrom: ".ellai-composer__handle",
        inertia: false,
        autoScroll: false,
        modifiers: [
            interact.modifiers.restrictRect({
                restriction: document.documentElement,
                endOnly: false,
            }),
        ],
        listeners: {
            start() {
                const rect = root.getBoundingClientRect();
                x = rect.left;
                y = rect.top;
                root.classList.add("ellai-composer--dragging");
            },
            move(event) {
                x += event.dx;
                y += event.dy;
                ({ x, y } = applyPosition(root, x, y));
            },
            async end() {
                root.classList.remove("ellai-composer--dragging");
                await dotnet.invokeMethodAsync("OnInteractPositionChanged", x, y);
            },
        },
    });

    const resize = async () => {
        if (!root.style.left && !root.style.top) {
            return;
        }
        const rect = root.getBoundingClientRect();
        ({ x, y } = applyPosition(root, rect.left, rect.top));
        await dotnet.invokeMethodAsync("OnInteractPositionChanged", x, y);
    };
    window.addEventListener("resize", resize, { passive: true });
    bindings.set(root, { interactable, resize });
}

export function moveBy(root, deltaX, deltaY) {
    const rect = root.getBoundingClientRect();
    return applyPosition(root, rect.left + deltaX, rect.top + deltaY);
}

export function detach(root, resetPosition = false) {
    const binding = bindings.get(root);
    if (binding) {
        binding.interactable.unset();
        window.removeEventListener("resize", binding.resize);
        bindings.delete(root);
    }
    root.classList.remove("ellai-composer--dragging");
    if (resetPosition) {
        root.style.removeProperty("left");
        root.style.removeProperty("top");
        root.style.removeProperty("inset-inline-end");
        root.style.removeProperty("inset-block-end");
    }
}
