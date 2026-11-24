using Silksong_Rando_Logic_Manager.Authentication;

namespace Silksong_Rando_Logic_Manager.Services.Publishing;

internal static class PublishingReviewHttp
{
    private const string Prefix = "/api/publishing/review-image";
    internal static string ImageUrl(Guid generation, long epoch, int entry, string side) => FormattableString.Invariant($"{Prefix}/{generation:D}/{entry}/{side}?epoch={epoch}");

    internal static void MapPublishingReview(this WebApplication app) => app.MapGet(Prefix + "/{generation:guid}/{entry:int}/{side}",
        async Task<IResult> (Guid generation, int entry, string side, long? epoch, HttpContext context, LiveAccess access,
            DocumentationPublishingService publishing, ILogger<DocumentationPublishingService> logger) =>
        {
            AccountHttp.NoStore(context);
            context.Response.Headers.XContentTypeOptions = "nosniff";
            try
            {
                return epoch is { } version
                    ? await publishing.ServeReviewImageAsync(access, generation, version, entry, side, context)
                    : Results.NotFound();
            }
            catch (PublishingBlock failure)
            {
                return Results.Problem(statusCode: 409, title: "The review image is unavailable.", detail: failure.Message);
            }
            catch (Exception failure) when (failure is FileNotFoundException or DirectoryNotFoundException)
            {
                logger.LogWarning(failure, "A retained publication review image could not be read.");
                return Results.Problem(statusCode: 409, title: "A retained review image no longer exists.");
            }
            catch (Exception failure) when (!context.Response.HasStarted && failure is not (AccessDeniedException or OperationCanceledException))
            {
                logger.LogError(failure, "Reading a retained publication review image failed.");
                return Results.Problem(statusCode: 500, title: "The review image could not be read. See server logs.");
            }
        }).RequireAuthorization(AccessPolicies.PublishDocumentation);
}
