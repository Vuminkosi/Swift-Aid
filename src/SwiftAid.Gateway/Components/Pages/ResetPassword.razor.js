export async function loadPasswordPolicy() {
    const response = await fetch("/auth/password/policy", {
        credentials: "same-origin",
        cache: "no-store",
        headers: { Accept: "application/json" }
    });
    if (!response.ok) {
        throw new Error("Password policy is unavailable.");
    }

    const policy = await response.json();
    if (!Number.isInteger(policy?.minimumLength) ||
        typeof policy.requireDigit !== "boolean" ||
        typeof policy.requireLowercase !== "boolean" ||
        typeof policy.requireUppercase !== "boolean" ||
        typeof policy.requireSymbol !== "boolean") {
        throw new Error("Password policy is invalid.");
    }

    return policy;
}

export async function confirmPasswordReset(userId, code, newPassword) {
    let requestToken;
    try {
        requestToken = await fetchRequestToken();
    } catch {
        return { ok: false, message: "Password reset is temporarily unavailable. Please try again." };
    }

    let response;
    try {
        response = await fetch("/auth/password/reset/confirm", {
            method: "POST",
            credentials: "same-origin",
            cache: "no-store",
            headers: {
                Accept: "application/json",
                "Content-Type": "application/json",
                "X-CSRF-TOKEN": requestToken
            },
            body: JSON.stringify({ userId, code, newPassword })
        });
    } catch {
        return { ok: false, message: "Check your connection and try again." };
    }

    if (response.ok || response.status === 204) {
        return { ok: true, message: null };
    }

    const payload = await safeJson(response);
    return {
        ok: false,
        message: typeof payload?.error === "string" && payload.error.trim().length > 0
            ? payload.error
            : "The reset link is invalid or has expired."
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
