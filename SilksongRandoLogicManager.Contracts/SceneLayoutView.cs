using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;

public enum V2Severity { Neutral, Warning, Danger }
/// <summary>Read-only scene projection. Coordinates remain lower-left/y-up scene units.</summary>
public sealed record SceneImageDisplayView(bool HasTransform, bool IsStale, bool? ImageExists, long ImageVersion = 0)
{
    public bool IsAvailable => HasTransform && !IsStale && ImageExists == true;
    public V2Severity BoundsImageSeverity { get; init; } = V2Severity.Neutral;
}
/// <summary>Read-only scene projection. Coordinates remain lower-left/y-up scene units.</summary>
public sealed record SceneLayoutView(bool HasValidBounds, double SceneMinX, double SceneMinY, double? SceneMaxX, double? SceneMaxY,
    IReadOnlyList<SceneSubroomFrameView> Frames, IReadOnlyList<SceneMarkerView> Markers,
    SceneImageDisplayView? Image = null, IReadOnlyList<SceneRelationshipThreadView>? Threads = null)
{
    public double? SceneUnitWidth => SceneBounds.TryGetSize(SceneMinX, SceneMinY, SceneMaxX, SceneMaxY, out var w, out _) ? w : null;
    public double? SceneUnitHeight => SceneBounds.TryGetSize(SceneMinX, SceneMinY, SceneMaxX, SceneMaxY, out _, out var h) ? h : null;
    public IReadOnlyList<SceneRelationshipThreadView> RelationshipThreads => Threads ?? [];
    public string? BoundsImageDiagnostic { get; init; }
}
public sealed record SceneSubroomFrameView(Guid EntityId, string Label, string? Title, double X, double Y, double Width, double Height);
public sealed record SceneMarkerView(Guid EntityId, string Kind, string Label, string? Title, double X, double Y)
{
    public bool IsEnemy { get; init; }
}
/// <summary>Resolved-ID-only live-canvas relationship with complete renderable endpoint geometry.</summary>
public sealed record SceneRelationshipThreadView(Guid MarkerEntityId, Guid FrameEntityId, double MarkerX, double MarkerY,
    double FrameX, double FrameY, double FrameWidth, double FrameHeight)
{
    public bool IsOneWayConnection { get; init; }
}
