using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SilksongRandoLogicManager.Client.RoomEditing;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    [Inject] private HttpClient HistoryHttp { get; set; } = default!;
    private sealed class HistoryOpening(RoomRowActionIntent? intent, Guid entity, EditorHistoryFamily family, string kind,
        string name, CancellationTokenSource read)
    {
        public string FocusKey { get; } = Guid.NewGuid().ToString("N");
        public RoomRowActionIntent? Intent { get; } = intent;
        public Guid Entity { get; } = entity;
        public EditorHistoryFamily Family { get; } = family;
        public string Kind { get; } = kind;
        public string Name { get; } = name;
        public CancellationTokenSource? Read { get; set; } = read;
        public bool FocusPending { get; set; } = true;
        public bool Unavailable { get; set; }
        public IReadOnlyList<EditorHistoryRevision>? Revisions { get; set; }
        public IReadOnlyDictionary<long, RoomHistoryDialog.LocalTime> Times { get; set; } = new Dictionary<long, RoomHistoryDialog.LocalTime>();
    }
    private HistoryOpening? historyOpening;
    private long? historyReceiverLossFocus;
    private bool HistoryCanOwn => !InspectionBlocked && nativeOwner is not null && !Shell.RoomSwitchPending
        && !externalRoomDeleted && !workspace.Queue.RecoveryRequired && !entryAdmissionUnavailable;
    private bool HistoryLauncherBlocked => historyOpening is not null || !HistoryCanOwn;
    private bool HistoryBackgroundInert => historyOpening is { FocusPending: false };
    private Guid? ConfirmedHistoryRoom => workspace.Confirmed.Document?.Header.Observation.EntityId == Entry.RoomId ? Entry.RoomId : null;

    private RoomTableRow? FindHistoryRow(RoomRowActionIntent intent)
    {
        IEnumerable<RoomTableRow> rows = intent.Section switch
        {
            RoomOrderSection.Subrooms => workspace.Subrooms,
            RoomOrderSection.Transitions => workspace.Transitions,
            RoomOrderSection.Connections => workspace.Connections,
            RoomOrderSection.Checks => workspace.CheckLocations.Active.Concat(workspace.CheckLocations.Archived),
            RoomOrderSection.Enemies => workspace.Enemies.Active.Concat(workspace.Enemies.Archived),
            _ => []
        };
        return rows.SingleOrDefault(row => row.ClientId == intent.ClientId && (!row.IsArchived || showArchived)
            && row.EntityId is { } id && row.LastConfirmed?.EntityId == id);
    }

    private bool HasHistoryReceiver(HistoryOpening opening) =>
        (opening.Intent is { } intent ? FindHistoryRow(intent)?.EntityId : ConfirmedHistoryRoom) == opening.Entity;

    private bool OwnsHistory(HistoryOpening opening) => ReferenceEquals(opening, historyOpening) && HistoryCanOwn
        && HasHistoryReceiver(opening);

    private Task OpenRoomDetailsHistoryAsync()
    {
        if (HistoryLauncherBlocked || ConfirmedHistoryRoom is not { } entity) return Task.CompletedTask;
        return OpenHistoryReadAsync(null, entity, EditorHistoryFamily.Room, "Room details history", workspace.Confirmed.Document!.Header.Values.FriendlyName);
    }

    private Task OpenHistoryAsync(RoomRowActionIntent intent)
    {
        // Branch before any authoring action admission or keyboard-geometry transfer.
        if (HistoryLauncherBlocked) return Task.CompletedTask;
        EnsurePresentation();
        if (FindHistoryRow(intent) is not { EntityId: { } entity } row) return Task.CompletedTask;
        var (family, kind, name) = row switch
        {
            RoomSubroomRow r => (EditorHistoryFamily.Subroom, "Subroom", r.Values.FriendlyName),
            RoomTransitionRow r => (EditorHistoryFamily.Transition, "Transition", r.Values.FriendlyName),
            RoomConnectionRow r => (EditorHistoryFamily.Connection, "Connection", r.Values.FriendlyName),
            RoomCheckRow r => (EditorHistoryFamily.Check, r.Enemies ? "Enemy" : "Check location", r.Values.FriendlyName),
            _ => throw new InvalidOperationException("Unsupported row history family.")
        };
        return OpenHistoryReadAsync(intent, entity, family, $"History · {kind}", name);
    }

    private async Task OpenHistoryReadAsync(RoomRowActionIntent? intent, Guid entity, EditorHistoryFamily family, string kind, string name)
    {
        using var read = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var opening = new HistoryOpening(intent, entity, family, kind, name, read);
        historyOpening = opening;
        StateHasChanged();
        try
        {
            var assembled = new List<EditorHistoryRevision>();
            EditorHistoryCursor? cursor = null;
            do
            {
                using var response = await HistoryHttp.PostAsJsonAsync("/api/v3/history/editor-record", new EditorHistoryRequest(family, entity, cursor), read.Token);
                response.EnsureSuccessStatusCode();
                var page = await response.Content.ReadFromJsonAsync<EditorHistoryPage>(read.Token)
                    ?? throw new JsonException("Missing editor history response.");
                if (!HistoryStillOwned(opening)) return;
                if (page.Status != EditorHistoryStatus.Available) { opening.Unavailable = true; return; }
                if (cursor is not null && page.Revisions.Length == 0)
                    throw new JsonException("A history continuation lost its expected older version.");
                var last = assembled.LastOrDefault();
                foreach (var revision in page.Revisions)
                {
                    if (revision.RowId <= 0 || revision.ChangeUtc.Kind != DateTimeKind.Utc || last is not null
                        && (revision.ChangeUtc > last.ChangeUtc || revision.ChangeUtc == last.ChangeUtc && revision.RowId >= last.RowId))
                        throw new JsonException("Invalid editor history chronology.");
                    assembled.Add(revision);
                    last = revision;
                }
                if (page.Next is { } next && (page.Revisions.Length == 0 || next.BeforeUtc != last!.ChangeUtc
                    || next.BeforeRowId != last.RowId || next.Ceiling < last.RowId || cursor is not null && next.Ceiling != cursor.Ceiling))
                    throw new JsonException("Invalid editor history continuation.");
                cursor = page.Next;
            } while (cursor is not null);
            if (assembled.Count > 0)
            {
                RoomHistoryDialog.LocalTime[] stamps;
                await using (var formatter = await ApplicationModuleLoader.ImportAsync(JS, Runtime, "room-history-time.js"))
                {
                    if (!HistoryStillOwned(opening)) return;
                    var instants = assembled.Select(revision => new DateTimeOffset(revision.ChangeUtc).ToUnixTimeMilliseconds()).ToArray();
                    stamps = await formatter.InvokeAsync<RoomHistoryDialog.LocalTime[]>("formatHistoryTimes", read.Token, (object)instants);
                }
                if (!HistoryStillOwned(opening)) return;
                if (stamps.Length != assembled.Count) throw new JsonException("Invalid browser history time projection.");
                opening.Times = assembled.Select((revision, index) => (revision.RowId, Stamp: stamps[index]))
                    .ToDictionary(item => item.RowId, item => item.Stamp);
            }
            opening.Revisions = assembled;
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { }
        catch (SessionAccessException exception)
        {
            if (HistoryStillOwned(opening, restoreOnReceiverLoss: false))
            {
                RetireHistory();
                await Shell.ReportRoomErrorAsync(exception, "Room access changed while reading history.");
            }
        }
        catch (Exception exception)
        {
            if (HistoryStillOwned(opening)) { Console.Error.WriteLine(exception); opening.Unavailable = true; }
        }
        finally
        {
            opening.Read = null;
            if (Current) StateHasChanged();
        }
    }

    private bool HistoryStillOwned(HistoryOpening opening, bool restoreOnReceiverLoss = true)
    {
        if (!ReferenceEquals(opening, historyOpening)) return false;
        long? interaction = null;
        if (HistoryCanOwn)
        {
            EnsurePresentation();
            if (HasHistoryReceiver(opening)) return true;
            // The still-mounted DOM is the only evidence of actual focus in this opening.
            if (restoreOnReceiverLoss && !Shell.GeneralErrorOpen && nativeOwner is { } owner)
                interaction = owner.Invoke<long?>("focusedRoomHistoryInteraction", opening.FocusKey);
        }
        RetireHistory();
        historyReceiverLossFocus = interaction;
        return false;
    }

    private Task CloseHistoryAsync(HistoryOpening opening)
    {
        if (!Current || Shell.GeneralErrorOpen || !ReferenceEquals(historyOpening, opening)) return Task.CompletedTask;
        RetireHistory();
        if (nativeOwner is not null)
        {
            var interaction = nativeOwner.Invoke<long>("roomInteractionGeneration");
            var target = opening.Intent is { } intent ? $"v3-row-history-{intent.Section}-{opening.Entity:D}" : "room-details-history";
            pendingBoundaryFocus = (target, interaction);
            returnFocusFallback = (target, "room-syntax-guide", interaction);
        }
        StateHasChanged();
        return Shell.DrainRoomRefreshAsync();
    }

    private void RetireHistory()
    {
        var opening = historyOpening;
        historyOpening = null;
        historyReceiverLossFocus = null;
        opening?.Read?.Cancel();
    }

    private void RestoreHistoryReceiverLossFocus()
    {
        var interaction = historyReceiverLossFocus;
        historyReceiverLossFocus = null;
        if (interaction is { } captured && historyOpening is null && HistoryCanOwn && !Shell.GeneralErrorOpen
            && !focusComments && pendingBoundaryFocus is null && nativeOwner is { } owner)
            owner.Invoke<bool>("focusRoomBoundary", "room-syntax-guide", captured);
    }

    private void FocusHistory()
    {
        if (historyOpening is { FocusPending: true } opening && OwnsHistory(opening)
            && Shell.FocusRoomDialog("v3-room-history-close"))
        {
            // Native focusout must see the previous enabled/editable DOM. Only
            // after focus has moved may a subsequent render make it inert.
            opening.FocusPending = false;
            StateHasChanged();
        }
    }

    private void CycleHistoryFocus(HistoryOpening opening, string target)
    {
        if (OwnsHistory(opening)) Shell.FocusRoomDialog(target);
    }
}
