namespace Silksong_Rando_Logic_Manager.Services;

public sealed class SceneReviewProjectionService
{
    public SceneReviewSpatialProjection Project(IEnumerable<SceneDumpObject> nodes, IReadOnlySet<Guid> visibleObjectIds, Guid? hoveredObjectId)
    {
        return Project(CreatePoints(nodes), visibleObjectIds, hoveredObjectId);
    }

    public IReadOnlyList<SceneReviewPoint> CreatePoints(IEnumerable<SceneDumpObject> nodes) => Flatten(nodes)
            .Select(node => (node.Id, Position: Position(node)))
            .Where(item => item.Position is not null)
            .Select(item => new SceneReviewPoint(item.Id, item.Position!.Value.X, -item.Position.Value.Y))
            .ToArray();

    public SceneReviewSpatialProjection Project(IReadOnlyList<SceneReviewPoint> allPoints, IReadOnlySet<Guid> visibleObjectIds, Guid? hoveredObjectId)
    {
        var points = allPoints.Where(point => visibleObjectIds.Contains(point.Id)).ToArray();
        var hoveredPoint = allPoints.SingleOrDefault(point => point.Id == hoveredObjectId);
        var bounds = Bounds(hoveredPoint is null ? points : points.Append(hoveredPoint).ToArray());
        return new SceneReviewSpatialProjection(points, hoveredPoint, bounds);
    }

    public SceneReviewImportDraft CreateDraft(SceneDumpObject node, SceneImportPreview preview, Guid? targetRoomId)
    {
        var targetMatches = preview.ObjectMatches.FirstOrDefault(match => match.ObjectId == node.Id)?.Matches
            .Where(match => match.RoomId == targetRoomId && match.Classification == node.Classification)
            .ToArray() ?? [];
        Guid? updateId = targetMatches.Length == 1 ? targetMatches[0].RecordId : null;
        return new SceneReviewImportDraft(node.Id, updateId is null ? SceneImportMode.Create : SceneImportMode.Update, updateId, node.Name);
    }

    public IReadOnlyList<SceneImportSelectableRecord> SelectableRecords(SceneImportPreview preview, SceneDumpClassification classification, Guid? targetRoomId) =>
        (classification == SceneDumpClassification.Exit ? preview.Transitions : preview.Checks)
            .Where(record => record.RoomId == targetRoomId)
            .ToArray();

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
public sealed record SceneReviewPoint(Guid Id, double X, double Y);
public sealed record SceneReviewBounds(double X, double Y, double Width, double Height);
public sealed record SceneReviewImportDraft(Guid ObjectId, SceneImportMode Mode, Guid? ExistingRecordId, string FriendlyName);
