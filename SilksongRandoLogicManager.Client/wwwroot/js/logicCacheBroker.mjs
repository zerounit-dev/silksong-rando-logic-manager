// Independent SharedWorker broker. It owns no SQLite/OPFS or Worker construction,
// observes no business commands, and sends no cache-change broadcasts/heartbeats.
import { captureFailure, normalizeFailure, unavailable } from './cacheDiagnostics.mjs';
import { namedRead, workerSummary } from './localLoadTiming.mjs';
const clients = new Set(), pending = new Map();
let owner = null, next = 0;
function fail(request, message, failure = unavailable('Broker', 'retirement', request.body, request.localId)) {
    pending.delete(request.id);
    try {
        request.client.port.postMessage({ type: 'reply', id: request.localId,
            applicationVersion, generation: request.client.generation, error: message, failure: normalizeFailure(failure), brokerRequestId: namedRead(request.body) ? request.id : undefined });
    } catch { /* A dead requester must not strand other requests or owner cleanup. */ }
}
function retireOwner(message, failure) {
    const old = owner;
    owner = null;
    for (const client of clients) client.preparedOwner = null;
    if (old) {
        try { old.port.close(); } catch { }
        for (const request of [...pending.values()])
            if (request.owner === old) fail(request, message, failure);
        // Routing retirement alone cannot close the dedicated worker's DB/lock.
        // Notify only its hosting generation, after invalidation and request failure.
        try { old.client.port.postMessage({ type: 'retire-owner', applicationVersion,
            generation: old.client.generation }); } catch { /* Browser cleanup is best effort. */ }
    }
}
function dispatch(request) {
    if (!owner) return;
    if (request.body.operation === 'install' && request.client.preparedOwner !== owner) {
        fail(request, 'Installation ownership changed. Acquire a fresh prepared database before retrying.'); return;
    }
    request.owner = owner;
    try { owner.port.postMessage({ ...request.body, id: request.id, applicationVersion }); }
    catch (error) { retireOwner('The local database owner became unavailable. No authoring work was repeated.', captureFailure(error, 'Broker', 'postMessage', request.body, request.localId)); }
}
function retire(client, failure) {
    client.preparedOwner = null;
    if (!clients.delete(client)) return;
    if (owner?.client === client) retireOwner('The local database owner retired. Retry an explicit read or Refresh.', failure);
    for (const request of [...pending.values()])
        if (request.client === client) pending.delete(request.id);
    try { client.port.close(); } catch { }
}
self.onconnect = event => {
    const client = { port: event.ports[0], generation: null };
    clients.add(client);
    client.port.onmessageerror = () => retire(client, unavailable('Broker', 'messageerror'));
    client.port.onmessage = event => {
        const body = event.data;
        if (!clients.has(client) || !body) return;
        if (['hello', 'owner', 'request'].includes(body.type) && body.applicationVersion !== applicationVersion) {
            try { client.port.postMessage({ type: 'reply', generation: body.generation, id: body.id, applicationVersion, releaseRejected: true }); }
            finally { retire(client); }
            return;
        }
        if (body.type === 'hello' && client.generation === null) {
            client.generation = body.generation;
            return;
        }
        if (client.generation === null || body.generation !== client.generation) return;
        switch (body.type) {
            case 'retire': retire(client); break;
            case 'owner-failed':
                if (owner?.client === client) retireOwner('The local database owner failed.', normalizeFailure(body.failure));
                // Initial open can fail before any owner is installed. Waiting
                // requests must fail honestly rather than depend on a heartbeat.
                if (!owner) for (const request of [...pending.values()])
                    fail(request, 'The local database could not be opened.', normalizeFailure(body.failure));
                break;
            case 'owner': {
                if (!event.ports[0]) return;
                retireOwner('The local database owner changed. An obsolete cache request was not repeated.');
                const installed = { client, port: event.ports[0] };
                owner = installed;
                installed.port.onmessageerror = () => {
                    if (owner === installed) retireOwner('The local database owner response could not be read.', unavailable('Broker', 'messageerror'));
                };
                installed.port.onmessage = event => {
                    if (owner !== installed) return;
                    const response = event.data, request = pending.get(response?.id);
                    if (!request || request.owner !== installed) return;
                    pending.delete(request.id);
                    if (response.applicationVersion !== applicationVersion || response.releaseRejected) {
                        try { request.client.port.postMessage({ type: 'reply', generation: request.client.generation, id: request.localId,
                            applicationVersion, ownerVersion: response.applicationVersion, releaseRejected: true }); }
                        finally { retireOwner('Incompatible local owner.'); }
                        return;
                    }
                    if (request.body.operation === 'prepare-install' && !response.error) request.client.preparedOwner = installed;
                    if (request.body.operation === 'install') request.client.preparedOwner = null;
                    request.client.port.postMessage({ type: 'reply', generation: request.client.generation,
                        applicationVersion, ownerVersion: response.applicationVersion,
                        id: request.localId, result: response.result, error: response.error, brokerRequestId: namedRead(request.body) ? request.id : undefined,
                        workerSummary: namedRead(request.body) ? workerSummary(response.workerSummary) : undefined,
                        failure: response.error ? normalizeFailure(response.failure) : undefined });
                };
                installed.port.start();
                // Only requests never sent to any owner may wait for first open.
                // In-flight requests retired above are never replayed on replacement.
                for (const request of pending.values()) if (!request.owner) dispatch(request);
                break;
            }
            case 'request': {
                const request = { id: ++next, localId: body.id, client, owner: null, body: body.request };
                pending.set(request.id, request);
                dispatch(request);
                break;
            }
            case 'cancel':
                for (const request of pending.values())
                    if (request.client === client && request.localId === body.id) pending.delete(request.id);
                break;
        }
    };
    client.port.start();
};
