// Inputs only, per room and per tab. No listeners, cross-tab events or graph cache.
export function read(key) {
    try { return { value: sessionStorage.getItem(key), notice: null }; }
    catch { return { value: null, notice: "Scenario storage is unavailable; defaults are in use." }; }
}
export function write(key, value) {
    try { sessionStorage.setItem(key, value); return true; }
    catch { return false; }
}
