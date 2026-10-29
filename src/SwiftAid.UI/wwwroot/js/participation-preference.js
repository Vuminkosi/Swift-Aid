const storageKey = "swiftaid-participation";

const knownKeys = [
    "community-health-worker",
    "nurse-clinician",
    "community-leader",
    "program-coordinator",
    "community-member"
];

export function getParticipation() {
    try {
        const stored = window.localStorage.getItem(storageKey);
        if (typeof stored === "string" && knownKeys.indexOf(stored) !== -1) {
            return stored;
        }
    } catch {
        // Private browsing or disabled storage: treat as no selection.
    }
    return "";
}

export function setParticipation(key) {
    const next = typeof key === "string" && knownKeys.indexOf(key) !== -1 ? key : "";
    try {
        if (next === "") {
            window.localStorage.removeItem(storageKey);
        } else {
            window.localStorage.setItem(storageKey, next);
        }
    } catch {
        // Keep the in-memory choice working even when storage is unavailable.
    }
    return next;
}
