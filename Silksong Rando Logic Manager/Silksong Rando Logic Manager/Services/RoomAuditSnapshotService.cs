using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using Silksong_Rando_Logic_Manager.Data;
using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class RoomAuditSnapshotService(IDbContextFactory<LogicDbContext> contexts, ISceneLayoutLoader scenes,
    Authentication.LiveAccess access, SceneImageFileService images, SharedPublicationCoordinator publications)
{
    internal async Task<CacheResult<AuditSnapshotResponse>> LoadAsync(Guid roomId, LogicCacheCheckpoint? known, CancellationToken token)
    {
        // Pin semantic source AND complete cache coverage in the same admitted SQL
        // snapshot. A later catch-up cannot certify what this pinned analysis read.
        using var lifecycle = await images.AcquireLifecycleAsync(token);
        using var publication = await publications.AcquireAsync(token);
        await access.RequireAsync(token: token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var read = await db.Database.BeginTransactionAsync(token);
        var snapshot = await LoadSnapshotAsync(db, roomId, token);
        var cache = await LogicCacheTransfer.ReadAdmittedAsync(db, publication, known, token);
        return new(new(snapshot), cache);
    }

    private async Task<RoomAuditSnapshot?> LoadSnapshotAsync(LogicDbContext db, Guid roomId, CancellationToken token)
    {
        var room = await db.Rooms.AsNoTracking().Where(x => x.Id == roomId && !x.IsArchived)
            .Select(x => new { x.Id, x.FriendlyName }).SingleOrDefaultAsync(token);
        if (room is null) return null;
        var subrooms = await db.Subrooms.AsNoTracking().Where(x => x.RoomId == roomId && !x.IsArchived)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new AuditSubroom(x.Id, x.FriendlyName)).ToArrayAsync(token);
        var exits = await db.RoomTransitions.AsNoTracking().Where(x => x.RoomId == roomId && !x.IsArchived)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new Row(x.Id, "exit", x.FriendlyName, x.ResolvedSourceSubroomId, null, x.Requirements, x.Alias)).ToArrayAsync(token);
        var connections = await db.SubroomConnections.AsNoTracking().Where(x => x.RoomId == roomId && !x.IsArchived)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new Row(x.Id, "connection", x.FriendlyName, x.ResolvedSourceSubroomId, x.ResolvedDestinationSubroomId, x.Requirements, x.Alias)).ToArrayAsync(token);
        var checks = await db.CheckLocations.AsNoTracking().Where(x => x.RoomId == roomId && !x.IsArchived)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new Row(x.Id, "check", x.FriendlyName, x.ResolvedSubroomId, null, x.Requirements, null, x.LocationType)).ToArrayAsync(token);
        var rooms = await db.Rooms.AsNoTracking().Where(x => !x.IsArchived)
            .Select(x => new RoomGraphRequirementRoom(x.Id, x.ReferenceId)).ToArrayAsync(token);
        var identities = await db.CheckLocations.AsNoTracking().Where(x => !x.IsArchived && !x.Room.IsArchived)
            .Select(x => new AuditCheckIdentity(x.Id, x.RoomId, x.FriendlyName, x.Room.FriendlyName)).ToArrayAsync(token);
        var predicates = await db.RequirementPredicates.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new Predicate(x.Id, x.Name, x.InputSyntax, x.OutputSyntax, x.Aliases)).ToArrayAsync(token);
        var items = await db.RequirementItems.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new Value(x.Id, x.Name, x.OutputValue, x.Aliases)).ToArrayAsync(token);
        var countables = await db.RequirementCountables.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new Value(x.Id, x.Name, x.OutputValue, x.Aliases)).ToArrayAsync(token);
        var souls = await db.RequirementSouls.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new Value(x.Id, x.Name, x.OutputValue, x.Aliases)).ToArrayAsync(token);
        var crests = await db.RequirementCrests.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new Value(x.Id, x.Name, x.OutputValue, x.Aliases)).ToArrayAsync(token);
        var methods = await db.RequirementMethods.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new Value(x.Id, x.Name, x.OutputValue, x.Aliases)).ToArrayAsync(token);
        var scene = await scenes.LoadAsync(db, roomId, token);
        // Only read transactions and scalar DTOs. No resolver, validator, exporter or SaveChanges call.
        var predicateMetadata = predicates.Select(x => new AuditPredicateDefinition(x.Id, x.Name, x.Input, x.Output,
            RequirementCatalogueLanguage.TryParsePersistedValue(x.Input, out var shape) ? shape : null)).ToImmutableArray();
        var definitions = predicates.Zip(predicateMetadata).Where(x => x.Second.InputShape is not null)
            .Select(x => new RoomGraphPredicateDefinition(x.First.Id, x.First.Name, x.Second.InputShape!.Value,
                x.First.Output, RequirementCatalogueLanguage.ParseAliases(x.First.Aliases))).ToArray();
        var context = new RoomGraphTranslationContext(definitions, Definitions(items), rooms,
            identities.Select(x => new RoomGraphRequirementCheck(x.Id, x.RoomId, x.Name, x.Id.ToString("D"))).ToArray(),
            Definitions(countables), Definitions(souls), Definitions(crests), Definitions(methods));
        var rows = exits.Concat(connections).Concat(checks).Select(x =>
        {
            token.ThrowIfCancellationRequested();
            return new AuditRow(x.Id, x.Kind, DisplayName(x.Name, x.Kind == "exit" ? "entrance" : x.Kind), x.Source, x.Destination,
                Freeze(RoomGraphRequirementTranslator.RecognizeForAudit(x.Requirements, roomId, context)), x.Alias, x.LocationType);
        }).ToImmutableArray();
        token.ThrowIfCancellationRequested();
        return new(roomId, DisplayName(room.FriendlyName, "room"),
            subrooms.Select(x => x with { Name = DisplayName(x.Name, "subroom") }).ToImmutableArray(), rows,
            identities.Select(x => x with { Name = DisplayName(x.Name, "check"), RoomName = DisplayName(x.RoomName, "room") }).ToImmutableArray(),
            Values(items), Values(crests), Values(methods), Values(souls), predicateMetadata, Values(countables), scene);
    }

    private static AuditRequirement Freeze(AuditRequirement requirement) => requirement with
    { Expression = FreezeExpression(requirement.Expression), Reasons = requirement.Reasons.ToImmutableArray() };
    private static AuditExpression? FreezeExpression(AuditExpression? expression) => expression switch
    {
        AuditAtom atom => atom with { Operands = atom.Operands.ToImmutableArray() },
        AuditBoolean boolean => boolean with { Left = FreezeExpression(boolean.Left)!, Right = FreezeExpression(boolean.Right)! },
        _ => null
    };
    private static RoomGraphItemDefinition[] Definitions(Value[] values) => values.Select(x => new RoomGraphItemDefinition(x.Id, x.Name, x.Output, RequirementCatalogueLanguage.ParseAliases(x.Aliases))).ToArray();
    public static string DisplayName(string? name, string kind) => string.IsNullOrWhiteSpace(name) ? $"Unnamed {kind}" : name;
    private static ImmutableArray<AuditValue> Values(Value[] values) => values.Select(x => new AuditValue(x.Id, x.Name, x.Output)).ToImmutableArray();
    private sealed record Row(Guid Id, string Kind, string Name, Guid? Source, Guid? Destination, string Requirements, string? Alias = null, string? LocationType = null);
    private sealed record Predicate(Guid Id, string Name, string Input, string Output, string Aliases);
    private sealed record Value(Guid Id, string Name, string Output, string Aliases);
}
