using Microsoft.AspNetCore.Antiforgery;
using System.Text.Json;
using Silksong_Rando_Logic_Manager.Authentication;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

public static partial class V3CatalogueHttp
{
    private const string CacheHeader = "X-Logic-Cache";

    private static void MapCache(RouteGroupBuilder api)
    {
        api.MapGet("/cache/startup", CacheStartupAsync).RequireAuthorization(AccessPolicies.Normal);
        api.MapGet("/cache/database", CacheDatabaseAsync).RequireAuthorization(AccessPolicies.Normal);
        api.MapPost("/cache/changes", async (HttpContext context, Wire.LogicCacheCheck request, LogicCacheTransfer transfer) =>
        {
            ValidateCheckpoint(request.Known);
            await WriteAsync(context, 200, await transfer.ChangesAsync(request.Known, context.RequestAborted));
        }).RequireAuthorization(AccessPolicies.Normal);
        api.MapPost("/cache/image-observations", async (HttpContext context, Wire.LogicCacheImageRequest request, LogicCacheTransfer transfer) =>
        {
            if (request.RoomIds is null || !ValidIds(request.RoomIds)) throw new RequestBindingException();
            await WriteAsync(context, 200, await transfer.ImageObservationsAsync(request.RoomIds, context.RequestAborted));
        }).RequireAuthorization(AccessPolicies.Normal);
    }

    private static async Task WriteCacheResultAsync<T>(HttpContext context, int status, T result)
    {
        var envelope = await context.RequestServices.GetRequiredService<LogicCacheTransfer>()
            .ResultAsync(result, CacheCheckpoint(context), CancellationToken.None);
        await WriteAsync(context, status, envelope);
    }

    private static Wire.LogicCacheCheckpoint? CacheCheckpoint(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(CacheHeader, out var header)) return null;
        if (header.Count != 1 || string.IsNullOrWhiteSpace(header[0])) throw new RequestBindingException();
        try
        {
            var value = JsonSerializer.Deserialize<Wire.LogicCacheCheckpoint>(header[0]!, Json)
                ?? throw new RequestBindingException();
            ValidateCheckpoint(value);
            return value;
        }
        catch (JsonException) { throw new RequestBindingException(); }
    }

    private static void ValidateCheckpoint(Wire.LogicCacheCheckpoint? value)
    {
        if (value is null) return;
        if (value.Format <= 0 || value.RunId == Guid.Empty || string.IsNullOrEmpty(value.Schema)
            || value.StartedUtc.Kind != DateTimeKind.Utc || value.StartedUtc.Ticks == 0
            || value.ThroughUtc.Kind != DateTimeKind.Utc || value.ThroughUtc.Ticks == 0)
            throw new RequestBindingException();
        // Well-formed but incompatible run/schema/format is a normal full transfer,
        // not a validation error or a fabricated browser migration.
    }

    private static async Task CacheStartupAsync(HttpContext context, LogicCacheTransfer transfer)
    {
        var token = context.RequestAborted;
        var known = CacheCheckpoint(context);
        var access = context.RequestServices.GetRequiredService<LiveAccess>();
        var identity = await access.CurrentAsync(token);
        Wire.LogicCacheChanges? cache = null;
        IReadOnlyList<Wire.LogicCacheImageObservation> observations = [];
        if (identity is { Replacement: false })
        {
            var startup = await transfer.StartupAsync(known, token);
            identity = startup.Identity; cache = startup.Cache; observations = startup.Images;
        }
        var mode = context.RequestServices.GetRequiredService<AuthenticationMode>();
        var csrf = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context).RequestToken!;
        var session = new Wire.AuthenticationSession(identity is not null, identity?.Replacement ?? false,
            identity?.Username, identity?.CanManageAdmins ?? false, identity?.CanManageUsers ?? false,
            identity?.CanAuthor ?? false, identity?.CanPublishDocumentation ?? false, mode.Bypass, identity?.WorkspaceKey, csrf);
        await WriteAsync(context, 200, new Wire.LogicCacheStartup(session, cache, observations));
    }

    private static async Task CacheDatabaseAsync(HttpContext context, LogicCacheTransfer transfer)
    {
        var token = context.RequestAborted;
        await using var prepared = await transfer.DatabaseAsync(token);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/vnd.sqlite3";
        context.Response.ContentLength = prepared.Content.Length;
        context.Response.Headers[CacheHeader] = JsonSerializer.Serialize(prepared.Checkpoint, Json);
        try { await prepared.Content.CopyToAsync(context.Response.Body, token); }
        catch
        {
            // Preserve ordinary typed errors before headers; after headers a
            // failed binary body must be a failed transfer, never valid SQLite.
            if (context.Response.HasStarted) context.Abort();
            else
            {
                context.Response.ContentLength = null;
                context.Response.ContentType = null;
                context.Response.Headers.Remove(CacheHeader);
            }
            throw;
        }
    }
}
