export async function loadLinkedMethods() {
    const identitiesResponse = await fetch("/auth/identities", {
        credentials: "same-origin",
        cache: "no-store",
        headers: { Accept: "application/json" }
    });

    if (identitiesResponse.status === 401) {
        return { state: "signedOut" };
    }

    if (!identitiesResponse.ok) {
        return { state: "unavailable" };
    }

    let linkedPayload = null;
    try {
        linkedPayload = await identitiesResponse.json();
    } catch {
        return { state: "unavailable" };
    }

    if (!Array.isArray(linkedPayload)) {
        return { state: "unavailable" };
    }

    const linked = linkedPayload.filter(id => typeof id === "string" && id.length > 0 && id.length <= 128);

    let providers = [];
    try {
        const providerResponse = await fetch("/auth/providers", {
            credentials: "same-origin",
            cache: "no-store",
            headers: { Accept: "application/json" }
        });
        if (providerResponse.ok) {
            const providerPayload = await providerResponse.json();
            if (Array.isArray(providerPayload)) {
                providers = providerPayload
                    .filter(provider => provider && typeof provider.id === "string")
                    .map(provider => ({
                        id: provider.id,
                        displayName: typeof provider.displayName === "string" && provider.displayName.trim().length > 0
                            ? provider.displayName
                            : provider.id,
                        available: provider.available === true
                    }));
            }
        }
    } catch {
        providers = [];
    }

    let requestToken = null;
    try {
        const antiforgeryResponse = await fetch("/auth/antiforgery", {
            credentials: "same-origin",
            cache: "no-store",
            headers: { Accept: "application/json" }
        });
        if (antiforgeryResponse.ok) {
            const antiforgery = await antiforgeryResponse.json();
            if (typeof antiforgery.requestToken === "string" && antiforgery.requestToken.length > 0) {
                requestToken = antiforgery.requestToken;
            }
        }
    } catch {
        requestToken = null;
    }

    if (typeof requestToken !== "string" || requestToken.length === 0) {
        return { state: "unavailable" };
    }

    return { state: "ready", linked, providers, requestToken };
}

export async function unlinkProvider(providerId, requestToken) {
    const response = await fetch("/auth/identities/" + encodeURIComponent(providerId) + "/unlink", {
        method: "POST",
        credentials: "same-origin",
        cache: "no-store",
        headers: {
            Accept: "application/json",
            "X-CSRF-TOKEN": requestToken
        }
    });

    if (response.status === 204) {
        return { ok: true, status: 204, message: null };
    }

    let message = null;
    try {
        const payload = await response.json();
        if (payload && typeof payload.error === "string" &&
            payload.error.length > 0 && payload.error.length <= 200) {
            message = payload.error;
        }
    } catch {
        message = null;
    }

    return { ok: false, status: response.status, message };
}
