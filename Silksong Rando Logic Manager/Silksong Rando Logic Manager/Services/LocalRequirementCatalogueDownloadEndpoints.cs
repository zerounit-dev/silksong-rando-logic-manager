using Microsoft.AspNetCore.Http.HttpResults;

namespace Silksong_Rando_Logic_Manager.Services;

public static class LocalRequirementCatalogueDownloadEndpoints
{
    public const string ExportPath = "/api/requirement-catalogue/export";
    private const string JsonUtf8ContentType = "application/json; charset=utf-8";

    public static IEndpointRouteBuilder MapLocalRequirementCatalogueDownloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ExportPath, DownloadAsync);
        return endpoints;
    }

    public static async Task<FileContentHttpResult> DownloadAsync(RequirementCatalogueExportService exports, CancellationToken cancellationToken = default)
    {
        var export = await exports.ExportAsync(cancellationToken);
        return TypedResults.File(export.JsonUtf8, JsonUtf8ContentType, export.FileName);
    }
}
