using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class RequirementCatalogueExportService(IDbContextFactory<LogicDbContext> dbContextFactory, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<RequirementCatalogueExportResult> ExportAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var predicates = await db.RequirementPredicates.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new RequirementCatalogueExchangePredicate(x.Id, x.Name, x.Category, x.InputSyntax, x.OutputSyntax, x.Notes, x.SortOrder, x.Aliases))
            .ToListAsync(cancellationToken);
        var items = await db.RequirementItems.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new RequirementCatalogueExchangeItem(x.Id, x.Name, x.Category, x.OutputValue, x.Notes, x.SortOrder, x.Aliases))
            .ToListAsync(cancellationToken);

        var document = new RequirementCatalogueExchangeDocument(1, predicates, items);
        var timestamp = timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        return new($"silksong-requirement-catalogue-{timestamp}.json", JsonSerializer.SerializeToUtf8Bytes(document, jsonOptions));
    }
}
