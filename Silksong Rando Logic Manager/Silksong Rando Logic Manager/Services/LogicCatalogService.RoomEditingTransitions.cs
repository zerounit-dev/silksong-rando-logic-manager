using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

// Server-only preparation evidence. It does not replace an operation's original
// baseline and is never accepted back from the browser as a replacement source.
internal sealed record RoomInverseSourceObservation(DateTime UpdatedUtc,
    Wire.RoomTransitionValues Values, Guid? DestinationRoomId, Guid? DestinationTransitionId);

public sealed partial class LogicCatalogService
{
    internal Task<RoomMutationEffect?> ExecuteTrackedTransitionAsync(RoomEditingTracker.Entry tracker)
        => ExecuteTrackedTransitionCoreAsync(tracker, null);

    internal Task<RoomMutationEffect?> ResolveTrackedTransitionAsync(RoomEditingTracker.Entry tracker,
        Wire.RoomHeadResolution resolution)
    {
        switch (resolution)
        {
            case Wire.ChooseRoomConflictValues choice when tracker.Blocked?.Evidence is Wire.RoomTransitionConflictEvidence conflict:
                tracker.TransitionReview = new(conflict.Reviewed, choice.Choice);
                return ExecuteTrackedTransitionCoreAsync(tracker, null);
            case Wire.PrepareRoomProposalAgain when tracker.Blocked is
                { Disposition: Wire.RoomBlockedDisposition.ProposalStale, Evidence: Wire.RoomInverseProposalEvidence }:
                return ExecuteTrackedTransitionCoreAsync(tracker, null);
            case Wire.ChooseRoomProposal choice when tracker.Blocked is
                { Disposition: Wire.RoomBlockedDisposition.ProposalAwaitingChoice, Evidence: Wire.RoomInverseProposalEvidence proposal }:
                if (proposal.TargetOnly && choice.Choice is Wire.RoomProposalChoice.Revert or Wire.RoomProposalChoice.SourceOnly)
                    return ExecuteTrackedTransitionCoreAsync(tracker, Wire.RoomProposalChoice.SourceOnly);
                if (choice.Choice == Wire.RoomProposalChoice.Revert)
                {
                    var head = tracker.CurrentHead ?? throw new RoomTrackingProtocolException("There is no admitted transition operation.");
                    tracker.RecordReceipt(new(head.Sequence, head.OperationId,
                        Wire.RoomTerminalDisposition.UserReverted,
                        null, [], null));
                    return Task.FromResult<RoomMutationEffect?>(new(tracker.Identity.RoomId, [], [],
                        RoomProjectionFamilies.None, Wire.RoomSceneConcerns.None, new([], [], [], [])));
                }
                return ExecuteTrackedTransitionCoreAsync(tracker, choice.Choice);
            default: throw new RoomTrackingProtocolException("The resolution does not match the transition workflow.");
        }
    }

    private async Task<RoomMutationEffect?> ExecuteTrackedTransitionCoreAsync(RoomEditingTracker.Entry tracker,
        Wire.RoomProposalChoice? choice)
    {
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(CancellationToken.None);
        using var publicationLease = publication;
        var operation = tracker.CurrentHead?.Operation ?? throw new RoomTrackingProtocolException("There is no admitted transition operation.");
        var (id, creating, targetOnly) = operation switch
        {
            Wire.CreateRoomTransition x => (x.EntityId, true, false),
            Wire.SaveRoomTransition x => (x.EntityId, false, false),
            Wire.RequestRoomTransitionInverse x => (x.EntityId, false, true),
            _ => throw new RoomTrackingProtocolException("The head is not a transition authoring operation.")
        };
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
        var room = await db.Rooms.AsNoTracking().Where(x => x.Id == tracker.Identity.RoomId)
            .Select(x => new { x.Id, x.ReferenceId, x.IsArchived }).SingleOrDefaultAsync();
        if (room is null) { RoomEditingMutationScope.Missing(tracker, tracker.Identity.RoomId); return null; }
        var source = await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == id);
        if (creating && source is not null)
        {
            RoomEditingMutationScope.Corrective(tracker, id,
                "The permanent creation identity already exists. Nothing was created. Copy pending work before reloading.");
            return null;
        }
        if (!creating && (source is null || source.RoomId != room.Id || source.IsArchived))
        { RoomEditingMutationScope.Missing(tracker, id); return null; }
        var current = source is null ? null : TransitionValues(source);
        Wire.RoomTransitionValues value;
        if (operation is Wire.CreateRoomTransition create) value = create.Submitted;
        else if (operation is Wire.SaveRoomTransition save)
        {
            var merge = Wire.RoomEditingMerge.Transition(save.Origin.Original, save.Submitted, current!, tracker.TransitionReview);
            if (!merge.CanApply)
            {
                tracker.TransitionReview = null;
                tracker.InverseSource = null;
                RoomEditingMutationScope.Block(tracker, Wire.RoomBlockedDisposition.ConflictAwaitingChoice,
                    merge.ReviewSuperseded ? "The reviewed transition values changed again. Review the current values." : "This transition changed elsewhere.",
                    new Wire.RoomTransitionConflictEvidence(current!, save.Submitted,
                        Wire.RoomEditingMerge.Transition(save.Origin.Original, save.Submitted, current!).DivergentFields));
                return null;
            }
            value = merge.Values;
        }
        else
        {
            // The explicit inverse action is target-only. It never turns a local
            // draft or an intervening source edit into an authored source save.
            var request = (Wire.RequestRoomTransitionInverse)operation;
            if (choice is null && current != request.Origin.Original)
            {
                RoomEditingMutationScope.Corrective(tracker, id,
                    "The source transition changed before inverse setup. Copy pending work before reloading.");
                return null;
            }
            value = current!;
        }

        Wire.RoomInverseProposalEvidence? captured = null;
        RoomTransition? target = null;
        if (choice is not null)
        {
            captured = tracker.Blocked?.Evidence as Wire.RoomInverseProposalEvidence
                ?? throw new RoomTrackingProtocolException("The captured inverse proposal is unavailable.");
            if (captured.SourceId != id || captured.CreatingSource != creating || captured.TargetOnly != targetOnly)
                throw new RoomTrackingProtocolException("The captured inverse source does not match the head.");
            if (choice == Wire.RoomProposalChoice.UpdateCapturedTargets)
            {
                // Re-derive eligibility only to verify the captured target. Never
                // substitute a newly discovered target or silently widen this Apply.
                var eligible = await FindTrackedInverseAsync(db, room.Id, room.ReferenceId, room.IsArchived, id, value, source, targetOnly, creating);
                target = await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == captured.TargetTransitionId);
                // Source FriendlyName is display copy, not inverse eligibility. A
                // disjoint external name edit must survive the source field merge.
                if (eligible is null || (eligible with { SourceFriendlyName = captured.SourceFriendlyName }) != captured || target is null
                    || target.UpdatedUtc != captured.TargetUpdatedUtc
                    || target.DestinationRoomReferenceText != captured.ExpectedDestinationRoomReferenceText
                    || target.DestinationTransitionAliasText != captured.ExpectedDestinationTransitionAliasText
                    || targetOnly && (tracker.InverseSource is not { } reviewed
                        || source!.UpdatedUtc != reviewed.UpdatedUtc || current != reviewed.Values
                        || source.ResolvedDestinationRoomId != reviewed.DestinationRoomId
                        || source.ResolvedDestinationTransitionId != reviewed.DestinationTransitionId))
                { StaleTrackedInverse(tracker, captured); return null; }
            }
        }
        else if (targetOnly || PotentialTrackedInverse(operation, value))
        {
            var candidate = await FindTrackedInverseAsync(db, room.Id, room.ReferenceId, room.IsArchived, id, value, source, targetOnly, creating);
            if (candidate is not null)
            {
                tracker.InverseSource = source is null ? null : new(source.UpdatedUtc, current!, source.ResolvedDestinationRoomId, source.ResolvedDestinationTransitionId);
                RoomEditingMutationScope.Block(tracker, Wire.RoomBlockedDisposition.ProposalAwaitingChoice,
                    "Review the captured inverse before applying. Only eligible blank destination fields will be filled.", candidate);
                return null;
            }
            if (targetOnly)
            {
                RoomEditingMutationScope.Corrective(tracker, id,
                    "This inverse setup is no longer applicable. Copy pending work before reloading.");
                return null;
            }
        }

        var scope = await RoomEditingMutationScope.CaptureAsync(db, tracker, publication);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var originalText = source is null ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : db.Entry(source).Properties.ToDictionary(x => x.Metadata.Name, x => x.OriginalValue as string, StringComparer.Ordinal);
        if (creating)
        {
            source = new RoomTransition { Id = id, RoomId = room.Id,
                SortOrder = await NextSortOrderAsync(db.RoomTransitions.Where(x => x.RoomId == room.Id && !x.IsArchived), CancellationToken.None) };
            ApplyTransitionValues(source, value);
            db.RoomTransitions.Add(source);
        }
        else if (!targetOnly)
        {
            var draft = TransitionFieldDiffMapper.Clone(source!);
            ApplyTransitionValues(draft, value);
            fields = TransitionFieldDiffMapper.Differences(source!, draft).ToHashSet(StringComparer.Ordinal);
            TransitionFieldDiffMapper.Apply(source!, draft, fields);
        }
        var targetFields = new HashSet<string>(StringComparer.Ordinal);
        if (target is not null)
        {
            if (captured!.FillDestinationRoomReferenceText)
            { target.DestinationRoomReferenceText = captured.SourceRoomReferenceId; targetFields.Add(nameof(RoomTransition.DestinationRoomReferenceText)); }
            if (captured.FillDestinationTransitionAliasText)
            { target.DestinationTransitionAliasText = captured.SourceAlias; targetFields.Add(nameof(RoomTransition.DestinationTransitionAliasText)); }
        }
        await ValidateResolvedOwnershipAsync(db, source!, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        var resolver = new LogicReferenceResolver(db, scopedResolverTrace);
        if (creating) await resolver.ResolveCreatedAsync(source!, CancellationToken.None);
        else if (RequiresResolution(source!, fields)) await resolver.ResolveScopedAsync(source!, fields, originalText, CancellationToken.None);
        if (target is not null) await resolver.ResolveScopedAsync(target, targetFields, new Dictionary<string, string?>(StringComparer.Ordinal), CancellationToken.None);
        if (creating || fields.Contains(nameof(RoomTransition.Requirements)))
            await ValidateRequirementsAsync(db, source!, RequirementBearingRowKind.Transition, CancellationToken.None);
        var scene = creating ? Wire.RoomSceneConcerns.Canvas | Wire.RoomSceneConcerns.Viewport
            : fields.Overlaps([nameof(RoomTransition.Alias), nameof(RoomTransition.FriendlyName), nameof(RoomTransition.SourceSubroomReferenceText)])
                ? Wire.RoomSceneConcerns.Canvas : Wire.RoomSceneConcerns.None;
        return await scope.CompleteAsync(db, tx, RoomProjectionFamilies.Transitions, scene, createdId: creating ? id : null);
    }

    private static bool PotentialTrackedInverse(Wire.RoomEditOperation operation, Wire.RoomTransitionValues value)
        => !string.IsNullOrWhiteSpace(value.Alias) && !string.IsNullOrWhiteSpace(value.DestinationRoomReferenceText)
            && !string.IsNullOrWhiteSpace(value.DestinationTransitionAliasText)
            && (operation is Wire.CreateRoomTransition || operation is Wire.SaveRoomTransition save
                && (save.Origin.Original.DestinationRoomReferenceText != save.Submitted.DestinationRoomReferenceText
                    || save.Origin.Original.DestinationTransitionAliasText != save.Submitted.DestinationTransitionAliasText));

    private static async Task<Wire.RoomInverseProposalEvidence?> FindTrackedInverseAsync(LogicDbContext db,
        Guid roomId, string roomReference, bool roomArchived, Guid sourceId, Wire.RoomTransitionValues value,
        RoomTransition? source, bool targetOnly, bool creating)
    {
        if (roomArchived || string.IsNullOrWhiteSpace(roomReference) || string.IsNullOrWhiteSpace(value.Alias)) return null;
        var roomReferences = await db.Rooms.AsNoTracking().Where(x => !x.IsArchived)
            .Select(x => new { x.Id, x.ReferenceId }).ToListAsync();
        if (roomReferences.Count(x => TextMatches(x.ReferenceId, roomReference)) != 1) return null;
        var aliases = await db.RoomTransitions.AsNoTracking().Where(x => !x.IsArchived && x.RoomId == roomId)
            .Select(x => new { x.Id, x.Alias }).ToListAsync();
        if (aliases.Count(x => TextMatches(x.Id == sourceId ? value.Alias : x.Alias, value.Alias)) != (creating ? 0 : 1)) return null;
        Guid destinationRoomId;
        Guid? resolvedTargetId = null;
        if (targetOnly)
        {
            if (source is null || source.ResolvedDestinationRoomId is not Guid resolvedRoom || source.ResolvedDestinationTransitionId is not Guid resolvedTarget
                || !roomReferences.Any(x => x.Id == resolvedRoom)) return null;
            destinationRoomId = resolvedRoom;
            resolvedTargetId = resolvedTarget;
        }
        else
        {
            var matches = roomReferences.Where(x => TextMatches(x.ReferenceId, value.DestinationRoomReferenceText)).ToArray();
            if (matches.Length != 1) return null;
            destinationRoomId = matches[0].Id;
        }
        var targets = await db.RoomTransitions.AsNoTracking().Where(x => !x.IsArchived && x.RoomId == destinationRoomId)
            .Select(x => new { x.Id, x.Alias, x.FriendlyName, x.UpdatedUtc, x.DestinationRoomReferenceText, x.DestinationTransitionAliasText }).ToListAsync();
        var eligible = targets.Where(x => targetOnly ? x.Id == resolvedTargetId : TextMatches(x.Alias, value.DestinationTransitionAliasText)).ToArray();
        if (eligible.Length != 1) return null;
        var target = eligible[0];
        var fillRoom = string.IsNullOrWhiteSpace(target.DestinationRoomReferenceText);
        var fillAlias = string.IsNullOrWhiteSpace(target.DestinationTransitionAliasText);
        return !fillRoom && !fillAlias ? null : new(sourceId, destinationRoomId, target.Id, target.UpdatedUtc,
            target.DestinationRoomReferenceText, target.DestinationTransitionAliasText, fillRoom, fillAlias,
            roomReference, value.Alias, value.FriendlyName, target.FriendlyName, targetOnly, creating);
    }

    private static Wire.RoomTransitionValues TransitionValues(RoomTransition x) => new(x.Alias, x.FriendlyName,
        x.SourceSubroomReferenceText, x.DestinationRoomReferenceText, x.DestinationTransitionAliasText,
        x.Requirements, x.Notes, x.IsTodo, x.IsVerified, x.IsUnsafeArrival);

    private static void ApplyTransitionValues(RoomTransition row, Wire.RoomTransitionValues value)
    {
        row.Alias = value.Alias; row.FriendlyName = value.FriendlyName;
        row.SourceSubroomReferenceText = value.SourceSubroomReferenceText;
        row.DestinationRoomReferenceText = value.DestinationRoomReferenceText;
        row.DestinationTransitionAliasText = value.DestinationTransitionAliasText;
        row.Requirements = value.Requirements; row.Notes = value.Notes;
        row.IsTodo = value.IsTodo; row.IsVerified = value.IsVerified; row.IsUnsafeArrival = value.IsUnsafeArrival;
    }

    private static void StaleTrackedInverse(RoomEditingTracker.Entry tracker, Wire.RoomInverseProposalEvidence captured) =>
        RoomEditingMutationScope.Block(tracker, Wire.RoomBlockedDisposition.ProposalStale,
            "The captured inverse source or target changed. Prepare a fresh proposal before applying.", captured);
}
