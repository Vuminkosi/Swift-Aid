const reconnectModal = document.getElementById("components-reconnect-modal");
const loader = document.getElementById("components-reconnect-loader");
const liveStatus = document.getElementById("components-reconnect-live-status");
const activeText = document.getElementById("components-reconnect-active-text");
const retryButton = document.getElementById("components-reconnect-button");
const resumeButton = document.getElementById("components-resume-button");
const reducedMotionPreference = typeof window.matchMedia === "function"
    ? window.matchMedia("(prefers-reduced-motion: reduce)")
    : null;

let loaderActive = false;

reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);
retryButton.addEventListener("click", retry);
resumeButton.addEventListener("click", resume);

reducedMotionPreference?.addEventListener("change", () => {
    if (loaderActive) {
        renderLoader();
    }
});

function setReconnectLoading(active, message = "") {
    loaderActive = active;
    reconnectModal.classList.toggle("components-reconnect-loader-active", active);
    reconnectModal.setAttribute("aria-busy", String(active));
    retryButton.disabled = active;
    resumeButton.disabled = active;
    liveStatus.textContent = message;
    activeText.textContent = message;

    if (!active) {
        loader.replaceChildren();
        loader.hidden = true;
        return;
    }

    loader.hidden = false;
    renderLoader();
}

function renderLoader() {
    if (!loaderActive) {
        return;
    }

    const image = document.createElement("img");
    image.className = "components-reconnect-loader-image";
    image.alt = "";
    image.setAttribute("aria-hidden", "true");
    image.width = 150;
    image.height = 150;
    image.style.cssText = "display:block;box-sizing:border-box;width:150px;height:150px;min-width:150px;min-height:150px;max-width:150px;max-height:150px;object-fit:fill;object-position:center;flex:none;";
    image.src = !reducedMotionPreference || reducedMotionPreference.matches
        ? loader.dataset.stillSrc
        : loader.dataset.gifSrc;
    loader.replaceChildren(image);
}

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        setReconnectLoading(true, "Trying to reconnect.");
        reconnectModal.showModal();
    } else if (event.detail.state === "hide") {
        setReconnectLoading(false);
        reconnectModal.close();
    } else if (event.detail.state === "failed") {
        setReconnectLoading(false, "Still offline. Try again or refresh the page.");
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } else if (event.detail.state === "rejected") {
        setReconnectLoading(false, "We could not continue. Refreshing the page.");
        location.reload();
    }
}

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    setReconnectLoading(true, "Trying to reconnect.");

    try {
        // Reconnect returns true on success, false when the server has no circuit, or throws when unreachable.
        const successful = await Blazor.reconnect();
        if (!successful) {
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (!resumeSuccessful) {
                location.reload();
            } else {
                reconnectModal.close();
            }
        }
    } catch {
        setReconnectLoading(false, "We could not reconnect. Try again or refresh the page.");
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } finally {
        if (loaderActive) {
            setReconnectLoading(false);
        }
    }
}

async function resume() {
    setReconnectLoading(true, "Continuing.");

    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            location.reload();
        }
    } catch {
        reconnectModal.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
        setReconnectLoading(false, "We could not continue. Try again or refresh the page.");
    } finally {
        if (loaderActive) {
            setReconnectLoading(false);
        }
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}
