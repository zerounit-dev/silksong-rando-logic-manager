namespace Silksong_Rando_Logic_Manager.Components.RoomEditorV2;

// UI-only interaction primitives shared by typed child tables. They deliberately
// know neither EF nor table projections/diffs/commands: typed tables provide
// those through their own adapters.
internal static class ChildTableInteractionPrimitives
{
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
        for (var index = 0; index < fields.Count; index++)
            if (fields[index] == field) { fieldIndex = index; break; }
        if (fieldIndex < 0 || rowIndex < 0) return null;
        return direction switch
        {
            "left" when fieldIndex > 0 => new(clientRowId, fields[fieldIndex - 1]),
            "right" when fieldIndex < fields.Count - 1 => new(clientRowId, fields[fieldIndex + 1]),
            "up" when rowIndex > 0 => new(rowClientId(rowIndex - 1), field),
            "down" when rowIndex < rowCount - 1 => new(rowClientId(rowIndex + 1), field),
            "down" when !archived && uncommitted => new(latentSuccessorId, field),
            _ => null
        };
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

    internal void Load(Guid entityId, bool isArchived, TBaseline baseline, TDraft draft)
    {
        EntityId = entityId; IsArchived = isArchived; Baseline = baseline; Draft = draft;
        Lifecycle = ChildTableRowLifecycle.Persisted; FailureMessage = null;
    }

    internal void ReplaceDraft(TDraft draft) => Draft = draft;
    internal void ReplaceBaseline(TBaseline baseline) => Baseline = baseline;
    internal void PromoteClientRowId(Guid clientRowId) => ClientRowId = clientRowId;
    internal bool BeginSave()
    {
        if (Lifecycle == ChildTableRowLifecycle.Saving) return false;
        Lifecycle = ChildTableRowLifecycle.Saving; FailureMessage = null; return true;
    }
    internal void CompleteCommitted(Guid? createdEntityId = null)
    {
        EntityId ??= createdEntityId; Lifecycle = ChildTableRowLifecycle.Persisted; FailureMessage = null;
    }
    internal void CompleteUnchanged() { Lifecycle = ChildTableRowLifecycle.Persisted; FailureMessage = null; }
    internal void Fail(string message, TBaseline? freshBaseline = default)
    {
        if (freshBaseline is not null) Baseline = freshBaseline;
        Lifecycle = ChildTableRowLifecycle.Failed; FailureMessage = message;
    }
    internal void BeginRetryFromEdit()
    {
        if (Lifecycle == ChildTableRowLifecycle.Failed) { Lifecycle = EntityId is null ? ChildTableRowLifecycle.Uncommitted : ChildTableRowLifecycle.Persisted; FailureMessage = null; }
    }
    internal void ResetForRoute()
    {
        FailureMessage = null;
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
        ChildTableInteractionController controller)
        where TRow : IChildTableRowInteraction
    {
        var old = active.Concat(archived).ToList();
        var draft = old.FirstOrDefault(row => entityId(row) is null);
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
        if (draft is not null)
            active.Add(draft);
        else if (!active.Any(row => entityId(row) is null))
            active.Add(createTail(controller.ConsumeTailId(null)));
    }
}

internal readonly record struct ChildTableFocusTarget(Guid? ClientRowId, string? Field, string ElementId)
{
    internal ChildTableFocusTarget(Guid clientRowId, string field)
        : this(clientRowId, field, ChildTableInteractionPrimitives.FieldId(clientRowId, field)) { }

    internal static ChildTableFocusTarget Element(string elementId) => new(null, null, elementId);
}
