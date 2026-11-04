async function requestToken() {
    const response = await fetch("/auth/antiforgery", {
        method: "GET",
        credentials: "same-origin",
        cache: "no-store",
        headers: { "Accept": "application/json" }
    });
    if (!response.ok) {
        return null;
    }

    const payload = await safeJson(response);
    return payload?.requestToken && payload?.headerName
        ? { value: payload.requestToken, header: payload.headerName }
        : null;
}

export async function changePassword(currentPassword, newPassword) {
    const token = await requestToken();
    if (!token) {
        return { ok: false, message: "Changing the password is temporarily unavailable." };
    }

    let response;
    try {
        response = await fetch("/auth/password/change", {
            method: "POST",
            credentials: "same-origin",
            cache: "no-store",
            headers: {
                "Content-Type": "application/json",
                [token.header]: token.value
            },
            body: JSON.stringify({ currentPassword, newPassword })
        });
    } catch {
        return { ok: false, message: "Check your connection and try again." };
    }

    if (response.status === 204) {
        return { ok: true, message: "Your password was changed." };
    }

    if (response.status === 401) {
        return { ok: false, message: "Sign in to change your password.", signedOut: true };
    }

    const payload = await safeJson(response);
    return {
        ok: false,
        message: (typeof payload?.error === "string" && payload.error.trim().length > 0)
            ? payload.error
            : "The password could not be changed."
    };
}

async function safeJson(response) {
    try {
        return await response.json();
    } catch {
        return null;
    }
}
