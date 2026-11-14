using System.Collections.Immutable;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Client.RoomEditing;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    private sealed record LocalModal(RoomRowAction Action, RoomTableRow? Row, RoomRowObservation? Room, string Name);
    private sealed record Coordinate(string Field, string Value, bool Valid);
    private LocalModal? localModal;
    private ElementReference localDialog;
    private bool localModalPending;
    private string? localReturnFocus;
    private string? localFeedback;
    private readonly Dictionary<string, string> coordinateDraft = [];
    private readonly Dictionary<string, string> coordinateInitial = [];
    private static readonly string[] GeometryFields = ["X", "Y", "width", "height"];
    private static readonly string[] MetadataFields = ["game ID", "in-game X", "in-game Y", "in-game Z", "local X", "local Y", "local Z", "annotation X", "annotation Y"];
    private static string CoordinateLabel(LocalModal modal, string field) => modal.Row is RoomCheckRow && field is "annotation X" or "annotation Y"
        ? field.Replace("annotation ", "annotation scene ", StringComparison.Ordinal) : field;
    private static RoomRowOrigin<T> RowOrigin<T>(RoomTableRow row, T values) => new(row.Dependencies.IsEmpty ? RoomOriginKind.Observed : RoomOriginKind.Projected,
        values, row.LastConfirmed, row.Dependencies);
    private Task OpenRowActionAsync(RoomTableRow row, RoomRowAction action)
    {
        if (action == RoomRowAction.FollowInverse && row is RoomTransitionRow { InverseRoomId: { } target })
        {
            var client = row.ClientId;
            var entity = row.EntityId;
            var values = ((RoomTransitionRow)row).Values;
            Shell.NavigateRoomAction(this, $"/rooms/{target:D}", () =>
            {
                if (!Current) return false;
                EnsurePresentation();
                return workspace.Transitions.SingleOrDefault(x => x.ClientId == client) is { IsArchived: false } current
                    && current.Values == values && current.InverseRoomId == target
                    && Catalogue.Rooms.TryGetValue(target, out var destination) && !destination.Header.Observation.IsArchived
                    && RoomPresentationRules.Same(values.DestinationRoomReferenceText, destination.Header.Values.ReferenceId)
                    && Catalogue.Diagnostics.Destinations.TryGetValue(target, out var lookup)
                    && lookup.ById.Values.Count(x => RoomPresentationRules.Same(x.Values.Alias, values.DestinationTransitionAliasText)
                        && x.ResolvedDestinationRoomId == Entry.RoomId && x.ResolvedDestinationTransitionId == entity) == 1;
            });
            return Task.CompletedTask;
        }
        if (action == RoomRowAction.Inverse)
        {
            if (row is RoomTransitionRow transition)
            {
                var operation = workspace.Enqueue(new RequestRoomTransitionInverse(transition.EntityId!.Value, RowOrigin(transition, transition.Values)));
                operationInitiators[operation.OperationId] = $"v2-transition-inverse-{transition.EntityId:D}";
            }
            else if (row is RoomConnectionRow connection) workspace.Enqueue(new ScaffoldRoomConnectionInverse(connection.EntityId!.Value, Guid.NewGuid(), RowOrigin(connection, connection.Values)));
            else throw new InvalidOperationException("This row has no inverse workflow.");
            return DrainAsync();
        }
        if (action == RoomRowAction.Annotation) return RequestRowAnnotationAsync(row);
        if (action is not (RoomRowAction.Geometry or RoomRowAction.Metadata or RoomRowAction.Delete)) throw new InvalidOperationException("Unrecognized room action.");
        coordinateDraft.Clear(); localFeedback = null;
        if (action == RoomRowAction.Geometry && row is RoomSubroomRow subroom)
        {
            var values = new[] { subroom.Geometry.X, subroom.Geometry.Y, subroom.Geometry.Width, subroom.Geometry.Height };
            for (var i = 0; i < GeometryFields.Length; i++) coordinateDraft[GeometryFields[i]] = Number(values[i]);
        }
        else if (action == RoomRowAction.Metadata)
        {
            var metadata = row switch { RoomTransitionRow r => r.Metadata, RoomCheckRow r => r.Metadata, _ => throw new InvalidOperationException() };
            coordinateDraft[MetadataFields[0]] = metadata.InGameId ?? "";
            double?[] numbers = [metadata.InGamePositionX, metadata.InGamePositionY, metadata.InGamePositionZ, metadata.LocalPositionX, metadata.LocalPositionY, metadata.LocalPositionZ, metadata.AnnotationSceneUnitX, metadata.AnnotationSceneUnitY];
            for (var i = 0; i < numbers.Length; i++) coordinateDraft[MetadataFields[i + 1]] = Number(numbers[i]);
        }
        coordinateInitial.Clear(); foreach (var pair in coordinateDraft) coordinateInitial.Add(pair.Key, pair.Value);
        var kind = row switch { RoomSubroomRow => "subroom", RoomTransitionRow => "transition", RoomConnectionRow => "connection", RoomCheckRow => "check", _ => throw new InvalidOperationException() };
        localReturnFocus = $"v2-{kind}-{(action == RoomRowAction.Geometry ? "geometry" : action == RoomRowAction.Metadata ? "metadata" : "open-delete")}-{row.EntityId:D}";
        localModal = new(action, row, null, row switch { RoomSubroomRow r => r.Values.FriendlyName, RoomTransitionRow r => r.Values.FriendlyName, RoomConnectionRow r => r.Values.FriendlyName, RoomCheckRow r => r.Values.FriendlyName, _ => throw new InvalidOperationException() }); localModalPending = true; StateHasChanged();
        return Task.CompletedTask;
    }
    private void OpenRoomDelete()
    {
        if (!TransferKeyboardBeforeAction()) return;
        if (AuthoringBlocked || !Archived || workspace.Queue.Confirmed.Document is not { } document) return;
        localReturnFocus = "room-permanent-delete";
        localModal = new(RoomRowAction.Delete, null, document.Header.Observation, workspace.Header.FriendlyName); localModalPending = true; localFeedback = null;
    }
    private Task CloseLocalModalAsync()
    {
        if (!Shell.CanInteract || localModal is null || controlPending || Shell.GeneralErrorOpen) return Task.CompletedTask;
        QueueLocalReturnFocus();
        localModal = null; localFeedback = null; coordinateDraft.Clear(); StateHasChanged();
        return Shell.DrainRoomRefreshAsync();
    }
    private Task LocalModalKeyDownAsync(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs args) => args.Key == "Escape" ? CloseLocalModalAsync() : Task.CompletedTask;
    private async Task RevertCheckAnnotationAsync()
    {
        if (!Current || controlPending || !Shell.CanUseRoom || nativeOwner is null || localModal is not { Action: RoomRowAction.Metadata, Row: RoomCheckRow }) return;
        try
        {
            // Clear actual native controls as well as their draft/render frames:
            // an unchanged initial empty value cannot drive a Razor value diff.
            nativeOwner.InvokeVoid("clearRoomMetadataAnnotation", localDialog);
            foreach (var field in MetadataFields[^2..]) coordinateDraft[field] = coordinateInitial[field] = "";
            StateHasChanged();
        }
        catch (Exception exception)
        {
            if (Current) await Shell.ReportRoomErrorAsync(exception, "The annotation draft could not be cleared. No metadata save was requested.");
        }
    }
    private async Task ApplyLocalModalAsync()
    {
        try { await ApplyLocalModalCoreAsync(); }
        catch (Exception exception)
        {
            if (Current) await Shell.ReportRoomErrorAsync(exception, "The room dialog could not be applied. Native values and any accepted queued operation have been retained; do not repeat a saved operation.");
        }
    }
    private async Task ApplyLocalModalCoreAsync()
    {
        if (!Current || controlPending || !Shell.CanUseRoom || nativeOwner is null || localModal is not { } modal) return;
        if (!TransferKeyboardBeforeAction()) return;
        var row = modal.Row;
        RoomEditOperation operation;
        if (modal.Action == RoomRowAction.Delete)
        {
            operation = row switch
            {
                RoomSubroomRow r when r.LastConfirmed is { IsArchived: true } observation => new DeleteRoomSubroom(observation),
                RoomTransitionRow r when r.LastConfirmed is { IsArchived: true } observation => new DeleteRoomTransition(observation),
                RoomConnectionRow r when r.LastConfirmed is { IsArchived: true } observation => new DeleteRoomConnection(observation),
                RoomCheckRow r when r.LastConfirmed is { IsArchived: true } observation => new DeleteRoomCheck(observation),
                null when modal.Room is { IsArchived: true } observation => new DeleteRoom(observation),
                _ => throw new InvalidOperationException("Permanent deletion requires a confirmed archived receiver.")
            };
        }
        else
        {
            var native = nativeOwner.Invoke<Coordinate[]>("readRoomCoordinates", localDialog);
            if (native.Any(x => !x.Valid)) { localFeedback = "Enter complete finite numbers, or leave optional coordinates blank."; return; }
            foreach (var input in native) coordinateDraft[input.Field] = input.Value;
            var names = modal.Action == RoomRowAction.Geometry ? GeometryFields : MetadataFields[1..];
            var values = new List<double?>();
            foreach (var name in names)
            {
                var text = coordinateDraft[name];
                if (string.IsNullOrWhiteSpace(text)) values.Add(null);
                else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) values.Add(number);
                else { localFeedback = "Enter complete finite numbers, or leave optional coordinates blank."; return; }
            }
            if (row is RoomSubroomRow subroom && modal.Action == RoomRowAction.Geometry)
            {
                if (Silksong_Rando_Logic_Manager.Services.RoomPresentationRules.Rectangle(values[0], values[1], values[2], values[3]) == Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts.V2Severity.Danger)
                { localFeedback = "Subroom geometry must be all absent or complete, finite, and positive."; return; }
                operation = new SaveRoomSubroomGeometry(subroom.EntityId!.Value, RowOrigin(subroom, subroom.Geometry), new(values[0], values[1], values[2], values[3]));
            }
            else
            {
                var originalMetadata = row switch { RoomTransitionRow r => r.Metadata, RoomCheckRow r => r.Metadata, _ => throw new InvalidOperationException() };
                var gameId = coordinateDraft[MetadataFields[0]] == (originalMetadata.InGameId ?? "") ? originalMetadata.InGameId : coordinateDraft[MetadataFields[0]];
                var submitted = new RoomInGameMetadata(gameId, values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]);
                operation = row switch
                {
                    RoomTransitionRow r => new SaveRoomTransitionMetadata(r.EntityId!.Value, RowOrigin(r, r.Metadata), submitted),
                    RoomCheckRow r => new SaveRoomCheckMetadata(r.EntityId!.Value, RowOrigin(r, r.Metadata), submitted),
                    _ => throw new InvalidOperationException()
                };
            }
        }
        // Freeze and enqueue before modal-close interop. Closing is presentation;
        // failure cannot erase the accepted intent or create a second operation.
        var queued = workspace.Enqueue(operation);
        if (localReturnFocus is { } target)
        {
            operationInitiators[queued.OperationId] = target;
            operationReturnFocus[queued.OperationId] = (target, nativeOwner.Invoke<long>("roomInteractionGeneration"));
        }
        localReturnFocus = null;
        localModal = null; coordinateDraft.Clear(); localFeedback = null;
        StateHasChanged(); await DrainAsync();
    }
    private static string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    private static string ModalKind(LocalModal modal) => modal.Row switch { RoomSubroomRow => "subroom", RoomTransitionRow => "transition", RoomConnectionRow => "connection", RoomCheckRow => "check", _ => "room" };
    private string MetadataGameClass => localModal?.Row is RoomTransitionRow row && Silksong_Rando_Logic_Manager.Services.RoomPresentationRules.Text(row.Metadata.InGameId)
        && workspace.Transitions.Any(other => other.EntityId != row.EntityId && !other.IsArchived && Silksong_Rando_Logic_Manager.Services.RoomPresentationRules.Same(other.Metadata.InGameId, row.Metadata.InGameId)) ? "danger" : "";
    private void QueueLocalReturnFocus()
    {
        if (nativeOwner is not null && localReturnFocus is { } target)
            pendingBoundaryFocus = (target, nativeOwner.Invoke<long>("roomInteractionGeneration"));
        localReturnFocus = null;
    }
}
