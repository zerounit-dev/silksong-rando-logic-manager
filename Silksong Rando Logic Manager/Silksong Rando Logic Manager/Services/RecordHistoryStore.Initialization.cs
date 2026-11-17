using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed partial class RecordHistoryStore
{
    // These DDL statements are recognition-only evidence of the one replaced store.
    // No legacy data is converted or read by normal history consumers.
    private static readonly IReadOnlyDictionary<string, string> LegacyDdl = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["HistoryFormat"] = "CREATE TABLE HistoryFormat(Id INTEGER PRIMARY KEY CHECK(Id=1),Format INTEGER NOT NULL CHECK(Format=1))",
        ["Epochs"] = "CREATE TABLE Epochs(EpochId TEXT COLLATE BINARY PRIMARY KEY,StartedUtc TEXT NOT NULL,SchemaJson TEXT NOT NULL)",
        ["Operations"] = "CREATE TABLE Operations(EpochId TEXT COLLATE BINARY NOT NULL,Ordinal INTEGER NOT NULL CHECK(Ordinal>=0),RecordedUtc TEXT NOT NULL,ActorId TEXT NOT NULL,Kind TEXT NOT NULL CHECK(Kind IN ('Baseline','Change')),PRIMARY KEY(EpochId,Ordinal),FOREIGN KEY(EpochId) REFERENCES Epochs(EpochId))",
        ["Revisions"] = "CREATE TABLE Revisions(EpochId TEXT COLLATE BINARY NOT NULL,Ordinal INTEGER NOT NULL,TableName TEXT COLLATE BINARY NOT NULL,RecordKey TEXT COLLATE BINARY NOT NULL,IsDeleted INTEGER NOT NULL CHECK(IsDeleted IN (0,1)),BeforeJson TEXT,AfterJson TEXT,PRIMARY KEY(EpochId,Ordinal,TableName,RecordKey),FOREIGN KEY(EpochId,Ordinal) REFERENCES Operations(EpochId,Ordinal),CHECK(BeforeJson IS NOT NULL OR AfterJson IS NOT NULL))",
        ["RecordLookup"] = "CREATE INDEX RecordLookup ON Revisions(EpochId,TableName,RecordKey,Ordinal)"
    };

    internal async Task InitializeAsync(LogicCacheSql.SchemaManifest manifest)
    {
        if (Initialized) throw new InvalidOperationException("Record history was already initialized.");
        // Even classification failure is history unavailability, not failed live-save
        // admission. Append reports it through the existing nonfatal warning seam.
        initialized = true;
        try
        {
            // Metadata only: no domain-row read, baseline, seed, or live-file copy.
            InitializeProjection(manifest);
            var legacy = false;
            var allColumn = false;
            if (File.Exists(path))
            {
                // Probe read-only and dispose ALL SQLite handles before filesystem cutover.
                await using var probe = Connection(true);
                await probe.OpenAsync(CancellationToken.None);
                await using var snapshot = probe.BeginTransaction(deferred: true);
                var objects = await ObjectsAsync(probe, snapshot);
                legacy = SameDdl(objects, LegacyDdl);
                if (legacy)
                {
                    // A matching schema is not evidence that the remaining file
                    // pages are sound. Never retire a corrupt recognized store.
                    await using (var integrity = Command(probe, snapshot, "PRAGMA integrity_check"))
                    await using (var checkedRows = await integrity.ExecuteReaderAsync(CancellationToken.None))
                    {
                        if (!await checkedRows.ReadAsync(CancellationToken.None) || checkedRows.GetString(0) != "ok"
                            || await checkedRows.ReadAsync(CancellationToken.None))
                            throw new InvalidDataException("Legacy history integrity validation failed.");
                    }
                    await using var marker = Command(probe, snapshot, "SELECT Id,Format FROM HistoryFormat");
                    await using var reader = await marker.ExecuteReaderAsync(CancellationToken.None);
                    if (!await reader.ReadAsync() || reader.GetInt64(0) != 1 || reader.GetInt64(1) != 1 || await reader.ReadAsync())
                        throw new InvalidDataException("Unrecognized legacy history marker.");
                }
                else
                {
                    allColumn = SameDdl(objects, AllColumnDdl());
                    if (!allColumn) await CheckMirrorsAsync(probe, snapshot);
                }
            }
            if (allColumn) await RefineMirrorsAsync();
            if (legacy) PreserveLegacyGroup();
            if (!File.Exists(path))
            {
                if (Sidecars().Any(File.Exists)) throw new InvalidDataException("Orphan history sidecars require operator reconciliation.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Exclusive creation prevents an existing unknown file from being overwritten.
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                await using var connection = Connection(false);
                await connection.OpenAsync(CancellationToken.None);
                await using var transaction = connection.BeginTransaction();
                foreach (var sql in MirrorDdl().Values) await ExecuteAsync(connection, transaction, sql);
                await CheckMirrorsAsync(connection, transaction);
                await transaction.CommitAsync(CancellationToken.None);
            }
            available = true;
        }
        catch (Exception exception) { LogFailure(exception, "initialization"); }
    }

    private Dictionary<string, string> MirrorDdl() => MirrorDdl(schema ?? throw new InvalidOperationException("Missing retained schema."));

    private Dictionary<string, string> AllColumnDdl() => MirrorDdl(
        (schema ?? throw new InvalidOperationException("Missing retained schema.")).Select(t => projections![t.Name].Source));

    private static Dictionary<string, string> MirrorDdl(IEnumerable<LogicCacheSql.TableSchema> tables)
    {
        var ddl = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var name = Mirror(table.Name);
            ddl.Add(name, $"CREATE TABLE {Quote(name)} ({string.Join(",", table.Columns.Select(c => Quote(c.Name) + " " + c.DeclaredType))},\"ChangeUtc\" TEXT NOT NULL,\"UserId\" TEXT NOT NULL,\"IsDeleted\" INTEGER NOT NULL)");
            ddl.Add(name + "RecordLookup", $"CREATE INDEX {Quote(name + "RecordLookup")} ON {Quote(name)} (\"Id\",\"ChangeUtc\")");
            ddl.Add(name + "TimeLookup", $"CREATE INDEX {Quote(name + "TimeLookup")} ON {Quote(name)} (\"ChangeUtc\")");
        }
        return ddl;
    }

    private async Task CheckMirrorsAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!SameMirrorDdl(await ObjectsAsync(connection, transaction), MirrorDdl()))
            throw new InvalidDataException("Unsupported history schema; no automatic repair or replacement is allowed.");
    }

    private async Task RefineMirrorsAsync()
    {
        await using var connection = Connection(false);
        await connection.OpenAsync(CancellationToken.None);
        // One immediate write transaction: recheck the entire recognized old store
        // under the write lock. DROP preserves rows, rowids, retained cells and indexes;
        // do not rebuild/reinsert, requalify, deduplicate or prune existing versions.
        await using var transaction = connection.BeginTransaction();
        if (!SameDdl(await ObjectsAsync(connection, transaction), AllColumnDdl()))
            throw new InvalidDataException("Prior all-column history changed before refinement.");
        foreach (var table in schema!)
        foreach (var column in RetentionByTable[table.Name].Excluded.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            await ExecuteAsync(connection, transaction, $"ALTER TABLE {Quote(Mirror(table.Name))} DROP COLUMN {Quote(column)}");
        await CheckMirrorsAsync(connection, transaction);
        await transaction.CommitAsync(CancellationToken.None);
    }

    // SQLite DROP COLUMN rewrites only a column's source span and can leave spaces
    // next to commas. Compare the complete generated token stream, not loose SQL
    // equivalence: identifiers, casing, types, column/index order and constraints
    // must all remain exact. Only whitespace BETWEEN tokens may differ; quoted
    // identifiers are indivisible. No comments, alternate quoting or extra tokens.
    private static bool SameMirrorDdl(IReadOnlyDictionary<string, string> observed, IReadOnlyDictionary<string, string> expected) =>
        observed.Count == expected.Count && expected.All(x => observed.TryGetValue(x.Key, out var sql)
            && DdlTokens(sql) is { } actual && DdlTokens(x.Value) is { } wanted && actual.SequenceEqual(wanted, StringComparer.Ordinal));

    private static string[]? DdlTokens(string sql)
    {
        var tokens = new List<string>();
        var offset = 0;
        foreach (Match match in Regex.Matches(sql, "\\G(?:\\s+|\"[^\"]+\"|[A-Za-z_][A-Za-z_0-9]*|[(),])", RegexOptions.CultureInvariant))
        {
            if (match.Index != offset) return null;
            offset += match.Length;
            if (!char.IsWhiteSpace(match.Value[0])) tokens.Add(match.Value);
        }
        return offset == sql.Length ? tokens.ToArray() : null;
    }

    private static async Task<Dictionary<string, string>> ObjectsAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await using var command = Command(connection, transaction, "SELECT name,sql FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*'");
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        var objects = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(CancellationToken.None)) objects.Add(reader.GetString(0), reader.GetString(1));
        return objects;
    }

    private static bool SameDdl(IReadOnlyDictionary<string, string> observed, IReadOnlyDictionary<string, string> expected) =>
        observed.Count == expected.Count && expected.All(x => observed.TryGetValue(x.Key, out var sql) && sql == x.Value);

    private IEnumerable<string> Sidecars() => new[] { path + "-wal", path + "-shm", path + "-journal" };

    private void PreserveLegacyGroup()
    {
        var group = new[] { path }.Concat(Sidecars()).Where(File.Exists).ToArray();
        var destination = Path.Combine(Path.GetDirectoryName(path)!, "legacy-backups", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        var handles = new List<(string Path, FileStream Stream)>();
        try
        {
            // Hold all original group members against writes while copying/verifying.
            foreach (var member in group) handles.Add((member, new FileStream(member, FileMode.Open, FileAccess.Read, FileShare.Read)));
            foreach (var (member, stream) in handles)
            {
                var copy = Path.Combine(destination, Path.GetFileName(member));
                using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.CopyTo(output); output.Flush(flushToDisk: true); }
                stream.Position = 0;
                var originalHash = SHA256.HashData(stream);
                using var preserved = File.OpenRead(copy);
                if (stream.Length != preserved.Length || !originalHash.AsSpan().SequenceEqual(SHA256.HashData(preserved)))
                    throw new IOException("Legacy history group preservation verification failed.");
            }
            if (!new[] { path }.Concat(Sidecars()).Where(File.Exists).SequenceEqual(group))
                throw new IOException("Legacy history group changed during preservation.");
        }
        finally { foreach (var (_, stream) in handles) stream.Dispose(); }
        // The fully verified private copy precedes ANY retirement. Main first means
        // an interrupted sidecar retirement cannot expose a truncated legacy group
        // as a valid database on next startup. Orphan sidecars fail closed above.
        File.Delete(path);
        foreach (var member in group.Skip(1)) File.Delete(member);
    }
}
