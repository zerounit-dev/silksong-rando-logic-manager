// CACHE1-DIAG only: metadata snapshots, never messages/requests or a last-error slot.
const origins = new Set(['Owner', 'Broker', 'Tab', 'Managed', 'Shell', 'Unknown']);
const stages = new Set(['request', 'initialize', 'postMessage', 'messageerror', 'error', 'timeout', 'retirement', 'visibility', 'interop', 'feature', 'http', 'Unknown']);
const operations = new Set(['state', 'query', 'install', 'patch', 'inspect']);
const queries = new Set(['catalogue', 'inventory', 'vocabulary', 'maps', 'room']);
const types = new Set(['Error', 'TypeError', 'RangeError', 'SyntaxError', 'ReferenceError', 'URIError', 'EvalError', 'DOMException', 'SQLite3Error', 'UnknownError', 'UnknownThrownValue']);
// Declared functions in the permitted sources; no inferred stack names.
const methods = {
    'logicCacheQueries.mjs': new Set(['extractNamedQuery', 'select', 'index', 'type', 'invalid', 'text', 'integer', 'definitions', 'selected']),
    'logicCacheOwner.mjs': new Set(['enqueue', 'openDatabase', 'state', 'schema', 'validateIntegrity', 'assertNoTriggers', 'writeState', 'conflict', 'install', 'typedCell', 'tableRows', 'sameRow', 'patch', 'inspect', 'query', 'operate', 'retire']),
    'logicCacheBroker.mjs': new Set(['fail', 'retireOwner', 'dispatch', 'retire']),
    'logicCache.mjs': new Set(['rejectAll', 'retire', 'connect', 'requestRaw', 'request', 'yieldTask', 'visibleAgain', 'pageHide', 'pageShow', 'initialize', 'dispose']),
    'catalogue.js': new Set(['showError', 'closeError', 'restoreErrorFocus', 'restoreFocus', 'eligibleFocus', 'visible', 'showDialog', 'copyDiagnosticDetails']),
    'sqlite3.mjs': new Set()
};
const sources = new Set(['logicCacheOwner.mjs', 'logicCacheQueries.mjs', 'logicCacheBroker.mjs', 'logicCache.mjs', 'catalogue.js', 'sqlite3.mjs']);
const token = (set, value) => typeof value === 'string' && set.has(value) ? value : 'Unknown';
const number = value => Number.isInteger(value) && value >= -2147483648 && value <= 2147483647 ? value : null;
const position = value => Number.isInteger(value) && value > 0 && value <= 2147483647 ? value : null;
function frozen(value) {
    for (const cause of value.causes) { for (const frame of cause.frames) Object.freeze(frame); Object.freeze(cause.frames); Object.freeze(cause); }
    Object.freeze(value.causes); return Object.freeze(value);
}
export function unavailable(origin = 'Unknown', stage = 'Unknown', request = null, id = null) {
    try {
        return frozen({ version: 1, origin: token(origins, origin), stage: token(stages, stage),
            operation: token(operations, request?.operation), query: token(queries, request?.name),
            requestId: Number.isSafeInteger(id) && id >= 0 ? id : null,
            causes: [{ type: 'UnknownError', method: null, frames: [], sqliteCode: null, httpStatus: null, unavailable: true }], truncated: false });
    } catch { return frozen({ version: 1, origin: 'Unknown', stage: 'Unknown', operation: 'Unknown', query: 'Unknown', requestId: null,
        causes: [{ type: 'UnknownError', method: null, frames: [], sqliteCode: null, httpStatus: null, unavailable: true }], truncated: false }); }
}
export function normalizeFailure(value) {
    try {
        if (value?.version !== 1 || !Array.isArray(value.causes)) return unavailable();
        let remaining = 16, truncated = value.truncated === true || value.causes.length > 4;
        const causes = value.causes.slice(0, 4).map(cause => {
            const frames = [];
            if (Array.isArray(cause.frames)) {
                if (cause.frames.length > remaining) truncated = true;
                for (const frame of cause.frames.slice(0, remaining)) {
                    if (!sources.has(frame.source)) continue;
                    frames.push({ source: frame.source, method: methods[frame.source].has(frame.method) ? frame.method : null,
                        line: position(frame.line), column: position(frame.column) });
                }
            }
            remaining -= frames.length;
            return { type: types.has(cause.type) ? cause.type : 'UnknownError', method: null,
                frames, sqliteCode: number(cause.sqliteCode), httpStatus: Number.isInteger(cause.httpStatus) && cause.httpStatus >= 100 && cause.httpStatus <= 599 ? cause.httpStatus : null,
                unavailable: cause.unavailable === true || frames.length === 0 };
        });
        if (!causes.length) return unavailable();
        return frozen({ version: 1, origin: token(origins, value.origin), stage: token(stages, value.stage),
            operation: token(operations, value.operation), query: token(queries, value.query),
            requestId: Number.isSafeInteger(value.requestId) && value.requestId >= 0 ? value.requestId : null, causes, truncated });
    } catch { return unavailable(); }
}
function frameFrom(line) {
    // Only bounded V8/Firefox frame forms, never the message/header or eval text.
    const v8 = /^\s*at (?:(?:async )?([A-Za-z_$][\w$.<>]*) \()?((?:https?:\/\/|file:\/\/|\/)[^\s()]+):(\d+):(\d+)\)?\s*$/.exec(line);
    const firefox = /^([A-Za-z_$][\w$.<>]*)?@((?:https?:\/\/|file:\/\/|\/)[^\s()]+):(\d+):(\d+)\s*$/.exec(line);
    const match = v8 ?? firefox;
    if (!match) return null;
    const path = new URL(match[2], 'https://diagnostic.invalid').pathname;
    const source = /^\/logic-code\/js\/(logicCacheOwner|logicCacheQueries|logicCacheBroker|logicCache)\.[a-z0-9]{10}\.mjs$/.exec(path)?.[1];
    const name = source ? source + '.mjs' : /^\/logic-code\/catalogue\.[a-z0-9]{10}\.js$/.test(path) ? 'catalogue.js'
        : /^\/logic-code\/lib\/sqlite\/sqlite3\.[a-z0-9]{10}\.mjs$/.test(path) ? 'sqlite3.mjs' : null;
    if (!name) return null;
    return { source: name, method: methods[name].has(match[1]) ? match[1] : null, line: position(Number(match[3])), column: position(Number(match[4])) };
}
export class LocalDiagnosticError extends Error {
    constructor(failure) {
        super('The local confirmed-cache operation failed.');
        Object.defineProperty(this, 'failure', { value: normalizeFailure(failure), writable: false, configurable: false });
    }
}
export function captureFailure(error, origin, stage, request = null, id = null) {
    try {
        if (error instanceof LocalDiagnosticError) return normalizeFailure(error.failure);
        const base = unavailable(origin, stage, request, id), causes = [];
        let current = error, remaining = 16, truncated = false, examined = 0;
        for (let i = 0; i < 4 && current != null; ++i) {
            const object = typeof current === 'object' || typeof current === 'function';
            let type = object ? 'UnknownError' : 'UnknownThrownValue', stack = null, code = null, next = null;
            try { if (object) { type = types.has(current.name) ? current.name : 'UnknownError'; code = number(current.resultCode); stack = current.stack; next = current.cause; } } catch { /* No getters can replace the original failure. */ }
            const frames = [];
            if (typeof stack === 'string') {
                const count = Math.min(stack.length, Math.max(0, 32768 - examined)); examined += count;
                if (count < stack.length) truncated = true;
                const lines = stack.slice(0, count).split('\n');
                // V8 first line is the message; Firefox may start directly with fn@source.
                const first = /^[A-Za-z_$][\w$.<>]*@/.test(lines[0]) ? 0 : 1;
                for (const line of lines.slice(first)) {
                    const frame = frameFrom(line);
                    if (!frame) continue;
                    if (!remaining) { truncated = true; break; }
                    frames.push(frame); --remaining;
                }
            }
            causes.push({ type, method: null, frames, sqliteCode: code, httpStatus: null, unavailable: frames.length === 0 });
            if (next === current) break;
            current = next;
            if (i === 3 && current != null) truncated = true;
        }
        return normalizeFailure({ ...base, causes: causes.length ? causes : base.causes, truncated });
    } catch { return unavailable(origin, stage, request, id); }
}
