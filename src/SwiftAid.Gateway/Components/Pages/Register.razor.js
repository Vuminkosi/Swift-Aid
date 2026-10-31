export async function loadRegistrationOptions() {
    const capabilityResponse = await fetch("/auth/capabilities", {
        credentials: "same-origin",
        cache: "no-store",
        headers: { Accept: "application/json" }
    });

    if (!capabilityResponse.ok) {
        throw new Error("Registration capabilities are unavailable.");
    }

    const capability = await capabilityResponse.json();
    if (typeof capability?.passwordSignInAvailable !== "boolean") {
        throw new Error("Registration capabilities are invalid.");
    }

    if (!capability.passwordSignInAvailable) {
        return { passwordSignInAvailable: false, policy: null };
    }

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

    return {
        passwordSignInAvailable: true,
        policy: {
            minimumLength: policy.minimumLength,
            requireDigit: policy.requireDigit,
            requireLowercase: policy.requireLowercase,
            requireUppercase: policy.requireUppercase,
            requireSymbol: policy.requireSymbol
        }
    };
}

export async function register(displayName, email, password) {
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
            body: JSON.stringify({ displayName, email, password })
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

export function focusRegistrationStatus() {
    document.querySelector(".register-live-status")?.focus();
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
