using System.Formats.Tar;
using System.IO.Compression;

if (args.Length < 4)
{
    Console.Error.WriteLine("Usage: LinuxTarballBuilder <package-root> <archive-path> <executable-path> [<executable-path>...]");
    return 2;
}

var packageRoot = Path.GetFullPath(args[0]);
var archivePath = Path.GetFullPath(args[1]);
var executablePaths = args.Skip(2)
    .Select(Path.GetFullPath)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

if (!Directory.Exists(packageRoot))
{
    Console.Error.WriteLine($"Package root was not found: {packageRoot}");
    return 2;
}

foreach (var executablePath in executablePaths)
{
    if (!File.Exists(executablePath))
    {
        Console.Error.WriteLine($"Executable archive entry was not found: {executablePath}");
        return 2;
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
using var archiveFile = File.Create(archivePath);
using var gzip = new GZipStream(archiveFile, CompressionLevel.SmallestSize);
using var archive = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);

foreach (var directory in Directory.EnumerateDirectories(packageRoot, "*", SearchOption.AllDirectories).Order())
{
    var entry = new PaxTarEntry(TarEntryType.Directory, EntryName(packageRoot, directory))
    {
        Mode = (UnixFileMode)493
    };
    archive.WriteEntry(entry);
}

foreach (var file in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories).Order())
{
    using var input = File.OpenRead(file);
    var entry = new PaxTarEntry(TarEntryType.RegularFile, EntryName(packageRoot, file))
    {
        DataStream = input,
        Mode = executablePaths.Contains(Path.GetFullPath(file)) ? (UnixFileMode)493 : (UnixFileMode)420
    };
    archive.WriteEntry(entry);
}

return 0;

static string EntryName(string packageRoot, string path) =>
    Path.GetRelativePath(packageRoot, path).Replace(Path.DirectorySeparatorChar, '/');
