using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

public enum ApplicationVersionChannel { Header, NativePreview, WorkspaceArtifact }
public sealed record ApplicationVersionEndpoint(ApplicationVersionChannel Channel);

public static class ApplicationVersionAdmission
{
    public static void UseApplicationVersionAdmission(this WebApplication app) => app.Use(async (context, next) =>
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<ApplicationVersionEndpoint>();
        if (metadata is null || metadata.Channel == ApplicationVersionChannel.WorkspaceArtifact
            && !context.Request.Headers.ContainsKey("X-Logic-Workspace")) { await next(context); return; }
        var supplied = metadata.Channel == ApplicationVersionChannel.NativePreview
            ? context.Request.Query["appVersion"] : context.Request.Headers[ApplicationVersion.Header];
        context.Response.Headers[ApplicationVersion.Header] = ApplicationBuild.Version;
        if (supplied.Count != 1 || !ApplicationVersion.Valid(supplied[0]) || supplied[0] != ApplicationBuild.Version)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { code = ApplicationVersion.MismatchCode, serverVersion = ApplicationBuild.Version }, CancellationToken.None);
            return;
        }
        await next(context); // Native binding occurs only after this admission.
    });
}
