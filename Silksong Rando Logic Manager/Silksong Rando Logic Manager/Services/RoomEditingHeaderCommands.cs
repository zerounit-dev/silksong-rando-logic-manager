using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed record RoomDocumentAdvance(Guid RoomId, DateTime Before, DateTime After);
internal sealed record RoomHeaderMutationEffect(Guid SourceRoomId, ImmutableArray<Guid> AffectedRoomIds,
    bool ReferencesResolved, bool MapResolutionChanged, ImmutableArray<RoomDocumentAdvance> Advances)
{
    public SharedRoomCommitPlan? SharedPlan { get; init; }
}

/// <summary>
/// R3 header transaction/proposal owner, not a V2 adapter wrapper. Invoked only
/// under the tracking entry gate. A reliable receipt is recorded immediately after
/// CommitAsync returns, before any postcommit read, notification or disposal.
/// Projection is a separate caller-owned read and cannot retry this mutation.
/// </summary>
internal sealed class RoomEditingHeaderCommands(IDbContextFactory<LogicDbContext> contexts, SharedPublicationCoordinator publications, Authentication.LiveAccess access)
{
    public Task<RoomHeaderMutationEffect?> ExecuteAsync(RoomEditingTracker.Entry entry) =>
        ExecuteCoreAsync(entry, null);

    // RegisterResolution has already bound the stable attempt ID to this exact
    // server-owned evidence revision before this method can be called.
    public Task<RoomHeaderMutationEffect?> ResolveAsync(RoomEditingTracker.Entry entry,
        Wire.RoomHeadResolution resolution)
    {
        var blocked = entry.Blocked ?? throw new RoomTrackingProtocolException("There is no blocked header operation.");
        switch (resolution)
        {
            case Wire.ChooseRoomConflictValues choice when blocked.Evidence is Wire.RoomHeaderConflictEvidence conflict:
                entry.HeaderReview = new(conflict.Reviewed, choice.Choice);
                return ExecuteCoreAsync(entry, null);
            case Wire.ChooseRoomProposal choice when blocked.Disposition == Wire.RoomBlockedDisposition.ProposalAwaitingChoice
                && blocked.Evidence is Wire.RoomReferenceProposalEvidence { SourceKind: Wire.RoomReferenceSourceKind.Room }:
                if (choice.Choice == Wire.RoomProposalChoice.Revert)
                {
                    var item = HeaderHead(entry);
                    entry.RecordReceipt(new(item.Sequence, item.OperationId, Wire.RoomTerminalDisposition.UserReverted,
                        null, [], null));
                    return Task.FromResult<RoomHeaderMutationEffect?>(new(entry.Identity.RoomId, [], false, false, []));
                }
                return ExecuteCoreAsync(entry, choice.Choice);
            case Wire.PrepareRoomProposalAgain when blocked.Disposition == Wire.RoomBlockedDisposition.ProposalStale
                && blocked.Evidence is Wire.RoomReferenceProposalEvidence { SourceKind: Wire.RoomReferenceSourceKind.Room }:
                return ExecuteCoreAsync(entry, null);
            default:
                throw new RoomTrackingProtocolException("This resolution does not match the blocked header workflow.");
        }
    }

    private async Task<RoomHeaderMutationEffect?> ExecuteCoreAsync(RoomEditingTracker.Entry entry,
        Wire.RoomProposalChoice? proposalChoice)
    {
        var item = HeaderHead(entry);
        var operation = (Wire.SaveRoomHeader)item.Operation;
        var roomId = entry.Identity.RoomId;
        var commitAttempted = false;
        var receipted = false;
        try
        {
            using var publication = await publications.AcquireAsync(CancellationToken.None);
            var historyActor = HistoryActor.From(await access.RequireOwnerAsync(entry.SessionOwner, Authentication.AccessPolicies.Author));
            await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
            await using var tx = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
            var source = await db.Rooms.SingleOrDefaultAsync(x => x.Id == roomId);
            if (source is null)
            {
                Block(entry, item, Wire.RoomBlockedDisposition.MissingBlocked,
                    "This room no longer exists. Copy pending work before reloading.", new Wire.RoomReloadRequiredEvidence(roomId));
                return null;
            }
            var current = Values(source);
            var merge = Wire.RoomEditingMerge.Header(operation.Origin.Original, operation.Submitted, current, entry.HeaderReview);
            if (!merge.CanApply)
            {
                // A superseded choice is not force-save permission. Recompute the
                // original four-case conflict against the new reviewed current row.
                var fresh = Wire.RoomEditingMerge.Header(operation.Origin.Original, operation.Submitted, current);
                entry.HeaderReview = null;
                Block(entry, item, Wire.RoomBlockedDisposition.ConflictAwaitingChoice,
                    merge.ReviewSuperseded ? "The reviewed room values changed again. Review the current values." : "This room changed elsewhere.",
                    new Wire.RoomHeaderConflictEvidence(current, operation.Submitted, fresh.DivergentFields));
                return null;
            }

            var referenceChanged = merge.Values.ReferenceId != current.ReferenceId;
            var transitionTargets = new List<RoomTransition>();
            var sceneTargets = new List<MapScene>();
            if (proposalChoice is not null)
            {
                var captured = entry.Blocked?.Evidence as Wire.RoomReferenceProposalEvidence
                    ?? throw new RoomTrackingProtocolException("The captured reference proposal is unavailable.");
                // The modal approved this exact new reference. If merging against
                // fresh source facts would change it, retire evidence without writes.
                if (captured.NewReferenceId != merge.Values.ReferenceId)
                {
                    Stale(entry, item, captured);
                    return null;
                }
                if (proposalChoice == Wire.RoomProposalChoice.UpdateCapturedTargets)
                {
                    foreach (var target in captured.Targets)
                    {
                        if (target.Field == Wire.RoomReferenceTargetField.TransitionDestinationRoom)
                        {
                            var row = await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == target.EntityId);
                            if (row is null || row.IsArchived || row.RoomId != target.RoomId || row.UpdatedUtc != target.UpdatedUtc
                                || row.DestinationRoomReferenceText != target.ExpectedText)
                            {
                                Stale(entry, item, captured);
                                return null;
                            }
                            transitionTargets.Add(row);
                        }
                        else if (target.Field == Wire.RoomReferenceTargetField.MapSceneRoom)
                        {
                            var row = await db.MapScenes.SingleOrDefaultAsync(x => x.Id == target.EntityId);
                            if (row is null || row.RoomReferenceText != target.ExpectedText)
                            {
                                Stale(entry, item, captured);
                                return null;
                            }
                            sceneTargets.Add(row);
                        }
                        else throw new RoomTrackingProtocolException("Unexpected room-reference target field.");
                    }
                }
            }
            else if (referenceChanged)
            {
                var transitions = await db.RoomTransitions.AsNoTracking()
                    .Where(x => !x.IsArchived && x.ResolvedDestinationRoomId == roomId)
                    .Select(x => new Wire.RoomReferenceTarget(x.RoomId, x.Id,
                        Wire.RoomReferenceTargetField.TransitionDestinationRoom, x.UpdatedUtc, x.DestinationRoomReferenceText)).ToListAsync();
                var scenes = await db.MapScenes.AsNoTracking().Where(x => x.ResolvedRoomId == roomId)
                    .Select(x => new Wire.RoomReferenceTarget(roomId, x.Id,
                        Wire.RoomReferenceTargetField.MapSceneRoom, null, x.RoomReferenceText)).ToListAsync();
                Block(entry, item, Wire.RoomBlockedDisposition.ProposalAwaitingChoice,
                    "Review the captured references before saving the room reference.",
                    new Wire.RoomReferenceProposalEvidence(Wire.RoomReferenceSourceKind.Room, roomId,
                        merge.Values.FriendlyName, merge.Values.ReferenceId, transitions.Concat(scenes).ToImmutableArray()));
                return null;
            }

            var anyTargetChange = transitionTargets.Any(x => x.DestinationRoomReferenceText != merge.Values.ReferenceId)
                || sceneTargets.Any(x => x.RoomReferenceText != merge.Values.ReferenceId);
            if (merge.Values == current && !anyTargetChange)
            {
                await tx.FinalizeAsync(CancellationToken.None);
                var unchanged = publication.PrepareAdvance(false, false);
                var unchangedPlan = await SharedRoomCommitPlan.PrepareAsync(db, tx, unchanged, CancellationToken.None, [roomId]);
                entry.RecordReceipt(new(item.Sequence, item.OperationId, Wire.RoomTerminalDisposition.Satisfied,
                    null, [roomId], null) { SharedTimestamps = unchanged.Timestamps,
                        SharedOriginalRooms = unchangedPlan.OriginalRooms });
                receipted = true;
                return new(roomId, [roomId], false, false, [new(roomId, source.CacheFlagUtc, source.CacheFlagUtc)])
                { SharedPlan = unchangedPlan };
            }

            if (merge.Values != current)
            {
                // Guard the actual version just compared, not the operation's
                // historical timestamp. The four-case merge remains fieldwise;
                // this predicate closes only the compare-to-write race, as in the
                // retained header command's conditional SQL seam.
                var updated = NextUpdatedUtc(source.UpdatedUtc);
                var values = merge.Values;
                var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [Rooms] SET [FriendlyName] = {values.FriendlyName}, [ReferenceId] = {values.ReferenceId},
                        [InGameId] = {values.InGameId}, [Contributors] = {values.Contributors},
                        [Comments] = {values.Comments}, [UpdatedUtc] = {updated}
                    WHERE [Id] = {roomId} AND [UpdatedUtc] = {source.UpdatedUtc}
                    """, CancellationToken.None);
                if (changed != 1)
                {
                    var freshSource = await db.Rooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == roomId);
                    if (freshSource is null)
                        Block(entry, item, Wire.RoomBlockedDisposition.MissingBlocked,
                            "This room no longer exists. Copy pending work before reloading.", new Wire.RoomReloadRequiredEvidence(roomId));
                    else
                    {
                        var freshValues = Values(freshSource);
                        var freshMerge = Wire.RoomEditingMerge.Header(operation.Origin.Original, operation.Submitted, freshValues);
                        entry.HeaderReview = null;
                        Block(entry, item, Wire.RoomBlockedDisposition.ConflictAwaitingChoice,
                            "The room changed during save admission. Review the current values.",
                            new Wire.RoomHeaderConflictEvidence(freshValues, operation.Submitted, freshMerge.DivergentFields));
                    }
                    return null;
                }
                await db.Entry(source).ReloadAsync(CancellationToken.None);
            }
            foreach (var row in transitionTargets)
                if (row.DestinationRoomReferenceText != merge.Values.ReferenceId)
                {
                    row.DestinationRoomReferenceText = merge.Values.ReferenceId;
                    row.UpdatedUtc = NextUpdatedUtc(row.UpdatedUtc);
                }
            foreach (var row in sceneTargets) row.RoomReferenceText = merge.Values.ReferenceId;
            // Header/proposal timestamps keep the existing monotonic header seam.
            // Subsequent resolver-only ID writes must not touch authored timestamps.
            // CacheFlagUtc remains database-owned and still advances for real writes.
            using (db.SuppressAuditMetadata())
            {
                await db.SaveChangesAsync(CancellationToken.None);
                if (referenceChanged || anyTargetChange)
                    await new LogicReferenceResolver(db).ResolveScopedAsync(source,
                        new HashSet<string>(StringComparer.Ordinal) { nameof(Room.ReferenceId) },
                        new Dictionary<string, string?>(StringComparer.Ordinal) { [nameof(Room.ReferenceId)] = current.ReferenceId }, CancellationToken.None);
            }

            await tx.FinalizeAsync(CancellationToken.None);
            var effects = tx.FinalEffects;
            var affected = effects.Rooms.Keys.Append(roomId).Distinct().ToImmutableArray();
            var mapChanged = effects.MapLinksChanged;
            var prepared = publication.PrepareAdvance(effects.RoomFactsChanged, false);
            var plan = await SharedRoomCommitPlan.PrepareAsync(db, tx, prepared, CancellationToken.None, affected);
            var receipt = new Wire.RoomTerminalReceipt(item.Sequence, item.OperationId,
                Wire.RoomTerminalDisposition.Committed, null, affected, null) { SharedTimestamps = prepared.Timestamps,
                    SharedOriginalRooms = plan.OriginalRooms };
            var advances = plan.Advances;
            commitAttempted = true;
            await tx.CommitAsync(() =>
            {
                try { entry.RecordReceipt(receipt); }
                finally { publication.PublishCommitted(prepared); }
            });
            receipted = true;
            return new(roomId, affected, referenceChanged || anyTargetChange, mapChanged, advances)
            { SharedPlan = plan };
        }
        catch
        {
            // Commit failure can be ambiguous. A reliable recorded receipt, however,
            // survives even a subsequent disposal failure and remains read-recoverable.
            if (commitAttempted && !receipted) entry.Invalidate();
            throw;
        }
    }

    private static Wire.RoomQueuedOperation HeaderHead(RoomEditingTracker.Entry entry) =>
        entry.CurrentHead is { Operation: Wire.SaveRoomHeader } item ? item
            : throw new RoomTrackingProtocolException("The tracking head is not a room-header save.");

    private static Wire.RoomHeaderValues Values(Room x) => new(x.FriendlyName, x.ReferenceId, x.InGameId, x.Contributors, x.Comments);
    private static DateTime NextUpdatedUtc(DateTime previous)
    {
        var now = DateTime.UtcNow;
        return now > previous ? now : previous.AddTicks(1);
    }
    private static void Stale(RoomEditingTracker.Entry entry, Wire.RoomQueuedOperation item, Wire.RoomReferenceProposalEvidence evidence) =>
        Block(entry, item, Wire.RoomBlockedDisposition.ProposalStale,
            "A captured source or reference changed. Prepare a fresh proposal before applying.", evidence);
    private static void Block(RoomEditingTracker.Entry entry, Wire.RoomQueuedOperation item,
        Wire.RoomBlockedDisposition disposition, string message, Wire.RoomHeadEvidence evidence) =>
        entry.RecordBlocked(new(item.Sequence, item.OperationId, disposition, Guid.NewGuid(),
            (entry.Blocked?.Revision ?? 0) + 1, message, evidence));
}
