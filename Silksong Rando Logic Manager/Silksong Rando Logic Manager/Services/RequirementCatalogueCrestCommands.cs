using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed partial class RequirementCatalogueService
{
    public Task<RequirementCatalogueCommandOutcome> ApplyAsync(ApplyRequirementCrest command, CancellationToken token = default) =>
        ExecuteAsync((db, ct) => ApplyCrestAsync(db, command, ct), token);

    private static async Task<RequirementCatalogueCommandOutcome> ApplyCrestAsync(LogicDbContext db, ApplyRequirementCrest command, CancellationToken ct)
    {
        if (!RequirementCatalogueLanguage.TryCanonicalizeAliases(command.AliasDraft, out var aliases, out _, out var error))
            return Invalid("Aliases", command.Id ?? Guid.Empty, command.Name, error!);
        var row = command.Id is { } id ? await db.RequirementCrests.SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        if (command.Id is not null && row is null) return RequirementCatalogueCommandOutcome.Missing("The crest no longer exists.");
        var rows = await db.RequirementCrests.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct);
        if (row is null)
        {
            row = new RequirementCrest { Id = Guid.NewGuid() };
            db.RequirementCrests.Add(row);
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

    public Task<RequirementCatalogueCommandOutcome> DeleteAsync(DeleteRequirementCrest command, CancellationToken token = default) => ExecuteAsync(async (db, ct) =>
    {
        var row = await db.RequirementCrests.FindAsync([command.Id], ct);
        if (row is null) return RequirementCatalogueCommandOutcome.Missing("The crest no longer exists.");
        db.Remove(row);
        var rows = await db.RequirementCrests.Where(x => x.Id != command.Id).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct);
        for (var i = 0; i < rows.Count; i++) rows[i].SortOrder = i;
        await db.SaveChangesAsync(ct);
        return RequirementCatalogueCommandOutcome.Committed();
    }, token);

    public Task<RequirementCatalogueCommandOutcome> ReorderAsync(ReorderRequirementCrest command, CancellationToken token = default) => ExecuteAsync(async (db, ct) =>
    {
        var rows = await db.RequirementCrests.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct);
        var row = rows.Find(x => x.Id == command.Id);
        if (row is null) return RequirementCatalogueCommandOutcome.Missing("The crest no longer exists.");
        if (command.TargetIndex < 0 || command.TargetIndex >= rows.Count) return Invalid("TargetIndex", command.Id, row.Name, "Target index is outside the crest order.");
        rows.Remove(row); rows.Insert(command.TargetIndex, row);
        for (var i = 0; i < rows.Count; i++) rows[i].SortOrder = i;
        await db.SaveChangesAsync(ct);
        return RequirementCatalogueCommandOutcome.Committed();
    }, token);
}
