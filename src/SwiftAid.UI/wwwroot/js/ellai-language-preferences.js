const key = 'swiftaid.ellai.languages.v1';
const supported = new Set(['eng', 'sna', 'ssw', 'nya', 'tso']);
const defaults = () => ({ inputLanguage: 'eng', outputLanguage: 'eng' });

export function load() {
    try {
        const value = JSON.parse(localStorage.getItem(key));
        return supported.has(value?.inputLanguage) && supported.has(value?.outputLanguage) ? value : defaults();
    } catch { return defaults(); }
}

export function save(value) {
    if (!supported.has(value?.inputLanguage) || !supported.has(value?.outputLanguage)) return false;
    try {
        localStorage.setItem(key, JSON.stringify({ inputLanguage: value.inputLanguage, outputLanguage: value.outputLanguage }));
        return true;
    } catch { return false; }
}
