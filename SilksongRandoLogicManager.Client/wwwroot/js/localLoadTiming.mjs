// Only explicitly named catalogue/map reads opt in. No data, storage or timers.
export const namedRead = request => request?.operation === 'query' && (request.name === 'catalogue' || request.name === 'maps');
export const clock = () => performance.now();
// Fixed numeric copy only: never forward request/result data or retain a last summary.
export function workerSummary(value) {
    try {
        if (!value) return null;
        const { executionMs, queueMs, statements, rows, cells, sourceValues, namedRows, extractionMs } = value;
        const values = [executionMs, queueMs, statements, rows, cells, sourceValues, namedRows, extractionMs];
        if (!values.every(x => Number.isFinite(x) && x >= 0 && x <= Number.MAX_SAFE_INTEGER)) return null;
        return { executionMs, queueMs, statements, rows, cells, sourceValues, namedRows, extractionMs };
    } catch { return null; }
}
export function record(realm, stage, name, request, broker, outcome, wall = null, summary = null) {
    try {
        const counts = workerSummary(summary);
        const metric = (value, milliseconds = false) => !Number.isFinite(value) || value < 0 || value > Number.MAX_SAFE_INTEGER
            ? 'unavailable' : milliseconds ? value.toFixed(1) : value;
        console.log(`[v3-local-load] realm=${realm} stage=${stage} query=${name} request=${Number.isSafeInteger(request) && request > 0 ? request : 'unavailable'} broker-request=${Number.isSafeInteger(broker) && broker > 0 ? broker : 'unavailable'} broker-mapping-available=${Number.isSafeInteger(broker) && broker > 0} outcome=${outcome} wall-ms=${metric(wall, true)} worker-summary-available=${counts !== null} worker-execution-before-response-post-wall-ms=${metric(counts?.executionMs, true)} queue-wall-ms=${metric(counts?.queueMs, true)} sql-starts=${metric(counts?.statements)} sql-rows=${metric(counts?.rows)} sql-selected-values=${metric(counts?.cells)} named-source-value-reads=${metric(counts?.sourceValues)} named-output-rows=${metric(counts?.namedRows)} inclusive-extraction-wall-ms=${metric(counts?.extractionMs, true)}`);
    } catch { } // Console/formatting must never replace the real result.
}
