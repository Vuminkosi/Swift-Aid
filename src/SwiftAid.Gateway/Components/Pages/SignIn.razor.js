const facebookSdkScriptId = "swiftaid-facebook-sdk";
let facebookSdkPromise;
let configuredFacebookSdk;

export async function loadSignInOptions() {
    const providerResponse = await fetch("/auth/providers", {
        credentials: "same-origin",
        cache: "no-store",
        headers: { Accept: "application/json" }
    });

    if (!providerResponse.ok) {
        throw new Error("Sign-in options are unavailable.");
    }

    const providerPayload = await providerResponse.json();
    const providers = Array.isArray(providerPayload)
        ? providerPayload
            .filter(provider => provider && typeof provider.id === "string")
            .map(provider => ({
                id: provider.id,
                displayName: typeof provider.displayName === "string" && provider.displayName.trim().length > 0
                    ? provider.displayName
                    : provider.id,
                available: provider.available === true
            }))
        : [];

    const facebookProvider = providers.find(provider => provider.id === "facebook");
    let facebookWebAvailable = false;

    if (facebookProvider?.available === true) {
        try {
            const response = await fetch("/auth/facebook/web/options", {
                credentials: "same-origin",
                cache: "no-store",
                headers: { Accept: "application/json" }
            });

            if (response.ok) {
                const options = await response.json();
                if (options?.available === true &&
                    typeof options.appId === "string" && options.appId.trim().length > 0 &&
                    typeof options.graphApiVersion === "string" && /^v\d+\.\d+$/.test(options.graphApiVersion)) {
                    try {
                        await loadFacebookSdk(options.appId, options.graphApiVersion);
                        facebookWebAvailable = true;
                    } catch {
                        facebookWebAvailable = false;
                    }
                }
            }
        } catch {
            facebookWebAvailable = false;
        }
    }

    const hasAvailableSignIn = providers.some(provider =>
        provider.available && provider.id !== "facebook") ||
        facebookWebAvailable;

    if (!hasAvailableSignIn) {
        return {
            providers,
            facebookWebAvailable: false,
            requestToken: null,
            optionsError: null
        };
    }

    const antiforgeryResponse = await fetch("/auth/antiforgery", {
        credentials: "same-origin",
        cache: "no-store",
        headers: { Accept: "application/json" }
    });

    if (!antiforgeryResponse.ok) {
        return {
            providers,
            facebookWebAvailable: false,
            requestToken: null,
            optionsError: "Sign-in is temporarily unavailable. Please try again."
        };
    }

    const antiforgery = await antiforgeryResponse.json();
    if (typeof antiforgery.requestToken !== "string" || antiforgery.requestToken.length === 0) {
        return {
            providers,
            facebookWebAvailable: false,
            requestToken: null,
            optionsError: "Sign-in is temporarily unavailable. Please try again."
        };
    }

    return {
        providers,
        facebookWebAvailable,
        requestToken: antiforgery.requestToken,
        optionsError: null
    };
}

export function wireFacebookLogin(buttonId, statusId, refreshButtonId, initialRequestToken, returnUrl) {
    const button = document.getElementById(buttonId);
    const liveStatus = document.getElementById(statusId);
    const refreshButton = document.getElementById(refreshButtonId);

    if (!button || !liveStatus || button.dataset.facebookWired === "true") {
        return;
    }

    let pending = false;
    let requestToken = initialRequestToken;
    button.dataset.facebookWired = "true";
    refreshButton?.addEventListener("click", async () => {
        refreshButton.disabled = true;
        try {
            requestToken = await fetchRequestToken();
            refreshButton.hidden = true;
            announce(liveStatus, "Sign-in options are ready. Choose Facebook to try again.");
        } catch {
            announce(liveStatus, "Sign-in options could not be refreshed. Please try again later.");
        } finally {
            refreshButton.disabled = false;
        }
    });
    button.addEventListener("click", () => {
        if (pending) {
            return;
        }

        const facebook = window.FB;
        if (!facebook || typeof facebook.login !== "function") {
            announce(liveStatus, "Facebook sign-in is unavailable right now. You can try again or continue without an account.");
            return;
        }

        pending = true;
        setButtonProgress(button, liveStatus, "Waiting for your choice in Facebook.");

        try {
            facebook.login(response => {
                void completeFacebookSignIn(response);
            }, { scope: "public_profile" });
        } catch {
            pending = false;
            resetButton(button);
            announce(liveStatus, "Facebook sign-in could not start. Please try again.");
        }
    });

    async function completeFacebookSignIn(response) {
        const status = response?.status;
        let accessToken = response?.authResponse?.accessToken;

        if (status === "unknown") {
            pending = false;
            resetButton(button);
            announce(liveStatus, "You dismissed Facebook sign-in. You can try again or continue without an account.");
            return;
        }

        if (status !== "connected" || typeof accessToken !== "string" || accessToken.length === 0) {
            pending = false;
            resetButton(button);
            announce(liveStatus, "Facebook sign-in did not finish. You can try again.");
            return;
        }

        setButtonProgress(button, liveStatus, "Finishing sign-in.");

        try {
            const exchangeResponse = await fetch("/auth/facebook/web/exchange", {
                method: "POST",
                credentials: "same-origin",
                cache: "no-store",
                headers: {
                    Accept: "application/json",
                    "Content-Type": "application/json",
                    "X-CSRF-TOKEN": requestToken
                },
                body: JSON.stringify({ accessToken, returnUrl })
            });

            if (!exchangeResponse.ok) {
                const code = await readSafeErrorCode(exchangeResponse);
                pending = false;
                resetButton(button);
                announce(liveStatus, messageForExchangeCode(code));
                if (code === "antiforgery_invalid") {
                    try {
                        requestToken = await fetchRequestToken();
                    } catch {
                        if (refreshButton) {
                            refreshButton.hidden = false;
                        }
                    }
                }
                return;
            }

            const result = await exchangeResponse.json();
            if (!isSafeLocalReturnUrl(result?.redirectUrl)) {
                pending = false;
                resetButton(button);
                announce(liveStatus, "Facebook sign-in could not be completed. Please try again.");
                return;
            }

            window.location.assign(result.redirectUrl);
        } catch {
            pending = false;
            resetButton(button);
            announce(liveStatus, "Facebook sign-in could not be completed. Check your connection and try again.");
        } finally {
            accessToken = "";
        }
    }
}

export function focusPrimaryOption() {
    const primaryOption = document.getElementById("facebook-sign-in-button") ??
        document.querySelector(".sign-in-provider-option form button[type='submit']");
    primaryOption?.focus();
}

function loadFacebookSdk(appId, graphApiVersion) {
    if (window.FB && typeof window.FB.login === "function") {
        initializeFacebookSdk(appId, graphApiVersion);
        return Promise.resolve();
    }

    if (!facebookSdkPromise) {
        facebookSdkPromise = new Promise((resolve, reject) => {
            let completed = false;
            const finish = (error) => {
                if (completed) {
                    return;
                }

                completed = true;
                window.clearTimeout(timeoutId);
                if (error) {
                    reject(error);
                } else {
                    resolve();
                }
            };

            const timeoutId = window.setTimeout(() => finish(new Error("Facebook SDK loading timed out.")), 15000);
            window.fbAsyncInit = () => {
                try {
                    initializeFacebookSdk(appId, graphApiVersion);
                    finish();
                } catch {
                    finish(new Error("Facebook SDK initialization failed."));
                }
            };

            let script = document.getElementById(facebookSdkScriptId);
            if (!script) {
                script = document.createElement("script");
                script.id = facebookSdkScriptId;
                script.async = true;
                script.defer = true;
                script.crossOrigin = "anonymous";
                script.src = "https://connect.facebook.net/en_US/sdk.js";
                script.addEventListener("error", () => finish(new Error("Facebook SDK failed to load.")), { once: true });
                document.head.appendChild(script);
            } else {
                script.addEventListener("error", () => finish(new Error("Facebook SDK failed to load.")), { once: true });
            }
        });
    }

    return facebookSdkPromise.catch(error => {
        facebookSdkPromise = undefined;
        throw error;
    });
}

function initializeFacebookSdk(appId, graphApiVersion) {
    const sdk = window.FB;
    if (!sdk || typeof sdk.init !== "function") {
        throw new Error("Facebook SDK is unavailable.");
    }

    const configuration = `${appId}:${graphApiVersion}`;
    if (configuredFacebookSdk === configuration) {
        return;
    }

    sdk.init({
        appId,
        status: false,
        cookie: false,
        localStorage: false,
        autoLogAppEvents: false,
        xfbml: false,
        version: graphApiVersion,
        fedCM: true
    });
    configuredFacebookSdk = configuration;
}

function isSafeLocalReturnUrl(value) {
    if (typeof value !== "string" || value.length === 0 || value.length > 2048 ||
        value[0] !== "/" || value.startsWith("//") || value.includes("\\") ||
        value.includes("\r") || value.includes("\n")) {
        return false;
    }

    try {
        return new URL(value, window.location.origin).origin === window.location.origin;
    } catch {
        return false;
    }
}

function setButtonProgress(button, liveStatus, message) {
    button.setAttribute("aria-busy", "true");
    button.textContent = "Connecting to Facebook…";
    announce(liveStatus, message);
}

async function fetchRequestToken() {
    const response = await fetch("/auth/antiforgery", {
        credentials: "same-origin",
        cache: "no-store",
        headers: { Accept: "application/json" }
    });
    if (!response.ok) {
        throw new Error("Antiforgery token refresh failed.");
    }
    const payload = await response.json();
    if (typeof payload.requestToken !== "string" || payload.requestToken.length === 0) {
        throw new Error("Antiforgery token refresh failed.");
    }
    return payload.requestToken;
}

async function readSafeErrorCode(response) {
    try {
        const payload = await response.json();
        const codes = new Set(["already_signed_in", "antiforgery_invalid", "invalid_provider_token", "provider_unavailable", "rate_limited", "web_login_disabled", "invalid_request", "oversized", "unsupported_media"]);
        return codes.has(payload?.code) ? payload.code : "unknown";
    } catch {
        return "unknown";
    }
}

function messageForExchangeCode(code) {
    switch (code) {
        case "already_signed_in": return "You’re already signed in. Your current account is unchanged.";
        case "antiforgery_invalid": return "Your sign-in session expired. Choose Facebook again to continue.";
        case "invalid_provider_token": return "Facebook could not confirm this sign-in. Please try again.";
        case "provider_unavailable": return "Facebook sign-in is temporarily unavailable. Please try again later.";
        case "rate_limited": return "Too many sign-in attempts. Please wait a moment and try again.";
        case "web_login_disabled": return "Facebook sign-in is temporarily unavailable. You can choose another option.";
        case "invalid_request":
        case "oversized":
        case "unsupported_media": return "Facebook sign-in could not be completed. Please try again.";
        default: return "Facebook sign-in could not be completed. Please try again.";
    }
}

function resetButton(button) {
    button.removeAttribute("aria-busy");
    button.textContent = "Continue with Facebook";
}

function announce(liveStatus, message) {
    liveStatus.textContent = message;
}
