export async function signOut() {
    try {
        const tokenResponse = await fetch("/auth/antiforgery", {
            method: "GET",
            credentials: "same-origin",
            cache: "no-store",
            headers: { "Accept": "application/json" }
        });
        if (!tokenResponse.ok) {
            return false;
        }

        const token = await tokenResponse.json();
        if (!token?.requestToken || !token?.headerName) {
            return false;
        }

        const response = await fetch("/auth/logout", {
            method: "POST",
            credentials: "same-origin",
            cache: "no-store",
            headers: { [token.headerName]: token.requestToken }
        });
        return response.status === 204;
    } catch {
        return false;
    }
}
