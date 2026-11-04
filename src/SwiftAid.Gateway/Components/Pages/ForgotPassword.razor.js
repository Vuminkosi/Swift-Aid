export async function requestPasswordReset(email) {
    let requestToken;
    try {
        requestToken = await fetchRequestToken();
    } catch {
        return { ok: false, message: "Password reset is temporarily unavailable. Please try again." };
    }

    let response;
    try {
        response = await fetch("/auth/password/reset/request", {
            method: "POST",
            credentials: "same-origin",
            cache: "no-store",
            headers: {
                Accept: "application/json",
                "Content-Type": "application/json",
                "X-CSRF-TOKEN": requestToken
            },
            body: JSON.stringify({ email })
        });
    } catch {
        return { ok: false, message: "Check your connection and try again." };
    }

    if (response.ok) {
        const payload = await safeJson(response);
        return {
            ok: true,
            message: payload?.message ?? "If an account matches this address, reset instructions have been sent."
        };
    }

    if (response.status === 503) {
        return { ok: false, message: "Password reset is temporarily unavailable. Please try again later." };
    }

    const payload = await safeJson(response);
    return {
        ok: false,
        message: typeof payload?.error === "string" && payload.error.trim().length > 0
            ? payload.error
            : "Enter a valid email address."
    };
}

async function fetchRequestToken() {
    const response = await fetch("/auth/antiforgery", {
        credentials: "same-origin",
        cache: "no-store",
        headers: { Accept: "application/json" }
    });

    if (!response.ok) {
        throw new Error("Antiforgery token request failed.");
    }

    const payload = await response.json();
    if (typeof payload?.requestToken !== "string" || payload.requestToken.length === 0) {
        throw new Error("Antiforgery token request failed.");
    }

    return payload.requestToken;
}

async function safeJson(response) {
    try {
        return await response.json();
    } catch {
        return null;
    }
}
