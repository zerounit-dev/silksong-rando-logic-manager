using Microsoft.Data.Sqlite;
using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Transaction-private before images of touched identities, including writes
/// from raw SQL and server triggers. Nothing in this journal is durable history.
/// The enclosing write scope owns admission, the transaction and completion finalization.</summary>
internal sealed class LogicChangeCapture : IAsyncDisposable
{
    internal sealed record Change(LogicCacheTable Table, IReadOnlyList<string> Columns,
        string Key, LogicCacheRow? Before, LogicCacheRow? After);

    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private readonly IReadOnlyDictionary<LogicCacheTable, LogicCacheSql.TableSchema> schema;
    private readonly List<(string Kind, string Name)> installed = [];
    private bool frozen;
    private bool disposed;

    private LogicChangeCapture(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyDictionary<LogicCacheTable, LogicCacheSql.TableSchema> schema)
    {
        this.connection = connection;
        this.transaction = transaction;
        this.schema = schema;
    }

    internal static LogicChangeCapture Create(SqliteConnection connection,
        SqliteTransaction transaction, LogicCacheSql.SchemaManifest manifest)
    {
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("Logic change capture requires its owning active transaction.");
        var tables = LogicCacheSql.DomainTables.ToDictionary(table => table,
            table => manifest.Tables.Single(x => x.Name == LogicCacheSql.Name(table)));
        // Validate the complete allowlist before installing any trigger. Journal
        // columns intentionally have NO affinity: every OLD SQLite storage type survives.
        foreach (var table in tables.Values)
        {
            if (table.Columns.Count(x => x.PrimaryKeyOrdinal != 0) != 1
                || !table.Columns.Any(x => x.Name == "Id" && x.PrimaryKeyOrdinal == 1
                    && x.DeclaredType == "TEXT")
                || table.Columns.Any(x => x.Name.StartsWith("__lc_", StringComparison.Ordinal)))
                throw new InvalidOperationException("Unexpected logic change-capture schema.");
        }

        return new LogicChangeCapture(connection, transaction, tables);
    }

    internal async Task InstallAsync(CancellationToken token)
    {
        // The scope already owns this instance, including a partially installed
        // journal. It must dispose the transaction BEFORE final TEMP cleanup.
        foreach (var table in LogicCacheSql.DomainTables)
            await InstallAsync(table, token);
    }

    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string Journal(LogicCacheTable table) => "LC_Before_" + LogicCacheSql.Name(table);
    private static string Trigger(LogicCacheTable table, string operation) => "LC_" + LogicCacheSql.Name(table) + "_" + operation;

    private async Task ExecuteAsync(string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.Connection is not null ? transaction : null;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task InstallAsync(LogicCacheTable table, CancellationToken token)
    {
        var name = LogicCacheSql.Name(table);
        var columns = schema[table].Columns.Select(x => Q(x.Name)).ToArray();
        var journal = Journal(table);
        // A fixed connection-local name deliberately rejects overlapping capture
        // owners. No IF NOT EXISTS that could silently reuse another scope's journal.
        await ExecuteAsync($"CREATE TEMP TABLE {Q(journal)} (\"__lc_key\" TEXT COLLATE BINARY PRIMARY KEY, \"__lc_existed\" INTEGER NOT NULL, {string.Join(", ", columns)}) WITHOUT ROWID", token);
        installed.Add(("TABLE", journal));
        var original = $"INSERT OR IGNORE INTO {Q(journal)} VALUES (OLD.\"Id\", 1, {string.Join(", ", columns.Select(x => "OLD." + x))});";
        var absent = $"INSERT OR IGNORE INTO {Q(journal)} (\"__lc_key\", \"__lc_existed\") VALUES (NEW.\"Id\", 0);";
        var existing = $"INSERT OR IGNORE INTO {Q(journal)} SELECT \"Id\", 1, {string.Join(", ", columns)} FROM main.{Q(name)} WHERE \"Id\" COLLATE BINARY = NEW.\"Id\" COLLATE BINARY;";
        await InstallTriggerAsync(table, "Insert", $"BEFORE INSERT ON main.{Q(name)} BEGIN {existing} {absent} END", token);
        await InstallTriggerAsync(table, "Update", $"BEFORE UPDATE ON main.{Q(name)} BEGIN {original} INSERT OR IGNORE INTO {Q(journal)} (\"__lc_key\", \"__lc_existed\") SELECT NEW.\"Id\", 0 WHERE OLD.\"Id\" COLLATE BINARY IS NOT NEW.\"Id\" COLLATE BINARY; END", token);
        await InstallTriggerAsync(table, "Delete", $"BEFORE DELETE ON main.{Q(name)} BEGIN {original} END", token);
    }

    private async Task InstallTriggerAsync(LogicCacheTable table, string operation, string body, CancellationToken token)
    {
        var name = Trigger(table, operation);
        await ExecuteAsync($"CREATE TEMP TRIGGER {Q(name)} {body}", token);
        installed.Add(("TRIGGER", name));
    }

    /// <summary>Called AFTER completion writes but BEFORE any final after-observation.
    /// SQLite itself rejects subsequent domain writes, including raw SQL/trigger work.
    /// The write scope must not commit before this succeeds.</summary>
    internal async Task FreezeAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frozen) return;
        foreach (var table in LogicCacheSql.DomainTables)
        foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            await InstallTriggerAsync(table, "Final_" + operation,
                $"BEFORE {operation} ON main.{Q(LogicCacheSql.Name(table))} BEGIN SELECT RAISE(ABORT, 'Logic source write after completion finalization'); END", token);
        frozen = true;
    }

    /// <summary>Consolidate first-before versus actual current row. Generated flags,
    /// audits and completion participate just like authored fields. A transient row
    /// disappearing is suppressed, NOT any surviving Room flag it changed.</summary>
    internal async Task<IReadOnlyList<Change>> ReadAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (transaction.Connection is null)
            throw new InvalidOperationException("Change capture must be read inside its original transaction.");
        var changes = new List<Change>();
        foreach (var table in LogicCacheSql.DomainTables)
        {
            var columns = schema[table].Columns.Select(x => x.Name).ToArray();
            var difference = string.Join(" OR ", columns.Select(x =>
                $"typeof(b.{Q(x)}) != typeof(n.{Q(x)}) OR b.{Q(x)} COLLATE BINARY IS NOT n.{Q(x)} COLLATE BINARY"));
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT b.\"__lc_key\", b.\"__lc_existed\", n.\"Id\" IS NOT NULL, {string.Join(", ", columns.Select(x => "b." + Q(x)))}, {string.Join(", ", columns.Select(x => "n." + Q(x)))} FROM temp.{Q(Journal(table))} b LEFT JOIN main.{Q(LogicCacheSql.Name(table))} n ON n.\"Id\" COLLATE BINARY = b.\"__lc_key\" COLLATE BINARY WHERE (b.\"__lc_existed\" = 0 AND n.\"Id\" IS NOT NULL) OR (b.\"__lc_existed\" = 1 AND (n.\"Id\" IS NULL OR {difference})) ORDER BY b.\"__lc_key\" COLLATE BINARY";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var key = reader.GetString(0);
                LogicCacheRow Row(int offset)
                {
                    var values = new LogicCacheCell[columns.Length];
                    for (var i = 0; i < values.Length; i++) values[i] = LogicCacheSql.Cell(reader, offset + i);
                    return new(key, Array.AsReadOnly(values));
                }
                changes.Add(new(table, Array.AsReadOnly(columns), key,
                    reader.GetBoolean(1) ? Row(3) : null,
                    reader.GetBoolean(2) ? Row(3 + columns.Length) : null));
            }
        }
        return changes.AsReadOnly();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        // Drop triggers before journal tables; both are TEMP and roll back with the
        // caller's transaction. No journal/body survives connection disposal.
        foreach (var (kind, name) in installed.AsEnumerable().Reverse())
            await ExecuteAsync($"DROP {kind} IF EXISTS temp.{Q(name)}", CancellationToken.None);
        installed.Clear();
    }
}
