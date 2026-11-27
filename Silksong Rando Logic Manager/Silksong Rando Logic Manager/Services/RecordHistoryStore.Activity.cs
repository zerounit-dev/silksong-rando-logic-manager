using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed partial class RecordHistoryStore
{
    internal sealed record ActivityEdit(DateTime RecordedUtc, Guid ActorId, Guid RoomId, string RoomName, string TableName, long RowId);
    internal sealed record ActivityRead(bool Available, DateTime? LatestUtc, IReadOnlyList<ActivityEdit> Edits)
    {
        internal Guid? LatestActorId { get; init; }
    }
    private static readonly string[] ActivityTables = ["Rooms", "Subrooms", "RoomTransitions", "SubroomConnections", "CheckLocations"];

    // Only scalar owner metadata is unioned. No full-row decoding or operation
    // grouping, including when unrelated saves happen to share the same timestamp.
    private static string ActivityRows => string.Join(" UNION ALL ", ActivityTables.Select(table =>
        $"SELECT ChangeUtc,UserId,{(table == "Rooms" ? "Id" : "RoomId")} AS RoomId,'{table}' AS TableName,rowid AS VersionRowId FROM {Quote(Mirror(table))} WHERE ChangeUtc >= $from AND ChangeUtc <= $through"));
    private static string LatestCandidates => $"WITH edits AS ({ActivityRows}),latest AS (SELECT *,DENSE_RANK() OVER (PARTITION BY UserId ORDER BY ChangeUtc DESC) AS latest FROM edits)";

    internal async Task<ActivityRead> ReadActivityAsync(DateTime? now, CancellationToken token,
        Func<IReadOnlyCollection<Guid>, Task<IReadOnlyDictionary<Guid, string>>>? currentNames = null)
    {
        try
        {
            if (!available) throw new InvalidOperationException("Record history is unavailable after initialization.");
            await using var connection = Connection(true);
            await connection.OpenAsync(token);
            await using var transaction = connection.BeginTransaction(deferred: true);
            await CheckMirrorsAsync(connection, transaction);
            if (now is null)
            {
                var times = string.Join(" UNION ALL ", ActivityTables.Select(table =>
                    $"SELECT MAX(ChangeUtc) AS ChangeUtc FROM {Quote(Mirror(table))}"));
                // Resolve exact-time ties by the binary-smallest stored actor ID,
                // not by an inferred edit order or room identity. Only scalar
                // columns are read, using the existing ChangeUtc indexes.
                var actors = string.Join(" UNION ALL ", ActivityTables.Select(table =>
                    $"SELECT MIN(UserId COLLATE BINARY) AS UserId FROM {Quote(Mirror(table))} WHERE ChangeUtc = (SELECT AtUtc FROM latestTime)"));
                await using var latest = Command(connection, transaction, $"""
                    WITH latestTime(AtUtc) AS (SELECT MAX(ChangeUtc) FROM ({times}))
                    SELECT AtUtc, (SELECT MIN(UserId COLLATE BINARY) FROM ({actors}))
                    FROM latestTime WHERE AtUtc IS NOT NULL
                    """);
                await using var reader = await latest.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token)) return new(true, null, []);
                return new(true, Utc(reader.GetString(0)), []) { LatestActorId = Guid.Parse(reader.GetString(1)) };
            }

            (string Name, object? Value)[] window = [("$from", now.Value.AddHours(-24).ToString("O", CultureInfo.InvariantCulture)),
                ("$through", now.Value.ToString("O", CultureInfo.InvariantCulture))];
            var owners = new List<Guid>();
            await using (var ownerQuery = Command(connection, transaction, LatestCandidates + " SELECT DISTINCT RoomId FROM latest WHERE latest=1", window))
            await using (var reader = await ownerQuery.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) owners.Add(Guid.Parse(reader.GetString(0)));
            if (owners.Count == 0) return new(true, null, []);
            var names = await (currentNames ?? throw new InvalidOperationException("Current room-name projection is required."))(owners);
            var parameters = window.ToList();
            var values = new List<string>();
            foreach (var id in owners)
            {
                var i = values.Count;
                parameters.Add(("$id" + i, id.ToString("D")));
                parameters.Add(("$name" + i, names.TryGetValue(id, out var name) ? name : null));
                parameters.Add(("$present" + i, names.ContainsKey(id) ? 1 : 0));
                values.Add($"($id{i},$name{i},$present{i})");
            }
            await using var command = Command(connection, transaction, LatestCandidates + $"""
                ,currentNames(Id,Name,Present) AS (VALUES {string.Join(",", values)}),
                named AS (SELECT l.*,CASE WHEN c.Present=1 THEN c.Name ELSE COALESCE(
                    (SELECT h.FriendlyName FROM RoomsHistory h WHERE lower(h.Id)=lower(l.RoomId)
                        AND length(trim(h.FriendlyName))>0 ORDER BY h.ChangeUtc DESC,h.rowid DESC LIMIT 1),
                    'Deleted/unknown room (' || lower(l.RoomId) || ')') END AS RoomName
                    FROM latest l JOIN currentNames c ON lower(c.Id)=lower(l.RoomId) WHERE l.latest=1),
                selected AS (SELECT *,ROW_NUMBER() OVER (PARTITION BY UserId
                    ORDER BY RoomName COLLATE BINARY,RoomId COLLATE BINARY,TableName COLLATE BINARY,VersionRowId) AS choice FROM named)
                SELECT ChangeUtc,UserId,RoomId,RoomName,TableName,VersionRowId FROM selected WHERE choice=1
                    ORDER BY ChangeUtc DESC,UserId COLLATE BINARY
                """, parameters.ToArray());
            var edits = new List<ActivityEdit>();
            await using var selected = await command.ExecuteReaderAsync(token);
            while (await selected.ReadAsync(token)) edits.Add(new(Utc(selected.GetString(0)), Guid.Parse(selected.GetString(1)),
                Guid.Parse(selected.GetString(2)), selected.GetString(3), selected.GetString(4), selected.GetInt64(5)));
            return new(true, null, edits);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { LogFailure(exception, "publisher activity read"); return new(false, null, []); }
    }
}
