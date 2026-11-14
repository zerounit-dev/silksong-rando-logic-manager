using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using SilksongRandoLogicManager.Contracts;
using Silksong_Rando_Logic_Manager.Authentication;

namespace Silksong_Rando_Logic_Manager.Services;

public static partial class V3CatalogueHttp
{
    private static void MapLanding(RouteGroupBuilder api)
    {
        api.MapPost("/map-links/merge", async (HttpContext context, MapLinkService links) =>
        {
            var result = await links.MergeBlankMapScenesIntoRoomsAsync(CancellationToken.None);
            await WriteCacheResultAsync(context, 200, new LandingCommit(true, result.MergedCount, result.Shared));
        }).RequireAuthorization(AccessPolicies.Author);
        api.MapPost("/map-links/apply", async (HttpContext context, ApplyLandingLinks command, MapLinkService links) =>
        {
            if (command.Drafts is null || command.Drafts.Any(x => x is null) || !ValidIds(command.Drafts.Select(x => x.MapSceneId).ToArray())) throw new RequestBindingException();
            MapLinkWriteOutcome result;
            try { result = await links.SaveAsync(command.Drafts.Select(x => new MapLinkDraft(x.MapSceneId, x.RoomReferenceText)).ToArray(), CancellationToken.None); }
            catch (MapLinkTargetMissingException)
            { await ErrorAsync(context, 404, "missing", "A map scene no longer exists. Close and reopen map links to review current rows."); return; }
            await WriteCacheResultAsync(context, 200, new LandingCommit(true, Shared: result.Shared));
        }).RequireAuthorization(AccessPolicies.Author);
        api.MapPost("/map-overlay/placement", LandingPlacementAsync).RequireAuthorization(AccessPolicies.Author);
    }

    private static async Task LandingPlacementAsync(HttpContext context, LandingPlacement placement)
    {
        if (placement.MapId == Guid.Empty || !double.IsFinite(placement.ScaleXPercent) || placement.ScaleXPercent <= 0 || !double.IsFinite(placement.ScaleYPercent) || placement.ScaleYPercent <= 0 || !double.IsFinite(placement.LeftOffsetPercent) || !double.IsFinite(placement.BottomOffsetPercent)) throw new RequestBindingException();
        await using var db = await context.RequestServices.GetRequiredService<IDbContextFactory<LogicDbContext>>().CreateDbContextAsync(context.RequestAborted);
        var overlay = await db.MapOverlays.AsNoTracking().Where(x => x.MapId == placement.MapId).OrderBy(x => x.SortOrder)
            .Select(x => new { x.ImageAssetKey, x.Map!.MapUnitMinX, x.Map.MapUnitMinY, x.Map.MapUnitMaxX, x.Map.MapUnitMaxY }).FirstOrDefaultAsync(context.RequestAborted);
        if (overlay is null)
        { await ErrorAsync(context, 404, "missing", "This map no longer has an image overlay."); return; }
        if (!context.RequestServices.GetRequiredService<MapOverlayAssetCatalog>().TryGet(overlay.ImageAssetKey, out var asset)
            || overlay.MapUnitMinX is not { } minX || overlay.MapUnitMinY is not { } minY || overlay.MapUnitMaxX is not { } maxX || overlay.MapUnitMaxY is not { } maxY
            || !context.RequestServices.GetRequiredService<MapOverlayPlacementService>().TryProject(minX, minY, maxX, maxY, placement.ScaleXPercent, placement.ScaleYPercent, placement.LeftOffsetPercent, placement.BottomOffsetPercent, asset.AspectRatio, out var projected)
            || !double.IsFinite(projected.X) || !double.IsFinite(projected.Y) || !double.IsFinite(projected.Width) || projected.Width <= 0 || !double.IsFinite(projected.Height) || projected.Height <= 0)
        { await ErrorAsync(context, 400, "invalid", "Scale must be finite and positive; offsets and projected placement must be finite."); return; }
        try { await context.RequestServices.GetRequiredService<MapOverlayService>().SavePlacementAsync(new(placement.MapId, placement.ScaleXPercent, placement.ScaleYPercent, placement.LeftOffsetPercent, placement.BottomOffsetPercent), CancellationToken.None); }
        catch (MapOverlayMissingException)
        { await ErrorAsync(context, 404, "missing", "This map no longer has an image overlay."); return; }
        await WriteCacheResultAsync(context, 200, new LandingCommit(true));
    }
}
