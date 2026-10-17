using System.Collections.Concurrent;
using System.Security.Cryptography;
using ImageMagick;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed record RoomImageUpload(Guid Handle, uint Width, uint Height, string Hash);
internal sealed class RoomImageOriginalUnavailableException() : InvalidOperationException("The uploaded original is missing or does not match its recorded identity. Replace it before rebuilding.");
internal sealed record StagedRoomImage(string Path, RoomImageUpload View, string ContentType);
internal sealed class RoomImageDeletionCleanupException(TimeSpan elapsed, IEnumerable<Exception> failures)
    : AggregateException("Room deleted; image cleanup failed.", failures)
{
    public TimeSpan Elapsed { get; } = elapsed;
}

public sealed class SceneImageFileService
{
    private readonly string scenesDirectory;
    private readonly string sourceDirectory;
    private readonly string originalsDirectory;
    private readonly string stagingDirectory;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<Guid, StagedRoomImage> staged = new();
    public const long UploadLimit = 256L * 1024 * 1024;

    public SceneImageFileService(IHostEnvironment environment) : this(environment.ContentRootPath)
    {
    }

    public SceneImageFileService(string contentRootPath)
    {
        scenesDirectory = Path.Combine(Path.GetFullPath(contentRootPath), "data", "scenes");
        sourceDirectory = Path.Combine(Path.GetFullPath(contentRootPath), "data", "scene-source");
        originalsDirectory = Path.Combine(Path.GetFullPath(contentRootPath), "data", "scene-originals");
        stagingDirectory = Path.Combine(Path.GetFullPath(contentRootPath), "data", "scene-image-staging");
    }

    public string GetRoomImagePath(Guid roomId) => Path.Combine(scenesDirectory, $"{roomId:D}.webp");
    internal string GetOriginalPath(Guid roomId) => Path.Combine(originalsDirectory, $"{roomId:D}.image");
    internal string ScenesDirectory => scenesDirectory;
    internal string OriginalsDirectory => originalsDirectory;

    public async Task<IDisposable> AcquireLifecycleAsync(CancellationToken token = default)
    {
        await lifecycle.WaitAsync(token);
        return new Admission(lifecycle);
    }
    private sealed class Admission(SemaphoreSlim gate) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; gate.Release(); } }
    }

    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    internal static bool Matches(string path, string? expected) => expected is not null && File.Exists(path) && Hash(path) == expected;
    internal bool CropMatches(Guid roomId, string? expected) => Matches(GetRoomImagePath(roomId), expected);

    public async Task<RoomImageUpload> StageAsync(Stream input, CancellationToken token = default)
    {
        var handle = Guid.NewGuid();
        Directory.CreateDirectory(stagingDirectory);
        var path = Path.Combine(stagingDirectory, $"{handle:N}.source");
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    total += count;
                    if (total > UploadLimit) throw new InvalidOperationException("Choose an image no larger than 256 MiB.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
            }
            using var image = ReadOriginal(path);
            var contentType = image.Format switch
            {
                MagickFormat.Png or MagickFormat.Png8 or MagickFormat.Png24 or MagickFormat.Png32 or MagickFormat.Png48 or MagickFormat.Png64 => "image/png",
                MagickFormat.Jpeg => "image/jpeg",
                MagickFormat.WebP => "image/webp",
                _ => throw new InvalidOperationException("Choose a PNG, JPEG or WebP image.")
            };
            var view = new RoomImageUpload(handle, image.Width, image.Height, Hash(path));
            token.ThrowIfCancellationRequested();
            staged[handle] = new(path, view, contentType);
            return view;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    internal StagedRoomImage GetStaged(Guid handle) => staged.TryGetValue(handle, out var image)
        ? image : throw new InvalidOperationException("The draft upload is no longer available. Select the image again.");
    internal string PrepareOriginal(Guid handle, string? expected, SceneImagePreparedSize size, CancellationToken token)
    {
        var input = GetStaged(handle);
        if (!Matches(input.Path, expected)) throw new RoomImageOriginalUnavailableException();
        var path = Path.Combine(stagingDirectory, $".{Guid.NewGuid():N}.prepared");
        try
        {
            using var image = ReadOriginal(input.Path);
            if (image.Width != input.View.Width || image.Height != input.View.Height)
                throw new InvalidOperationException("The staged image dimensions changed. Select the image again.");
            token.ThrowIfCancellationRequested();
            // One full oriented-source downsample, never from a preview or prior draft rendition.
            if (image.Width != size.Width || image.Height != size.Height)
            {
                image.FilterType = FilterType.Lanczos;
                image.Resize(new MagickGeometry(size.Width, size.Height) { IgnoreAspectRatio = true });
            }
            image.ResetPage();
            image.Format = MagickFormat.WebP;
            image.Settings.SetDefine(MagickFormat.WebP, "lossless", "true");
            image.Write(path);
            token.ThrowIfCancellationRequested();
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
    public async Task DiscardUploadAsync(Guid handle)
    {
        using var admission = await AcquireLifecycleAsync();
        if (staged.TryRemove(handle, out var image) && File.Exists(image.Path)) File.Delete(image.Path);
    }
    internal byte[]? OpenStagedPreview(Guid handle)
    {
        if (!staged.TryGetValue(handle, out var image)) return null;
        if (!Matches(image.Path, image.View.Hash)) return null;
        return PreviewBytes(image.Path);
    }
    internal static MagickImage ReadOriginal(string path)
    {
        var image = new MagickImage(path);
        try
        {
            if (image.Format is not (MagickFormat.Png or MagickFormat.Png8 or MagickFormat.Png24 or MagickFormat.Png32 or MagickFormat.Png48 or MagickFormat.Png64 or MagickFormat.Jpeg or MagickFormat.WebP))
                throw new InvalidOperationException("Choose a PNG, JPEG or WebP image.");
            image.AutoOrient(); return image;
        }
        catch { image.Dispose(); throw; }
    }
    internal static byte[] PreviewBytes(string path)
    {
        using var image = ReadOriginal(path);
        if (image.Width > 8192 || image.Height > 8192) image.Resize(new MagickGeometry(8192, 8192));
        image.Strip();
        image.Format = MagickFormat.WebP;
        return image.ToByteArray();
    }
    internal string OriginalForGeneration(Guid roomId, string? expected, Guid? upload)
    {
        var path = upload is { } handle ? GetStaged(handle).Path : GetOriginalPath(roomId);
        if (!Matches(path, expected)) throw new RoomImageOriginalUnavailableException();
        return path;
    }
    internal string CreateOriginalBackupPath(Guid roomId)
    {
        Directory.CreateDirectory(originalsDirectory);
        return Path.Combine(originalsDirectory, $".{roomId:D}-{Guid.NewGuid():N}.image");
    }
    internal Task DeleteOriginalAsync(Guid roomId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var path = GetOriginalPath(roomId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

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
