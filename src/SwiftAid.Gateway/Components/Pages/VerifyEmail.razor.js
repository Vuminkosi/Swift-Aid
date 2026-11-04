export async function requestEmailCode() {
    let requestToken;
    try {
        requestToken = await fetchRequestToken();
    } catch {
        return { ok: false, message: "Verification is temporarily unavailable. Please try again." };
    }

    let response;
    try {
        response = await fetch("/auth/email/otp/request", {
            method: "POST",
            credentials: "same-origin",
            cache: "no-store",
            headers: {
                Accept: "application/json",
                "Content-Type": "application/json",
                "X-CSRF-TOKEN": requestToken
            },
            body: JSON.stringify({})
        });
    } catch {
        return { ok: false, message: "Check your connection and try again." };
    }

    if (response.ok) {
        const payload = await safeJson(response);
        return {
            ok: true,
            message: payload?.message ?? "A verification code has been sent to your email address.",
            retryAfterSeconds: toNonNegativeInt(payload?.retryAfterSeconds)
        };
    }

    if (response.status === 429) {
        const payload = await safeJson(response);
        return {
            ok: false,
            message: (typeof payload?.error === "string" && payload.error.trim().length > 0)
                ? payload.error
                : "A code was sent recently. Wait before requesting a new one.",
            retryAfterSeconds: toNonNegativeInt(payload?.retryAfterSeconds)
        };
    }

    if (response.status === 401) {
        return { ok: false, message: "Sign in to verify your email address." };
    }

    return { ok: false, message: "Verification codes are temporarily unavailable. Please try again later." };
}

export async function confirmEmailCode(code) {
    let requestToken;
    try {
        requestToken = await fetchRequestToken();
    } catch {
        return { ok: false, message: "Verification is temporarily unavailable. Please try again." };
    }

    let response;
    try {
        response = await fetch("/auth/email/otp/confirm", {
            method: "POST",
            credentials: "same-origin",
            cache: "no-store",
            headers: {
                Accept: "application/json",
                "Content-Type": "application/json",
                "X-CSRF-TOKEN": requestToken
            },
            body: JSON.stringify({ code })
        });
    } catch {
        return { ok: false, message: "Check your connection and try again." };
    }

    if (response.ok) {
        return { ok: true, message: null };
    }

    if (response.status === 401) {
        return { ok: false, message: "Sign in to verify your email address." };
    }

    const payload = await safeJson(response);
    return {
        ok: false,
        message: typeof payload?.error === "string" && payload.error.trim().length > 0
            ? payload.error
            : "The code is invalid or has expired."
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

function toNonNegativeInt(value) {
    const parsed = typeof value === "number" ? Math.floor(value) : Number.parseInt(value, 10);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : 0;
}
