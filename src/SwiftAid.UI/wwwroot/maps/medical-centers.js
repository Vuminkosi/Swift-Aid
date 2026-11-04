// Only public, attributed reference locations belong in this layer.
export function selectMedicalCenters(catalogue, country) {
    if (catalogue?.version !== 1 || !Array.isArray(catalogue.facilities)) return [];
    const selected = String(country || '').toUpperCase();
    return catalogue.facilities.filter(item => item.country === selected &&
        ['Hospital', 'Clinic'].includes(item.type) &&
        typeof item.name === 'string' && item.name.trim().length > 0 &&
        Number.isFinite(item.latitude) && Math.abs(item.latitude) <= 90 &&
        Number.isFinite(item.longitude) && Math.abs(item.longitude) <= 180 &&
        /^(node|way|relation)\/[1-9][0-9]*$/.test(item.id));
}

export function medicalCenterSource(item) {
    return /^(node|way|relation)\/[1-9][0-9]*$/.test(item.id)
        ? `https://www.openstreetmap.org/${item.id}` : null;
}
