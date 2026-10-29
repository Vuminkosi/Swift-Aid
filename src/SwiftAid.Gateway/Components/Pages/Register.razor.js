export async function loadPasswordPolicy() {
    try {
        const response = await fetch("/auth/password/policy", {
            credentials: "same-origin",
            cache: "no-store",
            headers: { Accept: "application/json" }
        });

        if (!response.ok) {
            return { minimumLength: 8, requireDigit: true, requireLowercase: true, requireUppercase: false, requireSymbol: true };
        }

        const policy = await response.json();
        return {
            minimumLength: Number.isInteger(policy?.minimumLength) ? policy.minimumLength : 8,
            requireDigit: policy?.requireDigit !== false,
            requireLowercase: policy?.requireLowercase === true,
            requireUppercase: policy?.requireUppercase === true,
            requireSymbol: policy?.requireSymbol !== false
        };
    } catch {
        return { minimumLength: 8, requireDigit: true, requireLowercase: true, requireUppercase: false, requireSymbol: true };
    }
}

export async function register(email, password) {
    let requestToken;
    try {
        requestToken = await fetchRequestToken();
    } catch {
        return { ok: false, error: "Registration is temporarily unavailable. Please try again." };
    }

    let response;
    try {
        response = await fetch("/auth/password/register", {
            method: "POST",
            credentials: "same-origin",
            cache: "no-store",
            headers: {
                Accept: "application/json",
                "Content-Type": "application/json",
                "X-CSRF-TOKEN": requestToken
            },
            body: JSON.stringify({ email, password })
        });
    } catch {
        return { ok: false, error: "Registration is temporarily unavailable. Check your connection and try again." };
    }

    if (response.ok) {
        return { ok: true, error: null };
    }

    if (response.status === 409) {
        return { ok: false, error: "An account with this email already exists. Sign in instead." };
    }

    return { ok: false, error: await readErrorMessage(response) };
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

async function readErrorMessage(response) {
    try {
        const payload = await response.json();
        if (typeof payload?.error === "string" && payload.error.trim().length > 0) {
            return payload.error;
        }
    } catch {
        // Fall through to the generic message.
    }

    return "Registration failed. Please try again.";
}
