using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed partial class RecordHistoryStore
{
    // Bounded current authoring contract, not a naming rule or include-all default.
    // New live columns require owning-spec classification and a matching change here.
    private sealed record Retention(string Retained, string Excluded = "");
    private static readonly IReadOnlyDictionary<string, Retention> RetentionByTable = new Dictionary<string, Retention>(StringComparer.Ordinal)
    {
        ["RoomGroups"] = new("Id FriendlyName ZoneReferenceText IsVirtual SortOrder", "CreatedUtc UpdatedUtc ResolvedMapZoneId"),
        ["Rooms"] = new("Id RoomGroupId ReferenceId FriendlyName InGameId Contributors Comments SceneMinX SceneMinY SceneMaxX SceneMaxY SceneImagePixelsPerUnitX SceneImagePixelsPerUnitY SceneImagePixelOffsetX SceneImagePixelOffsetY UseSceneImage UseUploadedSceneImage SceneImageOriginalHash SortOrder IsArchived", "CreatedUtc UpdatedUtc ArchivedUtc CacheFlagUtc CompletionStatus IsSceneImageStale SceneImageCropHash"),
        ["Subrooms"] = new("Id RoomId ReferenceId FriendlyName Notes SceneUnitX SceneUnitY SceneUnitWidth SceneUnitHeight EnableAnnotation SortOrder IsArchived", "CreatedUtc UpdatedUtc ArchivedUtc"),
        ["RequirementExplanations"] = new("Id RoomId ReferenceId FriendlyName Link Notes SortOrder IsArchived", "CreatedUtc UpdatedUtc ArchivedUtc"),
        ["RoomTransitions"] = new("Id RoomId Alias FriendlyName InGameId IsUnsafeArrival InGamePositionX InGamePositionY InGamePositionZ AnnotationSceneUnitX AnnotationSceneUnitY EnableAnnotation LocalPositionX LocalPositionY LocalPositionZ SourceSubroomReferenceText DestinationRoomReferenceText DestinationTransitionAliasText Requirements Notes SortOrder IsTodo IsVerified IsArchived", "CreatedUtc UpdatedUtc ArchivedUtc RequirementsParseSucceeded ResolvedSourceSubroomId ResolvedDestinationRoomId ResolvedDestinationTransitionId"),
        ["SubroomConnections"] = new("Id RoomId Alias FriendlyName SourceSubroomReferenceText DestinationSubroomReferenceText Requirements Notes EnableAnnotation SceneUnitX SceneUnitY SortOrder IsTodo IsVerified IsArchived", "CreatedUtc UpdatedUtc ArchivedUtc RequirementsParseSucceeded ResolvedSourceSubroomId ResolvedDestinationSubroomId"),
        ["CheckLocations"] = new("Id RoomId FriendlyName InGameId InGamePositionX InGamePositionY InGamePositionZ AnnotationSceneUnitX AnnotationSceneUnitY LocalPositionX LocalPositionY LocalPositionZ SubroomReferenceText Requirements Notes LocationType EnableAnnotation SortOrder IsTodo IsVerified IsArchived", "CreatedUtc UpdatedUtc ArchivedUtc RequirementsParseSucceeded ResolvedSubroomId"),
        ["Maps"] = new("Id InGameId FriendlyName SortOrder MapUnitMinX MapUnitMinY MapUnitMaxX MapUnitMaxY"),
        ["MapZones"] = new("Id MapId InGameId FriendlyName MapUnitMinX MapUnitMinY MapUnitMaxX MapUnitMaxY"),
        ["MapScenes"] = new("Id MapZoneId InGameId FriendlyName RoomReferenceText", "ResolvedRoomId"),
        ["MapChunks"] = new("Id MapSceneId CacheIndex InitialState MapUnitMinX MapUnitMinY MapUnitMaxX MapUnitMaxY MapUnitZ"),
        ["MapOverlays"] = new("Id MapId FriendlyName ImageAssetKey ScaleXPercent ScaleYPercent LeftOffsetPercent BottomOffsetPercent SortOrder"),
        ["RequirementPredicates"] = new("Id Name Category InputSyntax OutputSyntax Aliases Notes SortOrder"),
        ["RequirementItems"] = new("Id Name Category OutputValue Aliases Notes SortOrder"),
        ["RequirementCrests"] = new("Id Name Category OutputValue Aliases Notes SortOrder"),
        ["RequirementMethods"] = new("Id Name Category OutputValue Aliases Notes SortOrder"),
        ["RequirementCountables"] = new("Id Name Category OutputValue Aliases Notes SortOrder"),
        ["RequirementSouls"] = new("Id Name Category OutputValue Aliases Notes SortOrder")
    };

    private sealed record Projection(LogicCacheSql.TableSchema Source, LogicCacheSql.TableSchema Retained, IReadOnlyList<int> SourceIndexes);
    private IReadOnlyDictionary<string, Projection>? projections;

    private void InitializeProjection(LogicCacheSql.SchemaManifest manifest)
    {
        var names = LogicCacheSql.DomainTables.Select(LogicCacheSql.Name).ToArray();
        if (names.Length != RetentionByTable.Count || !names.ToHashSet(StringComparer.Ordinal).SetEquals(RetentionByTable.Keys))
            throw new InvalidDataException("Unclassified history table inventory.");
        var mapped = new Dictionary<string, Projection>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var source = manifest.Tables.Single(t => t.Name == name);
            var retention = RetentionByTable[name];
            var retained = retention.Retained.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var excluded = retention.Excluded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var inventory = retained.Concat(excluded).ToArray();
            if (inventory.Distinct(StringComparer.Ordinal).Count() != inventory.Length
                || source.Columns.Count != inventory.Length || !source.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal).SetEquals(inventory)
                || source.Columns.Where((c, i) => c.Ordinal != i || c.DeclaredType is not ("TEXT" or "INTEGER" or "REAL" or "BLOB")).Any()
                || source.Columns.Count(c => c.PrimaryKeyOrdinal != 0) != 1
                || !source.Columns.Any(c => c.Name == "Id" && c.PrimaryKeyOrdinal == 1 && c.DeclaredType == "TEXT"))
                throw new InvalidDataException("Unclassified or unsupported original history columns.");
            // Source indexes address FULL shared capture. History ordinals are contiguous
            // after projection; never use a live cid to index a projected/read row.
            var indexes = source.Columns.Select((c, i) => (c, i)).Where(x => retained.Contains(x.c.Name)).Select(x => x.i).ToArray();
            var columns = indexes.Select((index, ordinal) => source.Columns[index] with { Ordinal = ordinal }).ToArray();
            mapped.Add(name, new(source, new(name, Array.AsReadOnly(columns), Array.Empty<LogicCacheSql.ForeignKey>()), Array.AsReadOnly(indexes)));
        }
        projections = mapped;
        schema = Array.AsReadOnly(names.Select(name => mapped[name].Retained).ToArray());
    }

    private sealed record RetainedChange(LogicCacheSql.TableSchema Table, LogicCacheRow Row, bool IsDeleted);

    private IReadOnlyList<RetainedChange> Qualify(IReadOnlyList<LogicChangeCapture.Change> changes)
    {
        var qualified = new List<RetainedChange>();
        foreach (var change in changes)
        {
            var map = projections?.GetValueOrDefault(LogicCacheSql.Name(change.Table))
                ?? throw new InvalidDataException("Missing history column classification.");
            if (!change.Columns.SequenceEqual(map.Source.Columns.Select(c => c.Name), StringComparer.Ordinal))
                throw new InvalidDataException("History capture column inventory changed.");
            LogicCacheRow? Project(LogicCacheRow? row)
            {
                if (row is null) return null;
                var id = map.Source.Columns.Single(c => c.Name == "Id").Ordinal;
                if (row.Key != change.Key || row.Values.Count != map.Source.Columns.Count
                    || row.Values[id] != new LogicCacheCell(LogicCacheCellType.Text, change.Key))
                    throw new InvalidDataException("Invalid final history row identity or shape.");
                var cells = map.SourceIndexes.Select(index => row.Values[index]).ToArray();
                foreach (var cell in cells) _ = Native(cell);
                return new(row.Key, Array.AsReadOnly(cells));
            }
            var before = Project(change.Before);
            var after = Project(change.After);
            if (before is null && after is null) throw new InvalidDataException("Missing final history values.");
            if (before is not null && after is not null && before.Values.SequenceEqual(after.Values)) continue;
            qualified.Add(new(map.Retained, after ?? before!, after is null));
        }
        return qualified.AsReadOnly();
    }
}
