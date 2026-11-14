// Tab-side transport/lifecycle only. The caller keeps its own feature admission,
// drafts, session and immutable command receipts; none are persisted or reset here.
import { captureFailure, normalizeFailure, unavailable, LocalDiagnosticError } from './cacheDiagnostics.mjs';
import { namedRead, clock, record } from './localLoadTiming.mjs';
let active = null, sequence = 0, disposed = false, callback = null;
let releaseRejected = false;
let installer = false;
class ReleaseRejected extends Error {}
async function rejectRelease() {
    releaseRejected = true;
    try { await callback?.invokeMethodAsync('LocalCacheReleaseRejected'); }
    finally { retire(); throw new ReleaseRejected('The local application installation is incompatible. Update your client.'); }
}
const pending = new Map();
function rejectAll(message, failure) {
    for (const entry of pending.values()) {
        clearTimeout(entry.timer);
        if (entry.observed) record('Tab', 'request-return', entry.value.name, entry.id, null, 'RetiredMappingUnavailable', clock() - entry.started);
        entry.reject(new LocalDiagnosticError(failure ?? unavailable('Tab', 'retirement', entry.value, entry.id)));
    }
    pending.clear();
}
function retire(failure) {
    const old = active;
    if (!old) return;
    active = null;
    rejectAll('This cache request generation retired. No authoring work was repeated.', failure);
    try { old.port.postMessage({ type: 'retire', generation: old.generation }); } catch { }
    old.port.close();
    // Best effort: do not wait for an unload/BFCache document to complete cleanup.
    // The dedicated worker closes DB/pool before releasing its Web Lock. Abrupt
    // document termination releases its lock/handles through the browser lifecycle.
    try { old.worker.postMessage({ type: 'retire' }); } catch { old.worker.terminate(); }
}
function connect() {
    if (disposed || releaseRejected) throw new ReleaseRejected('The local confirmed-cache runtime is retired.');
    if (active) return active;
    const generation = crypto.randomUUID();
    const broker = new SharedWorker(new URL('./logicCacheBroker.mjs', import.meta.url),
        { type: 'module', name: 'silksong-logic-cache-broker-' + applicationVersion });
    const worker = new Worker(new URL('./logicCacheOwner.mjs', import.meta.url), { type: 'module' });
    const client = { generation, broker, worker, port: broker.port };
    active = client;
    const ownerFailure = (event, failure) => {
        if (active !== client) return;
        const diagnostic = failure ?? captureFailure(event?.error, 'Tab', event?.type === 'messageerror' ? 'messageerror' : 'initialize');
        try { client.port.postMessage({ type: 'owner-failed', generation, failure: diagnostic }); } catch { }
        retire(diagnostic);
    };
    let ownerLoaded = false;
    worker.onerror = event => { if (!ownerLoaded) void rejectRelease().catch(() => {}); else ownerFailure(event); };
    worker.onmessageerror = ownerFailure;
    broker.onerror = () => { void rejectRelease().catch(() => {}); };
    client.port.onmessageerror = ownerFailure;
    worker.onmessage = event => {
        if (active !== client) return;
        if (event.data?.applicationVersion !== applicationVersion) { void rejectRelease().catch(() => {}); return; }
        if (event.data?.type === 'owner-failed') { ownerFailure(null, normalizeFailure(event.data.failure)); return; }
        if (event.data?.type !== 'owner-ready') return;
        ownerLoaded = true;
        // Chromium cannot create a Worker in SharedWorker. Transfer this dedicated
        // owner's port THROUGH its hosting tab, never a second database connection.
        const channel = new MessageChannel();
        try {
            worker.postMessage({ type: 'owner-port', applicationVersion }, [channel.port1]);
            client.port.postMessage({ type: 'owner', generation, applicationVersion }, [channel.port2]);
        } catch (error) { ownerFailure(null, captureFailure(error, 'Tab', 'postMessage')); }
    };
    client.port.onmessage = event => {
        if (active !== client || event.data?.generation !== generation) return;
        // Control admission precedes result/timing inspection and managed generic decoding.
        if (releaseRejected || event.data.applicationVersion !== applicationVersion || event.data.releaseRejected
            || event.data.ownerVersion && event.data.ownerVersion !== applicationVersion) { void rejectRelease().catch(() => {}); return; }
        if (event.data.type === 'retire-owner') { retire(); return; }
        const response = event.data, entry = pending.get(response.id);
        if (!entry || entry.generation !== generation) return;
        pending.delete(response.id); clearTimeout(entry.timer);
        if (entry.observed) record('Tab', 'request-return-before-managed-decode', entry.value.name, entry.id, response.brokerRequestId,
            response.error ? 'Failed' : response.result?.conflict ? 'Conflict' : 'Returned', clock() - entry.started, response.workerSummary);
        if (response.error) entry.reject(new LocalDiagnosticError(normalizeFailure(response.failure)));
        else entry.resolve({ requestId: response.id, result: response.result, brokerRequestId: response.brokerRequestId });
    };
    client.port.start();
    client.port.postMessage({ type: 'hello', generation, applicationVersion });
    return client;
}
function requestRaw(value) {
    let client;
    try { client = connect(); }
    catch (error) {
        if (namedRead(value)) record('Tab', 'request-return', value.name, null, null, 'ConnectFailedMappingUnavailable');
        throw error;
    }
    const id = ++sequence;
    const observed = namedRead(value), started = observed ? clock() : 0;
    if (observed) record('Tab', 'request-dispatch', value.name, id, null, 'StartMappingUnavailable');
    const context = { operation: value.operation, name: value.name };
    const timeout = ['prepare-install', 'install', 'patch'].includes(value.operation) ? 120000 : 30000;
    return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
            pending.delete(id);
            if (observed) record('Tab', 'request-return', context.name, id, null, 'TimeoutMappingUnavailable', clock() - started);
            try { client.port.postMessage({ type: 'cancel', generation: client.generation, id }); } catch { }
            reject(new LocalDiagnosticError(unavailable('Tab', 'timeout', context, id)));
        }, timeout);
        pending.set(id, { generation: client.generation, resolve, reject, timer, value: context, id, observed, started });
        try { client.port.postMessage({ type: 'request', generation: client.generation, applicationVersion, id, request: value }); }
        catch (error) {
            pending.delete(id); clearTimeout(timer);
            if (observed) record('Tab', 'request-return', context.name, id, null, 'PostFailedMappingUnavailable', clock() - started);
            reject(new LocalDiagnosticError(captureFailure(error, 'Tab', 'postMessage', context, id)));
        }
    });
}
// Sole managed request boundary, including successful null state. No thrown JSON.
export async function request(value) {
    if (releaseRejected) throw new ReleaseRejected('The local application runtime retired.');
    if (installer && !(value.currentRuntimeInstallation === true && ['prepare-install', 'install'].includes(value.operation)))
        throw new Error('Ordinary cache access is unavailable during explicit installation.');
    try { const observed = await requestRaw(value); if (releaseRejected || disposed) throw new ReleaseRejected(); return { success: true, ...observed, failure: null }; }
    catch (error) { if (releaseRejected || error instanceof ReleaseRejected) throw error; const failure = captureFailure(error, 'Tab', 'request', value); return { success: false, requestId: failure.requestId ?? 0, result: null, failure }; }
}
// Real browser task boundary, not a resolved Promise/.NET Task.Yield microtask.
export function yieldTask() { return new Promise(resolve => setTimeout(resolve, 0)); }
async function visibleAgain() {
    if (disposed || releaseRejected || installer || document.visibilityState !== 'visible' || !callback) return;
    // Restore native interaction first. This path performs LOCAL state access only;
    // the .NET consumer compares StateId before preparing changed RAM facts.
    await yieldTask();
    if (disposed || releaseRejected || installer || document.visibilityState !== 'visible' || !callback) return;
    const target = callback;
    let client;
    try {
        client = connect();
        const observed = await requestRaw({ operation: 'state' });
        if (!disposed && !releaseRejected && callback === target && active === client && document.visibilityState === 'visible')
            await target.invokeMethodAsync('LocalCacheVisible', observed);
    } catch (error) {
        if (!disposed && !releaseRejected && callback === target && (!client || active === client) && document.visibilityState === 'visible')
            await target.invokeMethodAsync('LocalCacheUnavailable', captureFailure(error, 'Tab', 'visibility', { operation: 'state' }));
    }
}
function pageHide() { retire(); }
function pageShow() {
    // Reconnect this retained document only. Never reinitialize the application,
    // touch drafts or add activation server/image freshness/network requests.
    if (!disposed && !releaseRejected && callback && !active) {
        try { connect(); } catch { /* First actual request reports the ordinary failure. */ }
    }
    void visibleAgain();
}
export async function initialize(dotnet, callerVersion, installRequired) {
    if (disposed) throw new Error('The confirmed-cache client is disposed.');
    if (callback) throw new Error('The application already owns cache lifecycle events.');
    callback = dotnet;
    installer = installRequired === true;
    if (callerVersion !== applicationVersion) return rejectRelease();
    document.addEventListener('visibilitychange', visibleAgain);
    window.addEventListener('pagehide', pageHide);
    window.addEventListener('pageshow', pageShow);
}
export function installationComplete() { if (releaseRejected || disposed) throw new ReleaseRejected(); installer = false; }
export function retireRuntime() { releaseRejected = true; retire(); }
export function dispose() {
    if (disposed) return;
    disposed = true;
    callback = null;
    document.removeEventListener('visibilitychange', visibleAgain);
    window.removeEventListener('pagehide', pageHide);
    window.removeEventListener('pageshow', pageShow);
    retire();
}
