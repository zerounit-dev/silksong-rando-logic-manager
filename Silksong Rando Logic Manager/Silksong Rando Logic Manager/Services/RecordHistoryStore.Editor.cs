using System.Globalization;
using Silksong_Rando_Logic_Manager.Authentication;
using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed partial class RecordHistoryStore
{
    private const int EditorPageSize = 100;
    private static EditorHistoryPage EditorUnavailable => new(EditorHistoryStatus.HistoryUnavailable, [], null);

    internal async Task<EditorHistoryPage> ReadEditorAsync(EditorHistoryRequest request,
        Func<Guid[], Task<IReadOnlyDictionary<Guid, string>>> readNames,
        Func<Guid[], Task<IReadOnlyDictionary<Guid, string>>> readGroups, CancellationToken token)
    {
        var name = request.Family switch
        {
            EditorHistoryFamily.Subroom => "Subrooms",
            EditorHistoryFamily.Explanation => nameof(LogicCacheTable.RequirementExplanations),
            EditorHistoryFamily.Transition => "RoomTransitions",
            EditorHistoryFamily.Connection => "SubroomConnections",
            EditorHistoryFamily.Check => "CheckLocations",
            EditorHistoryFamily.Room => "Rooms",
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
        // Microsoft.Data.Sqlite binds the live domain's Guid primary keys as uppercase D text.
        var key = request.RecordId.ToString("D").ToUpperInvariant();
        var positions = request.Family switch
        {
            EditorHistoryFamily.Subroom => new[] { "SceneUnitX", "SceneUnitY", "SceneUnitWidth", "SceneUnitHeight" },
            EditorHistoryFamily.Connection => new[] { "SceneUnitX", "SceneUnitY" },
            EditorHistoryFamily.Room or EditorHistoryFamily.Explanation => [],
            _ => new[] { "AnnotationSceneUnitX", "AnnotationSceneUnitY" }
        };
        string[] gameDetails = request.Family is EditorHistoryFamily.Transition or EditorHistoryFamily.Check
            ? ["InGameId", "InGamePositionX", "InGamePositionY", "InGamePositionZ", "LocalPositionX", "LocalPositionY", "LocalPositionZ"] : [];
        try
        {
            if (!available || schema is null) throw new InvalidOperationException("Record history is unavailable after initialization.");
            var table = schema.Single(x => x.Name == name);
            var columns = table.Columns.ToDictionary(x => x.Name, x => x.Ordinal, StringComparer.Ordinal);
            var hiddenOrdinals = positions.Concat(gameDetails).Select(x => columns[x]).ToHashSet();
            if (request.Family is not (EditorHistoryFamily.Room or EditorHistoryFamily.Explanation)) hiddenOrdinals.Add(columns["EnableAnnotation"]);
            List<StoredVersion> rows;
            long ceiling;
            await using (var connection = Connection(true))
            {
                await connection.OpenAsync(token);
                await using var transaction = connection.BeginTransaction(deferred: true);
                await CheckMirrorsAsync(connection, transaction);
                if (request.Cursor is { } cursor) ceiling = cursor.Ceiling;
                else
                {
                    await using var max = Command(connection, transaction, $"SELECT COALESCE(MAX(rowid),0) FROM {Quote(Mirror(name))} WHERE Id=$key", ("$key", key));
                    ceiling = (long)(await max.ExecuteScalarAsync(token))!;
                }
                var parameters = new List<(string Name, object? Value)> { ("$ceiling", ceiling), ("$limit", EditorPageSize + 1) };
                var range = " AND rowid <= $ceiling";
                if (request.Cursor is { } before)
                {
                    range += " AND (ChangeUtc,rowid) < ($beforeUtc,$beforeId)";
                    parameters.Add(("$beforeUtc", before.BeforeUtc.ToString("O", CultureInfo.InvariantCulture)));
                    parameters.Add(("$beforeId", before.BeforeRowId));
                }
                rows = await VersionsAsync(connection, transaction, table, key,
                    range + " ORDER BY ChangeUtc DESC,rowid DESC LIMIT $limit", token, parameters.ToArray());
            }

            var count = Math.Min(EditorPageSize, rows.Count);
            var revisions = new EditorHistoryRevision[count];
            var actors = rows.Take(count).Select(x => x.UserId).Where(x => x != Guid.Empty).Distinct().ToArray();
            var names = actors.Length == 0 ? null : await readNames(actors);
            var projected = rows.Take(count).Select(row => ProjectEditorValues(request.Family, row, columns, positions)).ToArray();
            var groupIds = projected.Select(x => x.RoomDetails?.GroupId).OfType<Guid>().Distinct().ToArray();
            var groups = groupIds.Length == 0 ? null : await readGroups(groupIds);
            for (var i = 0; i < count; ++i)
            {
                token.ThrowIfCancellationRequested();
                var row = rows[i];
                var older = i + 1 < rows.Count ? rows[i + 1] : null;
                var detailsOnly = false;
                if (hiddenOrdinals.Count > 0 && older is not null && row.IsDeleted == older.IsDeleted)
                {
                    // Compare all retained cells, including hidden ownership and sort fields.
                    for (var column = 0; column < table.Columns.Count; ++column)
                    {
                        if (row.Row.Values[column] == older.Row.Values[column]) continue;
                        if (!hiddenOrdinals.Contains(column)) { detailsOnly = false; break; }
                        detailsOnly = true;
                    }
                }
                var actor = row.UserId == Guid.Empty ? "Bypass" : names?.GetValueOrDefault(row.UserId) ?? "Unknown account";
                var values = projected[i];
                if (values.RoomDetails is { GroupId: { } groupId } details)
                    values = values with { RoomDetails = details with { GroupName = groups?.GetValueOrDefault(groupId) } };
                revisions[i] = new(long.Parse(row.Selector.RowId, CultureInfo.InvariantCulture), row.ChangeUtc,
                    row.UserId, actor, detailsOnly, values);
            }
            // The lookbehind is not emitted, and becomes the first row on the next page.
            return new(EditorHistoryStatus.Available, revisions, rows.Count > count
                ? new(ceiling, revisions[^1].ChangeUtc, revisions[^1].RowId) : null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (AccessDeniedException) { throw; }
        catch (Exception exception) { LogFailure(exception, "editor row read"); return EditorUnavailable; }
    }

    private static EditorHistoryValues ProjectEditorValues(EditorHistoryFamily family, StoredVersion row,
        Dictionary<string, int> columns, string[] positions)
    {
        object? Cell(string key)
        {
            var value = Native(row.Row.Values[columns[key]]);
            return value is DBNull ? null : value;
        }
        string? Text(string key) => (string?)Cell(key);
        bool? Flag(string key) => Cell(key) switch
        {
            null => null, 0L => false, 1L => true, _ => throw new InvalidDataException("Invalid history Boolean.")
        };
        double? NumberAt(string key) => Cell(key) switch
        {
            null => null, double value when double.IsFinite(value) => value, long value => value,
            _ => throw new InvalidDataException("Invalid history coordinate.")
        };
        EditorHistoryPoint Point(string prefix) => new(NumberAt(prefix + "X"), NumberAt(prefix + "Y"), NumberAt(prefix + "Z"));

        var state = row.IsDeleted ? EditorHistoryState.Deleted : Flag("IsArchived")!.Value ? EditorHistoryState.Archived : EditorHistoryState.Active;
        if (family == EditorHistoryFamily.Room)
            return new(Text("FriendlyName"), null, false, state, Reference: Text("ReferenceId"), InGameId: Text("InGameId"),
                RoomDetails: new(Text("Contributors"), Text("Comments"), Text("RoomGroupId") is { } group ? Guid.Parse(group) : null));
        if (family == EditorHistoryFamily.Explanation)
            return new(Text("FriendlyName"), Text("Notes"), false, state, Reference: Text("ReferenceId"), Link: Text("Link"));

        var values = new EditorHistoryValues(Text("FriendlyName"), Text("Notes"), Flag("EnableAnnotation")!.Value, state);
        var x = NumberAt(positions[0]); var y = NumberAt(positions[1]);
        if (family == EditorHistoryFamily.Subroom)
            return values with { Reference = Text("ReferenceId"), AnnotationRectangle = new(x, y, NumberAt(positions[2]), NumberAt(positions[3])) };

        values = values with { Requirements = Text("Requirements"), Todo = Flag("IsTodo")!.Value,
            Verify = Flag("IsVerified"), AnnotationPoint = new(x, y) };
        if (family == EditorHistoryFamily.Connection)
            return values with { Alias = Text("Alias"), Source = Text("SourceSubroomReferenceText"), Destination = Text("DestinationSubroomReferenceText") };

        values = values with { InGameId = Text("InGameId"), InGamePosition = Point("InGamePosition"), LocalPosition = Point("LocalPosition") };
        return family == EditorHistoryFamily.Check
            ? values with { Type = Text("LocationType"), Source = Text("SubroomReferenceText") }
            : values with { Alias = Text("Alias"), Source = Text("SourceSubroomReferenceText"), Destination = Text("DestinationRoomReferenceText"),
                DestinationAlias = Text("DestinationTransitionAliasText"), UnsafeArrival = Flag("IsUnsafeArrival")!.Value };
    }
}
