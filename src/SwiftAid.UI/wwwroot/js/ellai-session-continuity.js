// Only the opaque session reference is stored here. Messages and ownership stay on the Gateway.
const key = "swiftaid.ellai.session";

export function read() {
    try { return localStorage.getItem(key); } catch { return null; }
}

export function write(sessionId) {
    try {
        if (sessionId) localStorage.setItem(key, sessionId);
        else localStorage.removeItem(key);
    } catch { /* Storage may be disabled; the current session remains usable. */ }
}
