using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class MapOverlayService(IDbContextFactory<LogicDbContext> dbContextFactory, Authentication.LiveAccess access,
    SharedPublicationCoordinator publications)
{
    public async Task SavePlacementAsync(MapOverlayPlacementCommand draft, CancellationToken cancellationToken = default)
    {
        using var publication = await publications.AcquireAsync(cancellationToken);
        var historyActor = HistoryActor.From(await access.RequireAsync(Authentication.AccessPolicies.Author, cancellationToken));
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await LogicWriteScope.BeginAsync(db, publication, historyActor, cancellationToken);
        var updated = await db.MapOverlays.Where(x => x.MapId == draft.MapId).OrderBy(x => x.SortOrder).Take(1)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.ScaleXPercent, draft.ScaleXPercent)
                .SetProperty(x => x.ScaleYPercent, draft.ScaleYPercent)
                .SetProperty(x => x.LeftOffsetPercent, draft.LeftOffsetPercent)
                .SetProperty(x => x.BottomOffsetPercent, draft.BottomOffsetPercent), cancellationToken);
        if (updated != 1) throw new MapOverlayMissingException();
        await transaction.FinalizeAsync(cancellationToken);
        await transaction.CommitAsync(token: cancellationToken);
    }
}

public sealed class MapOverlayMissingException : InvalidOperationException
{
    public MapOverlayMissingException() : base("The selected map no longer has an image overlay. Reloaded map data; retry after reviewing the draft.") { }
}

public sealed record MapOverlayPlacementCommand(Guid MapId, double ScaleXPercent, double ScaleYPercent,
    double LeftOffsetPercent, double BottomOffsetPercent);
