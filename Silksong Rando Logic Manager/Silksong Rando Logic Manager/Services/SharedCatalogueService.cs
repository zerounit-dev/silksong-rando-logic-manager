using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class SharedCatalogueService(IDbContextFactory<LogicDbContext> contexts,
    SceneImageFileService images, SharedPublicationCoordinator publications)
{
    public Task<IDisposable> AcquireImagesAsync(CancellationToken token) => images.AcquireLifecycleAsync(token);
    public Task<SharedPublicationCoordinator.Admission> AcquirePublicationAsync(CancellationToken token) => publications.AcquireAsync(token);

    // Caller owns lifecycle/publication admission and its existing authoritative
    // transaction. Only affected aggregates are materialized, never the world.
    internal Task<Wire.SharedMutationSegment> ReadMutationAsync(LogicDbContext db,
        SharedRoomCommitPlan plan, CancellationToken token) => AssembleMutationAsync(db, plan, images, token);

    // Shared assembly for callers already owning their authoritative transaction
    // and lifecycle scope, including scene/inverse reliable postcommit projection.
    internal static async Task<Wire.SharedMutationSegment> AssembleMutationAsync(LogicDbContext db,
        SharedRoomCommitPlan plan, SceneImageFileService images, CancellationToken token)
    {
        var selected = plan.Rooms.Where(x => x.Value.Final is not null).Select(x => x.Key).ToArray();
        var rooms = await RoomEditingDocumentLoader.ReadScalarsAsync(db, selected, images, token);
        if (rooms.Length != selected.Length) throw new InvalidOperationException("Missing selected room publication.");
        return new(new(plan.Publication.Timestamps.Rooms, true,
            rooms.Select(x => new Wire.RoomFactChange(plan.Rooms[x.Observation.RoomId].Original?.Observation, x)).ToArray(),
            plan.Rooms.Values.Where(x => x.Original is not null && x.Final is null).Select(x => new Wire.RoomFactRemoval(x.Original!.Observation)).ToArray(),
            plan.Groups.Values.Select(x => new Wire.GroupFactChange(x.Original is { } old ? Group(old) : null,
                x.Final is { } current ? Group(current) : null)).ToArray(),
            plan.MembershipOrders, plan.GroupOrder?.ToArray()), null);
    }

    internal async Task<Wire.SharedMutationSegment> ReadCommittedMutationAsync(SharedRoomCommitPlan plan)
    {
        using var imageAdmission = await images.AcquireLifecycleAsync(CancellationToken.None);
        using var publication = await publications.AcquireAsync(CancellationToken.None);
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        await using var snapshot = await db.Database.BeginTransactionAsync(CancellationToken.None);
        await plan.VerifyAsync(db, CancellationToken.None);
        return await ReadMutationAsync(db, plan, CancellationToken.None);
    }

    internal Task<Wire.SharedMutationSegment?> ReadRecoveredMutationAsync(Wire.RoomTerminalReceipt receipt,
        IReadOnlyList<Wire.RoomCompleteDocument> documents, IReadOnlyList<Guid> deleted)
    {
        if (receipt.SharedTimestamps is not { } timestamps) return Task.FromResult<Wire.SharedMutationSegment?>(null);
        var original = receipt.SharedOriginalRooms.ToDictionary(x => x.RoomId);
        // Each document already owns its coherent dependency/order read. A second
        // group query could delete/change that dependency after the room snapshot.
        // Contradictory dependency bodies cannot form one coherent shared candidate;
        // the caller's existing pending-projection recovery retains the receipt.
        var groups = new Dictionary<Guid, Wire.CatalogueGroup>();
        foreach (var document in documents)
        {
            if (document.ReadContext is not { } read || read.RoomOrder.IsDefault || read.GroupOrder.IsDefault
                || !read.RoomOrder.Contains(document.Observation.RoomId))
                throw new InvalidOperationException("Missing recovered room read context.");
            if (document.Header.RoomGroupId is { } groupId)
            {
                if (read.Group is not { } group || group.Id != groupId || !read.GroupOrder.Contains(groupId)
                    || groups.TryGetValue(groupId, out var previous) && previous != group)
                    throw new InvalidOperationException("Inconsistent recovered room group dependency.");
                groups[groupId] = group;
            }
            else if (read.Group is not null) throw new InvalidOperationException("Unexpected recovered room group dependency.");
        }
        // Recovery reads documents sequentially. The last document's read supplies
        // its actual SQLite order, not a new query or a comprehensive absence claim.
        var ordering = documents.LastOrDefault()?.ReadContext;
        // These are independent later reads. Never certify the original bracket,
        // even when all requested documents happened to remain at their old flags.
        return Task.FromResult<Wire.SharedMutationSegment?>(new(new(timestamps.Rooms, false,
            documents.Select(x => new Wire.RoomFactChange(original.GetValueOrDefault(x.Observation.RoomId),
                new(x.Observation, x.Header, x.Subrooms, x.Transitions, x.Connections, x.Checks, x.Explanations))).ToArray(),
            deleted.Where(original.ContainsKey).Select(id => new Wire.RoomFactRemoval(original[id])).ToArray(),
            groups.Values.Select(x => new Wire.GroupFactChange(null, x)).ToArray(),
            [], ordering?.GroupOrder.ToArray()) { ReadPresentationOrder = ordering?.RoomOrder.ToArray() }, null));
    }

    private static Wire.CatalogueGroup Group(SharedPublicationCoordinator.GroupWriteFact x) => new(x.Id,
        x.FriendlyName, x.SortOrder, x.ZoneReferenceText, x.ResolvedMapZoneId, x.IsVirtual, DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc));

    internal static async Task<Wire.CatalogueVocabulary> ReadVocabularyAsync(LogicDbContext db, DateTime revision, CancellationToken token) => new(revision,
        await db.RequirementPredicates.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new Wire.VocabularyDefinition(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, x.InputSyntax, x.OutputSyntax, x.Notes)).ToArrayAsync(token),
        await db.RequirementCrests.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new Wire.VocabularyDefinition(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, null, x.OutputValue, x.Notes)).ToArrayAsync(token),
        await db.RequirementMethods.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new Wire.VocabularyDefinition(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, null, x.OutputValue, x.Notes)).ToArrayAsync(token),
        await db.RequirementItems.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new Wire.VocabularyDefinition(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, null, x.OutputValue, x.Notes)).ToArrayAsync(token),
        await db.RequirementCountables.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new Wire.VocabularyDefinition(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, null, x.OutputValue, x.Notes)).ToArrayAsync(token),
        await db.RequirementSouls.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new Wire.VocabularyDefinition(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, null, x.OutputValue, x.Notes)).ToArrayAsync(token));
}
