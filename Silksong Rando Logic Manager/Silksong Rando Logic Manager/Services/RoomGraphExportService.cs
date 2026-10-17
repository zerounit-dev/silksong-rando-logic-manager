using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using System.Globalization;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class RoomGraphExportService(IDbContextFactory<LogicDbContext> contexts) : IRoomGraphExportService
{
    public async Task<RoomGraphExportResult> GenerateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var snapshot = await RoomExportStructure.LoadSnapshotAsync(db, true, cancellationToken);
        var predicates = await db.RequirementPredicates.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new PredicateRow(x.Id, x.Name, x.Category, x.InputSyntax, x.OutputSyntax, x.Aliases, x.Notes, x.SortOrder)).ToListAsync(cancellationToken);
        var items = await db.RequirementItems.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new ItemRow(x.Id, x.Name, x.Category, x.OutputValue, x.Aliases, x.Notes, x.SortOrder)).ToListAsync(cancellationToken);
        var countables = await db.RequirementCountables.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new ItemRow(x.Id, x.Name, x.Category, x.OutputValue, x.Aliases, x.Notes, x.SortOrder)).ToListAsync(cancellationToken);
        var souls = await db.RequirementSouls.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new ItemRow(x.Id, x.Name, x.Category, x.OutputValue, x.Aliases, x.Notes, x.SortOrder)).ToListAsync(cancellationToken);
        var crests = await db.RequirementCrests.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new ItemRow(x.Id, x.Name, x.Category, x.OutputValue, x.Aliases, x.Notes, x.SortOrder)).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var globalFailures = new List<RoomGraphExportFailure>();
        var typeLegend = RoomExportStructure.BuildTypeLegend(globalFailures);
        var catalogue = BuildCatalogue(predicates, items, countables, souls, crests, globalFailures);
        var structure = RoomExportStructure.Project(snapshot, globalFailures);
        var context = catalogue is null ? null : new RoomGraphTranslationContext(catalogue.Predicates, catalogue.Items,
            structure.Rooms.Select(x => new RoomGraphRequirementRoom(x.Row.Id, x.Row.ReferenceId)).ToArray(),
            structure.Rooms.SelectMany(room => room.Locations.OrderBy(x => x.Row.SortOrder)
                .ThenBy(x => CanonicalValue(x.Row.Name), StringComparer.Ordinal).ThenBy(x => x.Row.Id))
                .Select(x => new RoomGraphRequirementCheck(x.Row.Id, x.Row.RoomId, x.Row.Name, string.IsNullOrEmpty(x.Id) ? null : x.Id)).ToArray(),
            catalogue.Countables, catalogue.Souls, catalogue.Crests);
        var rooms = structure.Rooms.Select(room =>
        {
            var transitions = room.Transitions.Select(x =>
            {
                var requirement = Translate(x.Row.Requirements, room.Row.Id, context);
                var reasons = WithRequirementReason(x.Reasons, requirement);
                return new RoomGraphTransition(x.Id, x.Row.Alias, x.Row.Name, x.Row.InGameId, x.Row.IsUnsafeArrival,
                    x.SourceNodeId, x.Target, requirement, x.Row.Notes, x.Row.IsTodo, x.Row.IsVerified, reasons.Count == 0 ? "active" : "unresolved", reasons);
            }).ToArray();
            var connections = room.Connections.Select(x =>
            {
                var requirement = Translate(x.Row.Requirements, room.Row.Id, context);
                var reasons = WithRequirementReason([], requirement);
                return new RoomGraphConnection(x.Id, x.Row.Alias, x.Row.Name, x.SourceNodeId, x.TargetNodeId, requirement,
                    x.Row.Notes, x.Row.IsTodo, x.Row.IsVerified, reasons.Count == 0 ? "active" : "unresolved", reasons);
            }).ToArray();
            var locations = room.Locations.Select(x =>
            {
                var requirement = Translate(x.Row.Requirements, room.Row.Id, context);
                var reasons = WithRequirementReason(x.Reasons, requirement);
                return new RoomGraphLocation(x.Id, x.Row.Name, x.Row.InGameId, x.Type, x.HasPersistedState, x.NodeId, requirement,
                    x.Row.Notes, x.Row.IsTodo, x.Row.IsVerified, reasons.Count == 0 ? "active" : "unresolved", reasons);
            }).ToArray();
            var group = structure.Groups.SingleOrDefault(x => x.Row.Id == room.Row.GroupId);
            return new RoomGraphRoom(room.Id, room.Row.Id.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant(), group?.Id ?? "",
                room.Row.Name, room.Row.InGameId, room.Row.Contributors ?? "", room.Row.Comments ?? "",
                room.Nodes.Select(x => new RoomGraphNode(x.Id, x.Name, x.Row?.Notes ?? "")).ToArray(), transitions, connections, locations);
        }).ToArray();
        if (structure.Failures.Count != 0) return RoomGraphExportResult.Failed(structure.Failures, structure.Warnings);
        var document = new RoomGraphDocument(3, new("silksong-logic-manager-export", rooms.Length),
            structure.Groups.Select(x => x.Id!).ToArray(),
            structure.Groups.Where(x => !x.Row.IsVirtual).Select(x => x.Id!).ToArray(),
            structure.Groups.Where(x => x.Row.IsVirtual).Select(x => x.Id!).ToArray(),
            predicates.Select(x => new RoomGraphTranslationPredicate(x.Name, x.Category, x.InputSyntax, x.OutputSyntax, RequirementCatalogueLanguage.ParseAliases(x.Aliases), x.Notes)).ToArray(),
            items.Select(x => new RoomGraphTranslationItem(x.Name, x.Category, RequirementCatalogueLanguage.ParseAliases(x.Aliases), x.OutputValue, x.Notes)).ToArray(), typeLegend, rooms);
        return RoomGraphExportResult.Success(RoomExportStructure.Serialize(document), structure.Warnings);
    }

    internal static string? CanonicalValue(string? value) => RoomExportStructure.CanonicalValue(value);

    private static IReadOnlyList<string> WithRequirementReason(IReadOnlyList<string> structuralReasons, RoomGraphRequirement requirement) =>
        requirement.Mode == "unresolved" ? [.. structuralReasons, "requirement is unresolved"] : structuralReasons;

    private static RoomGraphRequirement Translate(string raw, Guid roomId, RoomGraphTranslationContext? context) => context is null
        ? new(raw, "unresolved", [], ["requirement translation is unavailable because the shared catalogue configuration is invalid"])
        : RoomGraphRequirementTranslator.Translate(raw, roomId, context);

    private static Catalogue? BuildCatalogue(IReadOnlyList<PredicateRow> predicateRows, IReadOnlyList<ItemRow> itemRows,
        IReadOnlyList<ItemRow> countableRows, IReadOnlyList<ItemRow> soulRows, IReadOnlyList<ItemRow> crestRows, List<RoomGraphExportFailure> failures)
    {
        var initialFailureCount = failures.Count;
        var predicateInputs = new List<RequirementCataloguePredicateValidation>();
        var predicates = new List<RoomGraphPredicateDefinition>();
        foreach (var row in predicateRows)
        {
            if (!RequirementCatalogueLanguage.TryParsePersistedValue(row.InputSyntax, out var syntax)) syntax = (RequirementInputSyntax)(-1);
            predicateInputs.Add(new(row.Id, row.Name, row.Category, syntax, row.OutputSyntax, row.Notes, row.Aliases));
            if (Enum.IsDefined(syntax) && RequirementCatalogueLanguage.TryCanonicalizeAliases(row.Aliases, out var canonical, out var aliases, out _) && canonical == row.Aliases)
                predicates.Add(new(row.Id, row.Name, syntax, row.OutputSyntax, aliases));
        }
        var itemInputs = itemRows.Select(row => new RequirementCatalogueItemValidation(row.Id, row.Name, row.Category, row.OutputValue, row.Notes, row.Aliases)).ToArray();
        var items = itemRows.Where(row => RequirementCatalogueLanguage.TryCanonicalizeAliases(row.Aliases, out var canonical, out _, out _) && canonical == row.Aliases)
            .Select(row => new RoomGraphItemDefinition(row.Id, row.Name, row.OutputValue, RequirementCatalogueLanguage.ParseAliases(row.Aliases))).ToArray();
        foreach (var issue in RequirementCatalogueValidator.Validate(predicateInputs, itemInputs))
            failures.Add(new($"requirement catalogue definition '{issue.DefinitionName ?? issue.DefinitionId.ToString()}' is invalid: {issue.Message}"));
        foreach (var row in predicateRows)
            if (RequirementCatalogueLanguage.TryCanonicalizeAliases(row.Aliases, out var canonical, out _, out _) && canonical != row.Aliases)
                failures.Add(new($"requirement predicate '{row.Name}' has noncanonical aliases; apply the definition again before export"));
        foreach (var row in itemRows)
            if (RequirementCatalogueLanguage.TryCanonicalizeAliases(row.Aliases, out var canonical, out _, out _) && canonical != row.Aliases)
                failures.Add(new($"requirement item '{row.Name}' has noncanonical aliases; apply the definition again before export"));
        var invalidCheckOutputs = predicates.Where(x => x.Syntax == RequirementInputSyntax.PredicateThenCheck).Where(predicate =>
        {
            try
            {
                const string marker = "location:room::type/name";
                return RequirementAtomFormatter.Format(predicate.Syntax, predicate.OutputSyntax, new(CheckGraphId: marker)) != $"dependency:{marker}";
            }
            catch { return true; }
        }).Select(x => x.Name).ToArray();
        if (invalidCheckOutputs.Length != 0)
            failures.Add(new($"check-consuming requirement predicate output must emit exactly 'dependency:{{check}}'; correct: {string.Join(", ", invalidCheckOutputs)}"));
        var countables = BuildTypedValues(countableRows, "countable", failures);
        var souls = BuildTypedValues(soulRows, "soul", failures);
        var crests = BuildTypedValues(crestRows, "crest", failures);
        return failures.Count == initialFailureCount ? new(predicates, items, countables, souls, crests) : null;
    }

    private static IReadOnlyList<RoomGraphItemDefinition> BuildTypedValues(IReadOnlyList<ItemRow> rows, string kind, List<RoomGraphExportFailure> failures)
    {
        foreach (var issue in RequirementCatalogueValidator.ValidateReferenceValues(rows.Select(row =>
            new RequirementCatalogueItemValidation(row.Id, row.Name, row.Category, row.OutputValue, row.Notes, row.Aliases))))
            failures.Add(new($"requirement {kind} '{issue.DefinitionName ?? issue.DefinitionId.ToString()}' is invalid: {issue.Message}"));
        var result = new List<RoomGraphItemDefinition>();
        foreach (var row in rows)
        {
            if (!RequirementCatalogueLanguage.TryCanonicalizeAliases(row.Aliases, out var canonical, out var aliases, out _)) continue;
            if (canonical != row.Aliases) failures.Add(new($"requirement {kind} '{row.Name}' has noncanonical aliases; apply the definition again before export"));
            else result.Add(new(row.Id, row.Name, row.OutputValue, aliases));
        }
        return result;
    }

    private sealed record Catalogue(IReadOnlyList<RoomGraphPredicateDefinition> Predicates, IReadOnlyList<RoomGraphItemDefinition> Items, IReadOnlyList<RoomGraphItemDefinition> Countables, IReadOnlyList<RoomGraphItemDefinition> Souls, IReadOnlyList<RoomGraphItemDefinition> Crests);
    private sealed record PredicateRow(Guid Id, string Name, string? Category, string InputSyntax, string OutputSyntax, string Aliases, string Notes, int SortOrder);
    private sealed record ItemRow(Guid Id, string Name, string? Category, string OutputValue, string Aliases, string Notes, int SortOrder);
}
