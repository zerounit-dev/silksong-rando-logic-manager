using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Standard room metadata assembly for every scalar/full/family read.
/// Call only while holding image lifecycle admission before the read transaction.
/// No bytes, persistence, token advancement or independent observation protocol.</summary>
internal static class RoomBundleAssembly
{
    internal static Wire.RoomHeaderSnapshot Complete(Wire.RoomHeaderSnapshot header, SceneImageFileService images) =>
        header with { Metadata = new(images.Exists(header.Observation.EntityId)) };
}
