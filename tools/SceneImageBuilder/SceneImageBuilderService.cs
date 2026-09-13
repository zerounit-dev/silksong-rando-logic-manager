using System.Security.Cryptography;
using System.Text.Json;
using ImageMagick;

namespace SceneImageBuilder;

public static class SceneImageBuilderService
{
    public const int TileSize = 1024;
    public const int PreviewMaxDimension = 2048;
    private const string MasterFileName = "room-map-hd.png";
    private const string PreviewFileName = "preview.webp";
    private const string BuildFileName = "build.json";

    public static async Task<int> BuildAsync(string[] arguments)
    {
        if (arguments.Length != 1)
        {
            Console.Error.WriteLine("Usage: SceneImageBuilder <scene-source-directory>");
            return 2;
        }

        var result = await BuildAsync(arguments[0]);
        Console.WriteLine(result == SceneImageBuildResult.Skipped ? "Scene image source is unchanged; build skipped." : "Scene image preview and tiles generated.");
        return 0;
    }

    public static async Task<SceneImageBuildResult> BuildAsync(string sourceDirectory, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(sourceDirectory);
        var masterPath = Path.Combine(directory, MasterFileName);
        if (!File.Exists(masterPath)) throw new FileNotFoundException("Scene image master was not found.", masterPath);

        foreach (var interruptedBuild in Directory.EnumerateDirectories(directory, ".scene-image-build-*"))
        {
            Directory.Delete(interruptedBuild, true);
        }

        var hash = await HashAsync(masterPath, cancellationToken);
        var buildPath = Path.Combine(directory, BuildFileName);
        var prior = await ReadBuildAsync(buildPath, cancellationToken);
        if (prior is not null && prior.MasterSha256 == hash && OutputsExist(directory, prior)) return SceneImageBuildResult.Skipped;

        var stagingDirectory = Path.Combine(directory, $".scene-image-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            using var image = new MagickImage(masterPath);
            using (var preview = new MagickImage(masterPath))
            {
                preview.Resize(new MagickGeometry(PreviewMaxDimension, PreviewMaxDimension) { Greater = true });
                preview.Format = MagickFormat.WebP;
                preview.Write(Path.Combine(stagingDirectory, PreviewFileName));
            }

            var tilesDirectory = Path.Combine(stagingDirectory, "tiles");
            Directory.CreateDirectory(tilesDirectory);
            var columns = (int)Math.Ceiling(image.Width / (double)TileSize);
            var rows = (int)Math.Ceiling(image.Height / (double)TileSize);
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var width = Math.Min(TileSize, checked((int)image.Width) - column * TileSize);
                    var height = Math.Min(TileSize, checked((int)image.Height) - row * TileSize);
                    using var tile = image.Clone();
                    tile.Crop(new MagickGeometry(column * TileSize, row * TileSize, checked((uint)width), checked((uint)height)));
                    tile.ResetPage();
                    tile.Format = MagickFormat.WebP;
                    tile.Write(Path.Combine(tilesDirectory, $"{column}-{row}.webp"));
                }
            }

            var manifest = new SceneImageBuildManifest(hash, checked((int)image.Width), checked((int)image.Height), columns, rows);
            await File.WriteAllTextAsync(Path.Combine(stagingDirectory, BuildFileName), JsonSerializer.Serialize(manifest), cancellationToken);
            ReplaceDirectory(Path.Combine(directory, "tiles"), tilesDirectory);
            File.Move(Path.Combine(stagingDirectory, PreviewFileName), Path.Combine(directory, PreviewFileName), true);
            File.Move(Path.Combine(stagingDirectory, BuildFileName), buildPath, true);
            return SceneImageBuildResult.Generated;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
        }
    }

    private static bool OutputsExist(string directory, SceneImageBuildManifest manifest) =>
        File.Exists(Path.Combine(directory, PreviewFileName)) &&
        Directory.Exists(Path.Combine(directory, "tiles")) &&
        Enumerable.Range(0, manifest.Rows).All(row => Enumerable.Range(0, manifest.Columns).All(column => File.Exists(Path.Combine(directory, "tiles", $"{column}-{row}.webp"))));

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task<SceneImageBuildManifest?> ReadBuildAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SceneImageBuildManifest>(stream, cancellationToken: cancellationToken);
    }

    private static void ReplaceDirectory(string destination, string source)
    {
        var previous = destination + ".previous";
        if (Directory.Exists(previous)) Directory.Delete(previous, true);
        if (Directory.Exists(destination)) Directory.Move(destination, previous);
        Directory.Move(source, destination);
        if (Directory.Exists(previous)) Directory.Delete(previous, true);
    }
}

public enum SceneImageBuildResult { Generated, Skipped }
public sealed record SceneImageBuildManifest(string MasterSha256, int Width, int Height, int Columns, int Rows);
