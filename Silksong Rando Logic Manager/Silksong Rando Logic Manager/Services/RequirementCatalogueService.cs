using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Flat-parent catalogue queries and serialized transactional commands.</summary>
public sealed class RequirementCatalogueService(IDbContextFactory<LogicDbContext> dbContextFactory, RequirementCatalogueWriteCoordinator writes, ILogger<RequirementCatalogueService> logger)
{
    public async Task<RequirementCatalogueManagerView> LoadManagerAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var predicates = (await db.RequirementPredicates.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new { x.Id, x.Name, x.Category, x.InputSyntax, x.OutputSyntax, x.Notes, x.SortOrder, x.Aliases }).ToArrayAsync(cancellationToken))
            .Select(x => new RequirementPredicateView(x.Id, x.Name, x.Category, x.InputSyntax, x.OutputSyntax, x.Notes, x.SortOrder, x.Aliases, RequirementCatalogueLanguage.ParseAliases(x.Aliases))).ToArray();
        var items = (await db.RequirementItems.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new { x.Id, x.Name, x.Category, x.OutputValue, x.Notes, x.SortOrder, x.Aliases }).ToArrayAsync(cancellationToken))
            .Select(x => new RequirementItemView(x.Id, x.Name, x.Category, x.OutputValue, x.Notes, x.SortOrder, x.Aliases, RequirementCatalogueLanguage.ParseAliases(x.Aliases))).ToArray();
        return new(predicates, items);
    }

    public async Task<RequirementCatalogueReferenceView> LoadReferenceAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var predicates = (await db.RequirementPredicates.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new { x.Id, x.Name, x.Category, x.InputSyntax, x.Notes, x.Aliases }).ToArrayAsync(cancellationToken))
            .Select(x => new RequirementPredicateReferenceView(x.Id, x.Name, x.Category, x.InputSyntax, x.Notes, x.Aliases, RequirementCatalogueLanguage.ParseAliases(x.Aliases))).ToArray();
        var items = (await db.RequirementItems.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new { x.Id, x.Name, x.Category, x.Notes, x.Aliases }).ToArrayAsync(cancellationToken))
            .Select(x => new RequirementItemReferenceView(x.Id, x.Name, x.Category, x.Notes, x.Aliases, RequirementCatalogueLanguage.ParseAliases(x.Aliases))).ToArray();
        return new(predicates, items, CheckLocationTypeCatalogue.Definitions);
    }

    public async Task<RequirementCategorySuggestionsView> LoadCategorySuggestionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return new(await db.RequirementPredicates.AsNoTracking().Where(x => x.Category != null && x.Category != "").Select(x => x.Category!).Distinct().OrderBy(x => x).ToArrayAsync(cancellationToken),
            await db.RequirementItems.AsNoTracking().Where(x => x.Category != null && x.Category != "").Select(x => x.Category!).Distinct().OrderBy(x => x).ToArrayAsync(cancellationToken));
    }

    public Task<RequirementCatalogueCommandOutcome> ApplyAsync(ApplyRequirementPredicate command, CancellationToken token = default) => ExecuteAsync((db, ct) => ApplyPredicateAsync(db, command, ct), token);
    public Task<RequirementCatalogueCommandOutcome> ApplyAsync(ApplyRequirementItem command, CancellationToken token = default) => ExecuteAsync((db, ct) => ApplyItemAsync(db, command, ct), token);
    public Task<RequirementCatalogueCommandOutcome> DeleteAsync(DeleteRequirementPredicate command, CancellationToken token = default) => ExecuteAsync(async (db, ct) => { var row = await db.RequirementPredicates.FindAsync([command.Id], ct); if (row is null) return RequirementCatalogueCommandOutcome.Missing("The predicate no longer exists."); db.Remove(row); await db.SaveChangesAsync(ct); await RepairAsync(db.RequirementPredicates, ct); await db.SaveChangesAsync(ct); return RequirementCatalogueCommandOutcome.Committed(); }, token);
    public Task<RequirementCatalogueCommandOutcome> DeleteAsync(DeleteRequirementItem command, CancellationToken token = default) => ExecuteAsync(async (db, ct) => { var row = await db.RequirementItems.FindAsync([command.Id], ct); if (row is null) return RequirementCatalogueCommandOutcome.Missing("The item no longer exists."); db.Remove(row); await db.SaveChangesAsync(ct); await RepairAsync(db.RequirementItems, ct); await db.SaveChangesAsync(ct); return RequirementCatalogueCommandOutcome.Committed(); }, token);
    public Task<RequirementCatalogueCommandOutcome> ReorderAsync(ReorderRequirementPredicate command, CancellationToken token = default) => ExecuteAsync((db, ct) => ReorderPredicatesAsync(db, command, ct), token);
    public Task<RequirementCatalogueCommandOutcome> ReorderAsync(ReorderRequirementItem command, CancellationToken token = default) => ExecuteAsync((db, ct) => ReorderItemsAsync(db, command, ct), token);

    private static async Task<RequirementCatalogueCommandOutcome> ApplyPredicateAsync(LogicDbContext db, ApplyRequirementPredicate command, CancellationToken ct)
    {
        if (!RequirementCatalogueLanguage.TryParsePersistedValue(command.InputSyntax, out var syntax)) return Invalid("InputSyntax", command.Id ?? Guid.Empty, command.Name, "Input syntax is not supported.");
        if (!RequirementCatalogueLanguage.TryCanonicalizeAliases(command.AliasDraft, out var aliases, out _, out var error)) return Invalid("Aliases", command.Id ?? Guid.Empty, command.Name, error!);
        var row = command.Id is { } id ? await db.RequirementPredicates.SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        if (command.Id is not null && row is null) return RequirementCatalogueCommandOutcome.Missing("The predicate no longer exists.");
        if (row is null) { row = new RequirementPredicate { Id = Guid.NewGuid(), SortOrder = await db.RequirementPredicates.CountAsync(ct) }; db.RequirementPredicates.Add(row); }
        row.Name = command.Name ?? ""; row.Category = command.Category; row.InputSyntax = RequirementCatalogueLanguage.ToPersistedValue(syntax); row.OutputSyntax = command.OutputSyntax ?? ""; row.Aliases = aliases; row.Notes = command.Notes ?? "";
        var issues = RequirementCatalogueValidator.Validate(await PredicateValidations(db, ct), await ItemValidations(db, ct));
        if (issues.Count != 0) return RequirementCatalogueCommandOutcome.ValidationRejected(issues);
        await db.SaveChangesAsync(ct); await RepairAsync(db.RequirementPredicates, ct); await db.SaveChangesAsync(ct); return RequirementCatalogueCommandOutcome.Committed();
    }

    private static async Task<RequirementCatalogueCommandOutcome> ApplyItemAsync(LogicDbContext db, ApplyRequirementItem command, CancellationToken ct)
    {
        if (!RequirementCatalogueLanguage.TryCanonicalizeAliases(command.AliasDraft, out var aliases, out _, out var error)) return Invalid("Aliases", command.Id ?? Guid.Empty, command.Name, error!);
        var row = command.Id is { } id ? await db.RequirementItems.SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        if (command.Id is not null && row is null) return RequirementCatalogueCommandOutcome.Missing("The item no longer exists.");
        if (row is null) { row = new RequirementItem { Id = Guid.NewGuid(), SortOrder = await db.RequirementItems.CountAsync(ct) }; db.RequirementItems.Add(row); }
        row.Name = command.Name ?? ""; row.Category = command.Category; row.OutputValue = command.OutputValue ?? ""; row.Aliases = aliases; row.Notes = command.Notes ?? "";
        var issues = RequirementCatalogueValidator.Validate(await PredicateValidations(db, ct), await ItemValidations(db, ct));
        if (issues.Count != 0) return RequirementCatalogueCommandOutcome.ValidationRejected(issues);
        await db.SaveChangesAsync(ct); await RepairAsync(db.RequirementItems, ct); await db.SaveChangesAsync(ct); return RequirementCatalogueCommandOutcome.Committed();
    }

    private static async Task<RequirementCatalogueCommandOutcome> ReorderPredicatesAsync(LogicDbContext db, ReorderRequirementPredicate command, CancellationToken ct) { var rows = await db.RequirementPredicates.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct); return await ReorderRowsAsync(rows, command.Id, command.TargetIndex, "predicate", db, ct); }
    private static async Task<RequirementCatalogueCommandOutcome> ReorderItemsAsync(LogicDbContext db, ReorderRequirementItem command, CancellationToken ct) { var rows = await db.RequirementItems.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct); return await ReorderRowsAsync(rows, command.Id, command.TargetIndex, "item", db, ct); }
    private static async Task<RequirementCatalogueCommandOutcome> ReorderRowsAsync<T>(List<T> rows, Guid id, int target, string kind, LogicDbContext db, CancellationToken ct) where T : class { dynamic? row = rows.Cast<dynamic>().FirstOrDefault(x => x.Id == id); if (row is null) return RequirementCatalogueCommandOutcome.Missing($"The {kind} no longer exists."); if (target < 0 || target >= rows.Count) return Invalid("TargetIndex", id, null, $"Target index is outside the {kind} order."); rows.Remove((T)row); rows.Insert(target, (T)row); for (var i = 0; i < rows.Count; i++) ((dynamic)rows[i]).SortOrder = i; await db.SaveChangesAsync(ct); return RequirementCatalogueCommandOutcome.Committed(); }
    private static async Task RepairAsync(DbSet<RequirementPredicate> set, CancellationToken ct) { var rows = await set.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct); for (var i = 0; i < rows.Count; i++) rows[i].SortOrder = i; }
    private static async Task RepairAsync(DbSet<RequirementItem> set, CancellationToken ct) { var rows = await set.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct); for (var i = 0; i < rows.Count; i++) rows[i].SortOrder = i; }
    private static async Task<IReadOnlyList<RequirementCataloguePredicateValidation>> PredicateValidations(LogicDbContext db, CancellationToken ct)
    {
        var values = (await db.RequirementPredicates.Select(x => new { x.Id, x.Name, x.Category, x.InputSyntax, x.OutputSyntax, x.Notes, x.Aliases }).ToArrayAsync(ct))
            .ToDictionary(x => x.Id, x => new RequirementCataloguePredicateValidation(x.Id, x.Name, x.Category, RequirementCatalogueLanguage.TryParsePersistedValue(x.InputSyntax, out var syntax) ? syntax : (RequirementInputSyntax)(-1), x.OutputSyntax, x.Notes, x.Aliases));
        foreach (var entry in db.ChangeTracker.Entries<RequirementPredicate>().Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            var value = entry.Entity;
            values[value.Id] = new(value.Id, value.Name, value.Category, RequirementCatalogueLanguage.TryParsePersistedValue(value.InputSyntax, out var syntax) ? syntax : (RequirementInputSyntax)(-1), value.OutputSyntax, value.Notes, value.Aliases);
        }
        return values.Values.ToArray();
    }
    private static async Task<IReadOnlyList<RequirementCatalogueItemValidation>> ItemValidations(LogicDbContext db, CancellationToken ct)
    {
        var values = (await db.RequirementItems.Select(x => new RequirementCatalogueItemValidation(x.Id, x.Name, x.Category, x.OutputValue, x.Notes, x.Aliases)).ToArrayAsync(ct))
            .ToDictionary(x => x.Id);
        foreach (var entry in db.ChangeTracker.Entries<RequirementItem>().Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            var value = entry.Entity;
            values[value.Id] = new(value.Id, value.Name, value.Category, value.OutputValue, value.Notes, value.Aliases);
        }
        return values.Values.ToArray();
    }
    private async Task<RequirementCatalogueCommandOutcome> ExecuteAsync(Func<LogicDbContext, CancellationToken, Task<RequirementCatalogueCommandOutcome>> command, CancellationToken ct)
    {
        try
        {
            return await writes.ExecuteAsync(command, ct);
        }
        catch (Exception exception) when (TryClassifyStorageFailure(exception, out var message))
        {
            logger.LogWarning(exception, "Classified requirement catalogue storage failure.");
            return RequirementCatalogueCommandOutcome.Failed(message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected requirement catalogue command failure.");
            throw;
        }
    }

    internal static bool TryClassifyStorageFailure(Exception exception, out string message)
    {
        var sqlite = exception as SqliteException ?? exception.InnerException as SqliteException;
        message = sqlite?.SqliteErrorCode switch
        {
            19 => "The catalogue database rejected this change. Reload, correct the definition, and try again.",
            5 or 6 => "The catalogue database is busy. Close other writers and try again.",
            8 or 10 or 14 => "The catalogue database is unavailable for writing. Check its storage access and try again.",
            _ => ""
        };
        return message.Length != 0;
    }
    private static RequirementCatalogueCommandOutcome Invalid(string field, Guid id, string? name, string message) => RequirementCatalogueCommandOutcome.ValidationRejected([new(field, id, name, null, null, message)]);
}
