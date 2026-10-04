using System.Globalization;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class SceneReviewProjectionService
{
    public SceneReviewSpatialProjection Project(IEnumerable<SceneDumpObject> nodes, IReadOnlySet<Guid> visibleObjectIds, Guid? hoveredObjectId)
    {
        return Project(CreatePoints(nodes), visibleObjectIds, hoveredObjectId);
    }

    public IReadOnlyList<SceneReviewPoint> CreatePoints(IEnumerable<SceneDumpObject> nodes) => Flatten(nodes)
            .Select(node => (node.Id, node.Classification, Position: Position(node)))
            .Where(item => item.Position is not null)
            .Select(item => new SceneReviewPoint(item.Id, item.Position!.Value.X, -item.Position.Value.Y, item.Classification))
            .ToArray();

    public SceneReviewSpatialProjection Project(IReadOnlyList<SceneReviewPoint> allPoints, IReadOnlySet<Guid> visibleObjectIds, Guid? hoveredObjectId)
    {
        var points = allPoints.Where(point => visibleObjectIds.Contains(point.Id)).ToArray();
        var hoveredPoint = allPoints.SingleOrDefault(point => point.Id == hoveredObjectId);
        var bounds = Bounds(hoveredPoint is null ? points : points.Append(hoveredPoint).ToArray());
        return new SceneReviewSpatialProjection(points, hoveredPoint, bounds);
    }

    public IReadOnlyDictionary<string, int> CountCheckIdentities(IEnumerable<SceneDumpObject> nodes) => nodes
        .Where(node => node.Classification is SceneDumpClassification.Check or SceneDumpClassification.Enemy)
        .Select(SceneDumpIdentity.GameId).Where(id => !string.IsNullOrWhiteSpace(id))
        .GroupBy(id => id!.Trim(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    public SceneReviewImportDraft CreateDraft(SceneDumpObject node, SceneImportPreview preview, Guid? targetRoomId,
        IReadOnlyDictionary<string, int> checkIdentityCounts)
    {
        var targetMatches = SceneImportService.MatchObject(node, preview.Transitions, preview.Checks).Matches
            .Where(match => match.RoomId == targetRoomId && match.Classification ==
                (node.Classification == SceneDumpClassification.Exit ? SceneDumpClassification.Exit : SceneDumpClassification.Check))
            .ToArray();
        var gameId = SceneDumpIdentity.GameId(node);
        var uniqueIncoming = node.Classification == SceneDumpClassification.Exit ||
            gameId is not null && checkIdentityCounts.TryGetValue(gameId.Trim(), out var count) && count == 1;
        Guid? updateId = uniqueIncoming && targetMatches.Length == 1 ? targetMatches[0].RecordId : null;
        var name = updateId is { } id ? SelectableRecords(preview, node.Classification, targetRoomId).Single(record => record.Id == id).FriendlyName : node.Name;
        return new SceneReviewImportDraft(node.Id, updateId is null ? SceneImportMode.Create : SceneImportMode.Update, updateId, name);
    }

    public IReadOnlyList<SceneImportSelectableRecord> SelectableRecords(SceneImportPreview preview, SceneDumpClassification classification, Guid? targetRoomId) =>
        (classification == SceneDumpClassification.Exit ? preview.Transitions : preview.Checks)
            .Where(record => record.RoomId == targetRoomId)
            .ToArray();

    public void AllocateNames(IEnumerable<SceneDumpObject> sourceOrder, SceneImportPreview preview, Guid? targetRoomId,
        IDictionary<Guid, SceneReviewImportDraft> drafts)
    {
        var nodes = sourceOrder.ToArray();
        var canonicalNames = preview.EnemyNames
            .GroupBy(mapping => RequirementCatalogueLanguage.NormalizeIdentification(mapping.OutputValue), StringComparer.Ordinal)
            .Where(group => group.Key.Length != 0 && group.Select(mapping => mapping.Id).Distinct().Count() == 1)
            .Select(group => (group.Key, Name: group.First().Name))
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.Name))
            .ToDictionary(mapping => mapping.Key, mapping => mapping.Name, StringComparer.Ordinal);
        var createIds = nodes.Where(node => drafts.TryGetValue(node.Id, out var draft) && draft.Mode == SceneImportMode.Create).Select(node => node.Id).ToHashSet();
        var reserved = new HashSet<string>(preview.Transitions.Concat(preview.Checks)
            .Where(record => record.RoomId == targetRoomId).Select(record => record.FriendlyName.Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (var draft in drafts.Values.Where(draft => createIds.Contains(draft.ObjectId) && draft.AuthoredName is not null))
            reserved.Add(draft.AuthoredName!.Trim());
        foreach (var node in nodes)
        {
            if (!drafts.TryGetValue(node.Id, out var draft) || draft.Mode != SceneImportMode.Create) continue;
            if (draft.AuthoredName is { } authored) { drafts[node.Id] = draft with { FriendlyName = authored }; continue; }
            var basis = node.JournalName ?? node.Name;
            var enemy = node.Classification == SceneDumpClassification.Enemy;
            if (enemy && node.JournalName is { } journal &&
                canonicalNames.TryGetValue(RequirementCatalogueLanguage.NormalizeIdentification(journal), out var canonical))
                basis = canonical;
            var name = enemy ? $"{basis} 1" : basis;
            for (var number = 2; !reserved.Add(name.Trim()); number++)
                name = enemy ? $"{basis} {number.ToString(CultureInfo.InvariantCulture)}" : $"{basis} {number}";
            drafts[node.Id] = draft with { FriendlyName = name };
        }
    }

    private static SceneReviewBounds Bounds(IReadOnlyList<SceneReviewPoint> points)
    {
        if (points.Count == 0) return new SceneReviewBounds(-1, -1, 2, 2);
        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxY = points.Max(point => point.Y);
        var width = Math.Max(maxX - minX, 1d);
        var height = Math.Max(maxY - minY, 1d);
        return new SceneReviewBounds(minX - width * .03, minY - height * .03, width * 1.06, height * 1.06);
    }

    private static (double X, double Y)? Position(SceneDumpObject node) =>
        node.WorldPosition is { X: { } x, Y: { } y } && double.IsFinite(x) && double.IsFinite(y) ? (x, y) : null;

    private static IEnumerable<SceneDumpObject> Flatten(IEnumerable<SceneDumpObject> nodes) => nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));
}

public sealed record SceneReviewSpatialProjection(IReadOnlyList<SceneReviewPoint> Points, SceneReviewPoint? HoveredPoint, SceneReviewBounds Bounds);
public sealed record SceneReviewPoint(Guid Id, double X, double Y, SceneDumpClassification Classification);
public sealed record SceneReviewBounds(double X, double Y, double Width, double Height);
public sealed record SceneReviewImportDraft(Guid ObjectId, SceneImportMode Mode, Guid? ExistingRecordId, string FriendlyName, string? AuthoredName = null, bool HasDeliberateRecordSelection = false);
