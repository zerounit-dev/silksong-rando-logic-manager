namespace Silksong_Rando_Logic_Manager.Components.RoomEditorV2;

internal sealed record ChildTableBoundaryAdapter<TBaseline, TDraft>(
    Func<TBaseline, Guid, TDraft> FromBaseline,
    Func<TDraft, TDraft, TDraft, TDraft> Rebase,
    Func<TBaseline, TDraft, TDraft, DateTime?, TBaseline> Comparison,
    Func<TBaseline, DateTime> Version,
    Func<TBaseline?, TDraft, bool> PotentialProposal);

internal enum ChildTableBoundaryResult { Committed, Unchanged, Proposal, Failed, Unexpected }
internal sealed record ChildTableBoundaryCompletion<TBaseline>(ChildTableBoundaryResult Result,
    TBaseline? Baseline, Guid? CreatedId, string? Message, bool PresentationRefreshed, bool RequiresReload);

// Component-local supplied-boundary ownership, not a browser scheduler or a
// persistence service. Each supplied snapshot has its own receipt; nothing is
// debounced/coalesced. Typed table intents supply execution and pure diff policy.
internal sealed class ChildTableBoundaryOwnership<TBaseline, TDraft>(
    ChildTableRowInteraction<TBaseline, TDraft> row, ChildTableBoundaryAdapter<TBaseline, TDraft> adapter)
    where TBaseline : class where TDraft : class
{
    private sealed class Receipt(TDraft origin, TDraft authored, DateTime? version,
        ChildTableField field, ChildTableFocusTarget? focus, Guid? tailPromotion)
    {
        internal TDraft Origin { get; } = origin;
        internal TDraft Authored { get; } = authored;
        internal TDraft Snapshot = authored;
        internal DateTime? Version { get; } = version;
        internal ChildTableField Field = field;
        internal ChildTableFocusTarget? Focus = focus;
        internal Guid? TailPromotion = tailPromotion;
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Queue<Receipt> pending = [];
    private Receipt? current;
    private TDraft? lastBoundary, observedOrigin, committedBoundary;
    private DateTime? observedVersion;
    private long retirement;
    internal bool InFlight { get; private set; }
    internal bool RequiresReload { get; private set; }
    internal bool ProposalExclusive { get; private set; }
    internal bool ReconcileNative { get; private set; }
    internal bool OwnsCreation => row.EntityId is null && (InFlight || ProposalExclusive);
    internal bool HoldsPresentation => InFlight || RequiresReload || ProposalExclusive;
    internal bool AdmitsBoundary => !ProposalExclusive && !RequiresReload && !row.IsArchived;
    internal void GenuineEdit() => ReconcileNative = false;
    internal void BeginExplicitProposal(){ProposalExclusive=true;ReconcileNative=true;}

    internal async Task SubmitAsync(ChildTableField field, ChildTableInteractionController controller,
        bool meaningful, bool changed,
        Func<TBaseline?, TDraft, ChildTableField, ChildTableFocusTarget?, Task<ChildTableBoundaryCompletion<TBaseline>>> execute,
        Func<bool> owns, Func<Task> notify, Func<Task> publish, Action<string?> message)
    {
        if (!AdmitsBoundary || row.Lifecycle == ChildTableRowLifecycle.Failed || (!InFlight && row.EntityId is null && !meaningful)) return;
        if (!InFlight && row.EntityId is not null && !changed) return;
        if (InFlight && EqualityComparer<TDraft>.Default.Equals(lastBoundary, row.Draft))
        {
            var existing = pending.LastOrDefault() ?? current;
            if (existing is not null)
            {
                existing.Focus = controller.PendingFocus; existing.Field = field;existing.TailPromotion=controller.PendingTailPromotionId;
                await existing.Completion.Task;
            }
            return;
        }
        var origin = lastBoundary ?? (row.Baseline is { } baseline ? adapter.FromBaseline(baseline, row.ClientRowId) : row.Draft);
        var receipt = new Receipt(origin, row.Draft, InFlight ? observedVersion : row.Baseline is { } durable ? adapter.Version(durable) : null, field, controller.PendingFocus,controller.PendingTailPromotionId);
        lastBoundary = receipt.Authored;
        pending.Enqueue(receipt);
        var startsDrain = !InFlight;
        if (startsDrain)
        {
            InFlight = true; observedOrigin = origin; observedVersion = receipt.Version; committedBoundary = null;
            row.BeginSave();
            // Establish syntactic potential exclusion before the first await.
            var comparison=row.Baseline is { } fresh?adapter.Comparison(fresh,receipt.Origin,receipt.Authored,receipt.Version):null;
            BeginPotential(receipt, controller,comparison);
        }
        if (startsDrain) await DrainAsync(controller, execute, owns, notify, publish, message);
        else await notify();
        await receipt.Completion.Task;
    }
    private void BeginPotential(Receipt receipt, ChildTableInteractionController controller,TBaseline? comparison)
    {
        if (!adapter.PotentialProposal(comparison, receipt.Snapshot)) return;
        ProposalExclusive = true; ReconcileNative = true;
        row.ReplaceDraft(receipt.Snapshot);
        lastBoundary=receipt.Authored;
        controller.PendingFocus = receipt.Focus;
        controller.PendingTailPromotionId=receipt.TailPromotion;
        while (pending.Count > 0 && !ReferenceEquals(pending.Peek(), receipt)) pending.Dequeue().Completion.TrySetResult(false);
    }
    private async Task DrainAsync(ChildTableInteractionController controller,
        Func<TBaseline?, TDraft, ChildTableField, ChildTableFocusTarget?, Task<ChildTableBoundaryCompletion<TBaseline>>> execute,
        Func<bool> owns, Func<Task> notify, Func<Task> publish, Action<string?> message)
    {
        var epoch = retirement;
        bool currentOwner() => epoch == retirement && owns();
        try
        {
            await notify();
            while (pending.TryDequeue(out var receipt))
            {
                current = receipt;
                if(!currentOwner()){receipt.Completion.TrySetResult(false);return;}
                var comparison = row.Baseline is { } fresh ? adapter.Comparison(fresh, receipt.Origin, receipt.Authored, receipt.Version ?? observedVersion) : null;
                BeginPotential(receipt, controller,comparison);
                if (ProposalExclusive)
                    while (pending.TryDequeue(out var dropped)) dropped.Completion.TrySetResult(false);
                row.BeginSave();
                var result = await execute(comparison, receipt.Snapshot, receipt.Field, receipt.Focus);
                // Durable evidence belongs to this row even when presentation
                // ownership retired while SQLite finished. Never recreate it.
                if(result.Result==ChildTableBoundaryResult.Committed)
                {
                    row.CompleteCommitted(result.CreatedId);
                    if(result.Baseline is { } committed)row.ReplaceBaseline(committed);
                }
                if (!currentOwner()) { receipt.Completion.TrySetResult(false); return; }
                if (result.Result == ChildTableBoundaryResult.Committed && !result.PresentationRefreshed && !result.RequiresReload)
                {
                    // No old-room publication/focus and no implicit later saves.
                    RebaseCorrection(result.Baseline??row.Baseline);
                    if(row.Baseline is { } saved&&!EqualityComparer<TDraft>.Default.Equals(row.Draft,adapter.FromBaseline(saved,row.ClientRowId)))row.RetainCorrection();
                    ProposalExclusive=false;controller.PendingFocus=null;controller.PendingTailPromotionId=null;
                    RetirePending();receipt.Completion.TrySetResult(false);return;
                }
                message(result.Result==ChildTableBoundaryResult.Unexpected
                    ? "An unexpected error occurred; consult the detailed error modal and server logs. Edit a field to try again."
                    : result.Message);
                if (result.Result == ChildTableBoundaryResult.Proposal)
                {
                    ProposalExclusive = true; ReconcileNative = true;
                    row.ReplaceDraft(receipt.Snapshot); row.CompleteUnchanged();
                    controller.PendingFocus = null; // Modal owns the captured target.
                    RetirePending(); receipt.Completion.TrySetResult(true); break;
                }
                if (result.Result is ChildTableBoundaryResult.Committed or ChildTableBoundaryResult.Unchanged)
                {
                    if(result.Result==ChildTableBoundaryResult.Committed)row.CompleteCommitted(result.CreatedId);
                    else row.CompleteUnchanged();
                    if (result.Baseline is { } saved)
                    {
                        row.ReplaceBaseline(saved);
                        observedVersion ??= adapter.Version(saved);
                        var carry = adapter.FromBaseline(saved, row.ClientRowId);
                        foreach (var later in pending)
                        {
                            later.Snapshot = adapter.Rebase(later.Origin, later.Authored, carry);
                            carry = later.Snapshot;
                        }
                    }
                    committedBoundary = receipt.Authored;
                    if (result.RequiresReload)
                    {
                        RequiresReload = true;
                        ProposalExclusive = false;
                        RebaseCorrection(result.Baseline ?? row.Baseline);
                        if (pending.Count > 0 || !EqualityComparer<TDraft>.Default.Equals(row.Draft, receipt.Authored))
                            row.Fail("The change was saved. Reload room before correcting the retained later edits.", row.Baseline, true);
                        RetirePending(); receipt.Completion.TrySetResult(false); break;
                    }
                    ProposalExclusive = false;
                    if (pending.Count == 0) controller.PendingFocus = receipt.Focus;
                    receipt.Completion.TrySetResult(true);
                    // Returned ID/baseline are installed before pending admission clears.
                    await notify();
                }
                else
                {
                    RebaseCorrection(result.Baseline ?? row.Baseline);
                    row.Fail(result.Result == ChildTableBoundaryResult.Unexpected
                        ? "An unexpected error occurred; consult the detailed error modal and server logs. Edit a field to try again."
                        : result.Message ?? "Save failed; edit a field to try again.", result.Baseline, true);
                    controller.PendingTailPromotionId = null;
                    controller.PendingFocus = new(row.ClientRowId, pending.LastOrDefault()?.Field.Wire() ?? receipt.Field.Wire());
                    ProposalExclusive = false; RetirePending(); receipt.Completion.TrySetResult(false); break;
                }
                current = null;
            }
        }
        catch
        {
            if (!currentOwner()) return;
            RebaseCorrection(row.Baseline);
            row.Fail("An unexpected error occurred; consult the detailed error modal and server logs. Edit a field to try again.", row.Baseline, true);
            controller.PendingTailPromotionId = null;
            controller.PendingFocus = new(row.ClientRowId, current?.Field.Wire() ?? "name");
            ProposalExclusive = false; RetirePending();
        }
        finally
        {
            current?.Completion.TrySetResult(false);
            if (currentOwner() && !ProposalExclusive && !RequiresReload && !row.HasRetainedCorrection && row.Lifecycle != ChildTableRowLifecycle.Failed && row.Baseline is { } saved)
                row.ReplaceDraft(adapter.Rebase(lastBoundary ?? row.Draft, row.Draft, adapter.FromBaseline(saved, row.ClientRowId)));
            if (!currentOwner()){RetirePending();ProposalExclusive=false;row.ResetForRoute();}
            else if(row.Lifecycle==ChildTableRowLifecycle.Saving)row.CompleteUnchanged();
            InFlight = false; current = null; lastBoundary = null; observedOrigin = null; observedVersion = null; committedBoundary = null;
        }
        if (!currentOwner()) return;
        await notify();
        if(currentOwner())await publish();
    }
    private void RebaseCorrection(TBaseline? fresh)
    {
        if (fresh is null) return;
        var origin = committedBoundary ?? observedOrigin ?? (row.Baseline is { } baseline ? adapter.FromBaseline(baseline, row.ClientRowId) : row.Draft);
        row.ReplaceDraft(adapter.Rebase(origin, row.Draft, adapter.FromBaseline(fresh, row.ClientRowId)));
        row.ReplaceBaseline(fresh);
    }
    private void RetirePending()
    {
        while (pending.TryDequeue(out var receipt)) receipt.Completion.TrySetResult(false);
    }
    internal void ReleaseProposal(TBaseline? baseline, TDraft? draft, Guid? createdId = null, bool reload = false, bool failed = false, string? failure = null)
    {
        ProposalExclusive = false; ReconcileNative = true; RequiresReload = reload;
        if (createdId is not null) row.CompleteCommitted(createdId);
        if (baseline is not null) row.ReplaceBaseline(baseline);
        if (draft is not null) row.ReplaceDraft(draft);
        if (failed) row.Fail(failure ?? "The source changed elsewhere.", baseline, true);
        else row.CompleteUnchanged();
    }
    internal void ResumeAfterReload() => RequiresReload = false;
    internal void Reset()
    {
        retirement++; current?.Completion.TrySetResult(false); RetirePending(); ProposalExclusive = false; RequiresReload = false; ReconcileNative = false;
    }
}
