using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed partial class RequirementCatalogueService
{
    public Task<RequirementCatalogueCommandOutcome> ApplyAsync(ApplyRequirementCountable command, CancellationToken token = default) =>
        ExecuteAsync((db, ct) => ApplyCountableAsync(db, command, ct), token);

    public Task<RequirementCatalogueCommandOutcome> ApplyAsync(ApplyRequirementEnemyType command, CancellationToken token = default) =>
        ExecuteAsync((db, ct) => ApplyEnemyTypeAsync(db, command, ct), token);

    private static async Task<RequirementCatalogueCommandOutcome> ApplyCountableAsync(LogicDbContext db, ApplyRequirementCountable command, CancellationToken ct)
    {
        if (!RequirementCatalogueLanguage.TryCanonicalizeAliases(command.AliasDraft, out var aliases, out _, out var error))
            return Invalid("Aliases", command.Id ?? Guid.Empty, command.Name, error!);
        var row = command.Id is { } id ? await db.RequirementCountables.SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        if (command.Id is not null && row is null) return RequirementCatalogueCommandOutcome.Missing("The countable no longer exists.");
        var rows = await db.RequirementCountables.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct);
        if (row is null)
        {
            row = new RequirementCountable { Id = Guid.NewGuid() };
            db.RequirementCountables.Add(row);
            rows.Add(row);
        }
        row.Name = command.Name ?? ""; row.Category = command.Category; row.OutputValue = command.OutputValue ?? "";
        row.Aliases = aliases; row.Notes = command.Notes ?? "";
        var issues = RequirementCatalogueValidator.ValidateReferenceValues(rows.Select(x =>
            new RequirementCatalogueItemValidation(x.Id, x.Name, x.Category, x.OutputValue, x.Notes, x.Aliases)));
        if (issues.Count != 0) return RequirementCatalogueCommandOutcome.ValidationRejected(issues);
        for (var i = 0; i < rows.Count; i++) rows[i].SortOrder = i;
        await db.SaveChangesAsync(ct);
        return RequirementCatalogueCommandOutcome.Committed();
    }

    private static async Task<RequirementCatalogueCommandOutcome> ApplyEnemyTypeAsync(LogicDbContext db, ApplyRequirementEnemyType command, CancellationToken ct)
    {
        if (!RequirementCatalogueLanguage.TryCanonicalizeAliases(command.AliasDraft, out var aliases, out _, out var error))
            return Invalid("Aliases", command.Id ?? Guid.Empty, command.Name, error!);
        var row = command.Id is { } id ? await db.RequirementEnemyTypes.SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        if (command.Id is not null && row is null) return RequirementCatalogueCommandOutcome.Missing("The enemy type no longer exists.");
        var rows = await db.RequirementEnemyTypes.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct);
        if (row is null)
        {
            row = new RequirementEnemyType { Id = Guid.NewGuid() };
            db.RequirementEnemyTypes.Add(row);
            rows.Add(row);
        }
        row.Name = command.Name ?? ""; row.Category = command.Category; row.OutputValue = command.OutputValue ?? "";
        row.Aliases = aliases; row.Notes = command.Notes ?? "";
        var issues = RequirementCatalogueValidator.ValidateReferenceValues(rows.Select(x =>
            new RequirementCatalogueItemValidation(x.Id, x.Name, x.Category, x.OutputValue, x.Notes, x.Aliases)));
        if (issues.Count != 0) return RequirementCatalogueCommandOutcome.ValidationRejected(issues);
        for (var i = 0; i < rows.Count; i++) rows[i].SortOrder = i;
        await db.SaveChangesAsync(ct);
        return RequirementCatalogueCommandOutcome.Committed();
    }

    public Task<RequirementCatalogueCommandOutcome> DeleteAsync(DeleteRequirementCountable command, CancellationToken token = default) => ExecuteAsync(async (db, ct) =>
    {
        var row = await db.RequirementCountables.FindAsync([command.Id], ct);
        if (row is null) return RequirementCatalogueCommandOutcome.Missing("The countable no longer exists.");
        db.Remove(row);
        var rows = await db.RequirementCountables.Where(x => x.Id != command.Id).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct);
        for (var i = 0; i < rows.Count; i++) rows[i].SortOrder = i;
        await db.SaveChangesAsync(ct);
        return RequirementCatalogueCommandOutcome.Committed();
    }, token);

    public Task<RequirementCatalogueCommandOutcome> DeleteAsync(DeleteRequirementEnemyType command, CancellationToken token = default) => ExecuteAsync(async (db, ct) =>
    {
        var row = await db.RequirementEnemyTypes.FindAsync([command.Id], ct);
        if (row is null) return RequirementCatalogueCommandOutcome.Missing("The enemy type no longer exists.");
        db.Remove(row);
        var rows = await db.RequirementEnemyTypes.Where(x => x.Id != command.Id).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct);
        for (var i = 0; i < rows.Count; i++) rows[i].SortOrder = i;
        await db.SaveChangesAsync(ct);
        return RequirementCatalogueCommandOutcome.Committed();
    }, token);

    public Task<RequirementCatalogueCommandOutcome> ReorderAsync(ReorderRequirementCountable command, CancellationToken token = default) => ExecuteAsync(async (db, ct) =>
    {
        var rows = await db.RequirementCountables.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct);
        var row = rows.Find(x => x.Id == command.Id);
        if (row is null) return RequirementCatalogueCommandOutcome.Missing("The countable no longer exists.");
        if (command.TargetIndex < 0 || command.TargetIndex >= rows.Count) return Invalid("TargetIndex", command.Id, row.Name, "Target index is outside the countable order.");
        rows.Remove(row); rows.Insert(command.TargetIndex, row);
        for (var i = 0; i < rows.Count; i++) rows[i].SortOrder = i;
        await db.SaveChangesAsync(ct);
        return RequirementCatalogueCommandOutcome.Committed();
    }, token);

    public Task<RequirementCatalogueCommandOutcome> ReorderAsync(ReorderRequirementEnemyType command, CancellationToken token = default) => ExecuteAsync(async (db, ct) =>
    {
        var rows = await db.RequirementEnemyTypes.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct);
        var row = rows.Find(x => x.Id == command.Id);
        if (row is null) return RequirementCatalogueCommandOutcome.Missing("The enemy type no longer exists.");
        if (command.TargetIndex < 0 || command.TargetIndex >= rows.Count) return Invalid("TargetIndex", command.Id, row.Name, "Target index is outside the enemy type order.");
        rows.Remove(row); rows.Insert(command.TargetIndex, row);
        for (var i = 0; i < rows.Count; i++) rows[i].SortOrder = i;
        await db.SaveChangesAsync(ct);
        return RequirementCatalogueCommandOutcome.Committed();
    }, token);
}
