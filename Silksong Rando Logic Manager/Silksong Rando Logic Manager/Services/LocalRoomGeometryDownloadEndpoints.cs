using Microsoft.AspNetCore.Http.HttpResults;

namespace Silksong_Rando_Logic_Manager.Services;

public static class LocalRoomGeometryDownloadEndpoints
{
    public const string ExportPath = "/api/room-geometry/export";

    public static IEndpointRouteBuilder MapLocalRoomGeometryDownloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ExportPath, DownloadAsync).AllowAnonymous().DisableAntiforgery();
        return endpoints;
    }

    public static async Task<Results<FileContentHttpResult, ProblemHttpResult>> DownloadAsync(
        IRoomGeometryExportService exports, CancellationToken cancellationToken)
    {
        var result = await exports.GenerateAsync(cancellationToken);
        if (result.Succeeded)
            return TypedResults.File(result.Content!, "application/json; charset=utf-8", RoomGeometryExportResult.FileName);
        if (result.Content is null && result.Failures.Count > 0)
            return TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Room geometry export failed", detail: "Correct the reported room geometry export failures and retry.",
                extensions: new Dictionary<string, object?> { ["failures"] = result.Failures });
        throw new InvalidOperationException("Unknown room geometry export outcome.");
    }
}
