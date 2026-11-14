using System.Collections.Immutable;
using Microsoft.JSInterop;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using SilksongRandoLogicManager.Client.RoomEditing;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    private V2SceneSelectedItem? sceneSelection;
    private SceneAnnotationPlacementRequest? scenePlacement;
    private SceneAnnotationPlacementRequest? consumedSceneFocus;
    private long sceneOwnerGeneration;
    private RoomEditorV2.SceneContextPanePresentation? scenePane;
    private bool SceneContextReady => Current && workspace.IsAdmitted && !Archived && !externalRoomDeleted && showMapContext
        && scenePane?.IsReadyOwner(Entry.RoomId, sceneOwnerGeneration) == true;
    private string SceneUnavailableReason => !showMapContext ? "Scene context is hidden." : "Scene context is not ready.";
    private readonly HashSet<V2SceneSelectedItemKind> hiddenSceneLayers = [];
    private static V2SceneSelectedItemKind SceneLayer(V2SceneSelectedItemKind kind) => kind == V2SceneSelectedItemKind.Connection ? V2SceneSelectedItemKind.Transition : kind;
    private static V2SceneSelectedItemKind SceneLayer(string kind) => kind == "subroom" ? V2SceneSelectedItemKind.Subroom : kind == "check" ? V2SceneSelectedItemKind.Check : V2SceneSelectedItemKind.Transition;
    private Task SceneLayerVisibilityAsync(Guid room, long generation, V2SceneSelectedItemKind kind, bool shown)
    {
        if (!OwnsScene(room, generation)) return Task.CompletedTask;
        if (!shown) TransferKeyboardBeforeAction();
        var layer = SceneLayer(kind);
        if (shown) hiddenSceneLayers.Remove(layer);
        else
        {
            hiddenSceneLayers.Add(layer);
            if (sceneSelection is { } selected && SceneLayer(selected.Kind) == layer) sceneSelection = null;
            if (scenePlacement is { } request && SceneLayer(request.Kind) == layer) scenePlacement = null;
        }
        StateHasChanged(); return Task.CompletedTask;
    }
    private bool SceneRowReady(RoomTableRow row) => SceneContextReady && !KeyboardGeometryBlocked && workspace.Queue.Confirmed.Document?.Scene?.HasValidBounds == true
        && !row.IsArchived && row.EntityId is not null && !row.Attention
        && (row is not RoomConnectionRow || row.CanAnnotate);
    private V2SceneActionBoxView SceneActionBox
    {
        get
        {
            EnsurePresentation();
            var scene = workspace.Confirmed.Document?.Scene;
            var status = scene?.BoundsImageDiagnostic ?? (scenePlacement is { } request
                ? request.Kind == "subroom" ? "draw subroom rectangle; drag to the opposite corner, or press Escape to cancel" : $"place {request.Kind} annotation; click the scene, or press Escape to cancel"
                : scene?.Image?.IsStale == true ? "needs recapture" : "");
            RoomTableRow? row = null;
            if (sceneSelection is { } selected)
                try { row = SceneRow(selected.Kind == V2SceneSelectedItemKind.Transition ? "exit" : selected.Kind.ToString().ToLowerInvariant(), selected.EntityId); }
                catch (InvalidOperationException) { }
            var unavailable = AuthoringBlocked || row is null || !SceneRowReady(row);
            var reason = unavailable ? "Scene action unavailable." : "Select a scene item first.";
            if (row is null || unavailable) return new(false, false, false, false, false, reason, reason, reason, reason, reason, false, status, false);
            var (x, y, enabled) = row switch
            {
                RoomSubroomRow r => (r.Geometry.X, r.Geometry.Y, r.Geometry.EnableAnnotation),
                RoomTransitionRow r => (r.Metadata.AnnotationSceneUnitX, r.Metadata.AnnotationSceneUnitY, r.EnableAnnotation),
                RoomCheckRow r => (r.Metadata.AnnotationSceneUnitX, r.Metadata.AnnotationSceneUnitY, r.EnableAnnotation),
                RoomConnectionRow r => (r.X, r.Y, r.EnableAnnotation), _ => ((double?)null, (double?)null, false)
            };
            var valid = FinitePair(x, y) && (row is not RoomSubroomRow subroom || subroom.Geometry.Width is > 0 and { } w && double.IsFinite(w) && subroom.Geometry.Height is > 0 and { } h && double.IsFinite(h));
            var metadata = row switch { RoomTransitionRow r => r.Metadata, RoomCheckRow r => r.Metadata, _ => null };
            var reset = metadata is not null && FinitePair(metadata.InGamePositionX, metadata.InGamePositionY);
            var aligned = reset && valid && x == metadata!.InGamePositionX && y == metadata.InGamePositionY;
            return new(scenePlacement is null, reset, valid, valid, true, scenePlacement is null ? "re-arm placement" : "placement is already armed",
                "reset annotation to game position", "clear annotation geometry", enabled ? "hide annotation" : "show annotation",
                scenePlacement is null ? "dismiss scene interaction" : "cancel placement", enabled, status, aligned);
        }
    }
    private bool OwnsScene(Guid room, long generation) => Shell.CanInteract && SceneContextReady && room == Opened.Identity.RoomId && generation == sceneOwnerGeneration;
    private static string SceneKind(RoomTableRow row) => row switch { RoomSubroomRow => "subroom", RoomTransitionRow => "exit", RoomConnectionRow => "connection", RoomCheckRow => "check", _ => throw new InvalidOperationException() };
    private static V2SceneSelectedItemKind SelectionKind(RoomTableRow row) => row switch { RoomSubroomRow => V2SceneSelectedItemKind.Subroom, RoomTransitionRow => V2SceneSelectedItemKind.Transition, RoomConnectionRow => V2SceneSelectedItemKind.Connection, RoomCheckRow => V2SceneSelectedItemKind.Check, _ => throw new InvalidOperationException() };
    private RoomTableRow SceneRow(string kind, Guid id) => CurrentPresentationRow(kind == "exit" ? "transition" : kind, id);
    private RoomTableRow? TrySceneRow(string kind, Guid id)
    {
        EnsurePresentation(); // Preparation faults are not a missing-selection result.
        try { return workspace.Find(kind == "exit" ? "transition" : kind, id); }
        catch (InvalidOperationException) { return null; }
    }
    private void TransferCanonicalConnectionSelection(RoomCompleteDocument before, RoomCompleteDocument after, HashSet<Guid> reordered)
    {
        if (sceneSelection is not { Kind: V2SceneSelectedItemKind.Connection } selected) return;
        var old = before.Connections.SingleOrDefault(x => x.Observation.EntityId == selected.EntityId && !x.Observation.IsArchived);
        if (old is null) return;
        bool SameAlias(RoomConnectionSnapshot x) => string.Equals(x.Values.Alias.Trim(), old.Values.Alias.Trim(), StringComparison.OrdinalIgnoreCase);
        if (!before.Connections.Any(x => !x.Observation.IsArchived && SameAlias(x) && reordered.Contains(x.Observation.EntityId))) return;
        var previous = before.Connections.Where(x => !x.Observation.IsArchived && SameAlias(x)).OrderBy(x => x.SortOrder).ThenBy(x => x.Observation.EntityId).FirstOrDefault();
        var canonical = after.Connections.Where(x => !x.Observation.IsArchived && SameAlias(x)).OrderBy(x => x.SortOrder).ThenBy(x => x.Observation.EntityId).FirstOrDefault();
        if (previous?.Observation.EntityId != selected.EntityId || canonical is null || canonical.Observation.EntityId == selected.EntityId) return;
        var id = canonical.Observation.EntityId;
        sceneSelection = selected with { EntityId = id };
        if (scenePlacement is { Kind: "connection" } request
            && (request.GroupMarkerEntityId ?? request.EntityId) == selected.EntityId)
        {
            var replacement = request with { EntityId = id, GroupMarkerEntityId = id };
            if (ReferenceEquals(consumedSceneFocus, request)) consumedSceneFocus = replacement;
            scenePlacement = replacement;
        }
    }
    private static RoomDirectAnnotation Annotation(RoomInGameMetadata metadata, bool enabled) =>
        new(metadata.AnnotationSceneUnitX, metadata.AnnotationSceneUnitY, enabled, metadata.InGamePositionX, metadata.InGamePositionY);
    private Task RequestRowAnnotationAsync(RoomTableRow row)
    {
        var transferred = TransferKeyboardBeforeAction();
        if (row.EntityId is { } target) row = SceneRow(SceneKind(row), target);
        if (AuthoringBlocked || !SceneRowReady(row) || row.EntityId is not { } id) return Task.CompletedTask;
        var kind = SceneKind(row);
        var scene = workspace.Queue.Confirmed.Document?.Scene;
        var rendered = row is RoomSubroomRow ? scene?.Frames.Any(x => x.EntityId == id) == true
            : scene?.Markers.Any(x => x.EntityId == (row is RoomConnectionRow connection ? connection.AnnotationMarkerId ?? id : id) && x.Kind == kind) == true;
        if (!transferred && !rendered) return Task.CompletedTask;
        hiddenSceneLayers.Remove(SceneLayer(SelectionKind(row)));
        sceneSelection = new(SelectionKind(row), row is RoomConnectionRow c ? c.AnnotationMarkerId ?? id : id);
        if (rendered) { scenePlacement = null; StateHasChanged(); return Task.CompletedTask; }
        // Canonical presentation never replaces the initiating row's raw origin.
        // Both crosshairs use the same geometry/visibility as diagnostics/marker.
        var annotationRow = row is RoomConnectionRow { AnnotationMarkerId: { } marker }
            ? CurrentPresentationRow("connection", marker) : row;
        var complete = annotationRow switch
        {
            RoomSubroomRow r => r.Geometry.X is { } x && double.IsFinite(x) && r.Geometry.Y is { } y && double.IsFinite(y)
                && r.Geometry.Width is > 0 and { } w && double.IsFinite(w) && r.Geometry.Height is > 0 and { } h && double.IsFinite(h),
            RoomTransitionRow r => !r.EnableAnnotation && FinitePair(r.Metadata.AnnotationSceneUnitX, r.Metadata.AnnotationSceneUnitY),
            RoomCheckRow r => !r.EnableAnnotation && FinitePair(r.Metadata.AnnotationSceneUnitX, r.Metadata.AnnotationSceneUnitY),
            RoomConnectionRow r => !r.EnableAnnotation && FinitePair(r.X, r.Y), _ => false
        };
        if (complete && (row is not RoomSubroomRow subroom || !subroom.Geometry.EnableAnnotation))
        {
            EnqueueSceneAnnotation(row, RoomDirectAnnotationAction.ShowAndSelect, null);
            return DrainAsync();
        }
        scenePlacement = new(kind, id, $"v2-{(kind == "exit" ? "transition" : kind)}-place-{id:D}", (row as RoomConnectionRow)?.AnnotationMarkerId);
        StateHasChanged(); return Task.CompletedTask;
    }
    private static bool FinitePair(double? x, double? y) => x is { } a && y is { } b && double.IsFinite(a) && double.IsFinite(b);
    private void EnqueueSceneAnnotation(RoomTableRow row, RoomDirectAnnotationAction action, RoomAnnotationPoint? point)
    {
        var id = row.EntityId ?? throw new InvalidOperationException();
        RoomEditOperation operation = row switch
        {
            RoomSubroomRow r => new ChangeRoomSubroomAnnotation(id, RowOrigin(r, r.Geometry), action switch
            { RoomDirectAnnotationAction.Clear => RoomSubroomAnnotationAction.Clear, RoomDirectAnnotationAction.Remove => RoomSubroomAnnotationAction.Hide,
                RoomDirectAnnotationAction.Show or RoomDirectAnnotationAction.ShowAndSelect => RoomSubroomAnnotationAction.Show, _ => throw new InvalidOperationException() }),
            RoomTransitionRow r => new ChangeRoomTransitionAnnotation(id, RowOrigin(r, Annotation(r.Metadata, r.EnableAnnotation)), action, point),
            RoomCheckRow r => new ChangeRoomCheckAnnotation(id, RowOrigin(r, Annotation(r.Metadata, r.EnableAnnotation)), action, point),
            RoomConnectionRow r => new ChangeRoomConnectionAnnotation(id, ConnectionOrigin(r), action switch
            {
                RoomDirectAnnotationAction.Place => RoomConnectionAnnotationAction.Place, RoomDirectAnnotationAction.Move => RoomConnectionAnnotationAction.Move,
                RoomDirectAnnotationAction.Remove => RoomConnectionAnnotationAction.Disable, RoomDirectAnnotationAction.Clear => RoomConnectionAnnotationAction.Clear,
                RoomDirectAnnotationAction.Show => RoomConnectionAnnotationAction.Show, RoomDirectAnnotationAction.ShowAndSelect => RoomConnectionAnnotationAction.ShowAndSelect,
                _ => throw new InvalidOperationException("Connections have no game-position reset.")
            }, point),
            _ => throw new InvalidOperationException()
        };
        workspace.Enqueue(operation); StateHasChanged();
    }
    private RoomConnectionAnnotationOrigin ConnectionOrigin(RoomConnectionRow row)
    {
        var document = workspace.Queue.Confirmed.Document ?? throw new InvalidOperationException();
        var group = workspace.Connections.Where(x => !x.IsArchived && x.EntityId is not null && string.Equals(x.Values.Alias.Trim(), row.Values.Alias.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        var members = group.Select(x => new RoomConnectionAnnotationMember(x.EntityId!.Value, x.Values.Alias,
            document.Connections.SingleOrDefault(c => c.Observation.EntityId == x.EntityId)?.SortOrder ?? workspace.Placement(x).Index,
            x.IsArchived, x.X, x.Y, x.EnableAnnotation, x.LastConfirmed)).OrderBy(x => x.SortOrder).ThenBy(x => x.EntityId).ToImmutableArray();
        // Canonical identity follows projected manual placement. Member SortOrder
        // stays the original raw observation; never normalize concurrency evidence.
        return new(members, group[0].EntityId!.Value, group.SelectMany(x => x.Dependencies).Distinct().ToImmutableArray());
    }
    private Task SceneMountedAsync(Guid room, long generation)
    {
        if (Current && !Archived && !externalRoomDeleted && showMapContext && room == Entry.RoomId && generation > sceneOwnerGeneration) { sceneOwnerGeneration = generation; hiddenSceneLayers.Clear(); ObserveInitialSceneOwner(generation); }
        return Task.CompletedTask;
    }
    private Task SceneReadyAsync(Guid room, long generation)
    {
        if (OwnsScene(room, generation)) { TryCompleteInitialReadiness(); StateHasChanged(); }
        return Task.CompletedTask;
    }
    private bool TakeSceneFocus(Guid room, long generation, SceneAnnotationPlacementRequest request)
    {
        if (!OwnsScene(room, generation) || !ReferenceEquals(scenePlacement, request) || ReferenceEquals(consumedSceneFocus, request)) return false;
        consumedSceneFocus = request; return true;
    }
    private Task SceneSelectionAsync(V2SceneSelectionCallback callback)
    {
        if (!OwnsScene(callback.RoomId, callback.OwnerGeneration)) return Task.CompletedTask;
        TransferKeyboardBeforeAction();
        if (OwnsScene(callback.RoomId, callback.OwnerGeneration) && (callback.Selection is null || !hiddenSceneLayers.Contains(SceneLayer(callback.Selection.Kind)))) { sceneSelection = callback.Selection; scenePlacement = null; StateHasChanged(); }
        return Task.CompletedTask;
    }
    private Task<bool> ScenePlacementAsync(Guid room, long generation, SceneAnnotationPlacementRequest request, double x, double y)
    {
        if (!OwnsScene(room, generation)) return Task.FromResult(false);
        if (!TransferKeyboardBeforeAction()) return Task.FromResult(false);
        if (!OwnsScene(room, generation) || AuthoringBlocked || !ReferenceEquals(request, scenePlacement) || hiddenSceneLayers.Contains(SceneLayer(request.Kind)) || TrySceneRow(request.Kind, request.EntityId) is not { } row || !SceneRowReady(row) || !FinitePair(x, y)) return Task.FromResult(false);
        EnqueueSceneAnnotation(SceneRow(request.Kind, request.EntityId), RoomDirectAnnotationAction.Place, new(x, y));
        scenePlacement = null; _ = DrainAsync(); return Task.FromResult(true);
    }
    private Task<bool> SceneCancelPlacementAsync(Guid room, long generation, SceneAnnotationPlacementRequest request)
    {
        if (!OwnsScene(room, generation) || !ReferenceEquals(request, scenePlacement)) return Task.FromResult(false);
        scenePlacement = null;
        if (nativeOwner is not null) pendingBoundaryFocus = (request.InitiatorId, nativeOwner.Invoke<long>("roomInteractionGeneration"));
        StateHasChanged(); return Task.FromResult(true);
    }
    private Task<bool> SceneMoveAsync(Guid room, long generation, string kind, Guid id, double x, double y)
    {
        if (!OwnsScene(room, generation)) return Task.FromResult(false);
        if (!TransferKeyboardBeforeAction()) return Task.FromResult(false);
        if (!OwnsScene(room, generation) || AuthoringBlocked || hiddenSceneLayers.Contains(SceneLayer(kind)) || TrySceneRow(kind, id) is not { } row || !SceneRowReady(row) || !FinitePair(x, y)) return Task.FromResult(false);
        if (kind == "subroom")
        {
            var subroom = (RoomSubroomRow)SceneRow(kind, id);
            workspace.Enqueue(new SaveRoomSubroomGeometry(id, RowOrigin(subroom, subroom.Geometry), new(x, y, subroom.Geometry.Width, subroom.Geometry.Height)));
        }
        else EnqueueSceneAnnotation(SceneRow(kind, id), RoomDirectAnnotationAction.Move, new(x, y));
        StateHasChanged(); _ = DrainAsync(); return Task.FromResult(true);
    }
    private Task<bool> SceneRectangleAsync(Guid room, long generation, Guid id, double x, double y, double width, double height)
    {
        if (!OwnsScene(room, generation)) return Task.FromResult(false);
        if (!TransferKeyboardBeforeAction()) return Task.FromResult(false);
        if (!OwnsScene(room, generation) || AuthoringBlocked || hiddenSceneLayers.Contains(V2SceneSelectedItemKind.Subroom) || TrySceneRow("subroom", id) is not { } target || !SceneRowReady(target) || !FinitePair(x, y) || !FinitePair(width, height) || width <= 0 || height <= 0) return Task.FromResult(false);
        var row = (RoomSubroomRow)SceneRow("subroom", id);
        workspace.Enqueue(new SaveRoomSubroomGeometry(id, RowOrigin(row, row.Geometry), new(x, y, width, height)));
        scenePlacement = null; StateHasChanged(); _ = DrainAsync(); return Task.FromResult(true);
    }
    private Task SceneActionAsync(Guid room, long generation, V2SceneAction action)
    {
        if (!OwnsScene(room, generation)) return Task.CompletedTask;
        var transferred = TransferKeyboardBeforeAction();
        if (!transferred && action != V2SceneAction.Dismiss) return Task.CompletedTask;
        if (!OwnsScene(room, generation) || AuthoringBlocked || sceneSelection is not { } selected) return Task.CompletedTask;
        if (action == V2SceneAction.Dismiss) { if (scenePlacement is not null) scenePlacement = null; else sceneSelection = null; StateHasChanged(); return Task.CompletedTask; }
        var row = TrySceneRow(selected.Kind == V2SceneSelectedItemKind.Transition ? "exit" : selected.Kind.ToString().ToLowerInvariant(), selected.EntityId);
        if (row is null || !SceneRowReady(row)) return Task.CompletedTask;
        if (action == V2SceneAction.Rearm)
        { hiddenSceneLayers.Remove(SceneLayer(selected.Kind)); scenePlacement = new(SceneKind(row), row.EntityId!.Value, "scene-action-box", (row as RoomConnectionRow)?.AnnotationMarkerId); StateHasChanged(); return Task.CompletedTask; }
        var enabled = row switch { RoomSubroomRow r => r.Geometry.EnableAnnotation, RoomTransitionRow r => r.EnableAnnotation, RoomCheckRow r => r.EnableAnnotation, RoomConnectionRow r => r.EnableAnnotation, _ => false };
        EnqueueSceneAnnotation(row, action switch
        { V2SceneAction.Reset => RoomDirectAnnotationAction.Reset, V2SceneAction.Clear => RoomDirectAnnotationAction.Clear,
            V2SceneAction.ToggleVisibility => enabled ? RoomDirectAnnotationAction.Remove : RoomDirectAnnotationAction.Show, _ => throw new InvalidOperationException() }, null);
        return DrainAsync();
    }
}
