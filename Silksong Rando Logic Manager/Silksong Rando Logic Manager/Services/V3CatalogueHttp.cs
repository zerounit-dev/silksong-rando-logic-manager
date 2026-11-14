using System.Text.Json;
using Silksong_Rando_Logic_Manager.Authentication;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Native HTTP boundary; application services retain execution admission.</summary>
public static partial class V3CatalogueHttp
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true };
    internal sealed class RequestBindingException : Exception { }
    internal sealed record EndpointAdmission;

    internal static bool InitialCacheSession(HttpRequest request) => request.Method == "GET" &&
        string.Equals(request.Path.Value, "/api/v3/cache/startup", StringComparison.Ordinal);

    internal static bool NativeDraftPreview(HttpRequest request) => request.Method == "GET" &&
        request.Path.Value?.Split('/') is ["", "api", "v3", "rooms", var room, "tracking", var tracker, "image-drafts", var draft, "preview", var handle] &&
        Guid.TryParse(room, out _) && Guid.TryParse(tracker, out _) && Guid.TryParse(draft, out _) && Guid.TryParse(handle, out _);

    public static void MapV3Catalogue(this WebApplication app)
    {
        var api = app.MapGroup("/api/v3").WithMetadata(new EndpointAdmission(), new ApplicationVersionEndpoint(ApplicationVersionChannel.Header));
        api.AddEndpointFilter(async (invocation, next) =>
        {
            // Validate before any business operation, never after a known commit.
            _ = CacheCheckpoint(invocation.HttpContext);
            return await next(invocation);
        });
        api.MapGet("/room-groups/{id}", async (HttpContext context, Guid id, LogicCatalogService catalog) =>
        {
            var editor = await catalog.ReadSidebarGroupAsync(id, context.RequestAborted);
            if (editor is not null) await WriteAsync(context, 200, editor);
            else await ErrorAsync(context, 404, "missing", "This group no longer exists. Choose a current group.");
        }).RequireAuthorization(AccessPolicies.Normal);
        api.MapGet("/rooms/{id}/audit", async (HttpContext context, Guid id, RoomAuditSnapshotService audit) =>
            await WriteAsync(context, 200, await audit.LoadAsync(id, CacheCheckpoint(context), context.RequestAborted)))
            .RequireAuthorization(AccessPolicies.Normal);
        api.MapPost("/room-groups", async (HttpContext context, LogicCatalogService catalog) =>
            await SidebarResultAsync(context, await catalog.CreateSidebarGroupAsync(context.RequestAborted)))
            .RequireAuthorization(AccessPolicies.Author);
        api.MapPost("/rooms", async (HttpContext context, Wire.CreateRoom command, LogicCatalogService catalog) =>
            await SidebarResultAsync(context, await catalog.CreateSidebarRoomAsync(command.GroupId, context.RequestAborted)))
            .RequireAuthorization(AccessPolicies.Author);
        api.MapPost("/rooms/auto-group", async (HttpContext context, LogicCatalogService catalog) =>
            await SidebarResultAsync(context, await catalog.AutoMatchSidebarAsync(context.RequestAborted)))
            .RequireAuthorization(AccessPolicies.Author);
        api.MapPatch("/room-groups/{id}", async (HttpContext context, Guid id, Wire.SaveGroup command, LogicCatalogService catalog) =>
        {
            if (command.Baseline is null || command.Values is null || command.Baseline.Id != id
                || command.Baseline.FriendlyName is null || command.Values.FriendlyName is null
                || command.Baseline.UpdatedUtc.Kind != DateTimeKind.Utc) throw new RequestBindingException();
            await SidebarResultAsync(context, await catalog.SaveSidebarGroupAsync(id, command, context.RequestAborted));
        }).RequireAuthorization(AccessPolicies.Author);
        api.MapDelete("/room-groups/{id}", async (HttpContext context, Guid id, LogicCatalogService catalog) =>
            await SidebarResultAsync(context, await catalog.DeleteSidebarGroupAsync(id, context.RequestAborted)))
            .RequireAuthorization(AccessPolicies.Author);
        api.MapPost("/room-groups/{id}/move", async (HttpContext context, Guid id, Wire.MoveGroup command, LogicCatalogService catalog) =>
        {
            if (command.ExpectedGroupIds is null || !ValidIds(command.ExpectedGroupIds)) throw new RequestBindingException();
            await SidebarResultAsync(context, await catalog.MoveSidebarGroupAsync(id, command, context.RequestAborted));
        }).RequireAuthorization(AccessPolicies.Author);
        api.MapPost("/rooms/{id}/move", async (HttpContext context, Guid id, Wire.MoveRoom command, LogicCatalogService catalog) =>
        {
            if (command.ExpectedSourceRoomIds is null || command.ExpectedTargetRoomIds is null
                || !ValidIds(command.ExpectedSourceRoomIds) || !ValidIds(command.ExpectedTargetRoomIds)) throw new RequestBindingException();
            await SidebarResultAsync(context, await catalog.MoveSidebarRoomAsync(id, command, context.RequestAborted));
        }).RequireAuthorization(AccessPolicies.Author);
        MapCache(api);
        MapHistory(api);
        MapRequirements(api);
        MapScene(api);
        MapLanding(api);
        MapRoomEditing(api);
    }

    private static async Task SidebarResultAsync(HttpContext context, LogicCatalogService.SidebarCommandOutcome outcome)
    {
        if (outcome.Result is { } result)
        {
            if (outcome.Status == 201)
                context.Response.Headers.Location = result.CreatedRoomId is { } room ? $"/rooms/{room}" : $"/api/v3/room-groups/{result.GroupEditor!.Group.Id}";
            await WriteCacheResultAsync(context, outcome.Status, result);
        }
        else await WriteAsync(context, outcome.Status, outcome.Error!);
    }

    private static bool ValidIds(IReadOnlyList<Guid> ids) => ids.All(x => x != Guid.Empty) && ids.Distinct().Count() == ids.Count;
    private static async Task<JsonDocument> ReadObjectAsync(HttpContext context)
    {
        if (!context.Request.HasJsonContentType()) throw new BadHttpRequestException("Expected JSON.", 415);
        try
        {
            var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new RequestBindingException(); }
            return document;
        }
        catch (JsonException) { throw new RequestBindingException(); }
    }
    internal static Task ErrorAsync(HttpContext context, int status, string code, string message) =>
        WriteAsync(context, status, new Wire.ApiError(code, message, context.TraceIdentifier));
    private static Task WriteAsync<T>(HttpContext context, int status, T value)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(value, Json, cancellationToken: CancellationToken.None);
    }
}
