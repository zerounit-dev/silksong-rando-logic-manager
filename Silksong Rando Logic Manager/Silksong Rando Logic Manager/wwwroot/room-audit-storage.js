// Per-room inputs and one independent tab-mode preference. No listeners or graph cache.
export function read(key) {
    try { return { value: sessionStorage.getItem(key), notice: null }; }
    catch { return { value: null, notice: "Scenario storage is unavailable; defaults are in use." }; }
}
export function write(key, value) {
    try { sessionStorage.setItem(key, value); return true; }
    catch { return false; }
}

const requirementsModeKey = 'silksong-room-audit:requirements-mode';
export function readRequirementsMode() {
    try {
        const value = sessionStorage.getItem(requirementsModeKey);
        return value === null || value === 'Basic' || value === 'Advanced'
            ? { value, notice: null }
            : { value: null, notice: 'Stored requirements mode is invalid; Basic is in use.' };
    }
    catch { return { value: null, notice: 'Requirements mode storage is unavailable; Basic is in use.' }; }
}
export function writeRequirementsMode(value) {
    if (value !== 'Basic' && value !== 'Advanced') return false;
    return write(requirementsModeKey, value);
}
