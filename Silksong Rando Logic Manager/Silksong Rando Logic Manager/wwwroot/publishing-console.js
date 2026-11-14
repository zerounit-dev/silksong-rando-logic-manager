// One mounted page owns this object. No global listeners, routing or retained payloads.
export function create() {
    let retired = false;
    let jobOperation = null, jobSequence = 0;
    let previewOperation = null, previewSequence = 0;
    let previewStarted = null;
    return {
        previewStart(operation) {
            if (retired) return;
            previewStarted = { operation, at: performance.now() };
            console.info('[publishing] preview browser start', operation);
        },
        write(records, preview) {
            if (retired) return false;
            const started = performance.now();
            for (const record of records) {
                const operation = preview ? previewOperation : jobOperation;
                const sequence = preview ? previewSequence : jobSequence;
                if (record.operation === operation && record.sequence <= sequence) continue;
                // Copy only scalar values; DevTools must not lazily inspect a mutable object.
                const compact = Object.freeze(Object.fromEntries(Object.entries(record).filter(([, value]) => value !== null)));
                console.info(`[publishing] ${record.stage} · ${record.outcome}`, compact);
                // Advance per record only after its console call returned. An uncertain
                // interop completion can safely retry the same batch without duplicates.
                if (preview) { previewOperation = record.operation; previewSequence = record.sequence; }
                else { jobOperation = record.operation; jobSequence = record.sequence; }
            }
            console.info('[publishing] browser console presentation', Object.freeze({ presentationMs: performance.now() - started }));
            if (preview && previewStarted?.operation === previewOperation) {
                console.info('[publishing] preview browser-observed interval (native work + circuit delivery, not native-only)',
                    Object.freeze({ operation: previewOperation, browserObservedMs: performance.now() - previewStarted.at }));
                previewStarted = null;
            }
            return true;
        },
        delivery(roundtripMs) {
            if (!retired) console.info('[publishing] console interop roundtrip (not native work)', Object.freeze({ roundtripMs }));
        },
        dispose() { retired = true; previewStarted = null; }
    };
}
