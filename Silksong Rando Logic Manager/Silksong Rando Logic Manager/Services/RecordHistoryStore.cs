using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Silksong_Rando_Logic_Manager.Data;
using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Private, independent format-1 record history. Publication admission owns
/// all calls and the current-run watermark; this is not cache history or a logic DB.</summary>
internal sealed partial class RecordHistoryStore(ApplicationStoragePaths paths, ILogger<RecordHistoryStore> logger)
{
    private readonly string path = Path.Combine(paths.DataRoot, "history", "logic-history.db");
    private Guid epoch;
    private DateTime startedUtc;
    private long attempted;
    private IReadOnlyList<LogicCacheSql.TableSchema>? schema;
    private bool pendingNewFile;
    internal bool Initialized => schema is not null;
    private static readonly JsonSerializerOptions StoredJson = new() { RespectRequiredConstructorParameters = true };

    private void LogFailure(Exception exception, string operation)
    {
        // A history sink (including its logger) must not replace a committed outcome.
        try { logger.LogError(exception, "Record history {Operation} failed; authoritative logic is unchanged.", operation); }
        catch (Exception) { }
    }

    internal async Task InitializeAsync(LogicDbContext db, LogicCacheSql.SchemaManifest manifest)
    {
        if (Initialized) throw new InvalidOperationException("Record history was already initialized.");
        epoch = Guid.NewGuid();
        startedUtc = DateTime.UtcNow;
        attempted = 0;
        schema = Array.AsReadOnly(LogicCacheSql.DomainTables.Select(table =>
            manifest.Tables.Single(x => x.Name == LogicCacheSql.Name(table))).ToArray());
        try
        {
            // ONE observed post-backfill snapshot, not seventeen independently sampled reads.
            await using var snapshot = await db.Database.BeginTransactionAsync(CancellationToken.None);
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            var transaction = (SqliteTransaction)snapshot.GetDbTransaction();
            var rows = new List<Revision>();
            foreach (var table in schema)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"SELECT {string.Join(",", table.Columns.Select(x => Quote(x.Name)))} FROM main.{Quote(table.Name)} ORDER BY \"Id\" COLLATE BINARY";
                await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
                var id = table.Columns.Single(x => x.Name == "Id" && x.PrimaryKeyOrdinal == 1).Ordinal;
                while (await reader.ReadAsync(CancellationToken.None))
                {
                    var cells = table.Columns.Select((_, index) => LogicCacheSql.Cell(reader, index)).ToArray();
                    var key = reader.GetString(id);
                    rows.Add(new(table.Name, key, false, null, new(key, Array.AsReadOnly(cells))));
                }
            }
            await AppendAsync(0, startedUtc, Guid.Empty, "Baseline", rows);
        }
        catch (Exception exception) { LogFailure(exception, "baseline"); }
    }

    internal async Task AppendCommittedAsync(HistoryActor actor, IReadOnlyList<LogicChangeCapture.Change> changes)
    {
        if (changes.Count == 0) return;
        // Allocate BEFORE conversion, file access or format checks. Failed attempts
        // leave visible holes even when no epoch/header has ever reached disk.
        try
        {
            attempted = checked(attempted + 1);
            var recordedUtc = DateTime.UtcNow;
            var revisions = changes.Select(x => new Revision(LogicCacheSql.Name(x.Table), x.Key,
                x.After is null, x.Before, x.After)).ToArray();
            await AppendAsync(attempted, recordedUtc, actor.AccountId, "Change", revisions);
        }
        catch (Exception exception) { LogFailure(exception, "append"); }
    }

    private sealed record Revision(string TableName, string RecordKey, bool IsDeleted, LogicCacheRow? Before, LogicCacheRow? After);
    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private SqliteConnection Connection(bool write) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = write ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
        Pooling = false, ForeignKeys = true, DefaultTimeout = 30
    }.ToString());

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task AppendAsync(long ordinal, DateTime utc, Guid actor, string kind, IReadOnlyList<Revision> rows)
    {
        var tables = schema ?? throw new InvalidOperationException("Record history is not initialized.");
        foreach (var row in rows) ValidateRevision(tables, row);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) pendingNewFile = true;
        await using var connection = Connection(true);
        await connection.OpenAsync(CancellationToken.None);
        await using var transaction = connection.BeginTransaction();
        await EnsureFormatAsync(connection, transaction, pendingNewFile);
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO Epochs(EpochId,StartedUtc,SchemaJson) VALUES($epoch,$utc,$schema)",
            ("$epoch", epoch.ToString("D")), ("$utc", startedUtc.ToString("O", CultureInfo.InvariantCulture)),
            ("$schema", JsonSerializer.Serialize(tables, StoredJson)));
        await ExecuteAsync(connection, transaction,
            "INSERT INTO Operations(EpochId,Ordinal,RecordedUtc,ActorId,Kind) VALUES($epoch,$ordinal,$utc,$actor,$kind)",
            ("$epoch", epoch.ToString("D")), ("$ordinal", ordinal), ("$utc", utc.ToString("O", CultureInfo.InvariantCulture)),
            ("$actor", actor.ToString("D")), ("$kind", kind));
        foreach (var row in rows)
            await ExecuteAsync(connection, transaction,
                "INSERT INTO Revisions(EpochId,Ordinal,TableName,RecordKey,IsDeleted,BeforeJson,AfterJson) VALUES($epoch,$ordinal,$table,$key,$deleted,$before,$after)",
                ("$epoch", epoch.ToString("D")), ("$ordinal", ordinal), ("$table", row.TableName), ("$key", row.RecordKey),
                ("$deleted", row.IsDeleted ? 1 : 0),
                ("$before", row.Before is null ? null : JsonSerializer.Serialize(row.Before, StoredJson)),
                ("$after", row.After is null ? null : JsonSerializer.Serialize(row.After, StoredJson)));
        await transaction.CommitAsync(CancellationToken.None);
        pendingNewFile = false;
    }

    private static async Task EnsureFormatAsync(SqliteConnection connection, SqliteTransaction transaction, bool mayCreate)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        var count = (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
        if (count == 0 && !mayCreate) throw new InvalidDataException("An existing empty history file is not a supported history format.");
        if (count == 0)
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE HistoryFormat(Id INTEGER PRIMARY KEY CHECK(Id=1),Format INTEGER NOT NULL CHECK(Format=1));
                INSERT INTO HistoryFormat VALUES(1,1);
                CREATE TABLE Epochs(EpochId TEXT COLLATE BINARY PRIMARY KEY,StartedUtc TEXT NOT NULL,SchemaJson TEXT NOT NULL);
                CREATE TABLE Operations(EpochId TEXT COLLATE BINARY NOT NULL,Ordinal INTEGER NOT NULL CHECK(Ordinal>=0),RecordedUtc TEXT NOT NULL,ActorId TEXT NOT NULL,Kind TEXT NOT NULL CHECK(Kind IN ('Baseline','Change')),PRIMARY KEY(EpochId,Ordinal),FOREIGN KEY(EpochId) REFERENCES Epochs(EpochId));
                CREATE TABLE Revisions(EpochId TEXT COLLATE BINARY NOT NULL,Ordinal INTEGER NOT NULL,TableName TEXT COLLATE BINARY NOT NULL,RecordKey TEXT COLLATE BINARY NOT NULL,IsDeleted INTEGER NOT NULL CHECK(IsDeleted IN (0,1)),BeforeJson TEXT,AfterJson TEXT,PRIMARY KEY(EpochId,Ordinal,TableName,RecordKey),FOREIGN KEY(EpochId,Ordinal) REFERENCES Operations(EpochId,Ordinal),CHECK(BeforeJson IS NOT NULL OR AfterJson IS NOT NULL));
                CREATE INDEX RecordLookup ON Revisions(EpochId,TableName,RecordKey,Ordinal);
                """);
        await CheckFormatAsync(connection, transaction);
    }

    private static async Task CheckFormatAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        // A format marker alone cannot certify a usable file (for example a missing
        // Revisions table must not produce an apparently available boundaries page).
        var expected = new Dictionary<string, (string Name, string Type, int Key)[]>(StringComparer.Ordinal)
        {
            ["HistoryFormat"] = [("Id", "INTEGER", 1), ("Format", "INTEGER", 0)],
            ["Epochs"] = [("EpochId", "TEXT", 1), ("StartedUtc", "TEXT", 0), ("SchemaJson", "TEXT", 0)],
            ["Operations"] = [("EpochId", "TEXT", 1), ("Ordinal", "INTEGER", 2), ("RecordedUtc", "TEXT", 0), ("ActorId", "TEXT", 0), ("Kind", "TEXT", 0)],
            ["Revisions"] = [("EpochId", "TEXT", 1), ("Ordinal", "INTEGER", 2), ("TableName", "TEXT", 3), ("RecordKey", "TEXT", 4), ("IsDeleted", "INTEGER", 0), ("BeforeJson", "TEXT", 0), ("AfterJson", "TEXT", 0)]
        };
        await using (var inventory = connection.CreateCommand())
        {
            inventory.Transaction = transaction;
            inventory.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            await using var tables = await inventory.ExecuteReaderAsync(CancellationToken.None);
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (await tables.ReadAsync(CancellationToken.None)) names.Add(tables.GetString(0));
            if (!names.SetEquals(expected.Keys)) throw new InvalidDataException("Invalid history table inventory.");
        }
        foreach (var (table, columns) in expected)
        {
            await using var shape = connection.CreateCommand();
            shape.Transaction = transaction;
            shape.CommandText = "SELECT name,type,pk FROM pragma_table_info($table) ORDER BY cid";
            shape.Parameters.AddWithValue("$table", table);
            var observed = new List<(string Name, string Type, int Key)>();
            await using var fields = await shape.ExecuteReaderAsync(CancellationToken.None);
            while (await fields.ReadAsync(CancellationToken.None)) observed.Add((fields.GetString(0), fields.GetString(1), fields.GetInt32(2)));
            if (!observed.SequenceEqual(columns)) throw new InvalidDataException("Invalid history table shape.");
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id,Format FROM HistoryFormat";
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        if (!await reader.ReadAsync(CancellationToken.None) || reader.GetInt64(0) != 1 || reader.GetInt64(1) != 1
            || await reader.ReadAsync(CancellationToken.None)) throw new InvalidDataException("Unsupported or invalid history format.");
    }

    private static void ValidateRevision(IReadOnlyList<LogicCacheSql.TableSchema> tables, Revision revision)
    {
        var table = tables.Single(x => x.Name == revision.TableName);
        if (revision.Before is null && revision.After is null || revision.IsDeleted != (revision.After is null))
            throw new InvalidDataException("Invalid history revision presence.");
        foreach (var row in new[] { revision.Before, revision.After })
        {
            if (row is null) continue;
            var id = table.Columns.Single(x => x.Name == "Id" && x.PrimaryKeyOrdinal == 1).Ordinal;
            if (row.Key != revision.RecordKey || row.Values.Count != table.Columns.Count
                || row.Values[id].Type != LogicCacheCellType.Text || row.Values[id].Value != revision.RecordKey)
                throw new InvalidDataException("Invalid history row shape or identity.");
            foreach (var cell in row.Values)
            {
                var valid = cell.Type switch
                {
                    LogicCacheCellType.Null => cell.Value is null,
                    LogicCacheCellType.Text => cell.Value is not null,
                    LogicCacheCellType.Integer => long.TryParse(cell.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && Number(n) == cell.Value,
                    LogicCacheCellType.Real => double.TryParse(cell.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r.ToString("R", CultureInfo.InvariantCulture) == cell.Value,
                    LogicCacheCellType.Blob => cell.Value is not null && Convert.ToBase64String(Convert.FromBase64String(cell.Value)) == cell.Value,
                    _ => false
                };
                if (!valid) throw new InvalidDataException("Invalid history typed cell.");
            }
        }
    }
}
