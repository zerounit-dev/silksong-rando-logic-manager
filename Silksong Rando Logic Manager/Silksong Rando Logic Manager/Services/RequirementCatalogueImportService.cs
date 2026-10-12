using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Applies only a parser-accepted snapshot after the caller's one confirmation.</summary>
public sealed class RequirementCatalogueImportService(RequirementCatalogueWriteCoordinator writes, ILogger<RequirementCatalogueImportService> logger)
{
    public async Task<RequirementCatalogueImportOutcome> ReplaceConfirmedAsync(RequirementCatalogueSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        try
        {
            return await writes.ExecuteAsync(async (db, ct) =>
            {
                var issues = Validate(snapshot);
                if (issues.Count != 0) return RequirementCatalogueImportOutcome.ValidationRejected(issues);
                if (!HasValidStructure(snapshot)) return RequirementCatalogueImportOutcome.Failed("The confirmed catalogue snapshot is structurally invalid.");

                await db.RequirementPredicates.ExecuteDeleteAsync(ct);
                await db.RequirementCrests.ExecuteDeleteAsync(ct);
                await db.RequirementItems.ExecuteDeleteAsync(ct);
                await db.RequirementCountables.ExecuteDeleteAsync(ct);
                await db.RequirementEnemyTypes.ExecuteDeleteAsync(ct);

                var predicates = snapshot.Predicates.Select(predicate =>
                {
                    var entity = new RequirementPredicate
                    {
                        Id = predicate.Id, Name = predicate.Name, Category = predicate.Category, InputSyntax = predicate.InputSyntax,
                        OutputSyntax = predicate.OutputSyntax, Notes = predicate.Notes, SortOrder = predicate.SortOrder, Aliases = predicate.Aliases
                    };
                    return entity;
                }).ToArray();
                var items = snapshot.Items.Select(item =>
                {
                    var entity = new RequirementItem
                    {
                        Id = item.Id, Name = item.Name, Category = item.Category, OutputValue = item.OutputValue, Notes = item.Notes, SortOrder = item.SortOrder, Aliases = item.Aliases
                    };
                    return entity;
                }).ToArray();
                db.RequirementPredicates.AddRange(predicates);
                db.RequirementItems.AddRange(items);
                db.RequirementCrests.AddRange(snapshot.Crests.Select(x => new RequirementCrest
                {
                    Id = x.Id, Name = x.Name, Category = x.Category, OutputValue = x.OutputValue,
                    Notes = x.Notes, SortOrder = x.SortOrder, Aliases = x.Aliases
                }));
                db.RequirementCountables.AddRange(snapshot.Countables.Select(x => new RequirementCountable
                {
                    Id = x.Id, Name = x.Name, Category = x.Category, OutputValue = x.OutputValue,
                    Notes = x.Notes, SortOrder = x.SortOrder, Aliases = x.Aliases
                }));
                db.RequirementEnemyTypes.AddRange(snapshot.EnemyTypes.Select(x => new RequirementEnemyType
                {
                    Id = x.Id, Name = x.Name, Category = x.Category, OutputValue = x.OutputValue,
                    Notes = x.Notes, SortOrder = x.SortOrder, Aliases = x.Aliases
                }));
                await db.SaveChangesAsync(ct);
                return RequirementCatalogueImportOutcome.Committed();
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (RequirementCatalogueService.TryClassifyStorageFailure(exception, out var message))
        {
            logger.LogWarning(exception, "Classified requirement catalogue replacement storage failure.");
            return RequirementCatalogueImportOutcome.Failed(message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected requirement catalogue replacement failure.");
            throw;
        }
    }

    private static IReadOnlyList<RequirementCatalogueValidationIssue> Validate(RequirementCatalogueSnapshot snapshot) =>
        RequirementCatalogueValidator.Validate(
            snapshot.Predicates.Select(x => new RequirementCataloguePredicateValidation(x.Id, x.Name, x.Category,
                RequirementCatalogueLanguage.TryParsePersistedValue(x.InputSyntax, out var syntax) ? syntax : (RequirementInputSyntax)(-1), x.OutputSyntax, x.Notes,
                x.Aliases)),
            snapshot.Items.Select(x => new RequirementCatalogueItemValidation(x.Id, x.Name, x.Category, x.OutputValue, x.Notes,
                x.Aliases)))
        .Concat(RequirementCatalogueValidator.ValidateReferenceValues(snapshot.Crests.Select(x =>
            new RequirementCatalogueItemValidation(x.Id, x.Name, x.Category, x.OutputValue, x.Notes, x.Aliases))))
        .Concat(RequirementCatalogueValidator.ValidateReferenceValues(snapshot.Countables.Select(x =>
            new RequirementCatalogueItemValidation(x.Id, x.Name, x.Category, x.OutputValue, x.Notes, x.Aliases))))
        .Concat(RequirementCatalogueValidator.ValidateReferenceValues(snapshot.EnemyTypes.Select(x =>
            new RequirementCatalogueItemValidation(x.Id, x.Name, x.Category, x.OutputValue, x.Notes, x.Aliases))))
        .ToArray();

    private static bool HasValidStructure(RequirementCatalogueSnapshot snapshot)
    {
        if (!HasContiguousOrder(snapshot.Predicates.Select(x => x.SortOrder)) || !HasContiguousOrder(snapshot.Items.Select(x => x.SortOrder)) ||
            !HasContiguousOrder(snapshot.Crests.Select(x => x.SortOrder)) ||
            !HasContiguousOrder(snapshot.Countables.Select(x => x.SortOrder)) || !HasContiguousOrder(snapshot.EnemyTypes.Select(x => x.SortOrder))) return false;
        if (!snapshot.Predicates.Select(x => x.Aliases).Concat(snapshot.Crests.Concat(snapshot.Items).Concat(snapshot.Countables).Concat(snapshot.EnemyTypes).Select(x => x.Aliases))
            .All(aliases => RequirementCatalogueLanguage.TryCanonicalizeAliases(aliases, out var canonical, out _, out _) &&
                string.Equals(aliases, canonical, StringComparison.Ordinal))) return false;
        var identities = new HashSet<Guid>();
        return snapshot.Predicates.Select(x => x.Id).Concat(snapshot.Crests.Concat(snapshot.Items).Concat(snapshot.Countables).Concat(snapshot.EnemyTypes).Select(x => x.Id))
            .All(id => id != Guid.Empty && identities.Add(id));
    }

    private static bool HasContiguousOrder(IEnumerable<int> values) => values.Select((value, index) => value == index).All(value => value);
}
