using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public interface ISceneLayoutLoader
{
    Task<SceneLayoutView?> LoadAsync(Guid roomId, CancellationToken cancellationToken);
    /// <summary>Coordinator-only scalar batch operation; components never receive a context.</summary>
    Task<SceneLayoutView?> LoadAsync(LogicDbContext db, Guid roomId, CancellationToken cancellationToken)
        => throw new NotSupportedException("This scene loader cannot join a logical-room snapshot.");
}

/// <summary>Phase-4 scalar-only scene projection; it deliberately has no map or component dependency.</summary>
public sealed class SceneLayoutLoader(IDbContextFactory<LogicDbContext> contexts, SceneImageFileService? images = null) : ISceneLayoutLoader
{
    public async Task<SceneLayoutView?> LoadAsync(Guid roomId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await db.Database.BeginTransactionAsync(cancellationToken);
        return await LoadAsync(db, roomId, cancellationToken);
    }

    public async Task<SceneLayoutView?> LoadAsync(LogicDbContext db, Guid roomId, CancellationToken cancellationToken)
    {
        var room = await db.Rooms.AsNoTracking().Where(x => x.Id == roomId)
            .Select(x => new SceneRoomLoad(x.Id, x.IsArchived, x.SceneUnitWidth, x.SceneUnitHeight, x.SceneImageScaleXPercent, x.SceneImageScaleYPercent, x.SceneImagePanXPercent, x.SceneImagePanYPercent, x.IsSceneImageStale, x.UpdatedUtc)).SingleOrDefaultAsync(cancellationToken);
        if (room is null || room.Archived) return null;
        var subrooms = await db.Subrooms.AsNoTracking().Where(x => x.RoomId == roomId && !x.IsArchived)
            .Select(x => new SceneSubroomLoad(x.Id, x.FriendlyName, x.SceneUnitX, x.SceneUnitY, x.SceneUnitWidth, x.SceneUnitHeight, x.EnableAnnotation)).ToListAsync(cancellationToken);
        var transitions = await db.RoomTransitions.AsNoTracking().Where(x => x.RoomId == roomId && !x.IsArchived && x.EnableAnnotation)
            .Select(x => new SceneTransitionLoad(x.Id, x.Alias, x.FriendlyName, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY)).ToListAsync(cancellationToken);
        var checks = await db.CheckLocations.AsNoTracking().Where(x => x.RoomId == roomId && !x.IsArchived && x.EnableAnnotation)
            .Select(x => new SceneCheckLoad(x.Id, x.FriendlyName, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY)).ToListAsync(cancellationToken);
        var connections = await db.SubroomConnections.AsNoTracking().Where(x => x.RoomId == roomId && !x.IsArchived)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new SceneConnectionLoad(x.Id, x.Alias, x.FriendlyName, x.SourceSubroomReferenceText, x.DestinationSubroomReferenceText, x.ResolvedSourceSubroomId, x.ResolvedDestinationSubroomId, x.EnableAnnotation, x.SceneUnitX, x.SceneUnitY)).ToListAsync(cancellationToken);
        return SceneLayoutMapper.Map(room, subrooms, transitions, checks, connections, images?.Exists(room.Id) == true);
    }
}

internal sealed record SceneRoomLoad(Guid Id, bool Archived, double? Width, double? Height, double? ScaleX, double? ScaleY, double? PanX, double? PanY, bool IsStale, DateTime UpdatedUtc);
internal sealed record SceneSubroomLoad(Guid Id, string FriendlyName, double? X, double? Y, double? Width, double? Height, bool Enabled);
internal sealed record SceneTransitionLoad(Guid Id, string Alias, string FriendlyName, double? OverrideX, double? OverrideY);
internal sealed record SceneCheckLoad(Guid Id, string FriendlyName, double? OverrideX, double? OverrideY);
internal sealed record SceneConnectionLoad(Guid Id, string Alias, string FriendlyName, string Source, string Destination, Guid? SourceId, Guid? DestinationId, bool Enabled, double? X, double? Y);

internal static class SceneLayoutMapper
{
    internal static SceneLayoutView Map(SceneRoomLoad room, IReadOnlyList<SceneSubroomLoad> subrooms, IReadOnlyList<SceneTransitionLoad> transitions, IReadOnlyList<SceneCheckLoad> checks, IReadOnlyList<SceneConnectionLoad> connections, bool imageExists = false)
    {
        var validBounds = PositivePair(room.Width, room.Height);
        var frames = subrooms.Where(x => x.Enabled && Rectangle(x.X, x.Y, x.Width, x.Height))
            .OrderBy(x => x.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new SceneSubroomFrameView(x.Id, x.FriendlyName, Title(x.FriendlyName), x.X!.Value, x.Y!.Value, x.Width!.Value, x.Height!.Value)).ToArray();
        var markers = new List<SceneMarkerView>();
        foreach (var transition in transitions)
            AddAnnotation(markers, transition.Id, "exit", transition.Alias, transition.FriendlyName, transition.OverrideX, transition.OverrideY);
        foreach (var check in checks)
            AddAnnotation(markers, check.Id, "check", check.FriendlyName, check.FriendlyName, check.OverrideX, check.OverrideY);
        if (subrooms.Count > 0)
            foreach (var group in connections.GroupBy(x => Key(x.Alias)).Where(x => ValidConnectionGroup(x.ToArray(), connections)))
            {
                var rows = group.ToArray(); var first = rows[0];
                if (!rows.All(x => x.Enabled) || !SynchronizedPair(rows.Select(x => (x.X, x.Y)).ToArray())) continue;
                if (CompletePair(first.X, first.Y)) markers.Add(new(first.Id, "connection", first.Alias, Title(first.FriendlyName), first.X!.Value, first.Y!.Value));
            }
        var hasTransform = CompletePair(room.ScaleX, room.ScaleY) && CompletePair(room.PanX, room.PanY) && room.ScaleX > 0 && room.ScaleY > 0;
        return new(validBounds, room.Width, room.Height, frames, markers.OrderBy(x => x.Kind).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToArray(), new(hasTransform, room.IsStale, imageExists, room.UpdatedUtc.Ticks));
    }

    private static void AddAnnotation(List<SceneMarkerView> markers, Guid id, string kind, string label, string friendly, double? overrideX, double? overrideY)
    {
        if (CompletePair(overrideX, overrideY)) markers.Add(new(id, kind, label, Title(friendly), overrideX!.Value, overrideY!.Value));
    }
    private static bool ValidConnectionGroup(SceneConnectionLoad[] group, IReadOnlyList<SceneConnectionLoad> all) =>
        group.Length is 1 or 2 && group[0].Alias.Length is >= 1 and <= 3 &&
        group.All(x => Same(x.FriendlyName, group[0].FriendlyName) && SamePair(x, group[0])) &&
        all.Where(x => Same(x.FriendlyName, group[0].FriendlyName)).Select(x => Key(x.Alias)).Distinct().Count() == 1 &&
        (group.Length == 1 || !SameDirection(group[0], group[1]));
    private static bool SamePair(SceneConnectionLoad a, SceneConnectionLoad b) => SameDirection(a, b) || (SameEndpoint(a.Source, a.SourceId, b.Destination, b.DestinationId) && SameEndpoint(a.Destination, a.DestinationId, b.Source, b.SourceId));
    private static bool SameDirection(SceneConnectionLoad a, SceneConnectionLoad b) => SameEndpoint(a.Source, a.SourceId, b.Source, b.SourceId) && SameEndpoint(a.Destination, a.DestinationId, b.Destination, b.DestinationId);
    private static bool SameEndpoint(string a, Guid? aId, string b, Guid? bId) => aId is not null && bId is not null ? aId == bId : Same(a, b);
    private static bool SynchronizedPair((double? X, double? Y)[] pairs) => pairs.All(x => AbsentPair(x.X, x.Y)) || pairs.All(x => CompletePair(x.X, x.Y) && x.X == pairs[0].X && x.Y == pairs[0].Y);
    private static bool PositivePair(double? x, double? y) => x is double a && y is double b && double.IsFinite(a) && double.IsFinite(b) && a > 0 && b > 0;
    internal static bool HasPositiveBounds(double? x, double? y) => PositivePair(x, y);
    private static bool Rectangle(double? x, double? y, double? width, double? height) => x is double a && y is double b && PositivePair(width, height) && double.IsFinite(a) && double.IsFinite(b);
    private static bool CompletePair(double? x, double? y) => x is double a && y is double b && double.IsFinite(a) && double.IsFinite(b);
    private static bool AbsentPair(double? x, double? y) => x is null && y is null;
    private static string? Title(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string Key(string value) => value.Trim().ToUpperInvariant();
    private static bool Same(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
