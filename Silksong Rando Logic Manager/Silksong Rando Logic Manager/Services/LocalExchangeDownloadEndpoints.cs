using Microsoft.AspNetCore.Http.HttpResults;

namespace Silksong_Rando_Logic_Manager.Services;

public static class LocalExchangeDownloadEndpoints
{
    public const string CompletePath = "/exports/complete";
    public const string CurrentRoomPath = "/exports/rooms/{roomId:guid}";
    public const string CurrentRoomPathPrefix = "/exports/rooms/";
    public const string ZonePath = "/exports/zones/{roomId:guid}";
    public const string ZonePathPrefix = "/exports/zones/";
    private const string JsonUtf8ContentType = "application/json; charset=utf-8";

    public static IEndpointRouteBuilder MapLocalExchangeDownloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(CompletePath, DownloadCompleteAsync);
        endpoints.MapGet(CurrentRoomPath, DownloadCurrentRoomAsync);
        endpoints.MapGet(ZonePath, DownloadZoneAsync);
        return endpoints;
    }

    public static async Task<Results<ProblemHttpResult, FileContentHttpResult>> DownloadCompleteAsync(
        bool includeAreaMap,
        DistributedExportService exports,
        CancellationToken cancellationToken = default)
    {
        var outcome = await exports.ExportCompleteAsync(includeAreaMap, cancellationToken);
        return outcome switch
        {
            CompleteExported exported => Download(exported.Export),
            CompleteExportInvalidLocationType invalid => Problem(invalid.Failure),
            _ => throw new InvalidOperationException("Unknown complete export outcome.")
        };
    }

    public static async Task<Results<NotFound, ProblemHttpResult, FileContentHttpResult>> DownloadCurrentRoomAsync(
        Guid roomId,
        DistributedExportService exports,
        CancellationToken cancellationToken = default)
    {
        var outcome = await exports.ExportCurrentRoomAsync(roomId, cancellationToken);
        return outcome switch
        {
            CurrentRoomExported exported => TypedResults.File(exported.Export.JsonUtf8, JsonUtf8ContentType, exported.Export.FileName),
            CurrentRoomMissing => TypedResults.NotFound(),
            CurrentRoomExportInvalidLocationType invalid => Problem(invalid.Failure),
            _ => throw new InvalidOperationException("Unknown current-room export outcome.")
        };
    }

    public static async Task<Results<NotFound, ProblemHttpResult, FileContentHttpResult>> DownloadZoneAsync(
        Guid roomId,
        DistributedExportService exports,
        CancellationToken cancellationToken = default)
    {
        var outcome = await exports.ExportZoneAsync(roomId, cancellationToken);
        return outcome switch
        {
            ZoneExported exported => TypedResults.File(exported.Export.JsonUtf8, JsonUtf8ContentType, exported.Export.FileName),
            ZoneExportUnavailable => TypedResults.NotFound(),
            ZoneExportInvalidLocationType invalid => Problem(invalid.Failure),
            _ => throw new InvalidOperationException("Unknown zone export outcome.")
        };
    }

    private static FileContentHttpResult Download(DistributedExportResult export) =>
        TypedResults.File(export.JsonUtf8, JsonUtf8ContentType, export.FileName);

    private static ProblemHttpResult Problem(DistributedExportInvalidLocationType failure) => TypedResults.Problem(
        statusCode: StatusCodes.Status422UnprocessableEntity,
        title: "Check location Type correction required",
        detail: $"Room '{failure.RoomName}' ({failure.RoomId}) contains check '{failure.CheckName}' ({failure.CheckId}) with an unavailable Type. Select a current Type for that check before exporting.");
}
