using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed partial class TransitionInverseSetupService
{
    public async Task<SceneInverseProposal?> PrepareSceneAsync(Guid sourceId, CancellationToken token)
    {
        var actor = await access.RequireAsync(Authentication.AccessPolicies.Author, token);
        await using var db = await dbContextFactory.CreateDbContextAsync(token);
        await using var snapshot = await db.Database.BeginTransactionAsync(token);
        var rooms = await db.Rooms.AsNoTracking().ToListAsync(token);
        var transitions = await db.RoomTransitions.AsNoTracking().ToListAsync(token);
        return SceneProposal(db, sourceId, rooms, transitions, actor.Owner);
    }

    public async Task<SceneInverseResult> ConfirmSceneAsync(SceneInverseConfirm command)
    {
        // Admission is independent of request disconnect, as for the confirmed import.
        using var imageAdmission = await sceneImages.AcquireLifecycleAsync(CancellationToken.None);
        using var publication = await publications.AcquireAsync();
        var actor = await access.RequireAsync(Authentication.AccessPolicies.Author);
        await using var db = await dbContextFactory.CreateDbContextAsync();
        await using var transaction = await LogicWriteScope.BeginAsync(db, publication, HistoryActor.From(actor), CancellationToken.None);
        var rooms = await db.Rooms.ToListAsync();
        var transitions = await db.RoomTransitions.ToListAsync();
        var proposal = SceneProposal(db, command.SourceId, rooms, transitions, actor.Owner);
        if (proposal is null || !string.Equals(proposal.Baseline, command.Baseline, StringComparison.Ordinal))
            throw new SceneEvidenceStaleException();
        var target = transitions.Single(t => t.Id == proposal.TargetId);
        if (proposal.FillRoom) target.DestinationRoomReferenceText = proposal.RoomReference;
        if (proposal.FillAlias) target.DestinationTransitionAliasText = proposal.Alias;
        await db.SaveChangesAsync();
        await new LogicReferenceResolver(db).ResolveAsync();
        await transaction.FinalizeAsync(CancellationToken.None);
        var plan = await SceneAffectedPublication.PrepareAsync(db, publication, transaction,
            [transitions.Single(t => t.Id == command.SourceId).RoomId, target.RoomId]);
        var result = new SceneInverseResult(true, null, null, plan.Recovery);
        await transaction.CommitAsync(() => publication.PublishCommitted(plan.Publication));
        try
        {
            await transaction.DisposeAsync();
            await using var snapshot = await db.Database.BeginTransactionAsync();
            return result with { Patch = await SceneAffectedPublication.ProjectAsync(db, plan, sceneImages) };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Inverse update committed; affected scalar projection unavailable.");
            return result;
        }
    }

    private static SceneInverseProposal? SceneProposal(LogicDbContext db, Guid sourceId, IReadOnlyList<Room> rooms, IReadOnlyList<RoomTransition> transitions, string owner)
    {
        var source = transitions.SingleOrDefault(t => t.Id == sourceId);
        var draft = CreateDraft(source, rooms, transitions);
        if (draft is null) return null;
        var target = transitions.Single(t => t.Id == draft.TargetTransitionId);
        var sourceRoom = rooms.Single(r => r.Id == source!.RoomId);
        var targetRoom = rooms.Single(r => r.Id == target.RoomId);
        // Exact persisted scalar proposal context, not client fill flags. Navigation
        // collections are excluded and eligibility uniqueness is re-evaluated above.
        object Scalars(object entity) => db.Model.FindEntityType(entity.GetType())!.GetProperties()
            .Where(p => entity is not Room || p.Name is not (nameof(Room.CacheFlagUtc) or nameof(Room.CompletionStatus)))
            .OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new { p.Name, Value = p.PropertyInfo!.GetValue(entity) }).ToArray();
        var baseline = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { Owner = owner, Source = Scalars(source!), Target = Scalars(target), SourceRoom = Scalars(sourceRoom), TargetRoom = Scalars(targetRoom), Draft = draft })));
        return new(sourceId, target.Id, draft.SourceTransitionFriendlyName, draft.TargetTransitionFriendlyName,
            draft.SourceRoomReferenceId, draft.SourceTransitionAlias, draft.FillDestinationRoomReference, draft.FillDestinationTransitionAlias, baseline);
    }
}
