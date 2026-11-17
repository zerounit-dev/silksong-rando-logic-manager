using System.Globalization;
using Microsoft.Data.Sqlite;
using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed record HistorySelector(string TableName, string RecordKey, string RowId)
{
    internal bool SameRecordAs(HistorySelector other) => TableName == other.TableName && RecordKey == other.RecordKey;
}
internal sealed record HistoryQuery(string Capability, string? TableName = null, string? RecordKey = null,
    long? After = null, int Limit = 100, HistorySelector? First = null, HistorySelector? Second = null, DateTime? AtUtc = null);

internal sealed partial class RecordHistoryStore
{
    internal sealed record StoredVersion(HistorySelector Selector, DateTime ChangeUtc, Guid UserId, bool IsDeleted, LogicCacheRow Row);
    internal sealed record RecordedState(string Status, IReadOnlyList<string> Columns, StoredVersion? Version);
    internal sealed record ColumnDifference(string Column, LogicCacheCell First, LogicCacheCell Second);

    // Caller holds publication admission and has revalidated Normal and workspace
    // after waiting. Both selected versions are read in this single snapshot.
    internal async Task<object> ReadAsync(HistoryQuery query, CancellationToken token)
    {
        if (query.Capability == "diff" && (query.First is null || query.Second is null || !query.First.SameRecordAs(query.Second)))
            throw new ArgumentException("History diff requires two versions of the same original record.");
        var name = query.First?.TableName ?? query.TableName;
        var table = schema?.SingleOrDefault(x => x.Name == name);
        if (!RetentionByTable.ContainsKey(name ?? "")) return new { Status = "UnknownTable" };
        try
        {
            if (!available || table is null) throw new InvalidOperationException("Record history is unavailable after initialization.");
            await using var connection = Connection(true);
            await connection.OpenAsync(token);
            await using var transaction = connection.BeginTransaction(deferred: true);
            await CheckMirrorsAsync(connection, transaction);
            var columns = table.Columns.Select(x => x.Name).ToArray();
            if (query.Capability == "diff")
            {
                var first = await SelectedVersionAsync(connection, transaction, table, query.First!, token);
                var second = await SelectedVersionAsync(connection, transaction, table, query.Second!, token);
                var differences = new List<ColumnDifference>();
                if (first is not null && second is not null)
                    for (var i = 0; i < columns.Length; i++)
                        if (first.Row.Values[i] != second.Row.Values[i]) differences.Add(new(columns[i], first.Row.Values[i], second.Row.Values[i]));
                return new { Status = first is null || second is null ? "NoRecordedState" : "Recorded",
                    Columns = columns, First = first, Second = second, Differences = differences };
            }
            if (query.Capability == "time")
            {
                var rows = await VersionsAsync(connection, transaction, table, query.RecordKey!,
                    " AND ChangeUtc <= $utc ORDER BY ChangeUtc DESC,rowid DESC LIMIT 1", token,
                    ("$utc", query.AtUtc!.Value.ToString("O", CultureInfo.InvariantCulture)));
                var version = rows.SingleOrDefault();
                return new RecordedState(version is null ? "NoRecordedState" : version.IsDeleted ? "Deleted" : "Recorded", columns, version);
            }
            var versions = await VersionsAsync(connection, transaction, table, query.RecordKey!,
                " AND rowid > $after ORDER BY rowid LIMIT $limit", token, ("$after", query.After ?? 0), ("$limit", query.Limit + 1));
            var more = versions.Count > query.Limit;
            var page = versions.Take(query.Limit).ToArray();
            return new { Status = "Available", Columns = columns, Versions = page, HasMore = more,
                EndOfSnapshot = !more, NextAfterRowId = page.LastOrDefault()?.Selector.RowId ?? (query.After is { } a ? Number(a) : null) };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { LogFailure(exception, "read"); return new { Status = "HistoryUnavailable" }; }
    }

    private static async Task<StoredVersion?> SelectedVersionAsync(SqliteConnection connection, SqliteTransaction transaction,
        LogicCacheSql.TableSchema table, HistorySelector selector, CancellationToken token) =>
        (await VersionsAsync(connection, transaction, table, selector.RecordKey, " AND rowid=$rowid LIMIT 1", token,
            ("$rowid", long.Parse(selector.RowId, CultureInfo.InvariantCulture)))).SingleOrDefault();

    private static async Task<List<StoredVersion>> VersionsAsync(SqliteConnection connection, SqliteTransaction transaction,
        LogicCacheSql.TableSchema table, string key, string suffix, CancellationToken token, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction,
            $"SELECT rowid,ChangeUtc,UserId,IsDeleted,{string.Join(",", table.Columns.Select(x => Quote(x.Name)))} FROM {Quote(Mirror(table.Name))} WHERE Id=$key" + suffix,
            new[] { ("$key", (object?)key) }.Concat(parameters).ToArray());
        var rows = new List<StoredVersion>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var cells = table.Columns.Select((_, i) => LogicCacheSql.Cell(reader, i + 4)).ToArray();
            var id = table.Columns.Single(x => x.Name == "Id").Ordinal;
            if (cells[id] != new LogicCacheCell(LogicCacheCellType.Text, key)) throw new InvalidDataException("Invalid history record identity.");
            var deleted = reader.GetInt64(3) switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid history deletion marker.") };
            rows.Add(new(new(table.Name, key, Number(reader.GetInt64(0))), Utc(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                deleted, new(key, Array.AsReadOnly(cells))));
        }
        return rows;
    }
}
