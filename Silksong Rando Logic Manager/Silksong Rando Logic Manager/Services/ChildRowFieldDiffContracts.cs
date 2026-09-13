using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>
/// Explicit, entity-owned field mapping used by ordinary child-row writes.  This
/// deliberately avoids reflection: authored fields are part of each entity's
/// persistence contract rather than an incidental UI field list.
/// </summary>
public interface IChildRowFieldDiffMapper<T> where T : ArchivableEntity
{
    IReadOnlyList<string> EditableFields { get; }
    T Clone(T value);
    IReadOnlyList<string> Differences(T baseline, T draft);
    void Apply(T target, T source, IEnumerable<string> fields);
    void Reconcile(T live, T committed, T request, IReadOnlyCollection<string> committedFields, bool resolved);
}

public enum ChildRowSaveStatus { Committed, Unchanged, Conflict, Missing }

/// <summary>Primary rows carry the initiating command result; secondary rows carry server-derived patches only.</summary>
public sealed record ChildRowSavePatch<T>(ChildRowSaveStatus Status, T? SavedRow, IReadOnlyList<string> CommittedFields, IReadOnlyList<T> AffectedRows, IReadOnlyList<ReferenceResolution> Diagnostics, CatalogSaveOutcome Outcome) where T : ArchivableEntity;

/// <summary>
/// Per displayed-row coordinator shared by every child type. A typed mapper
/// provides snapshots and field-local reconciliation; this coordinator owns
/// queueing, conflict rebasing, and the no-op-after-failure rule.
/// </summary>
public sealed class ChildRowSaveCoordinator<T>(T durableBaseline, IChildRowFieldDiffMapper<T> mapper) where T : ArchivableEntity
{
    private readonly IChildRowFieldDiffMapper<T> mapper = mapper;
    private T baseline = mapper.Clone(durableBaseline);
    private T? queued;
    private T? retryDraft;
    private bool retryRequiresEdit;

    public bool IsSaving { get; private set; }
    public bool IsReloadRequired { get; private set; }
    public ChildRowSaveStatus? LastOutcome { get; private set; }

    public async Task SaveAsync(T live, Func<T, T, Task<ChildRowSavePatch<T>>> save, Action<T, ChildRowSavePatch<T>, bool> committed, Action<T?, ChildRowSavePatch<T>> expected, Action<Exception> unexpected, Func<Task<T?>>? reconcileAfterException = null)
    {
        if (IsReloadRequired || (retryRequiresEdit && retryDraft is not null && mapper.Differences(retryDraft, live).Count == 0)) return;
        retryRequiresEdit = false;
        retryDraft = null;
        var operation = (Baseline: mapper.Clone(baseline), Draft: mapper.Clone(live));
        if (IsSaving) { queued = operation.Draft; return; }
        IsSaving = true;
        try
        {
            while (true)
            {
                try
                {
                    var patch = await save(operation.Baseline, operation.Draft);
                    LastOutcome = patch.Status;
                    if (patch.Status == ChildRowSaveStatus.Missing)
                    {
                        queued = null; IsReloadRequired = true; expected(null, patch); return;
                    }
                    if (patch.Status == ChildRowSaveStatus.Conflict)
                    {
                        var local = queued ?? operation.Draft;
                        var localFields = mapper.Differences(operation.Baseline, local);
                        baseline = mapper.Clone(patch.SavedRow!);
                        var rebased = mapper.Clone(baseline);
                        mapper.Apply(rebased, local, localFields);
                        queued = null; retryRequiresEdit = true; retryDraft = mapper.Clone(rebased);
                        expected(rebased, patch); return;
                    }
                    if (patch.SavedRow is not null) baseline = mapper.Clone(patch.SavedRow);
                    var hasQueued = queued is not null;
                    committed(operation.Draft, patch, hasQueued);
                    if (queued is null) return;
                    var fields = mapper.Differences(operation.Baseline, queued);
                    var draft = mapper.Clone(baseline);
                    mapper.Apply(draft, queued, fields);
                    operation = (mapper.Clone(baseline), draft);
                    queued = null;
                }
                catch (Exception exception)
                {
                    // A transport exception can arrive after SQLite committed.
                    // Read durable state before exposing a generic failure.
                    if (reconcileAfterException is not null)
                    {
                        try
                        {
                            var durable = await reconcileAfterException();
                            if (durable is null)
                            {
                                queued = null; IsReloadRequired = true;
                                expected(null, new(ChildRowSaveStatus.Missing, null, [], [], [], CatalogSaveOutcome.Unchanged));
                                return;
                            }
                            var fields = mapper.Differences(operation.Baseline, operation.Draft);
                            if (durable.UpdatedUtc != operation.Baseline.UpdatedUtc && fields.All(field => mapper.Differences(operation.Draft, durable).All(other => other != field)))
                            {
                                baseline = mapper.Clone(durable);
                                committed(operation.Draft, new(ChildRowSaveStatus.Committed, durable, fields, [durable], [], new(true, false, false, false, false, TimeSpan.Zero)), queued is not null);
                                queued = null;
                                return;
                            }
                        }
                        catch { /* preserve the original diagnostic exception */ }
                    }
                    queued = null; unexpected(exception); return;
                }
            }
        }
        finally { IsSaving = false; }
    }
}
