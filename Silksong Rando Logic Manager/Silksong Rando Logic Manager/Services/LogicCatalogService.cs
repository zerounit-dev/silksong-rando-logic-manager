using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using System.Diagnostics;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed partial class LogicCatalogService(IDbContextFactory<LogicDbContext> dbContextFactory,
    RequirementValidationService requirementValidation, SharedCatalogueService sharedCatalogue, Authentication.LiveAccess access, SceneImageFileService? sceneImages = null,
    ScopedResolverTrace? scopedResolverTrace = null)
{
    private async Task<(SharedPublicationCoordinator.Admission Publication, HistoryActor Actor)> AcquireAuthorPublicationAsync(CancellationToken token)
    {
        var admission = await sharedCatalogue.AcquirePublicationAsync(token);
        try { return (admission, HistoryActor.From(await access.RequireAsync(Authentication.AccessPolicies.Author, token))); }
        catch { admission.Dispose(); throw; }
    }
    private static readonly LogicValidationService Validation = new();
    private static readonly ConnectionAnnotationService ConnectionAnnotations = new();
    private readonly RequirementValidationService requirementStatus = requirementValidation;

    public async Task<GroupEditorData?> GetGroupEditorDataAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(token: cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await db.Database.BeginTransactionAsync(cancellationToken);
        return await ReadGroupEditorAsync(db, groupId, cancellationToken);
    }

    private static async Task<GroupEditorData?> ReadGroupEditorAsync(LogicDbContext db, Guid groupId, CancellationToken cancellationToken)
    {
        var group = await db.RoomGroups.AsNoTracking()
            .Where(x => x.Id == groupId)
            .Select(x => new { x.Id, x.FriendlyName, x.ZoneReferenceText, x.IsVirtual, x.ResolvedMapZoneId, x.UpdatedUtc })
            .SingleOrDefaultAsync(cancellationToken);
        if (group is null)
        {
            return null;
        }

        var zones = await db.MapZones.AsNoTracking()
            .OrderBy(x => x.InGameId)
            .Select(x => new MapZoneReferenceTarget(x.Id, x.InGameId))
            .ToListAsync(cancellationToken);
        ReferenceResolutionStatus? status = string.IsNullOrWhiteSpace(group.ZoneReferenceText)
            ? null
            : LogicReferenceResolver.GetMapZoneReferenceStatus(group.ZoneReferenceText, group.ResolvedMapZoneId, zones);
        var view = new GroupEditorView(group.Id, group.FriendlyName, group.ZoneReferenceText, group.IsVirtual, group.UpdatedUtc);
        var baseline = new RoomGroupEditorBaseline(group.Id, group.FriendlyName, group.ZoneReferenceText, group.IsVirtual, group.UpdatedUtc);
        var draft = new RoomGroupEditorDraft(group.Id, group.FriendlyName, group.ZoneReferenceText, group.IsVirtual);
        return new GroupEditorData(view, baseline, draft, zones.Select(x => x.InGameId).ToList(), status);
    }

    internal static async Task<bool> TryAutoAssignRoomGroupFromMapZoneAsync(LogicDbContext db, Room room, CancellationToken cancellationToken = default)
    {
        if (room.IsArchived || room.RoomGroupId is not null || string.IsNullOrWhiteSpace(room.InGameId)) return false;
        var groups = await db.RoomGroups
            .Where(group => group.ResolvedMapZoneId != null)
            .Select(group => new RoomGroupZoneMatch(group.Id, group.ResolvedMapZoneId!.Value))
            .ToListAsync(cancellationToken);
        var mapScenes = await db.MapScenes
            .Select(scene => new MapSceneZoneMatch(scene.MapZoneId, scene.InGameId))
            .ToListAsync(cancellationToken);
        return TryAssignRoomGroupFromMapZone(room, groups, mapScenes);
    }

    internal async Task<CatalogCreateOutcome<Subroom>> CreateSubroomWithOutcomeAsync(Guid roomId, Subroom draft,
        RoomCreationAdmission? admission, CancellationToken cancellationToken)
    {
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(cancellationToken);
        using var publicationLease = publication;
        draft = new Subroom { FriendlyName = draft.FriendlyName, ReferenceId = draft.ReferenceId, Notes = draft.Notes };
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await LogicWriteScope.BeginAsync(db, publication, historyActor, cancellationToken);
        if (admission is not null)
        {
            await admission.CaptureAsync(db, roomId);
            if (await db.Subrooms.AnyAsync(x => x.Id == admission.EntityId, cancellationToken))
                throw new RoomCreationIdentityCollisionException();
        }
        var subroom = new Subroom
        {
            RoomId = roomId,
            FriendlyName = draft.FriendlyName,
            ReferenceId = draft.ReferenceId,
            Notes = draft.Notes,
            SortOrder = await NextSortOrderAsync(db.Subrooms.Where(x => x.RoomId == roomId && !x.IsArchived), cancellationToken)
        };
        if (admission is not null) subroom.Id = admission.EntityId;
        db.Subrooms.Add(subroom);
        await db.SaveChangesAsync(cancellationToken);
        var resolverStopwatch = Stopwatch.StartNew();
        await new LogicReferenceResolver(db, scopedResolverTrace).ResolveCreatedAsync(subroom, cancellationToken);
        resolverStopwatch.Stop();
        await transaction.FinalizeAsync(cancellationToken);
        var prepared = publication.PrepareAdvance(transaction.FinalEffects.RoomFactsChanged, false);
        string? historyWarning = null;
        if (admission is null)
        {
            historyWarning = (await transaction.CommitAsync(() => publication.PublishCommitted(prepared), cancellationToken)).Warning;
        }
        else await admission.CommitAsync(db, transaction, publication, prepared);
        return new(subroom, true, false, false, resolverStopwatch.Elapsed) { HistoryWarning = historyWarning };
    }

    internal async Task<CatalogCreateOutcome<SubroomConnection>> CreateConnectionWithOutcomeAsync(Guid roomId, SubroomConnection draft,
        RoomCreationAdmission? admission, CancellationToken cancellationToken)
    {
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(cancellationToken);
        using var publicationLease = publication;
        draft = new SubroomConnection { Alias = draft.Alias, FriendlyName = draft.FriendlyName, SourceSubroomReferenceText = draft.SourceSubroomReferenceText, DestinationSubroomReferenceText = draft.DestinationSubroomReferenceText, Requirements = draft.Requirements, Notes = draft.Notes, IsTodo = draft.IsTodo, IsVerified = draft.IsVerified };
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await LogicWriteScope.BeginAsync(db, publication, historyActor, cancellationToken);
        if (admission is not null)
        {
            await admission.CaptureAsync(db, roomId);
            if (await db.SubroomConnections.AnyAsync(x => x.Id == admission.EntityId, cancellationToken))
                throw new RoomCreationIdentityCollisionException();
        }
        var connection = new SubroomConnection
        {
            RoomId = roomId,
            FriendlyName = draft.FriendlyName,
            Alias = draft.Alias,
            SourceSubroomReferenceText = draft.SourceSubroomReferenceText,
            DestinationSubroomReferenceText = draft.DestinationSubroomReferenceText,
            Requirements = draft.Requirements,
            Notes = draft.Notes,
            IsTodo = draft.IsTodo,
            IsVerified = draft.IsVerified,
            SortOrder = await NextSortOrderAsync(db.SubroomConnections.Where(x => x.RoomId == roomId && !x.IsArchived), cancellationToken)
        };
        if (admission is not null) connection.Id = admission.EntityId;
        db.SubroomConnections.Add(connection);
        await db.SaveChangesAsync(cancellationToken);
        var resolverStopwatch = Stopwatch.StartNew();
        await new LogicReferenceResolver(db, scopedResolverTrace).ResolveCreatedAsync(connection, cancellationToken);
        resolverStopwatch.Stop();
        await ValidateRequirementsAsync(db, connection, RequirementBearingRowKind.Connection, cancellationToken);
        await transaction.FinalizeAsync(cancellationToken);
        var prepared = publication.PrepareAdvance(transaction.FinalEffects.RoomFactsChanged, false);
        string? historyWarning = null;
        if (admission is null)
        {
            historyWarning = (await transaction.CommitAsync(() => publication.PublishCommitted(prepared), cancellationToken)).Warning;
        }
        else await admission.CommitAsync(db, transaction, publication, prepared);
        return new(connection, true, connection.IsTodo, false, resolverStopwatch.Elapsed) { HistoryWarning = historyWarning };
    }

    internal async Task<CatalogCreateOutcome<CheckLocation>> CreateCheckWithOutcomeAsync(Guid roomId, CheckLocation draft,
        RoomCreationAdmission? admission, CancellationToken cancellationToken)
    {
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(cancellationToken);
        using var publicationLease = publication;
        draft = new CheckLocation { FriendlyName = draft.FriendlyName, SubroomReferenceText = draft.SubroomReferenceText, Requirements = draft.Requirements, Notes = draft.Notes, LocationType = draft.LocationType, IsTodo = draft.IsTodo, IsVerified = draft.IsVerified };
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await LogicWriteScope.BeginAsync(db, publication, historyActor, cancellationToken);
        if (admission is not null)
        {
            await admission.CaptureAsync(db, roomId);
            if (await db.CheckLocations.AnyAsync(x => x.Id == admission.EntityId, cancellationToken))
                throw new RoomCreationIdentityCollisionException();
        }
        var orderedChecks = await OrderedActiveChecksAsync(db, roomId, cancellationToken);
        var check = new CheckLocation
        {
            RoomId = roomId,
            FriendlyName = draft.FriendlyName,
            SubroomReferenceText = draft.SubroomReferenceText,
            Requirements = draft.Requirements,
            Notes = draft.Notes,
            LocationType = draft.LocationType,
            IsTodo = draft.IsTodo,
            IsVerified = draft.IsVerified
        };
        if (admission is not null) check.Id = admission.EntityId;
        db.CheckLocations.Add(check);
        orderedChecks.Add(check);
        NormalizeCheckOrder(orderedChecks, check.Id);
        await db.SaveChangesAsync(cancellationToken);
        var resolverStopwatch = Stopwatch.StartNew();
        await new LogicReferenceResolver(db, scopedResolverTrace).ResolveCreatedAsync(check, cancellationToken);
        resolverStopwatch.Stop();
        await ValidateRequirementsAsync(db, check, RequirementBearingRowKind.Check, cancellationToken);
        await transaction.FinalizeAsync(cancellationToken);
        var prepared = publication.PrepareAdvance(transaction.FinalEffects.RoomFactsChanged, false);
        string? historyWarning = null;
        if (admission is null)
        {
            historyWarning = (await transaction.CommitAsync(() => publication.PublishCommitted(prepared), cancellationToken)).Warning;
        }
        else await admission.CommitAsync(db, transaction, publication, prepared);
        return new(check, true, check.IsTodo, false, resolverStopwatch.Elapsed) { HistoryWarning = historyWarning };
    }

    private static Task<List<CheckLocation>> OrderedActiveChecksAsync(LogicDbContext db, Guid roomId, CancellationToken token) =>
        db.CheckLocations.Where(x => x.RoomId == roomId && !x.IsArchived)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(token);

    // The supplied list is the prior stable sequence (or the requested section
    // permutation). Only an actual cross-section move/new row appends to target.
    private static void NormalizeCheckOrder(IReadOnlyList<CheckLocation> rows, Guid? appendId = null)
    {
        var index = 0;
        foreach (var enemies in new[] { false, true })
        {
            var section = rows.Where(x => CheckLocationTypeCatalogue.IsEnemy(x.LocationType) == enemies);
            foreach (var row in section.Where(x => x.Id != appendId).Concat(section.Where(x => x.Id == appendId)))
                row.SortOrder = index++;
        }
    }

    private async Task ValidateRequirementsAsync(LogicDbContext db, ArchivableEntity row, RequirementBearingRowKind kind,
        CancellationToken cancellationToken)
    {
        var context = await requirementStatus.LoadContextAsync(db, cancellationToken);
        await ValidateRequirementsAsync(db, context, row, kind, cancellationToken);
    }

    private async Task ValidateRequirementsAsync(LogicDbContext db, RequirementValidationContext context,
        ArchivableEntity row, RequirementBearingRowKind kind, CancellationToken cancellationToken)
    {
        ApplyRequirementsStatus(context, row, kind);
        using (db.SuppressAuditMetadata()) await db.SaveChangesAsync(cancellationToken);
    }

    private void ApplyRequirementsStatus(RequirementValidationContext context, ArchivableEntity row,
        RequirementBearingRowKind kind)
    {
        if (row.IsArchived) return;
        var roomId = row switch
        {
            RoomTransition value => value.RoomId,
            SubroomConnection value => value.RoomId,
            CheckLocation value => value.RoomId,
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };
        if (!context.Rooms.Any(room => room.Id == roomId)) return;
        var requirements = row switch
        {
            RoomTransition value => value.Requirements,
            SubroomConnection value => value.Requirements,
            CheckLocation value => value.Requirements,
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };
        var status = requirementStatus.ValidateSafely(context, roomId, row.Id, kind, requirements);
        switch (row)
        {
            case RoomTransition value: value.RequirementsParseSucceeded = status; break;
            case SubroomConnection value: value.RequirementsParseSucceeded = status; break;
            case CheckLocation value: value.RequirementsParseSucceeded = status; break;
        }
    }

    private static bool TextMatches(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool RequiresResolution(AuditedEntity entity, IReadOnlySet<string> properties) => entity switch
    {
        Room => properties.Contains(nameof(Room.ReferenceId)),
        Subroom => properties.Contains(nameof(Subroom.ReferenceId)),
        RoomTransition => properties.Contains(nameof(RoomTransition.Alias)) || properties.Contains(nameof(RoomTransition.SourceSubroomReferenceText)) || properties.Contains(nameof(RoomTransition.DestinationRoomReferenceText)) || properties.Contains(nameof(RoomTransition.DestinationTransitionAliasText)),
        SubroomConnection => properties.Contains(nameof(SubroomConnection.SourceSubroomReferenceText)) || properties.Contains(nameof(SubroomConnection.DestinationSubroomReferenceText)),
        CheckLocation => properties.Contains(nameof(CheckLocation.SubroomReferenceText)),
        RoomGroup => properties.Contains(nameof(RoomGroup.ZoneReferenceText)),
        _ => false
    };

    private static bool RequiresSceneLayoutRefresh(AuditedEntity entity, IReadOnlySet<string> properties) => entity switch
    {
        Room => properties.Overlaps([nameof(Room.SceneMinX), nameof(Room.SceneMinY), nameof(Room.SceneMaxX), nameof(Room.SceneMaxY),
            nameof(Room.SceneImagePixelsPerUnitX), nameof(Room.SceneImagePixelsPerUnitY), nameof(Room.SceneImagePixelOffsetX), nameof(Room.SceneImagePixelOffsetY), nameof(Room.UseSceneImage)]),
        Subroom => properties.Overlaps([nameof(Subroom.FriendlyName), nameof(Subroom.SceneUnitX), nameof(Subroom.SceneUnitY), nameof(Subroom.SceneUnitWidth), nameof(Subroom.SceneUnitHeight)]),
        RoomTransition => properties.Overlaps([nameof(RoomTransition.FriendlyName), nameof(RoomTransition.Alias), nameof(RoomTransition.EnableAnnotation), nameof(RoomTransition.InGamePositionX), nameof(RoomTransition.InGamePositionY), nameof(RoomTransition.AnnotationSceneUnitX), nameof(RoomTransition.AnnotationSceneUnitY)]),
        SubroomConnection => properties.Overlaps([nameof(SubroomConnection.Alias), nameof(SubroomConnection.FriendlyName), nameof(SubroomConnection.EnableAnnotation), nameof(SubroomConnection.SceneUnitX), nameof(SubroomConnection.SceneUnitY)]),
        CheckLocation => properties.Overlaps([nameof(CheckLocation.FriendlyName), nameof(CheckLocation.EnableAnnotation), nameof(CheckLocation.InGamePositionX), nameof(CheckLocation.InGamePositionY), nameof(CheckLocation.AnnotationSceneUnitX), nameof(CheckLocation.AnnotationSceneUnitY)]),
        _ => false
    };

    private static async Task ValidateResolvedOwnershipAsync<TEntity>(LogicDbContext db, TEntity entity, CancellationToken cancellationToken) where TEntity : AuditedEntity
    {
        switch (entity)
        {
            case RoomTransition transition when transition.ResolvedSourceSubroomId is { } sourceSubroomId && !await db.Subrooms.AnyAsync(x => x.Id == sourceSubroomId && x.RoomId == transition.RoomId, cancellationToken):
                throw new InvalidOperationException("Resolved transition source subroom must belong to the owning room.");
            case RoomTransition transition when transition.ResolvedDestinationTransitionId is { } destinationTransitionId && transition.ResolvedDestinationRoomId is { } destinationRoomId && !await db.RoomTransitions.AnyAsync(x => x.Id == destinationTransitionId && x.RoomId == destinationRoomId, cancellationToken):
                throw new InvalidOperationException("Resolved transition destination must belong to the resolved destination room.");
            case SubroomConnection connection when connection.ResolvedSourceSubroomId is { } sourceId && !await db.Subrooms.AnyAsync(x => x.Id == sourceId && x.RoomId == connection.RoomId, cancellationToken):
                throw new InvalidOperationException("Resolved connection source subroom must belong to the owning room.");
            case SubroomConnection connection when connection.ResolvedDestinationSubroomId is { } destinationId && !await db.Subrooms.AnyAsync(x => x.Id == destinationId && x.RoomId == connection.RoomId, cancellationToken):
                throw new InvalidOperationException("Resolved connection destination subroom must belong to the owning room.");
            case CheckLocation check when check.ResolvedSubroomId is { } subroomId && !await db.Subrooms.AnyAsync(x => x.Id == subroomId && x.RoomId == check.RoomId, cancellationToken):
                throw new InvalidOperationException("Resolved check subroom must belong to the owning room.");
        }
    }

    private static bool ValidRectangle(double? x, double? y, double? width, double? height) => x is { } xValue && y is { } yValue && width is { } widthValue && height is { } heightValue && double.IsFinite(xValue) && double.IsFinite(yValue) && double.IsFinite(widthValue) && double.IsFinite(heightValue) && widthValue > 0 && heightValue > 0;

    private static async Task<int> NextSortOrderAsync<TEntity>(IQueryable<TEntity> query, CancellationToken cancellationToken) where TEntity : class
    {
        var sortOrder = await query.Select(x => (int?)EF.Property<int>(x, "SortOrder")).MaxAsync(cancellationToken);
        return (sortOrder ?? -1) + 1;
    }

    private static async Task<string> CreateUniqueRoomReferenceIdAsync(LogicDbContext db, string friendlyName, CancellationToken cancellationToken)
    {
        var baseId = CreateReferenceId(friendlyName);
        var candidate = baseId;
        var suffix = 2;
        while (await db.Rooms.AnyAsync(x => !x.IsArchived && x.ReferenceId == candidate, cancellationToken))
        {
            candidate = $"{baseId}-{suffix++}";
        }
        return candidate;
    }

    private static string CreateReferenceId(string value)
    {
        var characters = value.Trim().ToLowerInvariant().Select(x => char.IsLetterOrDigit(x) ? x : '-').ToArray();
        var compact = string.Join('-', new string(characters).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(compact) ? "room" : compact;
    }

    private static bool TryAssignRoomGroupFromMapZone(Room room, IEnumerable<RoomGroupZoneMatch> groups, IEnumerable<MapSceneZoneMatch> mapScenes)
    {
        var zoneIds = mapScenes
            .Where(scene => string.Equals(room.InGameId!.Trim(), scene.InGameId.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(scene => scene.MapZoneId)
            .Distinct()
            .ToList();
        if (zoneIds.Count != 1) return false;

        var groupIds = groups.Where(group => group.MapZoneId == zoneIds[0]).Select(group => group.Id).ToList();
        if (groupIds.Count != 1) return false;

        room.RoomGroupId = groupIds[0];
        return true;
    }

}

internal sealed record RoomGroupZoneMatch(Guid Id, Guid MapZoneId);
internal sealed record MapSceneZoneMatch(Guid MapZoneId, string InGameId);

public sealed record GroupEditorView(Guid Id, string FriendlyName, string? ZoneReferenceText, bool IsVirtual, DateTime UpdatedUtc);
public sealed record RoomGroupEditorBaseline(Guid Id, string FriendlyName, string? ZoneReferenceText, bool IsVirtual, DateTime UpdatedUtc);
public sealed class RoomGroupEditorDraft(Guid id, string friendlyName, string? zoneReferenceText, bool isVirtual)
{
    public Guid Id { get; } = id;
    public string FriendlyName { get; set; } = friendlyName;
    public string? ZoneReferenceText { get; set; } = zoneReferenceText;
    public bool IsVirtual { get; set; } = isVirtual;
}
public sealed record GroupEditorData(
    GroupEditorView View,
    RoomGroupEditorBaseline Baseline,
    RoomGroupEditorDraft Draft,
    IReadOnlyList<string> ZoneIds,
    ReferenceResolutionStatus? ZoneReferenceStatus);
internal sealed record MapZoneReferenceTarget(Guid Id, string InGameId);

public sealed record CatalogSaveOutcome(bool Changed, bool Resolved, bool RequiresDocumentRefresh, bool RequiresSidebarRefresh, bool RequiresSceneLayoutRefresh, TimeSpan ResolverElapsed)
{
    public static CatalogSaveOutcome Unchanged { get; } = new(false, false, false, false, false, TimeSpan.Zero);
}

public sealed record CatalogCreateOutcome<TEntity>(TEntity Entity, bool RequiresDocumentRefresh, bool RequiresSidebarRefresh, bool RequiresSceneLayoutRefresh, TimeSpan ResolverElapsed) where TEntity : AuditedEntity
{
    public string? HistoryWarning { get; init; }
}
