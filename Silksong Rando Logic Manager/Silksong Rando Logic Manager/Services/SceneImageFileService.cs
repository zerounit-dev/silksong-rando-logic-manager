namespace Silksong_Rando_Logic_Manager.Services;

public sealed class SceneImageFileService
{
    private readonly string scenesDirectory;
    private readonly string sourceDirectory;

    public SceneImageFileService(IHostEnvironment environment) : this(environment.ContentRootPath)
    {
    }

    public SceneImageFileService(string contentRootPath)
    {
        scenesDirectory = Path.Combine(Path.GetFullPath(contentRootPath), "data", "scenes");
        sourceDirectory = Path.Combine(Path.GetFullPath(contentRootPath), "data", "scene-source");
    }

    public string GetRoomImagePath(Guid roomId) => Path.Combine(scenesDirectory, $"{roomId:D}.webp");

    public string CreateTemporaryRoomImagePath(Guid roomId)
    {
        Directory.CreateDirectory(scenesDirectory);
        return Path.Combine(scenesDirectory, $".{roomId:D}-{Guid.NewGuid():N}.webp");
    }

    public bool Exists(Guid roomId) => File.Exists(GetRoomImagePath(roomId));

    public FileStream? OpenRead(Guid roomId)
    {
        var path = GetRoomImagePath(roomId);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    public FileStream? OpenPreview()
    {
        var path = Path.Combine(sourceDirectory, "preview.webp");
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    public string SourceDirectory => sourceDirectory;

    public Task DeleteAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetRoomImagePath(roomId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
