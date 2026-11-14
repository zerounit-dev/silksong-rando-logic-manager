using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed record SceneRoomLoad(Guid Id, bool Archived, double MinX, double MinY, double? MaxX, double? MaxY, double? ScaleX, double? ScaleY, double? PixelOffsetX, double? PixelOffsetY, bool UseSceneImage, bool UseUploadedSceneImage, bool IsStale, DateTime UpdatedUtc);
public sealed record SceneSubroomLoad(Guid Id, string FriendlyName, double? X, double? Y, double? Width, double? Height, bool Enabled);
public sealed record SceneTransitionLoad(Guid Id, string Alias, string FriendlyName, double? OverrideX, double? OverrideY, Guid? SourceSubroomId);
public sealed record SceneCheckLoad(Guid Id, string FriendlyName, double? OverrideX, double? OverrideY, Guid? SubroomId, string? LocationType);
public sealed record SceneConnectionLoad(Guid Id, string Alias, string FriendlyName, string Source, string Destination, Guid? SourceId, Guid? DestinationId, bool Enabled, double? X, double? Y);

/// <summary>Pure scene projection shared by retained database loaders and local room presentation.</summary>
public static class SceneLayoutMapper
{
    public const double RecommendedScale = 6.35;

    public static SceneLayoutView Map(SceneRoomLoad room, IReadOnlyList<SceneSubroomLoad> subrooms, IReadOnlyList<SceneTransitionLoad> transitions, IReadOnlyList<SceneCheckLoad> checks, IReadOnlyList<SceneConnectionLoad> connections, bool? imageExists = null)
    {
        var validBounds = SceneBounds.TryGetSize(room.MinX, room.MinY, room.MaxX, room.MaxY, out _, out _);
        var frames = subrooms.Where(x => x.Enabled && Rectangle(x.X, x.Y, x.Width, x.Height))
            .OrderBy(x => x.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new SceneSubroomFrameView(x.Id, x.FriendlyName, Title(x.FriendlyName), x.X!.Value, x.Y!.Value, x.Width!.Value, x.Height!.Value)).ToArray();
        var framesById = frames.ToDictionary(x => x.EntityId);
        var markers = new List<SceneMarkerView>();
        var threads = new List<SceneRelationshipThreadView>();
        foreach (var transition in transitions)
        {
            var marker = AddAnnotation(markers, transition.Id, "exit", transition.Alias, transition.FriendlyName, transition.OverrideX, transition.OverrideY);
            AddThread(threads, marker, transition.SourceSubroomId, framesById);
        }
        foreach (var check in checks)
        {
            var marker = AddAnnotation(markers, check.Id, "check", check.FriendlyName, check.FriendlyName, check.OverrideX, check.OverrideY, CheckLocationTypeCatalogue.IsEnemy(check.LocationType));
            AddThread(threads, marker, check.SubroomId, framesById);
        }
        if (subrooms.Count > 0)
            foreach (var group in connections.GroupBy(x => Key(x.Alias)).Where(x => ValidConnectionGroup(x.ToArray(), connections)))
            {
                var rows = group.ToArray(); var first = rows[0];
                if (!first.Enabled) continue;
                if (!CompletePair(first.X, first.Y)) continue;
                var marker = new SceneMarkerView(first.Id, "connection", first.Alias, Title(first.FriendlyName), first.X!.Value, first.Y!.Value);
                markers.Add(marker);
                foreach (var endpointId in rows.SelectMany(x => new[] { x.SourceId, x.DestinationId }).OfType<Guid>().Distinct())
                    AddThread(threads, marker, endpointId, framesById, rows.Length == 1);
            }
        var hasTransform = room.UseSceneImage && SceneBounds.HasUsableScales(room.ScaleX, room.ScaleY) && SceneBounds.HasUsablePixelOffsets(room.PixelOffsetX, room.PixelOffsetY);
        var mappingPresent = room.ScaleX is not null || room.ScaleY is not null || room.PixelOffsetX is not null || room.PixelOffsetY is not null;
        var boundsImageSeverity = !room.UseSceneImage ? V2Severity.Neutral
            : room.IsStale || imageExists == false ? V2Severity.Danger
            : !room.UseUploadedSceneImage && (room.ScaleX != RecommendedScale || room.ScaleY != RecommendedScale) ? V2Severity.Warning
            : V2Severity.Neutral;
        return new(validBounds, room.MinX, room.MinY, room.MaxX, room.MaxY, frames, markers.OrderBy(x => x.Kind).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToArray(), new(hasTransform, room.UseSceneImage && room.IsStale, room.UseSceneImage ? imageExists : false, room.UpdatedUtc.Ticks) { BoundsImageSeverity = boundsImageSeverity }, threads)
        { BoundsImageDiagnostic = !validBounds ? "scene bounds unavailable or unusable" : room.UseSceneImage && mappingPresent && !hasTransform ? "scene image mapping incomplete or unusable" : null };
    }

    private static SceneMarkerView? AddAnnotation(List<SceneMarkerView> markers, Guid id, string kind, string label, string friendly, double? overrideX, double? overrideY, bool isEnemy = false)
    {
        if (!CompletePair(overrideX, overrideY)) return null;
        var marker = new SceneMarkerView(id, kind, label, Title(friendly), overrideX!.Value, overrideY!.Value) { IsEnemy = isEnemy };
        markers.Add(marker);
        return marker;
    }
    private static void AddThread(List<SceneRelationshipThreadView> threads, SceneMarkerView? marker, Guid? frameId, IReadOnlyDictionary<Guid, SceneSubroomFrameView> framesById, bool isOneWayConnection = false)
    {
        if (marker is null || frameId is null) return;
        if (framesById.TryGetValue(frameId.Value, out var frame)) threads.Add(new(marker.EntityId, frame.EntityId, marker.X, marker.Y, frame.X, frame.Y, frame.Width, frame.Height) { IsOneWayConnection = isOneWayConnection });
    }
    private static bool ValidConnectionGroup(SceneConnectionLoad[] group, IReadOnlyList<SceneConnectionLoad> all) =>
        group.Length is 1 or 2 && group[0].Alias.Length is >= 1 and <= 3 &&
        group.All(x => Same(x.FriendlyName, group[0].FriendlyName) && SamePair(x, group[0])) &&
        all.Where(x => Same(x.FriendlyName, group[0].FriendlyName)).Select(x => Key(x.Alias)).Distinct().Count() == 1 &&
        (group.Length == 1 || !SameDirection(group[0], group[1]));
    private static bool SamePair(SceneConnectionLoad a, SceneConnectionLoad b) => SameDirection(a, b) || (SameEndpoint(a.Source, a.SourceId, b.Destination, b.DestinationId) && SameEndpoint(a.Destination, a.DestinationId, b.Source, b.SourceId));
    private static bool SameDirection(SceneConnectionLoad a, SceneConnectionLoad b) => SameEndpoint(a.Source, a.SourceId, b.Source, b.SourceId) && SameEndpoint(a.Destination, a.DestinationId, b.Destination, b.DestinationId);
    private static bool SameEndpoint(string a, Guid? aId, string b, Guid? bId) => aId is not null && bId is not null ? aId == bId : Same(a, b);
    private static bool PositivePair(double? x, double? y) => x is double a && y is double b && double.IsFinite(a) && double.IsFinite(b) && a > 0 && b > 0;
    public static bool HasPositiveBounds(double? x, double? y) => PositivePair(x, y);
    private static bool Rectangle(double? x, double? y, double? width, double? height) => x is double a && y is double b && PositivePair(width, height) && double.IsFinite(a) && double.IsFinite(b);
    private static bool CompletePair(double? x, double? y) => x is double a && y is double b && double.IsFinite(a) && double.IsFinite(b);
    private static string? Title(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string Key(string value) => value.Trim().ToUpperInvariant();
    private static bool Same(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
