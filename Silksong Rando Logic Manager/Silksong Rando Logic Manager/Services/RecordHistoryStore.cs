using System.Globalization;
using Microsoft.Data.Sqlite;
using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed record HistoryWriteOutcome(string? Warning = null)
{
    internal static readonly HistoryWriteOutcome Success = new();
    internal static readonly HistoryWriteOutcome Failed = new("Your changes were saved, but recording change history failed. See server logs for details.");
}

/// <summary>Private full-record mirrors. The caller holds publication admission;
/// neither initialization nor reads invent an initial state for unchanged records.</summary>
internal sealed partial class RecordHistoryStore(ApplicationStoragePaths paths, ILogger<RecordHistoryStore> logger)
{
    private readonly string path = Path.Combine(paths.DataRoot, "history", "logic-history.db");
    private IReadOnlyList<LogicCacheSql.TableSchema>? schema;
    private bool available;
    private bool initialized;
    internal bool Initialized => initialized;

    private void LogFailure(Exception exception, string operation)
    {
        try { logger.LogError(exception, "Record history {Operation} failed; authoritative logic is unchanged.", operation); }
        catch (Exception) { }
    }

    internal async Task<HistoryWriteOutcome> AppendCommittedAsync(HistoryActor actor, IReadOnlyList<LogicChangeCapture.Change> changes)
    {
        if (changes.Count == 0) return HistoryWriteOutcome.Success;
        try
        {
            var qualified = Qualify(changes);
            if (qualified.Count == 0) return HistoryWriteOutcome.Success;
            if (!available) throw new InvalidOperationException("Record history is unavailable after initialization.");
            var utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            await using var connection = Connection(false);
            await connection.OpenAsync(CancellationToken.None);
            await using var transaction = connection.BeginTransaction();
            await CheckMirrorsAsync(connection, transaction);
            foreach (var change in qualified)
            {
                var table = change.Table;
                var row = change.Row;
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"INSERT INTO {Quote(Mirror(table.Name))} ({string.Join(",", table.Columns.Select(x => Quote(x.Name)))},ChangeUtc,UserId,IsDeleted) VALUES ({string.Join(",", table.Columns.Select((_, i) => "$v" + i))},$utc,$user,$deleted)";
                for (var i = 0; i < row.Values.Count; i++)
                {
                    var cell = row.Values[i];
                    var type = cell.Type switch
                    {
                        LogicCacheCellType.Null or LogicCacheCellType.Text => SqliteType.Text,
                        LogicCacheCellType.Integer => SqliteType.Integer,
                        LogicCacheCellType.Real => SqliteType.Real,
                        LogicCacheCellType.Blob => SqliteType.Blob,
                        _ => throw new InvalidDataException("Invalid native history cell type.")
                    };
                    command.Parameters.Add("$v" + i, type).Value = Native(cell);
                }
                command.Parameters.AddWithValue("$utc", utc);
                command.Parameters.AddWithValue("$user", actor.AccountId.ToString("D"));
                command.Parameters.AddWithValue("$deleted", change.IsDeleted ? 1 : 0);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            await transaction.CommitAsync(CancellationToken.None);
            return HistoryWriteOutcome.Success;
        }
        catch (Exception exception) { LogFailure(exception, "append"); return HistoryWriteOutcome.Failed; }
    }

    private static object Native(LogicCacheCell cell) => cell.Type switch
    {
        LogicCacheCellType.Null when cell.Value is null => DBNull.Value,
        LogicCacheCellType.Text when cell.Value is not null => cell.Value,
        LogicCacheCellType.Integer when long.TryParse(cell.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            && Number(n) == cell.Value => n,
        LogicCacheCellType.Real when double.TryParse(cell.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
            && r.ToString("R", CultureInfo.InvariantCulture) == cell.Value => r,
        LogicCacheCellType.Blob when cell.Value is not null => Convert.FromBase64String(cell.Value),
        _ => throw new InvalidDataException("Invalid native history cell value.")
    };

    private static string Mirror(string name) => name + "History";
    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private SqliteConnection Connection(bool readOnly) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
        Pooling = false, DefaultTimeout = 30
    }.ToString());

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        await using var command = Command(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static DateTime Utc(string text)
    {
        if (!DateTime.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var utc)
            || utc.Kind != DateTimeKind.Utc) throw new InvalidDataException("Invalid history UTC metadata.");
        return utc;
    }
}
