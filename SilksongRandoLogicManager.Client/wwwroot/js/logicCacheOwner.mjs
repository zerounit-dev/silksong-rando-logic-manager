import { ownerLock, databasePath, metadataTable, tables, fields, vocabularyTables,
    checkpoint, sameCheckpoint, quote, bindCell, uuid } from './logicCacheProtocol.mjs';
import { captureFailure, unavailable, LocalDiagnosticError } from './cacheDiagnostics.mjs';
import { namedRead, clock, workerSummary } from './localLoadTiming.mjs';
import { extractNamedQuery } from './logicCacheQueries.mjs';

// One candidate per tab, but ONLY the Web Lock holder imports SQLite/opens OPFS.
// Even asynchronous initialization/install/close is on this one promise chain.
let retired = false, sqlite, pool, db, columns;
let chain = Promise.resolve(), releaseOwner, ownerPort;
const releaseFile = 'silksong-logic-cache-release';
class ReleaseRejected extends Error {}
async function readRelease() {
    const root = await navigator.storage.getDirectory();
    let file;
    try { file = await root.getFileHandle(releaseFile); }
    catch (error) { if (error.name === 'NotFoundError') return null; throw error; }
    const bytes = new Uint8Array(await (await file.getFile()).arrayBuffer());
    if (bytes.length !== 25 || bytes.some(x => x > 127)) return null;
    const value = new TextDecoder('ascii').decode(bytes);
    return /^app-[0-9]{21}$/.test(value) ? value : null;
}
async function writeRelease(value) {
    const root = await navigator.storage.getDirectory();
    const file = await root.getFileHandle(releaseFile, { create: true });
    const stream = await file.createWritable();
    try { await stream.write(new TextEncoder().encode(value)); await stream.close(); }
    catch (error) { try { await stream.abort(); } catch {} throw error; }
}
async function ensurePool() {
    if (pool) return;
    try {
        const module = await import('../lib/sqlite/sqlite3.mjs');
        sqlite = await module.default({ locateFile: name => {
            if (name.endsWith('.wasm')) return '__LOGIC_SQLITE_WASM_URL__';
            throw new Error('Undeclared SQLite executable dependency.');
        }});
    } catch (cause) { throw new ReleaseRejected('Required pinned SQLite code could not be loaded.', { cause }); }
    if (!sqlite.wasm.bigIntEnabled) throw new Error('Exact SQLite integers require BigInt.');
    pool = await sqlite.installOpfsSAHPoolVfs({ name: 'silksong-logic-sahpool',
        directory: '/silksong-logic-cache', preserveOnInitFailure: true });
}
class UnsupportedCacheSchemaError extends Error {}
const waiting = new AbortController();
function enqueue(action) {
    const operation = chain.then(action);
    chain = operation.catch(() => {});
    return operation;
}
const rows = (sql, bind = [], observation = null, cellCount = 0) => {
    if (observation) observation.statements++;
    const result = db.exec({ sql, bind, rowMode: 'object', returnValue: 'resultRows' });
    if (observation) { observation.rows += result.length; observation.cells += result.length * cellCount; }
    return result;
};
const scalar = (sql, observation = null) => {
    if (observation) observation.statements++;
    const result = db.selectValue(sql);
    if (observation) { observation.rows++; observation.cells++; }
    return result;
};
function openDatabase(observation = null) {
    db = new pool.OpfsSAHPoolDb(databasePath, 'c');
    if (observation) observation.statements++;
    db.exec('PRAGMA foreign_keys=ON');
}
function state(observation = null) {
    if (!db) return null;
    try {
        if (!scalar(`SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='${metadataTable}'`, observation)) return null;
        const values = rows(`SELECT Format, RunId, StartedUtc, ThroughUtc, Schema, StateId FROM ${quote(metadataTable)} WHERE Id=1`, [], observation, 6);
        if (values.length !== 1 || !uuid(values[0].StateId)) return null;
        const x = values[0];
        let current;
        try { current = { stateId: x.StateId, checkpoint: checkpoint({ format: Number(x.Format), runId: x.RunId,
            startedUtc: x.StartedUtc, throughUtc: x.ThroughUtc, schema: x.Schema }) }; }
        catch { return null; }
        if (!columns) {
            const validated = schema(observation);
            assertNoTriggers(observation);
            columns = validated;
        }
        return current;
    } catch (error) {
        // Only explicit compatibility failures make retained storage cold. Do not
        // alter its bytes/metadata or hide ordinary SQL, permission or I/O errors.
        // Incoming install validation calls schema/assertNoTriggers directly and
        // must still fail, rather than accepting invalid incoming bytes as cold.
        if (error instanceof UnsupportedCacheSchemaError) { columns = null; return null; }
        const code = error.resultCode & 0xff;
        if (code !== sqlite.capi.SQLITE_NOTADB && code !== sqlite.capi.SQLITE_CORRUPT) throw error;
        db.close(); db = null; columns = null;
        return null;
    }
}
function schema(observation = null) {
    const names = rows("SELECT name FROM sqlite_schema WHERE type='table' AND substr(name,1,7)<>'sqlite_'", [], observation, 1).map(x => x.name);
    const allowed = new Set([...tables, metadataTable, '__EFMigrationsHistory', '__EFMigrationsLock']);
    if (names.some(x => !allowed.has(x)) || !tables.every(x => names.includes(x)) || !names.includes('__EFMigrationsHistory'))
        throw new UnsupportedCacheSchemaError('Unsupported confirmed-cache schema.');
    const result = new Map();
    for (const table of tables) {
        const info = rows('SELECT name, pk FROM pragma_table_info(?) ORDER BY cid', [table], observation, 2);
        const actual = info.map(x => x.name), expected = fields[table];
        if (actual.length !== expected.length || !expected.every(x => actual.includes(x)) ||
            info.filter(x => Number(x.pk) !== 0).length !== 1 || !info.some(x => x.name === 'Id' && Number(x.pk) === 1))
            throw new UnsupportedCacheSchemaError('Incomplete confirmed-cache table schema.');
        const fks = rows('SELECT "table" AS target FROM pragma_foreign_key_list(?)', [table], observation, 1);
        if (fks.some(x => !tables.includes(x.target))) throw new UnsupportedCacheSchemaError('Unexpected confirmed-cache relationship.');
        result.set(table, actual);
    }
    return result;
}
function validateIntegrity() {
    if (scalar('PRAGMA integrity_check') !== 'ok' || rows('PRAGMA foreign_key_check').length)
        throw new Error('The confirmed-cache database is inconsistent.');
}
function assertNoTriggers(observation = null) {
    if (rows("SELECT name FROM sqlite_schema WHERE type='trigger'", [], observation, 1).length)
        throw new UnsupportedCacheSchemaError('Authoring triggers must not run in the browser cache.');
}
function writeState(value) {
    db.exec({ sql: `INSERT INTO ${quote(metadataTable)} (Id,Format,RunId,StartedUtc,ThroughUtc,Schema,StateId)
        VALUES(1,?,?,?,?,?,?) ON CONFLICT(Id) DO UPDATE SET Format=excluded.Format,RunId=excluded.RunId,
        StartedUtc=excluded.StartedUtc,ThroughUtc=excluded.ThroughUtc,Schema=excluded.Schema,StateId=excluded.StateId`,
        bind: [value.checkpoint.format, value.checkpoint.runId, value.checkpoint.startedUtc,
            value.checkpoint.throughUtc, value.checkpoint.schema, value.stateId] });
}
function conflict(expected) {
    const current = state();
    return current?.stateId !== (expected ?? undefined) ? { conflict: true, state: current } : null;
}
async function install(request) {
    const value = checkpoint(request.checkpoint);
    const mismatch = conflict(request.expectedStateId);
    if (mismatch) return mismatch;
    if (request.userReplacement !== true && sameCheckpoint(state()?.checkpoint, value))
        throw new Error('An already compatible checkpoint requires typed catch-up, not binary replacement.');
    if (!(request.bytes instanceof ArrayBuffer) && !(request.bytes instanceof Uint8Array))
        throw new Error('Missing prepared SQLite bytes.');
    // A completed invalidation precedes every database-byte/metadata mutation.
    await writeRelease('');
    if (retired) throw new Error('The cache owner retired before installation.');
    await ensurePool();
    if (retired) throw new Error('The cache owner retired before installation.');
    // Invalidate the old metadata BEFORE an import which may replace only part of
    // the file. A failed import must never expose old metadata over partial content.
    if (db) { db.exec(`DROP TABLE IF EXISTS ${quote(metadataTable)}`); db.close(); }
    db = null; columns = null;
    try {
        pool.importDb(databasePath, request.bytes);
        openDatabase();
        if (scalar(`SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='${metadataTable}'`))
            throw new Error('A server transfer cannot provide browser metadata.');
        columns = schema();
        validateIntegrity();
        db.exec('BEGIN');
        try {
            for (const trigger of rows("SELECT name, tbl_name FROM sqlite_schema WHERE type='trigger'")) {
                if (!trigger.name.startsWith('CF_') || !['Rooms', 'Subrooms', 'RequirementExplanations', 'RoomTransitions', 'SubroomConnections', 'CheckLocations'].includes(trigger.tbl_name))
                    throw new Error('Unexpected trigger in the prepared logic database.');
                db.exec(`DROP TRIGGER ${quote(trigger.name)}`);
            }
            assertNoTriggers();
            // A server transfer may not supply browser metadata, even if malformed.
            db.exec(`DROP TABLE IF EXISTS ${quote(metadataTable)}`);
            db.exec(`CREATE TABLE ${quote(metadataTable)} (Id INTEGER NOT NULL PRIMARY KEY CHECK(Id=1),
                Format INTEGER NOT NULL, RunId TEXT NOT NULL, StartedUtc TEXT NOT NULL,
                ThroughUtc TEXT NOT NULL, Schema TEXT NOT NULL, StateId TEXT NOT NULL)`);
            const next = { stateId: crypto.randomUUID(), checkpoint: value };
            writeState(next);
            db.exec('COMMIT');
        } catch (error) { db.exec('ROLLBACK'); throw error; }
        await writeRelease(applicationVersion);
        return { conflict: false, changed: true, state: state() };
    } catch (error) {
        // Imported files lack browser metadata by construction. Do not delete the
        // SAH pool, restore old bytes or manufacture a checkpoint on failure.
        columns = null;
        if (db) {
            try { db.exec(`DROP TABLE IF EXISTS ${quote(metadataTable)}`); }
            finally { db.close(); db = null; }
        }
        throw error;
    }
}
function typedCell(statement, index) {
    const c = sqlite.capi;
    switch (c.sqlite3_column_type(statement.pointer, index)) {
        case c.SQLITE_NULL: return { type: 'Null', value: null };
        case c.SQLITE_INTEGER: return { type: 'Integer', value: c.sqlite3_column_int64(statement.pointer, index).toString() };
        case c.SQLITE_FLOAT: return { type: 'Real', value: statement.getFloat(index).toString() };
        case c.SQLITE_TEXT: return { type: 'Text', value: statement.getString(index) };
        case c.SQLITE_BLOB: {
            const bytes = statement.getBlob(index);
            let binary = '';
            for (const byte of bytes) binary += String.fromCharCode(byte);
            return { type: 'Blob', value: btoa(binary) };
        }
        default: throw new Error('Unsupported SQLite cell type.');
    }
}
function tableRows(table, where = '', bind = [], names = columns.get(table)) {
    const order = ['Rooms', 'RoomGroups'].includes(table) ? 'ORDER BY SortOrder,FriendlyName,Id'
        : ['Subrooms', 'RoomTransitions', 'SubroomConnections', 'CheckLocations', ...vocabularyTables].includes(table)
            ? 'ORDER BY SortOrder,Id' : '';
    const statement = db.prepare(`SELECT ${names.map(quote).join(',')} FROM ${quote(table)} ${where} ${order}`);
    try {
        if (bind.length > 0) statement.bind(bind);
        const values = [], id = names.indexOf('Id');
        while (statement.step()) {
            const cells = names.map((_, index) => typedCell(statement, index));
            values.push({ key: cells[id].value, values: cells });
        }
        return { table, columns: names, upserts: values, deletions: [] };
    } finally { statement.finalize(); }
}
function sameRow(a, b) {
    if (!a || a.values.length !== b.values.length) return false;
    return a.values.every((cell, index) => {
        const other = b.values[index];
        if (cell.type !== other.type) return false;
        // INTEGER is compared as BigInt, never Number; REAL compares its exact
        // double value rather than .NET/JS exponent spelling. BLOB/text stay exact.
        if (cell.type === 'Integer') return BigInt(cell.value) === BigInt(other.value);
        if (cell.type === 'Real') return Number(cell.value) === Number(other.value);
        return cell.value === other.value;
    });
}
function patch(request) {
    const mismatch = conflict(request.expectedStateId);
    if (mismatch) return mismatch;
    const current = state(), changes = request.changes;
    if (!current || !changes || changes.requiresDatabase || !sameCheckpoint(current.checkpoint, changes.from))
        throw new Error('Cache patch does not cover the current database.');
    const through = checkpoint(changes.through);
    if (through.runId !== current.checkpoint.runId || through.schema !== current.checkpoint.schema ||
        through.startedUtc !== current.checkpoint.startedUtc || !Array.isArray(changes.tables))
        throw new Error('Cache patch requires a prepared database.');
    assertNoTriggers();
    const staged = new Map();
    let changed = !sameCheckpoint(current.checkpoint, through);
    for (const part of changes.tables) {
        const names = columns.get(part.table);
        if (!names || staged.has(part.table) || !Array.isArray(part.columns) ||
            names.length !== part.columns.length || !names.every((x, i) => x === part.columns[i]) ||
            !Array.isArray(part.upserts) || !Array.isArray(part.deletions))
            throw new Error('Incomplete confirmed-cache table patch.');
        const deleted = new Set(), inserted = new Map();
        for (const key of part.deletions) {
            if (typeof key !== 'string' || deleted.has(key)) throw new Error('Invalid cache deletion identity.');
            deleted.add(key);
        }
        for (const row of part.upserts) {
            if (!row || typeof row.key !== 'string' || inserted.has(row.key) || deleted.has(row.key) ||
                !Array.isArray(row.values) || row.values.length !== names.length)
                throw new Error('Invalid complete cache row.');
            const bound = row.values.map(bindCell);
            if (bound[names.indexOf('Id')] !== row.key) throw new Error('Cache row identity does not match its primary key.');
            inserted.set(row.key, { row, bound });
        }
        for (const key of new Set([...deleted, ...inserted.keys()])) {
            const old = tableRows(part.table, 'WHERE Id=?', [key]).upserts[0];
            if (inserted.has(key) ? !sameRow(old, inserted.get(key).row) : !!old) changed = true;
        }
        staged.set(part.table, { names, deleted, inserted });
    }
    if (!changed) return { conflict: false, changed: false, state: current };
    db.exec('PRAGMA foreign_keys=OFF');
    let begun = false;
    try {
        db.exec('BEGIN'); begun = true;
        for (const table of [...tables].reverse()) {
            const part = staged.get(table);
            if (!part) continue;
            for (const key of new Set([...part.deleted, ...part.inserted.keys()]))
                db.exec({ sql: `DELETE FROM ${quote(table)} WHERE Id=?`, bind: [key] });
        }
        for (const table of tables) {
            const part = staged.get(table);
            if (!part) continue;
            const statement = db.prepare(`INSERT INTO ${quote(table)} (${part.names.map(quote).join(',')}) VALUES(${part.names.map(() => '?').join(',')})`);
            try {
                for (const { bound } of part.inserted.values()) { statement.bind(bound); statement.step(); statement.reset(true); }
            } finally { statement.finalize(); }
        }
        if (rows('PRAGMA foreign_key_check').length) throw new Error('Cache patch has inconsistent relationships.');
        const next = { stateId: crypto.randomUUID(), checkpoint: through };
        writeState(next);
        db.exec('COMMIT');
        return { conflict: false, changed: true, state: next };
    } catch (error) {
        if (begun) {
            try { db.exec('ROLLBACK'); }
            catch { db.close(); db = null; columns = null; }
        }
        throw error;
    } finally { db?.exec('PRAGMA foreign_keys=ON'); }
}
function inspect() {
    const current = state();
    if (!current) return { state: null };
    // TEXT casts preserve SQLite's full integer range across structured clone/JSON.
    const integer = sql => BigInt(scalar(sql));
    const logicalBytes = integer('SELECT CAST(page_count AS TEXT) FROM pragma_page_count')
        * integer('SELECT CAST(page_size AS TEXT) FROM pragma_page_size');
    const counts = tables.map(table => ({ table, rows: integer(`SELECT CAST(count(*) AS TEXT) FROM ${quote(table)}`).toString() }));
    return { state: current, logicalBytes: logicalBytes.toString(), tables: counts,
        domainRows: counts.reduce((sum, item) => sum + BigInt(item.rows), 0n).toString() };
}
function query(request, observation = null) {
    const current = state(observation);
    if (!current) throw new Error('A complete confirmed-cache database has not been installed.');
    if (request.expectedStateId && request.expectedStateId !== current.stateId) return { conflict: true, state: current };
    return extractNamedQuery(db, sqlite, request, current, observation, clock);
}
async function operate(request, observation = null) {
    if (retired) throw new Error('The confirmed-cache owner has retired.');
    if (request.applicationVersion !== applicationVersion) throw new ReleaseRejected();
    const release = await readRelease();
    if (retired || request.applicationVersion !== applicationVersion) throw new ReleaseRejected();
    const installation = request.operation === 'prepare-install' || request.operation === 'install';
    if (release && release !== applicationVersion && !(installation && request.currentRuntimeInstallation === true)) throw new ReleaseRejected();
    if (release !== applicationVersion) {
        if (db) { db.close(); db = null; columns = null; }
        if (request.operation === 'state' || request.operation === 'prepare-install') return null;
        if (request.operation === 'inspect') return { state: null };
        if (request.operation !== 'install') throw new Error('The local cache is unready.');
        return install(request);
    }
    await ensurePool();
    if (retired || request.applicationVersion !== applicationVersion) throw new ReleaseRejected();
    // A partial failed import may not even have a readable SQLite header. State
    // reports unusable (cold), and explicit install can replace that disposable
    // file. Other I/O/open failures remain ordinary failures, not silent clearing.
    if (!db) {
        try { openDatabase(observation); }
        catch (error) {
            const code = error.resultCode & 0xff;
            if (code !== sqlite.capi.SQLITE_NOTADB && code !== sqlite.capi.SQLITE_CORRUPT) throw error;
            db?.close(); db = null;
            if (request.operation === 'state' || request.operation === 'prepare-install') return null;
            if (request.operation === 'inspect') return { state: null };
            if (request.operation !== 'install') throw error;
        }
    }
    switch (request.operation) {
        case 'state': return state();
        case 'prepare-install': return state();
        case 'inspect': return inspect();
        case 'install': return install(request);
        case 'patch': return patch(request);
        case 'query': return query(request, observation);
        default: throw new Error('Unknown confirmed-cache operation.');
    }
}
async function retire() {
    if (retired) return;
    retired = true;
    waiting.abort();
    try { await enqueue(() => { if (db) { db.close(); db = null; } pool?.pauseVfs(); }); }
    finally { ownerPort?.close(); releaseOwner?.(); self.close(); }
}
self.onmessage = event => {
    if (event.data?.type === 'retire') { void retire(); return; }
    if (event.data?.type !== 'owner-port' || ownerPort || !event.ports[0]) return;
    if (event.data.applicationVersion !== applicationVersion) { self.postMessage({ applicationVersion, releaseRejected: true }); void retire(); return; }
    ownerPort = event.ports[0];
    ownerPort.onmessage = event => {
        const request = event.data;
        if (retired || request?.applicationVersion !== applicationVersion) {
            ownerPort.postMessage({ id: request?.id, applicationVersion, releaseRejected: true });
            return;
        }
        const observed = namedRead(request), arrived = observed ? clock() : 0;
        const counts = observed ? { statements: 0, rows: 0, cells: 0, sourceValues: 0, namedRows: 0, extractionMs: 0 } : null;
        let started = 0, queue = 0;
        void enqueue(() => {
            if (observed) { started = clock(); queue = started - arrived; }
            return operate(request, counts);
        }).then(result => {
            const summary = observed ? workerSummary({ executionMs: clock() - started, queueMs: queue, ...counts }) : undefined;
            ownerPort.postMessage({ id: request.id, applicationVersion, result, workerSummary: summary });
        }, error => {
            const summary = observed ? workerSummary({ executionMs: clock() - started, queueMs: queue, ...counts }) : undefined;
            ownerPort.postMessage({ id: request.id, applicationVersion, releaseRejected: error instanceof ReleaseRejected, error: 'The local confirmed-cache operation failed. Retry only an explicit read or Refresh; authoring work was not replayed.',
                failure: captureFailure(error, 'Owner', 'request', request, request.id), workerSummary: summary });
        });
    };
    ownerPort.onmessageerror = () => {
        try { self.postMessage({ type: 'owner-failed', applicationVersion, failure: unavailable('Owner', 'messageerror') }); }
        finally { void retire(); }
    };
    ownerPort.start();
};
void navigator.locks.request(ownerLock, { mode: 'exclusive', signal: waiting.signal }, async () => {
    if (retired) return;
    // Invocation-local: retain the first failure across existing cleanup, not a last-error slot.
    let originatingFailure = null;
    try {
      try {
      await enqueue(async () => {
        // Inspect release under the exclusive lock, before any SAH/SQLite opening.
        await readRelease();
      });
      if (retired) { await chain; return; }
      self.postMessage({ type: 'owner-ready', applicationVersion });
      await new Promise(resolve => { releaseOwner = resolve; });
      } catch (error) {
        originatingFailure = captureFailure(error, 'Owner', 'initialize');
        throw error;
      } finally {
        if (db) { db.close(); db = null; }
        pool?.pauseVfs();
      }
    } catch (error) {
        throw new LocalDiagnosticError(originatingFailure ?? captureFailure(error, 'Owner', 'initialize'));
    }
}).catch(error => { if (!retired) { self.postMessage({ type: 'owner-failed', applicationVersion, failure: captureFailure(error, 'Owner', 'initialize') }); self.close(); } });
