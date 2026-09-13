using SceneImageBuilder;
using ImageMagick;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class SceneImageFileServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"silksong-scene-image-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Builder_GeneratesPreviewAndInternalTilesThenSkipsUnchangedMaster()
    {
        var source = Path.Combine(root, "data", "scene-source");
        Directory.CreateDirectory(source);
        using (var master = new MagickImage(MagickColors.Red, 2, 2))
        {
            master.Write(Path.Combine(source, "room-map-hd.png"));
        }

        Assert.Equal(SceneImageBuildResult.Generated, await SceneImageBuilderService.BuildAsync(source));
        Assert.True(File.Exists(Path.Combine(source, "preview.webp")));
        Assert.True(File.Exists(Path.Combine(source, "tiles", "0-0.webp")));
        Assert.True(File.Exists(Path.Combine(source, "build.json")));
        Assert.Equal(SceneImageBuildResult.Skipped, await SceneImageBuilderService.BuildAsync(source));
    }

    [Fact]
    public async Task RoomFiles_AreGuidDerivedAndDeletedWithoutTouchingOtherFiles()
    {
        var service = new SceneImageFileService(root);
        var roomId = Guid.NewGuid();
        var otherRoomId = Guid.NewGuid();
        var path = service.GetRoomImagePath(roomId);
        var otherPath = service.GetRoomImagePath(otherRoomId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await File.WriteAllBytesAsync(otherPath, [4, 5, 6]);

        Assert.Equal(Path.Combine(root, "data", "scenes", $"{roomId:D}.webp"), path);
        Assert.True(service.Exists(roomId));
        await service.DeleteAsync(roomId);

        Assert.False(service.Exists(roomId));
        Assert.True(service.Exists(otherRoomId));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
