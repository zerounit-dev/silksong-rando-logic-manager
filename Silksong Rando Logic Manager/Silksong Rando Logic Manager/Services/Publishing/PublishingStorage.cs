using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Silksong_Rando_Logic_Manager.Services.Publishing;

internal sealed class PublishingBlock(string message) : Exception(message);

internal sealed record PublishingSettings(bool Enabled, string RepositoryUrl, string Branch,
    string AuthorName, string AuthorEmail, string GitHubUsername, string GitHubToken)
{
    public bool CredentialsReady => !string.IsNullOrWhiteSpace(GitHubUsername) && !string.IsNullOrWhiteSpace(GitHubToken);
    public static PublishingSettings Read(IConfiguration configuration)
    {
        var c = configuration.GetSection("DocumentationPublishing");
        return new(c.GetValue<bool>("Enabled"), c["RepositoryUrl"] ?? "", c["Branch"] ?? "",
            c["AuthorName"] ?? "", c["AuthorEmail"] ?? "", c["GitHubUsername"] ?? "", c["GitHubToken"] ?? "");
    }
    public void Validate()
    {
        if (!Enabled) throw new PublishingBlock("Documentation publishing is disabled. Configure DocumentationPublishing on the server.");
        if (!Uri.TryCreate(RepositoryUrl, UriKind.Absolute, out var u) || u.Scheme != "https" ||
            u.Host != "github.com" || !u.IsDefaultPort || u.UserInfo.Length != 0 || u.Query.Length != 0 || u.Fragment.Length != 0 ||
            !Regex.IsMatch(u.AbsolutePath, @"^/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:\.git)?$"))
            throw new PublishingBlock("Configure an HTTPS github.com owner/repository URL without credentials, query or fragment.");
        if (!LibGit2Sharp.Reference.IsValidName("refs/heads/" + Branch))
            throw new PublishingBlock("Configure a valid existing publication branch.");
        if (string.IsNullOrWhiteSpace(AuthorName) || string.IsNullOrWhiteSpace(AuthorEmail))
            throw new PublishingBlock("Configure the publication commit author name and email.");
    }
    public string Identity => PublishingStorage.Hash(System.Text.Encoding.UTF8.GetBytes(RepositoryUrl + "\n" + Branch));
}

// The journal contains identities/hashes, never configuration credentials.
internal sealed class PublishingJournal
{
    [JsonRequired] public string Phase { get; set; } = "Preparing";
    [JsonRequired] public string Configuration { get; set; } = "";
    [JsonRequired] public string BaseCommit { get; set; } = "";
    [JsonRequired] public string Renderer { get; set; } = PublishingRenderer.Version;
    [JsonRequired] public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
    [JsonRequired] public List<string> Removed { get; set; } = [];
    [JsonRequired] public Dictionary<string, string?> Preimages { get; set; } = new(StringComparer.Ordinal);
    public string? IndexHash { get; set; }
    public string? IntendedTree { get; set; }
    public string? Commit { get; set; }
    public string? Message { get; set; }
}

internal sealed class PublishingStorage : IDisposable
{
    public string Root { get; }
    public string Repository => Path.Combine(Root, "Repository");
    public string Staging => Path.Combine(Root, "Staging");
    public string Candidate => Path.Combine(Staging, "Candidate");
    public string Rollback => Path.Combine(Staging, "Rollback");
    private FileStream? lease;
    internal PublishingTimingScope? Timing { get; set; }
    public PublishingStorage(string dataRoot) => Root = Path.GetFullPath(Path.Combine(dataRoot, "Publishing"));
    public void Admit()
    {
        CheckPath(Root);
        Directory.CreateDirectory(Root);
        lease ??= new FileStream(Path.Combine(Root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        CheckTree(Root);
        Directory.CreateDirectory(Staging);
    }
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string FileHash(string path)
    {
        CheckPath(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    public static void CheckPath(string path)
    {
        for (var p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new PublishingBlock("Publishing paths must not contain symbolic links or reparse points. Preserve the workspace and ask the operator to repair it.");
    }
    public static void CheckTree(string root)
    {
        CheckPath(root);
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            CheckPath(path);
            if (Directory.Exists(path)) CheckTree(path);
        }
    }
    public static string Resolve(string root, string relative)
    {
        ValidateRelativePath(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new PublishingBlock("Publication path escapes its workspace.");
        CheckPath(path);
        return path;
    }
    private static void ValidateRelativePath(string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.StartsWith('/') || relative.Contains(':') ||
            relative.Split('/').Any(x => x is "" or "." or ".." || x.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                x.EndsWith('.') || x.EndsWith(' ') || x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new PublishingBlock("Unsafe publication path. Preserve the workspace for operator inspection.");
    }
    public void Save(PublishingJournal journal)
    {
        if (Timing is { } timing) timing.Measure(PublishingMetric.Save,
            () => Atomic(Path.Combine(Staging, "current.json"), JsonSerializer.SerializeToUtf8Bytes(journal)));
        else Atomic(Path.Combine(Staging, "current.json"), JsonSerializer.SerializeToUtf8Bytes(journal));
    }
    public PublishingJournal? Load()
    {
        var path = Path.Combine(Staging, "current.json");
        if (!File.Exists(path)) return null;
        PublishingJournal retained;
        try
        {
            retained = JsonSerializer.Deserialize<PublishingJournal>(File.ReadAllBytes(path))
                ?? throw InvalidJournal();
        }
        catch (JsonException) { throw InvalidJournal(); }
        ValidateJournal(retained);
        return retained;
    }
    private static PublishingBlock InvalidJournal() => new("The publishing journal has incomplete or corrupt recovery evidence. Preserve the entire workspace and have the operator reconcile it; publishing and discard are blocked.");
    private static bool Hex(string? value, int length) => value is not null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static void ValidateJournal(PublishingJournal retained)
    {
        // These are the existing writer's persisted invariants, not defaults for
        // repairing missing evidence. Partial preparation legitimately has no base
        // or candidate yet; replacement and committed phases never do.
        if (retained.Phase is not ("Preparing" or "Interrupted" or "Failed" or "Prepared" or "Replacing" or "Advancing" or "PendingPush" or "Published") ||
            !Hex(retained.Configuration, 64) || string.IsNullOrWhiteSpace(retained.Renderer) || retained.BaseCommit is null ||
            (retained.BaseCommit.Length != 0 && !Hex(retained.BaseCommit, 40)) ||
            retained.Files is null || retained.Removed is null || retained.Preimages is null ||
            retained.Files.Any(x => string.IsNullOrEmpty(x.Key) || !Hex(x.Value, 64)) ||
            retained.Removed.Any(string.IsNullOrEmpty) || retained.Removed.Distinct(StringComparer.Ordinal).Count() != retained.Removed.Count ||
            retained.Removed.Any(retained.Files.ContainsKey) ||
            retained.Preimages.Any(x => string.IsNullOrEmpty(x.Key) || (x.Value is not null && !Hex(x.Value, 64))) ||
            (retained.IndexHash is not null && !Hex(retained.IndexHash, 64)) ||
            (retained.IntendedTree is not null && !Hex(retained.IntendedTree, 40)) ||
            (retained.Commit is not null && !Hex(retained.Commit, 40)))
            throw InvalidJournal();
        if (retained.Phase is "Prepared" or "Replacing" or "Advancing" or "PendingPush" or "Published")
        {
            if (!Hex(retained.BaseCommit, 40)) throw InvalidJournal();
            if (retained.Phase != "Advancing" && retained.Files.Count == 0) throw InvalidJournal();
        }
        if (retained.Phase is "Replacing" or "Advancing" or "PendingPush" or "Published")
        {
            if (!Hex(retained.Commit, 40) || !Hex(retained.IntendedTree, 40) || !Hex(retained.IndexHash, 64) ||
                (retained.Phase != "Advancing" && (retained.Preimages.Count == 0 || string.IsNullOrWhiteSpace(retained.Message))))
                throw InvalidJournal();
        }
        foreach (var relative in retained.Files.Keys.Concat(retained.Removed).Concat(retained.Preimages.Keys))
            ValidateRelativePath(relative);
    }
    public void Atomic(string path, byte[] bytes)
    {
        var timing = Timing;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var success = false;
        string temporary;
        try
        {
        path = Path.GetFullPath(path);
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new PublishingBlock("Publication writes must stay inside the owned workspace.");
        CheckPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Scratch belongs to this explicit storage instance, never a destination
        // sibling or part of Candidate. Collisions fail without opening old bytes.
        CheckPath(Staging);
        Directory.CreateDirectory(Staging);
        temporary = Path.Combine(Staging, Guid.NewGuid().ToString("N"));
        CheckPath(temporary);
        success = true;
        }
        finally { timing?.Add(PublishingMetric.AtomicPrepare, started, success); }
        var output = Measured(PublishingMetric.AtomicPrepare, () => new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        try
        {
            Measured(PublishingMetric.Write, () => { output.Write(bytes); return true; });
            if (timing is not null) timing.Bytes = (timing.Bytes ?? 0) + bytes.LongLength;
            Measured(PublishingMetric.Flush, () => { output.Flush(true); return true; });
        }
        finally { Measured(PublishingMetric.Close, () => { output.Dispose(); return true; }); }
        // Recheck both paths before the sole replacement. On failure/interruption,
        // retain private scratch; existing phase guards own staging cleanup. Never
        // search for/delete siblings or assume a filename proves ownership.
        Measured(PublishingMetric.Recheck, () => { CheckPath(temporary); CheckPath(path); return true; });
        Measured(PublishingMetric.Move, () => { File.Move(temporary, path, true); return true; });
        if (timing is not null) timing.Files = (timing.Files ?? 0) + 1;
        T Measured<T>(PublishingMetric metric, Func<T> work) => timing is null ? work() : timing.Measure(metric, work);
    }
    public void Validate(PublishingJournal journal)
    {
        CheckTree(Staging);
        if (journal.Files.Keys.Concat(journal.Removed).Any(x => !PublishingGit.Owned(x)))
            throw new PublishingBlock("Candidate ownership evidence is invalid. Preserve the workspace for operator inspection.");
        var actual = Directory.EnumerateFiles(Candidate, "*", SearchOption.AllDirectories)
            .Select(x => Path.GetRelativePath(Candidate, x).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(journal.Files.Keys.Order(StringComparer.Ordinal)) ||
            journal.Files.Any(x => FileHash(Resolve(Candidate, x.Key)) != x.Value))
            throw new PublishingBlock("The prepared candidate changed. Do not commit it; discard and prepare again.");
    }
    public void Clear()
    {
        CheckTree(Staging);
        if (Directory.Exists(Staging))
        {
            // Native Git objects can be ReadOnly on Windows. Normalize only that
            // file bit in disposable staging; do not change Unix permissions.
            if (OperatingSystem.IsWindows())
                foreach (var file in Directory.EnumerateFiles(Staging, "*", SearchOption.AllDirectories))
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            Directory.Delete(Staging, true);
        }
        Directory.CreateDirectory(Staging);
    }
    public void Dispose() => lease?.Dispose();
}
