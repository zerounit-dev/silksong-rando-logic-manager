using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Tests;

internal sealed class TestAreaMapLoader : IAreaMapLoader
{
    public int LandingLoads { get; private set; }
    public int RoomLoads { get; private set; }
    public Task<LandingAreaMapView> LoadLandingAsync(CancellationToken cancellationToken = default)
    {
        LandingLoads++;
        return Task.FromResult(new LandingAreaMapView(LandingAreaMapState.NoImportedMap, null));
    }
    public Task<AreaMapSurfaceView?> LoadRoomAsync(Guid roomId, GlobalAreaMapGeometryView? retainedGeometry,
        CancellationToken cancellationToken = default)
    {
        RoomLoads++;
        return Task.FromResult<AreaMapSurfaceView?>(null);
    }
}
