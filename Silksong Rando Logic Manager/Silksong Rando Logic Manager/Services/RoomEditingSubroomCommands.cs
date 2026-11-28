using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Subroom authored save and exact captured reference proposal transaction.</summary>
internal sealed class RoomEditingSubroomCommands(IDbContextFactory<LogicDbContext> contexts, SharedPublicationCoordinator publications, Authentication.LiveAccess access)
{
    public Task<RoomMutationEffect?> ExecuteAsync(RoomEditingTracker.Entry entry) => ExecuteCoreAsync(entry, null);

    public Task<RoomMutationEffect?> ResolveAsync(RoomEditingTracker.Entry entry, Wire.RoomHeadResolution resolution)
    {
        switch (resolution)
        {
            case Wire.ChooseRoomConflictValues choice when entry.Blocked?.Evidence is Wire.RoomSubroomConflictEvidence conflict:
                entry.SubroomReview = new(conflict.Reviewed, choice.Choice);
                return ExecuteCoreAsync(entry, null);
            case Wire.ChooseRoomProposal choice when entry.Blocked is
                { Disposition: Wire.RoomBlockedDisposition.ProposalAwaitingChoice,
                  Evidence: Wire.RoomReferenceProposalEvidence { SourceKind: Wire.RoomReferenceSourceKind.Subroom } }:
                if (choice.Choice == Wire.RoomProposalChoice.Revert)
                {
                    var head = Head(entry);
                    entry.RecordReceipt(new(head.Sequence, head.OperationId, Wire.RoomTerminalDisposition.UserReverted, null, [], null));
                    return Task.FromResult<RoomMutationEffect?>(new(entry.Identity.RoomId, [], [],
                        RoomProjectionFamilies.None, Wire.RoomSceneConcerns.None, new([], [], [], [], [])));
                }
                return ExecuteCoreAsync(entry, choice.Choice);
            case Wire.PrepareRoomProposalAgain when entry.Blocked is
                { Disposition: Wire.RoomBlockedDisposition.ProposalStale,
                  Evidence: Wire.RoomReferenceProposalEvidence { SourceKind: Wire.RoomReferenceSourceKind.Subroom } }:
                return ExecuteCoreAsync(entry, null);
            default: throw new RoomTrackingProtocolException("The resolution does not match the subroom workflow.");
        }
    }

    private async Task<RoomMutationEffect?> ExecuteCoreAsync(RoomEditingTracker.Entry entry, Wire.RoomProposalChoice? choice)
    {
        using var publication = await publications.AcquireAsync(CancellationToken.None);
        var historyActor = HistoryActor.From(await access.RequireOwnerAsync(entry.SessionOwner, Authentication.AccessPolicies.Author));
        var command = (Wire.SaveRoomSubroom)Head(entry).Operation;
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
        var source = await db.Subrooms.SingleOrDefaultAsync(x => x.Id == command.EntityId
            && x.RoomId == entry.Identity.RoomId && !x.IsArchived);
        if (source is null) { RoomEditingMutationScope.Missing(entry, command.EntityId); return null; }
        var current = new Wire.RoomSubroomValues(source.FriendlyName, source.ReferenceId, source.Notes);
        var merge = Wire.RoomEditingMerge.Subroom(command.Origin.Original, command.Submitted, current, entry.SubroomReview);
        if (!merge.CanApply)
        {
            var fresh = Wire.RoomEditingMerge.Subroom(command.Origin.Original, command.Submitted, current);
            entry.SubroomReview = null;
            RoomEditingMutationScope.Block(entry, Wire.RoomBlockedDisposition.ConflictAwaitingChoice,
                merge.ReviewSuperseded ? "The reviewed subroom values changed again. Review the current values." : "This subroom changed elsewhere.",
                new Wire.RoomSubroomConflictEvidence(current, command.Submitted, fresh.DivergentFields));
            return null;
        }
        var referenceChanged = merge.Values.ReferenceId != current.ReferenceId;
        var targets = new List<(Wire.RoomReferenceTarget Evidence, ArchivableEntity Row)>();
        if (choice is not null)
        {
            var captured = entry.Blocked?.Evidence as Wire.RoomReferenceProposalEvidence
                ?? throw new RoomTrackingProtocolException("The captured subroom proposal is unavailable.");
            if (captured.SourceId != source.Id || captured.NewReferenceId != merge.Values.ReferenceId)
            { Stale(entry, captured); return null; }
            if (choice == Wire.RoomProposalChoice.UpdateCapturedTargets)
                foreach (var target in captured.Targets)
                {
                    ArchivableEntity? row = target.Field switch
                    {
                        Wire.RoomReferenceTargetField.TransitionSourceSubroom => await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == target.EntityId),
                        Wire.RoomReferenceTargetField.ConnectionSourceSubroom or Wire.RoomReferenceTargetField.ConnectionDestinationSubroom => await db.SubroomConnections.SingleOrDefaultAsync(x => x.Id == target.EntityId),
                        Wire.RoomReferenceTargetField.CheckSubroom => await db.CheckLocations.SingleOrDefaultAsync(x => x.Id == target.EntityId),
                        _ => throw new RoomTrackingProtocolException("Unexpected captured subroom reference field.")
                    };
                    if (row is null || row.IsArchived || row.UpdatedUtc != target.UpdatedUtc
                        || Owner(row) != target.RoomId || Text(row, target.Field) != target.ExpectedText)
                    { Stale(entry, captured); return null; }
                    targets.Add((target, row));
                }
        }
        else if (referenceChanged)
        {
            var transitions = await db.RoomTransitions.AsNoTracking().Where(x => !x.IsArchived && x.ResolvedSourceSubroomId == source.Id)
                .Select(x => new Wire.RoomReferenceTarget(x.RoomId, x.Id, Wire.RoomReferenceTargetField.TransitionSourceSubroom,
                    x.UpdatedUtc, x.SourceSubroomReferenceText)).ToListAsync();
            var connections = await db.SubroomConnections.AsNoTracking().Where(x => !x.IsArchived
                && (x.ResolvedSourceSubroomId == source.Id || x.ResolvedDestinationSubroomId == source.Id))
                .Select(x => new { x.RoomId, x.Id, x.UpdatedUtc, x.ResolvedSourceSubroomId, x.ResolvedDestinationSubroomId,
                    x.SourceSubroomReferenceText, x.DestinationSubroomReferenceText }).ToListAsync();
            var checks = await db.CheckLocations.AsNoTracking().Where(x => !x.IsArchived && x.ResolvedSubroomId == source.Id)
                .Select(x => new Wire.RoomReferenceTarget(x.RoomId, x.Id, Wire.RoomReferenceTargetField.CheckSubroom,
                    x.UpdatedUtc, x.SubroomReferenceText)).ToListAsync();
            var inventory = transitions.Concat(connections.Where(x => x.ResolvedSourceSubroomId == source.Id).Select(x =>
                    new Wire.RoomReferenceTarget(x.RoomId, x.Id, Wire.RoomReferenceTargetField.ConnectionSourceSubroom, x.UpdatedUtc, x.SourceSubroomReferenceText)))
                .Concat(connections.Where(x => x.ResolvedDestinationSubroomId == source.Id).Select(x =>
                    new Wire.RoomReferenceTarget(x.RoomId, x.Id, Wire.RoomReferenceTargetField.ConnectionDestinationSubroom, x.UpdatedUtc, x.DestinationSubroomReferenceText)))
                .Concat(checks).ToImmutableArray();
            RoomEditingMutationScope.Block(entry, Wire.RoomBlockedDisposition.ProposalAwaitingChoice,
                "Review the captured references before saving the subroom reference.",
                new Wire.RoomReferenceProposalEvidence(Wire.RoomReferenceSourceKind.Subroom, source.Id,
                    merge.Values.FriendlyName, merge.Values.ReferenceId, inventory));
            return null;
        }

        var scope = await RoomEditingMutationScope.CaptureAsync(db, entry, publication);
        var changedTargets = targets.Where(x => Text(x.Row, x.Evidence.Field) != merge.Values.ReferenceId).ToArray();
        source.FriendlyName = merge.Values.FriendlyName;
        source.ReferenceId = merge.Values.ReferenceId;
        source.Notes = merge.Values.Notes;
        foreach (var (evidence, row) in changedTargets)
            switch (evidence.Field)
            {
                case Wire.RoomReferenceTargetField.TransitionSourceSubroom: ((RoomTransition)row).SourceSubroomReferenceText = merge.Values.ReferenceId; break;
                case Wire.RoomReferenceTargetField.ConnectionSourceSubroom: ((SubroomConnection)row).SourceSubroomReferenceText = merge.Values.ReferenceId; break;
                case Wire.RoomReferenceTargetField.ConnectionDestinationSubroom: ((SubroomConnection)row).DestinationSubroomReferenceText = merge.Values.ReferenceId; break;
                case Wire.RoomReferenceTargetField.CheckSubroom: ((CheckLocation)row).SubroomReferenceText = merge.Values.ReferenceId; break;
            }
        await db.SaveChangesAsync(CancellationToken.None);
        if (referenceChanged || changedTargets.Length != 0)
            await new LogicReferenceResolver(db).ResolveScopedAsync(source,
                new HashSet<string>(StringComparer.Ordinal) { nameof(Subroom.ReferenceId) },
                new Dictionary<string, string?>(StringComparer.Ordinal) { [nameof(Subroom.ReferenceId)] = current.ReferenceId }, CancellationToken.None);
        var scene = merge.Values.FriendlyName != current.FriendlyName || referenceChanged
            ? Wire.RoomSceneConcerns.Canvas : Wire.RoomSceneConcerns.None;
        return await scope.CompleteAsync(db, tx,
            referenceChanged || changedTargets.Length != 0 ? RoomProjectionFamilies.All : RoomProjectionFamilies.Subrooms, scene);
    }

    private static Guid Owner(ArchivableEntity row) => row switch
    { RoomTransition x => x.RoomId, SubroomConnection x => x.RoomId, CheckLocation x => x.RoomId, _ => throw new ArgumentOutOfRangeException(nameof(row)) };
    private static string? Text(ArchivableEntity row, Wire.RoomReferenceTargetField field) => field switch
    {
        Wire.RoomReferenceTargetField.TransitionSourceSubroom => ((RoomTransition)row).SourceSubroomReferenceText,
        Wire.RoomReferenceTargetField.ConnectionSourceSubroom => ((SubroomConnection)row).SourceSubroomReferenceText,
        Wire.RoomReferenceTargetField.ConnectionDestinationSubroom => ((SubroomConnection)row).DestinationSubroomReferenceText,
        Wire.RoomReferenceTargetField.CheckSubroom => ((CheckLocation)row).SubroomReferenceText,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
    private static Wire.RoomQueuedOperation Head(RoomEditingTracker.Entry entry) => entry.CurrentHead is
        { Operation: Wire.SaveRoomSubroom } head ? head : throw new RoomTrackingProtocolException("The admitted head is not a subroom save.");
    private static void Stale(RoomEditingTracker.Entry entry, Wire.RoomReferenceProposalEvidence captured) =>
        RoomEditingMutationScope.Block(entry, Wire.RoomBlockedDisposition.ProposalStale,
            "A captured source or reference changed. Prepare a fresh proposal before applying.", captured);
}
