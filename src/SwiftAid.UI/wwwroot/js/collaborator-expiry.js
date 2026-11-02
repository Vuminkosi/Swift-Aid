// datetime-local has no offset. Resolve it on the user's device, not the server.
export function toUtc(localValue) {
    if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(localValue)) return null;
    const value = new Date(localValue);
    if (!Number.isFinite(value.getTime())) return null;
    const pad = number => String(number).padStart(2, '0');
    const roundTrip = `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}T${pad(value.getHours())}:${pad(value.getMinutes())}`;
    // Reject daylight-saving gaps and normalized invalid dates.
    return roundTrip === localValue ? value.toISOString() : null;
}
