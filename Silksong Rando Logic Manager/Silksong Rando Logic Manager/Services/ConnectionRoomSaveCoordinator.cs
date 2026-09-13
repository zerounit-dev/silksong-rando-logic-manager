using System.Collections.Concurrent;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>
/// Coordinates every persisted connection write visible in one room document.
/// The key is deliberately the displayed room, not an alias: an alias edit can
/// split or merge the group which an annotation mutation affects.
/// </summary>
public sealed class ConnectionRoomSaveCoordinator(LogicCatalogService catalog, Func<Task>? beforeWrite = null)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> roomGates = [];
    // Compatibility registration is retained for non-editor callers while the
    // editor always supplies its immutable coordinator baseline explicitly.
    private readonly ConcurrentDictionary<Guid, SubroomConnection> registeredBaselines = [];
    public event Action<ConnectionSavePatch>? PatchCommitted;

    public void RegisterRows(IEnumerable<SubroomConnection> rows)
    {
        foreach (var row in rows) registeredBaselines[row.Id] = Clone(row);
    }

    public Task<ChildRowSavePatch<SubroomConnection>> SaveAsync(SubroomConnection draft, Guid displayedRoomId, CancellationToken cancellationToken = default)
    {
        var baseline = registeredBaselines.GetOrAdd(draft.Id, _ => Clone(draft));
        return SaveAsync(Clone(baseline), draft, displayedRoomId, cancellationToken);
    }

    public async Task<ChildRowSavePatch<SubroomConnection>> SaveAsync(SubroomConnection baseline, SubroomConnection draft, Guid displayedRoomId, CancellationToken cancellationToken = default)
    {
        await using var lease = await EnterAsync(displayedRoomId, cancellationToken);
        if (beforeWrite is not null) await beforeWrite();
        var patch = await catalog.SaveConnectionWithPatchAsync(baseline, draft, displayedRoomId, cancellationToken);
        if (patch.SavedRow is not null) registeredBaselines[patch.SavedRow.Id] = Clone(patch.SavedRow);
        Publish(new ConnectionSavePatch(displayedRoomId, patch.AffectedRows, patch.Diagnostics, patch.Outcome, patch.SavedRow));
        return patch;
    }

    public async Task<ConnectionSavePatch> AddAnnotationAsync(Guid connectionId, Guid displayedRoomId, CancellationToken cancellationToken = default)
        => await AnnotateAsync(connectionId, displayedRoomId, catalog.AddConnectionAnnotationAsync, cancellationToken);

    public async Task<ConnectionSavePatch> RemoveAnnotationAsync(Guid connectionId, Guid displayedRoomId, CancellationToken cancellationToken = default)
        => await AnnotateAsync(connectionId, displayedRoomId, catalog.RemoveConnectionAnnotationAsync, cancellationToken);

    public async Task<ConnectionSavePatch> ShowAnnotationAsync(Guid connectionId, Guid displayedRoomId, CancellationToken cancellationToken = default)
        => await AnnotateAsync(connectionId, displayedRoomId, catalog.ShowConnectionAnnotationAsync, cancellationToken);

    public async Task<ConnectionSavePatch> MoveAnnotationAsync(Guid connectionId, Guid displayedRoomId, double x, double y, CancellationToken cancellationToken = default)
    {
        await using var lease = await EnterAsync(displayedRoomId, cancellationToken);
        if (beforeWrite is not null) await beforeWrite();
        var rows = await catalog.MoveConnectionAnnotationAsync(displayedRoomId, connectionId, x, y, cancellationToken);
        var patch = new ConnectionSavePatch(displayedRoomId, rows, [], CatalogSaveOutcome.Unchanged, null);
        Publish(patch);
        return patch;
    }

    public async Task<ConnectionSavePatch> PlaceAnnotationAsync(Guid connectionId, Guid displayedRoomId, double x, double y, CancellationToken cancellationToken = default)
    {
        await using var lease = await EnterAsync(displayedRoomId, cancellationToken);
        if (beforeWrite is not null) await beforeWrite();
        var rows = await catalog.PlaceConnectionAnnotationAsync(connectionId, x, y, cancellationToken);
        var patch = new ConnectionSavePatch(displayedRoomId, rows, [], CatalogSaveOutcome.Unchanged, null);
        Publish(patch);
        return patch;
    }

    public async Task<ConnectionSavePatch> ClearAnnotationAsync(Guid connectionId, Guid displayedRoomId, CancellationToken cancellationToken = default)
        => await AnnotateAsync(connectionId, displayedRoomId, catalog.ClearConnectionAnnotationAsync, cancellationToken);

    private async Task<ConnectionSavePatch> AnnotateAsync(Guid connectionId, Guid displayedRoomId, Func<Guid, CancellationToken, Task<IReadOnlyList<SubroomConnection>>> action, CancellationToken cancellationToken)
    {
        await using var lease = await EnterAsync(displayedRoomId, cancellationToken);
        if (beforeWrite is not null) await beforeWrite();
        var rows = await action(connectionId, cancellationToken);
        var patch = new ConnectionSavePatch(displayedRoomId, rows, [], CatalogSaveOutcome.Unchanged, null);
        Publish(patch);
        return patch;
    }

    private async Task<IAsyncDisposable> EnterAsync(Guid roomId, CancellationToken cancellationToken)
    {
        var gate = roomGates.GetOrAdd(roomId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new GateLease(gate);
    }

    private void Publish(ConnectionSavePatch patch)
    {
        PatchCommitted?.Invoke(patch);
    }

    private static SubroomConnection Clone(SubroomConnection value) => new()
    {
        Id = value.Id, RoomId = value.RoomId, Alias = value.Alias, FriendlyName = value.FriendlyName,
        SourceSubroomReferenceText = value.SourceSubroomReferenceText, DestinationSubroomReferenceText = value.DestinationSubroomReferenceText,
        Requirements = value.Requirements, Notes = value.Notes, EnableAnnotation = value.EnableAnnotation,
        SceneUnitX = value.SceneUnitX, SceneUnitY = value.SceneUnitY, ResolvedSourceSubroomId = value.ResolvedSourceSubroomId,
        ResolvedDestinationSubroomId = value.ResolvedDestinationSubroomId, SortOrder = value.SortOrder, IsTodo = value.IsTodo,
        IsVerified = value.IsVerified, IsArchived = value.IsArchived, ArchivedUtc = value.ArchivedUtc,
        CreatedUtc = value.CreatedUtc, UpdatedUtc = value.UpdatedUtc
    };

    private sealed class GateLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
    }
}
