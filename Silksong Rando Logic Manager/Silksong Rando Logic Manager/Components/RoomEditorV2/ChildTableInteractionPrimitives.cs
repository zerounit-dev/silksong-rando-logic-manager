namespace Silksong_Rando_Logic_Manager.Components.RoomEditorV2;

// UI-only interaction primitives shared by typed child tables. They deliberately
// know neither EF nor table projections/diffs/commands: typed tables provide
// those through their own adapters.
internal static class ChildTableInteractionPrimitives
{
    internal static ChildTableNavigationDecision Navigate<TBaseline, TDraft>(
        ChildTableRowInteraction<TBaseline, TDraft> row, ChildTableInteractionController controller,
        IReadOnlyList<string> fields, IReadOnlyList<Guid> rowIds, bool meaningful, bool changed, bool boundaryOwned,
        ChildTableField field, ChildTableDirection direction,
        Func<int, string, ChildTableDirection, ChildTableFocusTarget?> targetAt,
        Func<TDraft, Guid, TDraft> promoteDraft)
    {
        if (row.Lifecycle == ChildTableRowLifecycle.Failed) return ChildTableNavigationDecision.Rejected;
        var index = -1;
        for (var n = 0; n < rowIds.Count; n++) if (rowIds[n] == row.ClientRowId) { index = n; break; }
        if (index < 0 || targetAt(index, field.Wire(), direction) is not { } target) return ChildTableNavigationDecision.Rejected;
        if (boundaryOwned)
        {
            if (controller.PendingFocus is not { ClientRowId: not null, Field: not null })
            {
                controller.PendingFocus = target;
                if (target.ClientRowId == controller.LatentSuccessorId)
                {
                    controller.PendingTailPromotionId = target.ClientRowId;
                    controller.LatentSuccessorId = Guid.NewGuid();
                }
            }
            else controller.Advance(direction, fields, row.ClientRowId, rowIds, targetAt);
            return ChildTableNavigationDecision.SubmitBoundary;
        }
        controller.PendingFocus = target;
        if (row.EntityId is not null)
            return !row.IsArchived && changed ? ChildTableNavigationDecision.SubmitBoundary : ChildTableNavigationDecision.Focus;
        if (!meaningful)
        {
            if (target.ClientRowId == controller.LatentSuccessorId)
            {
                row.PromoteClientRowId(controller.LatentSuccessorId, promoteDraft);
                controller.LatentSuccessorId = Guid.NewGuid();
            }
            return ChildTableNavigationDecision.Focus;
        }
        if (target.ClientRowId == controller.LatentSuccessorId)
        {
            controller.PendingTailPromotionId = target.ClientRowId;
            controller.LatentSuccessorId = Guid.NewGuid();
        }
        return ChildTableNavigationDecision.SubmitBoundary;
    }
    internal static string FieldId(Guid clientRowId, string field) => $"editor-row-{clientRowId}-{field}";

    internal static ChildTableFocusTarget? Target(
        IReadOnlyList<string> fields,
        Guid clientRowId,
        int rowIndex,
        int rowCount,
        bool archived,
        bool uncommitted,
        Guid latentSuccessorId,
        string field,
        string direction,
        Func<int, Guid> rowClientId)
    {
        var fieldIndex = -1;
        if (!ChildTableBrowserFacts.TryField(field, out _) || !ChildTableBrowserFacts.TryDirection(direction, out var typedDirection)) return null;
        for (var index = 0; index < fields.Count; index++)
            if (fields[index] == field) { fieldIndex = index; break; }
        if (fieldIndex < 0 || rowIndex < 0) return null;
        return typedDirection switch
        {
            ChildTableDirection.Left when fieldIndex > 0 => new(clientRowId, fields[fieldIndex - 1]),
            ChildTableDirection.Right when fieldIndex < fields.Count - 1 => new(clientRowId, fields[fieldIndex + 1]),
            ChildTableDirection.Up when rowIndex > 0 => new(rowClientId(rowIndex - 1), field),
            ChildTableDirection.Down when rowIndex < rowCount - 1 => new(rowClientId(rowIndex + 1), field),
            ChildTableDirection.Down when !archived && uncommitted => new(latentSuccessorId, field),
            _ => null
        };
    }
}

internal enum ChildTableNavigationDecision { Rejected, Focus, SubmitBoundary }

// ComponentBase schedules an automatic render after an awaited markup event.
// JSInvokable routes do not use this guard and cannot suppress a later render.
internal sealed class ChildTableEventCompletionGuard
{
    private bool suppress;
    internal async Task RunAsync(Func<Task> action, Func<bool> owns)
    {
        try { await action(); }
        finally { if(!owns())suppress=true; }
    }
    internal bool ShouldRender(bool active)
    {
        if(!active)return false;
        if(!suppress)return true;
        suppress=false;return false;
    }
}

internal enum ChildTableRowLifecycle { Uncommitted, Saving, Failed, Persisted }

/// <summary>
/// The non-EF stable identity and lifecycle portion of a typed child row.  The
/// table remains responsible for mapping its typed baseline/draft and invoking
/// its typed command; this primitive owns the transitions those operations use.
/// </summary>
internal sealed class ChildTableRowInteraction<TBaseline, TDraft>
{
    internal ChildTableRowInteraction(Guid clientRowId, TDraft draft)
    {
        ClientRowId = clientRowId;
        Draft = draft;
    }

    internal Guid ClientRowId { get; private set; }
    internal Guid? EntityId { get; private set; }
    internal bool IsArchived { get; private set; }
    internal ChildTableRowLifecycle Lifecycle { get; private set; } = ChildTableRowLifecycle.Uncommitted;
    internal string? FailureMessage { get; private set; }
    internal TBaseline? Baseline { get; private set; }
    internal TDraft Draft { get; private set; }
    internal bool HasRetainedCorrection { get; private set; }

    internal void Load(Guid entityId, bool isArchived, TBaseline baseline, TDraft draft, bool retainCorrection = false,
        Func<TBaseline, TDraft, TDraft, TDraft>? rebaseCorrection = null)
    {
        if (retainCorrection && HasRetainedCorrection && !isArchived)
        {
            if (Baseline is { } previous && rebaseCorrection is not null)
                Draft = rebaseCorrection(previous, Draft, draft);
            EntityId = entityId; IsArchived = isArchived; Baseline = baseline;
            return;
        }
        EntityId = entityId; IsArchived = isArchived; Baseline = baseline; Draft = draft;
        Lifecycle = ChildTableRowLifecycle.Persisted; FailureMessage = null; HasRetainedCorrection = false;
    }

    internal void ReplaceDraft(TDraft draft) => Draft = draft;
    internal void ReplaceBaseline(TBaseline baseline) => Baseline = baseline;
    internal void RetainCorrection() => HasRetainedCorrection = true;
    internal void PromoteClientRowId(Guid clientRowId, Func<TDraft, Guid, TDraft> promoteDraft)
    {
        ClientRowId = clientRowId;
        Draft = promoteDraft(Draft, clientRowId);
    }
    internal bool BeginSave()
    {
        if (IsArchived || Lifecycle is ChildTableRowLifecycle.Saving or ChildTableRowLifecycle.Failed) return false;
        Lifecycle = ChildTableRowLifecycle.Saving; FailureMessage = null; return true;
    }
    internal void CompleteCommitted(Guid? createdEntityId = null)
    {
        EntityId ??= createdEntityId; Lifecycle = ChildTableRowLifecycle.Persisted; FailureMessage = null; HasRetainedCorrection = false;
    }
    internal void CompleteUnchanged() { Lifecycle = EntityId is null ? ChildTableRowLifecycle.Uncommitted : ChildTableRowLifecycle.Persisted; FailureMessage = null; HasRetainedCorrection = false; }
    internal void Fail(string message, TBaseline? freshBaseline = default, bool retainCorrection = false)
    {
        if (freshBaseline is not null) Baseline = freshBaseline;
        Lifecycle = ChildTableRowLifecycle.Failed; FailureMessage = message; HasRetainedCorrection = retainCorrection;
    }
    internal void BeginRetryFromEdit()
    {
        if (Lifecycle == ChildTableRowLifecycle.Failed) { Lifecycle = EntityId is null ? ChildTableRowLifecycle.Uncommitted : ChildTableRowLifecycle.Persisted; FailureMessage = null; }
    }
    internal void ResetForRoute()
    {
        FailureMessage = null;
        HasRetainedCorrection = false;
        Lifecycle = EntityId is null ? ChildTableRowLifecycle.Uncommitted : ChildTableRowLifecycle.Persisted;
    }
}

/// <summary>Shared transient table state: latent final tail, pending focus and route reset.</summary>
internal sealed class ChildTableInteractionController
{
    internal Guid LatentSuccessorId { get; set; } = Guid.NewGuid();
    internal Guid? PendingTailPromotionId { get; set; }
    internal ChildTableFocusTarget? PendingFocus { get; set; }

    internal void SetPendingFocus(ChildTableFocusTarget? target) => PendingFocus = target;
    internal ChildTableFocusTarget? TakePendingFocus() { var target = PendingFocus; PendingFocus = null; return target; }
    internal void PromoteTail(Guid clientRowId) { PendingTailPromotionId = clientRowId; }
    internal Guid ConsumeTailId(Guid? existingTailId)
    {
        var id = PendingTailPromotionId ?? existingTailId ?? Guid.NewGuid();
        PendingTailPromotionId = null; LatentSuccessorId = Guid.NewGuid(); return id;
    }
    internal void AdvanceVirtualDown(string field)
    {
        PendingFocus = new ChildTableFocusTarget(LatentSuccessorId, field);
        PendingTailPromotionId = LatentSuccessorId;
        LatentSuccessorId = Guid.NewGuid();
    }
    internal void Advance(ChildTableDirection direction, IReadOnlyList<string> fields, Guid initiator,
        IReadOnlyList<Guid> rowIds, Func<int, string, ChildTableDirection, ChildTableFocusTarget?> target)
    {
        if (PendingFocus is not { ClientRowId: Guid currentId, Field: string field } current) return;
        var rowIndex = -1;
        for (var index = 0; index < rowIds.Count; index++) if (rowIds[index] == currentId) { rowIndex = index; break; }
        if (rowIndex >= 0) PendingFocus = target(rowIndex, field, direction) ?? current;
        else
        {
            var fieldIndex = -1;
            for (var index = 0; index < fields.Count; index++) if (fields[index] == field) { fieldIndex = index; break; }
            if (direction == ChildTableDirection.Left && fieldIndex > 0) PendingFocus = new(currentId, fields[fieldIndex - 1]);
            else if (direction == ChildTableDirection.Right && fieldIndex >= 0 && fieldIndex < fields.Count - 1) PendingFocus = new(currentId, fields[fieldIndex + 1]);
            else if (direction == ChildTableDirection.Up) PendingFocus = new(initiator, field);
            else if (direction == ChildTableDirection.Down) AdvanceVirtualDown(field);
        }
        if (PendingFocus is { ClientRowId: Guid next } && !rowIds.Contains(next)) PendingTailPromotionId = next;
        else PendingTailPromotionId = null;
    }
    internal void ResetForRoute(IEnumerable<IChildTableRowInteraction> rows)
    {
        foreach (var row in rows) row.ResetForRoute();
        PendingFocus = null; PendingTailPromotionId = null; LatentSuccessorId = Guid.NewGuid();
    }
}

internal interface IChildTableRowInteraction
{
    Guid ClientRowId { get; }
    Guid? EntityId { get; }
    void ResetForRoute();
}

internal static class ChildTableRowReconciliation
{
    internal static void Reconcile<TRow, TView>(
        IList<TRow> active,
        IList<TRow> archived,
        IEnumerable<TView> activeViews,
        IEnumerable<TView> archivedViews,
        Func<TRow, Guid?> entityId,
        Func<TView, Guid> viewEntityId,
        Func<TView, TRow> create,
        Action<TRow, TView> load,
        Func<Guid, TRow> createTail,
        ChildTableInteractionController controller,
        bool retainAllTails = false,
        bool appendTail = true)
        where TRow : IChildTableRowInteraction
    {
        var old = active.Concat(archived).ToList();
        var drafts = old.Where(row => entityId(row) is null).ToList();
        active.Clear(); archived.Clear();
        foreach (var view in activeViews)
        {
            var row = old.FirstOrDefault(candidate => entityId(candidate) == viewEntityId(view)) ?? create(view);
            load(row, view); active.Add(row);
        }
        foreach (var view in archivedViews)
        {
            var row = old.FirstOrDefault(candidate => entityId(candidate) == viewEntityId(view)) ?? create(view);
            load(row, view); archived.Add(row);
        }
        foreach (var draft in retainAllTails ? drafts : drafts.Take(1)) active.Add(draft);
        if (appendTail && !active.Any(row => entityId(row) is null))
            active.Add(createTail(controller.ConsumeTailId(null)));
    }
}

internal readonly record struct ChildTableFocusTarget(Guid? ClientRowId, string? Field, string ElementId)
{
    internal ChildTableFocusTarget(Guid clientRowId, string field)
        : this(clientRowId, field, ChildTableInteractionPrimitives.FieldId(clientRowId, field)) { }

    internal static ChildTableFocusTarget Element(string elementId) => new(null, null, elementId);
}
