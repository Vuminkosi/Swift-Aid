const key = 'swiftaid.ellai.languages.v1';
const supported = new Set(['eng', 'sna', 'ssw', 'nya', 'tso']);
const voices = new Set(['af_heart', 'am_michael']);
const defaults = () => ({ inputLanguage: 'eng', outputLanguage: 'eng', englishVoiceId: 'af_heart' });

export function load() {
    try {
        const value = JSON.parse(localStorage.getItem(key));
        return supported.has(value?.inputLanguage) && supported.has(value?.outputLanguage)
            ? { inputLanguage: value.inputLanguage, outputLanguage: value.outputLanguage,
                englishVoiceId: voices.has(value.englishVoiceId) ? value.englishVoiceId : 'af_heart' }
            : defaults();
    } catch { return defaults(); }
}

export function save(value) {
    if (!supported.has(value?.inputLanguage) || !supported.has(value?.outputLanguage)) return false;
    const voice = value.englishVoiceId ?? 'af_heart';
    if (!voices.has(voice)) return false;
    try {
        localStorage.setItem(key, JSON.stringify({ inputLanguage: value.inputLanguage, outputLanguage: value.outputLanguage,
            englishVoiceId: voice }));
        return true;
    } catch { return false; }
}
