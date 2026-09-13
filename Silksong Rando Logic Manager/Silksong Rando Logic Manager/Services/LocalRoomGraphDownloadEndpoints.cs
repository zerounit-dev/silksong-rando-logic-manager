using Microsoft.AspNetCore.Http.HttpResults;

namespace Silksong_Rando_Logic_Manager.Services;

public static class LocalRoomGraphDownloadEndpoints
{
    public const string ExportPath = "/api/room-graph/export";
    private const string JsonUtf8ContentType = "application/json; charset=utf-8";

    public static IEndpointRouteBuilder MapLocalRoomGraphDownloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ExportPath, DownloadAsync)
            .AllowAnonymous()
            .DisableAntiforgery();
        return endpoints;
    }

    public static async Task<Results<FileContentHttpResult, ProblemHttpResult>> DownloadAsync(
        IRoomGraphExportService exports,
        CancellationToken cancellationToken)
    {
        var result = await exports.GenerateAsync(cancellationToken);

        if (result.Succeeded)
        {
            return TypedResults.File(result.Content!, JsonUtf8ContentType, RoomGraphExportResult.FileName);
        }

        if (result.Content is null && result.Failures.Count > 0)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Room graph export failed",
                detail: "Correct the reported room graph export failures and retry.",
                extensions: new Dictionary<string, object?>
                {
                    ["failures"] = result.Failures
                });
        }

        throw new InvalidOperationException("Unknown room graph export outcome.");
    }
}
