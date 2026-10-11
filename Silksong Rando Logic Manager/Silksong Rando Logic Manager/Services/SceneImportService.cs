using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class SceneImportService(IDbContextFactory<LogicDbContext> dbContextFactory,
    RequirementValidationService requirementValidation, SceneImageFileService? sceneImages = null)
{
    private readonly RequirementValidationService requirementStatus = requirementValidation;
    public async Task<SceneImportPreview> PreviewAsync(SceneDumpReview review, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await PreviewAsync(db, review, cancellationToken);
    }

    // BI1 keeps preview reads in the same logical snapshot as its database fingerprint.
    internal static async Task<SceneImportPreview> PreviewAsync(LogicDbContext db, SceneDumpReview review, CancellationToken cancellationToken)
    {
        var rooms = await db.Rooms.AsNoTracking().Where(x => !x.IsArchived)
            .Select(x => new SceneImportRoom(x.Id, x.FriendlyName, x.ReferenceId, x.InGameId, x.SceneMaxX, x.SceneMaxY)).ToListAsync(cancellationToken);
        var transitions = await db.RoomTransitions.AsNoTracking().Where(x => !x.IsArchived && !x.Room.IsArchived)
            .Select(x => new SceneImportSelectableRecord(x.Id, x.RoomId, SceneDumpClassification.Exit, x.FriendlyName, x.InGameId)).ToListAsync(cancellationToken);
        var checks = await db.CheckLocations.AsNoTracking().Where(x => !x.IsArchived && !x.Room.IsArchived)
            .Select(x => new SceneImportSelectableRecord(x.Id, x.RoomId, SceneDumpClassification.Check, x.FriendlyName, x.InGameId)).ToListAsync(cancellationToken);
        var enemyNames = await db.RequirementEnemyTypes.AsNoTracking()
            .Select(x => new SceneImportEnemyName(x.Id, x.OutputValue, x.Name)).ToListAsync(cancellationToken);
        var sceneRooms = rooms.Where(x => Matches(x.InGameId, review.SceneName)).ToArray();
        var matches = Flatten(review.RootObjects)
            .Select(node => MatchObject(node, transitions, checks))
            .ToArray();
        return new SceneImportPreview(sceneRooms, matches, transitions, checks, enemyNames.AsReadOnly());
    }

    public async Task<SceneImportResult> ImportAsync(SceneDumpReview review, Guid? targetRoomId, IReadOnlySet<Guid> selectedObjectIds, SceneUnitSize? replacementDimensions = null, CancellationToken cancellationToken = default)
    {
        var preview = await PreviewAsync(review, cancellationToken);
        var roomId = targetRoomId ?? (preview.MatchingRooms.Count == 1 ? preview.MatchingRooms[0].Id : (Guid?)null);
        var nodes = Flatten(review.RootObjects).Where(node => selectedObjectIds.Contains(node.Id)).ToArray();
        if (nodes.Length != selectedObjectIds.Count || nodes.Any(node => !SceneDumpIdentity.IsImportable(node.Classification)))
            throw new InvalidOperationException("Import instructions must select an exit, check or enemy scene object.");
        var projection = new SceneReviewProjectionService();
        var identityCounts = projection.CountCheckIdentities(Flatten(review.RootObjects));
        var drafts = nodes.ToDictionary(node => node.Id, node => projection.CreateDraft(node, preview, roomId, identityCounts));
        projection.AllocateNames(nodes, preview, roomId, drafts);
        return await ImportAsync(review, targetRoomId, nodes.Select(node => drafts[node.Id])
            .Select(draft => new SceneImportInstruction(draft.ObjectId, draft.Mode, draft.ExistingRecordId, draft.FriendlyName)).ToArray(), replacementDimensions, cancellationToken);
    }

    public Task<SceneImportResult> ImportAsync(SceneDumpReview review, Guid? targetRoomId, IReadOnlyList<SceneImportInstruction> instructions, SceneUnitSize? replacementDimensions = null, CancellationToken cancellationToken = default) =>
        ImportCoreAsync(review, targetRoomId, instructions, replacementDimensions, false, cancellationToken);

    /// <summary>BI1 admission: one existing active room, Enemy instructions only, no dimension/group changes.</summary>
    public Task<SceneImportResult> ImportEnemiesIntoExistingRoomAsync(SceneDumpReview review, Guid targetRoomId,
        IReadOnlyList<SceneImportInstruction> instructions, CancellationToken cancellationToken = default) =>
        ImportCoreAsync(review, targetRoomId, instructions, null, true, cancellationToken);

    private async Task<SceneImportResult> ImportCoreAsync(SceneDumpReview review, Guid? targetRoomId,
        IReadOnlyList<SceneImportInstruction> instructions, SceneUnitSize? replacementDimensions,
        bool existingRoomEnemiesOnly, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rooms = await db.Rooms.Where(x => !x.IsArchived).ToListAsync(cancellationToken);
        var transitions = await db.RoomTransitions.Where(x => !x.IsArchived).ToListAsync(cancellationToken);
        var checks = await db.CheckLocations.Where(x => !x.IsArchived).ToListAsync(cancellationToken);
        var matchingRooms = rooms.Where(x => Matches(x.InGameId, review.SceneName)).ToArray();
        if (existingRoomEnemiesOnly && (matchingRooms.Length != 1 || matchingRooms[0].Id != targetRoomId || instructions.Count == 0))
            throw new InvalidOperationException("BI1 requires a unique existing active scene-name match and nonempty Enemy selection.");
        Room targetRoom;

        if (targetRoomId is { } selectedRoomId)
        {
            targetRoom = matchingRooms.SingleOrDefault(x => x.Id == selectedRoomId)
                ?? throw new InvalidOperationException("The selected target room is no longer an active scene-name match.");
        }
        else if (matchingRooms.Length == 0)
        {
            targetRoom = new Room
            {
                FriendlyName = review.SceneName,
                InGameId = review.SceneName,
                ReferenceId = await CreateUniqueReferenceIdAsync(db, review.SceneName, cancellationToken),
                SortOrder = await NextSortOrderAsync(db.Rooms.Where(x => !x.IsArchived && x.RoomGroupId == null), cancellationToken)
            };
            db.Rooms.Add(targetRoom);
            rooms.Add(targetRoom);
        }
        else if (matchingRooms.Length == 1)
        {
            targetRoom = matchingRooms[0];
        }
        else
        {
            throw new InvalidOperationException("Select one of the matching active rooms before importing.");
        }

        if (!existingRoomEnemiesOnly) ApplySceneDimensions(targetRoom, review.SceneUnitSize, replacementDimensions);

        var importedTransitions = new List<RoomTransition>();
        var importedChecks = new List<CheckLocation>();
        var nodes = Flatten(review.RootObjects).ToDictionary(node => node.Id);
        if (instructions.Select(instruction => instruction.ObjectId).Distinct().Count() != instructions.Count) throw new InvalidOperationException("Each scene object may be imported once.");
        foreach (var instruction in instructions)
        {
            if (!nodes.TryGetValue(instruction.ObjectId, out var node) || !SceneDumpIdentity.IsImportable(node.Classification)) throw new InvalidOperationException("Import instructions must select an exit, check or enemy scene object.");
            if (existingRoomEnemiesOnly && node.Classification != SceneDumpClassification.Enemy)
                throw new InvalidOperationException("BI1 instructions may select only Enemy scene objects.");
            if (instruction.Mode == SceneImportMode.Update)
            {
                if (node.Classification == SceneDumpClassification.Exit)
                {
                    var transition = transitions.SingleOrDefault(item => item.Id == instruction.ExistingRecordId && item.RoomId == targetRoom.Id)
                        ?? throw new InvalidOperationException("The selected transition update target is not an active record in the target room.");
                    ApplyMetadata(transition, node, SceneDumpIdentity.GameId(node));
                }
                else
                {
                    var check = checks.SingleOrDefault(item => item.Id == instruction.ExistingRecordId && item.RoomId == targetRoom.Id)
                        ?? throw new InvalidOperationException("The selected check update target is not an active record in the target room.");
                    ApplyMetadata(check, node, SceneDumpIdentity.GameId(node));
                }
            }
            else if (instruction.Mode != SceneImportMode.Create)
            {
                throw new InvalidOperationException("The scene import mode is invalid.");
            }
            else if (node.Classification == SceneDumpClassification.Exit)
            {
                var point = node.Components.SingleOrDefault(x => x.Type == "TransitionPoint");
                var destinationRoom = point is null ? null : rooms.Where(x => Matches(x.InGameId, point.TargetScene)).ToArray();
                var destinationTransitions = destinationRoom?.Length == 1
                    ? transitions.Where(x => x.RoomId == destinationRoom[0].Id && Matches(x.InGameId, point!.EntryPoint)).ToArray()
                    : [];
                var transition = new RoomTransition
                {
                    RoomId = targetRoom.Id,
                    FriendlyName = instruction.FriendlyName,
                    InGameId = node.Name,
                    InGamePositionX = node.WorldPosition?.X,
                    InGamePositionY = node.WorldPosition?.Y,
                    InGamePositionZ = node.WorldPosition?.Z,
                    LocalPositionX = node.LocalPosition?.X,
                    LocalPositionY = node.LocalPosition?.Y,
                    LocalPositionZ = node.LocalPosition?.Z,
                    AnnotationSceneUnitX = CompleteFinitePair(node.WorldPosition?.X, node.WorldPosition?.Y) ? node.WorldPosition!.X : null,
                    AnnotationSceneUnitY = CompleteFinitePair(node.WorldPosition?.X, node.WorldPosition?.Y) ? node.WorldPosition!.Y : null,
                    EnableAnnotation = true,
                    Requirements = node.IsAct3Only ? "Act 3" : string.Empty,
                    DestinationRoomReferenceText = destinationRoom?.Length == 1 && destinationTransitions.Length == 1 ? destinationRoom[0].ReferenceId : null,
                    DestinationTransitionAliasText = destinationTransitions.Length == 1 ? destinationTransitions[0].Alias : null,
                    SortOrder = await NextSortOrderAsync(db.RoomTransitions.Where(x => x.RoomId == targetRoom.Id && !x.IsArchived), cancellationToken) + importedTransitions.Count
                };
                importedTransitions.Add(transition);
                transitions.Add(transition);
            }
            else if (node.Classification is SceneDumpClassification.Check or SceneDumpClassification.Enemy)
            {
                var gameId = SceneDumpIdentity.GameId(node);
                    var check = new CheckLocation
                    {
                        RoomId = targetRoom.Id,
                        FriendlyName = instruction.FriendlyName,
                        InGameId = gameId,
                        InGamePositionX = node.WorldPosition?.X,
                        InGamePositionY = node.WorldPosition?.Y,
                        InGamePositionZ = node.WorldPosition?.Z,
                        LocalPositionX = node.LocalPosition?.X,
                        LocalPositionY = node.LocalPosition?.Y,
                        LocalPositionZ = node.LocalPosition?.Z,
                        AnnotationSceneUnitX = CompleteFinitePair(node.WorldPosition?.X, node.WorldPosition?.Y) ? node.WorldPosition!.X : null,
                        AnnotationSceneUnitY = CompleteFinitePair(node.WorldPosition?.X, node.WorldPosition?.Y) ? node.WorldPosition!.Y : null,
                        EnableAnnotation = true,
                        LocationType = node.Classification == SceneDumpClassification.Enemy ? "enemy" : null,
                        Requirements = node.Classification == SceneDumpClassification.Enemy
                            ? SceneEnemySpawnNotesResolver.EnemyRequirements(node)
                            : node.IsAct3Only ? "Act 3" : string.Empty,
                        Notes = node.Classification == SceneDumpClassification.Enemy
                            ? SceneEnemySpawnNotesResolver.EnemyNotes(node) : string.Empty,
                        SortOrder = await NextSortOrderAsync(db.CheckLocations.Where(x => x.RoomId == targetRoom.Id && !x.IsArchived), cancellationToken) + importedChecks.Count
                    };
                    importedChecks.Add(check);
                    checks.Add(check);
            }
        }

        db.AddRange(importedTransitions);
        db.AddRange(importedChecks);
        await db.SaveChangesAsync(cancellationToken);
        await new LogicReferenceResolver(db).ResolveAsync(cancellationToken);
        var requirementContext = await requirementStatus.LoadContextAsync(db, cancellationToken);
        foreach (var transition in importedTransitions)
            transition.RequirementsParseSucceeded = requirementStatus.ValidateSafely(requirementContext, transition.RoomId,
                transition.Id, RequirementBearingRowKind.Transition, transition.Requirements);
        foreach (var check in importedChecks)
            check.RequirementsParseSucceeded = requirementStatus.ValidateSafely(requirementContext, check.RoomId,
                check.Id, RequirementBearingRowKind.Check, check.Requirements);
        using (db.SuppressAuditMetadata()) await db.SaveChangesAsync(cancellationToken);
        if (!existingRoomEnemiesOnly && await LogicCatalogService.TryAutoAssignRoomGroupFromMapZoneAsync(db, targetRoom, cancellationToken))
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new SceneImportResult(targetRoom.Id, importedTransitions.Select(x => x.Id).ToArray(), importedChecks.Select(x => x.Id).ToArray());
    }

    public static SceneImportObjectMatch MatchObject(SceneDumpObject node, IEnumerable<SceneImportSelectableRecord> transitions, IEnumerable<SceneImportSelectableRecord> checks)
    {
        var gameId = SceneDumpIdentity.GameId(node);
        var matches = transitions.Concat(checks).Where(record => Matches(record.InGameId, gameId))
            .Select(record => new SceneImportRecordMatch(record.Id, record.RoomId, record.Classification)).ToArray();
        return new(node.Id, node.Classification, gameId, matches);
    }
    private static bool CompleteFinitePair(double? x, double? y) => x is double a && y is double b && double.IsFinite(a) && double.IsFinite(b);

    private static void ApplyMetadata(RoomTransition transition, SceneDumpObject node, string? gameId)
    {
        transition.InGameId = gameId;
        transition.InGamePositionX = node.WorldPosition?.X;
        transition.InGamePositionY = node.WorldPosition?.Y;
        transition.InGamePositionZ = node.WorldPosition?.Z;
        transition.LocalPositionX = node.LocalPosition?.X;
        transition.LocalPositionY = node.LocalPosition?.Y;
        transition.LocalPositionZ = node.LocalPosition?.Z;
    }

    private static void ApplyMetadata(CheckLocation check, SceneDumpObject node, string? gameId)
    {
        check.InGameId = gameId;
        check.InGamePositionX = node.WorldPosition?.X;
        check.InGamePositionY = node.WorldPosition?.Y;
        check.InGamePositionZ = node.WorldPosition?.Z;
        check.LocalPositionX = node.LocalPosition?.X;
        check.LocalPositionY = node.LocalPosition?.Y;
        check.LocalPositionZ = node.LocalPosition?.Z;
    }

    private static IEnumerable<SceneDumpObject> Flatten(IEnumerable<SceneDumpObject> nodes) => nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));
    private static SceneDumpObject? FindNode(IEnumerable<SceneDumpObject> nodes, Guid id) => Flatten(nodes).SingleOrDefault(node => node.Id == id);

    private static bool Matches(string? left, string? right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static async Task<int> NextSortOrderAsync<TEntity>(IQueryable<TEntity> query, CancellationToken cancellationToken) where TEntity : class =>
        (await query.Select(x => (int?)EF.Property<int>(x, "SortOrder")).MaxAsync(cancellationToken) ?? -1) + 1;

    private static async Task<string> CreateUniqueReferenceIdAsync(LogicDbContext db, string sceneName, CancellationToken cancellationToken)
    {
        var baseId = string.Join('-', new string(sceneName.Trim().ToLowerInvariant().Select(x => char.IsLetterOrDigit(x) ? x : '-').ToArray()).Split('-', StringSplitOptions.RemoveEmptyEntries));
        baseId = string.IsNullOrWhiteSpace(baseId) ? "room" : baseId;
        var candidate = baseId;
        for (var suffix = 2; await db.Rooms.AnyAsync(x => !x.IsArchived && x.ReferenceId == candidate, cancellationToken); suffix++) candidate = $"{baseId}-{suffix}";
        return candidate;
    }

    private void ApplySceneDimensions(Room room, SceneUnitSize? parsedDimensions, SceneUnitSize? replacementDimensions)
    {
        if (parsedDimensions is not { } parsed)
        {
            if (replacementDimensions is not null) throw new InvalidOperationException("Scene dimensions can only be replaced from a valid parsed scene size.");
            return;
        }

        if (room.SceneMaxX is null && room.SceneMaxY is null)
        {
            SetMaxima(room, parsed.Width, parsed.Height);
            return;
        }

        if (replacementDimensions is null)
        {
            return;
        }

        if (room.SceneMaxX is not { } currentWidth || room.SceneMaxY is not { } currentHeight ||
            !double.IsFinite(currentWidth) || !double.IsFinite(currentHeight) || currentWidth <= 0 || currentHeight <= 0 ||
            currentWidth == parsed.Width && currentHeight == parsed.Height)
        {
            throw new InvalidOperationException("Scene dimensions can only be replaced when complete current dimensions differ from the parsed size.");
        }

        if (!double.IsFinite(replacementDimensions.Width) || !double.IsFinite(replacementDimensions.Height) || replacementDimensions.Width <= 0 || replacementDimensions.Height <= 0)
        {
            throw new InvalidOperationException("Replacement scene dimensions must be finite positive values.");
        }

        SetMaxima(room, replacementDimensions.Width, replacementDimensions.Height);
    }

    private void SetMaxima(Room room, double maxX, double maxY)
    {
        if (!SceneBounds.TryGetSize(room.SceneMinX, room.SceneMinY, maxX, maxY, out _, out _))
            throw new InvalidOperationException("Imported scene maxima must exceed the retained minima and produce finite positive dimensions.");
        if ((room.SceneMaxX != maxX || room.SceneMaxY != maxY) && sceneImages?.Exists(room.Id) == true) room.IsSceneImageStale = true;
        room.SceneMaxX = maxX; room.SceneMaxY = maxY;
    }
}

public sealed record SceneImportRoom(Guid Id, string FriendlyName, string ReferenceId, string? InGameId, double? SceneMaxX, double? SceneMaxY);
public sealed record SceneImportPreview(IReadOnlyList<SceneImportRoom> MatchingRooms, IReadOnlyList<SceneImportObjectMatch> ObjectMatches, IReadOnlyList<SceneImportSelectableRecord> Transitions, IReadOnlyList<SceneImportSelectableRecord> Checks, IReadOnlyList<SceneImportEnemyName> EnemyNames);
public sealed record SceneImportEnemyName(Guid Id, string OutputValue, string Name);
public sealed record SceneImportObjectMatch(Guid ObjectId, SceneDumpClassification Classification, string? GameId, IReadOnlyList<SceneImportRecordMatch> Matches);
public sealed record SceneImportRecordMatch(Guid RecordId, Guid RoomId, SceneDumpClassification Classification);
public sealed record SceneImportSelectableRecord(Guid Id, Guid RoomId, SceneDumpClassification Classification, string FriendlyName, string? InGameId);
public sealed record SceneImportInstruction(Guid ObjectId, SceneImportMode Mode, Guid? ExistingRecordId, string FriendlyName);
public enum SceneImportMode { Create, Update }
public sealed record SceneImportResult(Guid RoomId, IReadOnlyList<Guid> TransitionIds, IReadOnlyList<Guid> CheckIds);
