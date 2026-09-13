using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class TransitionInverseSetupService(IDbContextFactory<LogicDbContext> dbContextFactory)
{
    public async Task<TransitionInverseSetupDraft?> GetDraftAsync(Guid sourceTransitionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rooms = await db.Rooms.AsNoTracking().ToListAsync(cancellationToken);
        var transitions = await db.RoomTransitions.AsNoTracking().ToListAsync(cancellationToken);
        return CreateDraft(transitions.SingleOrDefault(x => x.Id == sourceTransitionId), rooms, transitions);
    }

    public async Task<bool> ApplyAsync(Guid sourceTransitionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rooms = await db.Rooms.ToListAsync(cancellationToken);
        var transitions = await db.RoomTransitions.ToListAsync(cancellationToken);
        var source = transitions.SingleOrDefault(x => x.Id == sourceTransitionId);
        var draft = CreateDraft(source, rooms, transitions);
        if (draft is null)
        {
            return false;
        }

        var target = transitions.Single(x => x.Id == draft.TargetTransitionId);
        if (draft.FillDestinationRoomReference)
        {
            target.DestinationRoomReferenceText = draft.SourceRoomReferenceId;
        }

        if (draft.FillDestinationTransitionAlias)
        {
            target.DestinationTransitionAliasText = draft.SourceTransitionAlias;
        }

        await db.SaveChangesAsync(cancellationToken);
        await new LogicReferenceResolver(db).ResolveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public static TransitionInverseSetupDraft? CreateDraft(RoomTransition? source, IEnumerable<Room> rooms, IEnumerable<RoomTransition> transitions)
    {
        if (source is null || source.IsArchived || source.ResolvedDestinationRoomId is not { } targetRoomId || source.ResolvedDestinationTransitionId is not { } targetTransitionId)
        {
            return null;
        }

        var roomRows = rooms.ToArray();
        var transitionRows = transitions.ToArray();
        var sourceRoom = roomRows.SingleOrDefault(x => x.Id == source.RoomId);
        var targetRoom = roomRows.SingleOrDefault(x => x.Id == targetRoomId);
        var target = transitionRows.SingleOrDefault(x => x.Id == targetTransitionId);
        if (sourceRoom is null || sourceRoom.IsArchived || targetRoom is null || targetRoom.IsArchived || target is null || target.IsArchived || target.RoomId != targetRoomId)
        {
            return null;
        }

        var fillRoom = string.IsNullOrWhiteSpace(target.DestinationRoomReferenceText);
        var fillAlias = string.IsNullOrWhiteSpace(target.DestinationTransitionAliasText);
        if (!fillRoom && !fillAlias ||
            !UniquelyMatches(sourceRoom.ReferenceId, sourceRoom.Id, roomRows.Where(x => !x.IsArchived).Select(x => (x.Id, x.ReferenceId))) ||
            !UniquelyMatches(source.Alias, source.Id, transitionRows.Where(x => !x.IsArchived && x.RoomId == source.RoomId).Select(x => (x.Id, x.Alias))))
        {
            return null;
        }

        return new TransitionInverseSetupDraft(source.Id, sourceRoom.ReferenceId, source.Alias, source.FriendlyName, target.Id, target.FriendlyName, target.DestinationRoomReferenceText, target.DestinationTransitionAliasText, fillRoom, fillAlias);
    }

    private static bool UniquelyMatches(string value, Guid expectedId, IEnumerable<(Guid Id, string Value)> candidates)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var matches = candidates.Where(x => string.Equals(value.Trim(), x.Value.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 && matches[0].Id == expectedId;
    }
}

public sealed record TransitionInverseSetupDraft(
    Guid SourceTransitionId,
    string SourceRoomReferenceId,
    string SourceTransitionAlias,
    string SourceTransitionFriendlyName,
    Guid TargetTransitionId,
    string TargetTransitionFriendlyName,
    string? ExistingDestinationRoomReference,
    string? ExistingDestinationTransitionAlias,
    bool FillDestinationRoomReference,
    bool FillDestinationTransitionAlias);
