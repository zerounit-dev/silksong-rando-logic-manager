using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>
/// Serializes all in-process catalogue writes because catalogue rows deliberately
/// have no optimistic concurrency value. Callers validate and commit within the
/// admitted SQLite transaction.
/// </summary>
public sealed class RequirementCatalogueWriteCoordinator(IDbContextFactory<LogicDbContext> dbContextFactory,
    SharedPublicationCoordinator publications)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<T> ExecuteAsync<T>(Authentication.LiveAccess access, Func<LogicDbContext, CancellationToken, Task<T>> operation,
        Func<T, VocabularyMutationPatch, T> attachPublication, bool replacement, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var publication = await publications.AcquireAsync(cancellationToken);
            var historyActor = HistoryActor.From(await access.RequireAsync(Authentication.AccessPolicies.Author, cancellationToken));
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await LogicWriteScope.BeginAsync(db, publication, historyActor, cancellationToken);
            var before = await SharedCatalogueService.ReadVocabularyAsync(db, publication.Tokens.RequirementsUtc, cancellationToken);
            var result = await operation(db, cancellationToken);
            await transaction.FinalizeAsync(cancellationToken);
            var after = await SharedCatalogueService.ReadVocabularyAsync(db, publication.Tokens.RequirementsUtc, cancellationToken);
            var changed = !before.Predicates.SequenceEqual(after.Predicates) || !before.Crests.SequenceEqual(after.Crests)
                || !before.Methods.SequenceEqual(after.Methods) || !before.Items.SequenceEqual(after.Items)
                || !before.Countables.SequenceEqual(after.Countables) || !before.Souls.SequenceEqual(after.Souls);
            var prepared = publication.PrepareAdvance(false, changed);
            var patch = replacement
                ? new VocabularyMutationPatch(prepared.Timestamps.Requirements, true, [], [], after with { Token = prepared.After.RequirementsUtc })
                : TargetedPatch(before, after, prepared.Timestamps.Requirements);
            var publishedResult = attachPublication(result, patch);
            await transaction.CommitAsync(() => publication.PublishCommitted(prepared), cancellationToken);
            return publishedResult;
        }
        finally
        {
            gate.Release();
        }
    }

    private static VocabularyMutationPatch TargetedPatch(CatalogueVocabulary before, CatalogueVocabulary after, ScopeTimestampBracket timestamps)
    {
        IReadOnlyList<VocabularyDefinition>[] original = [before.Predicates, before.Crests, before.Methods, before.Items, before.Countables, before.Souls];
        IReadOnlyList<VocabularyDefinition>[] current = [after.Predicates, after.Crests, after.Methods, after.Items, after.Countables, after.Souls];
        var definitions = new List<DefinitionFactChange>();
        var orders = new List<DefinitionKindOrder>();
        for (var i = 0; i < original.Length; i++)
        {
            var old = original[i].ToDictionary(x => x.Id); var next = current[i].ToDictionary(x => x.Id);
            foreach (var id in old.Keys.Union(next.Keys))
            {
                var prior = old.GetValueOrDefault(id); var value = next.GetValueOrDefault(id);
                if (prior != value) definitions.Add(new((CatalogueDefinitionKind)i, prior, value));
            }
            // Survivor SortOrder repairs are definition changes above, not merely
            // a presentation hint. Carry the authoritative order of that kind too.
            if (!original[i].Select(x => x.Id).SequenceEqual(current[i].Select(x => x.Id)))
                orders.Add(new((CatalogueDefinitionKind)i, current[i].Select(x => x.Id).ToArray()));
        }
        return new(timestamps, true, definitions, orders, null);
    }
}
