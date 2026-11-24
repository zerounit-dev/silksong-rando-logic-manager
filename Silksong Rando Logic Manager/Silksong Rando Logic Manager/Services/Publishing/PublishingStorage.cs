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
    internal string MissingCredentials => "Missing or blank server keys: " + string.Join(", ",
        new[] { ("DocumentationPublishing:GitHubUsername", GitHubUsername), ("DocumentationPublishing:GitHubToken", GitHubToken) }
            .Where(x => string.IsNullOrWhiteSpace(x.Item2)).Select(x => x.Item1)) + ".";
    public static PublishingSettings Read(IConfiguration configuration)
    {
        var c = configuration.GetSection("DocumentationPublishing");
        return new(c.GetValue<bool>("Enabled"), c["RepositoryUrl"] ?? "", c["Branch"] ?? "",
            c["AuthorName"] ?? "", c["AuthorEmail"] ?? "", c["GitHubUsername"] ?? "", c["GitHubToken"] ?? "");
    }
    public void Validate()
    {
        if (!Enabled) throw new PublishingBlock("DocumentationPublishing:Enabled is false. If publishing is intended on this server, enable it in the server configuration. Publishing settings are captured at application registration; editing a file does not change this process's settings.");
        if (!Uri.TryCreate(RepositoryUrl, UriKind.Absolute, out var u) || u.Scheme != "https" ||
            u.Host != "github.com" || !u.IsDefaultPort || u.UserInfo.Length != 0 || u.Query.Length != 0 || u.Fragment.Length != 0 ||
            !Regex.IsMatch(u.AbsolutePath, @"^/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:\.git)?$"))
            throw new PublishingBlock("DocumentationPublishing:RepositoryUrl is missing or invalid. Set an absolute HTTPS github.com/owner/repository URL using the default port, no credentials, query or fragment, and only letters, digits, underscores, dots or hyphens in owner/repository. The rejected value is not displayed. Settings are captured at application registration.");
        if (!LibGit2Sharp.Reference.IsValidName("refs/heads/" + Branch))
            throw new PublishingBlock("DocumentationPublishing:Branch does not form a valid Git refs/heads/ name. Set the exact existing branch name, without the refs/heads/ prefix; use Git branch-name rules (no spaces, '..', trailing dot or invalid ref characters). Settings are captured at application registration.");
        if (string.IsNullOrWhiteSpace(AuthorName) || string.IsNullOrWhiteSpace(AuthorEmail))
            throw new PublishingBlock("Missing or blank server keys: " + string.Join(", ", new[] { ("DocumentationPublishing:AuthorName", AuthorName), ("DocumentationPublishing:AuthorEmail", AuthorEmail) }.Where(x => string.IsNullOrWhiteSpace(x.Item2)).Select(x => x.Item1)) + ". Set nonblank publication commit author name/email before preparing. Settings are captured at application registration; no values are displayed.");
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
        CheckPath(Root, "Publishing");
        Directory.CreateDirectory(Root);
        lease ??= new FileStream(Path.Combine(Root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        CheckTree(Root);
        Directory.CreateDirectory(Staging);
    }
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string FileHash(string path)
    {
        using var stream = OpenFileForHash(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    private static FileStream OpenFileForHash(string path)
    {
        CheckPath(path, Path.GetFileName(path));
        return File.OpenRead(path);
    }
    private static (string Hash, long Length) FileHashWithLength(string path, Span<byte> buffer)
    {
        using var stream = OpenFileForHash(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            hash.AppendData(buffer[..count]);
            length += count;
        }
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), length);
    }
    public static void CheckPath(string path, string logicalPath)
    {
        for (var p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new PublishingBlock($"Path {logicalPath} crosses a symbolic link/reparse point at {Path.GetRelativePath(Path.GetFullPath(path), p).Replace('\\', '/')} relative to that path ('.' means itself; '..' means its parent). Keep the workspace intact; have the server operator inspect that component and provide ordinary owned storage before publishing. Do not follow or delete the link to bypass the check.");
    }
    public static void CheckTree(string root, string? logicalPath = null)
    {
        logicalPath ??= Path.GetFileName(root);
        CheckPath(root, logicalPath);
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var child = logicalPath + "/" + Path.GetFileName(path);
            CheckPath(path, child);
            if (Directory.Exists(path)) CheckTree(path, child);
        }
    }
    public static string Resolve(string root, string relative)
    {
        ValidateRelativePath(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new PublishingBlock($"Relative publication path {relative} resolves outside its supplied workspace. Keep retained candidate/journal evidence; have the application maintainer correct this path's ownership before any write or preview.");
        CheckPath(path, Path.GetFileName(root) + "/" + relative);
        return path;
    }
    private static void ValidateRelativePath(string relative)
    {
        string? reason = string.IsNullOrEmpty(relative) ? "path is empty" : relative.Contains('\\') ? "path contains a backslash" :
            relative.StartsWith('/') ? "path is rooted" : relative.Contains(':') ? "path contains a colon" : null;
        if (reason is null)
            foreach (var (part, index) in relative.Split('/').Select((part, index) => (part, index)))
            {
                var problem = part is "" or "." or ".." ? "empty/dot/parent component" : part.Equals(".git", StringComparison.OrdinalIgnoreCase) ? "reserved .git component" :
                    part.EndsWith('.') ? "trailing dot" : part.EndsWith(' ') ? "trailing space" : part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "invalid filename character" : null;
                if (problem is not null) { reason = $"component {index + 1} has a {problem}"; break; }
            }
        if (reason is not null)
            throw new PublishingBlock($"A publication path is unsafe: {reason}. Keep Staging/current.json and candidate files; have the application maintainer identify and correct the generating/retained path entry before publication. The rejected value is not echoed because it may be an absolute private path.");
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
                ?? throw InvalidJournal("JSON root is null instead of a journal object");
        }
        catch (JsonException) { throw InvalidJournal("JSON cannot be deserialized as the required journal object (invalid JSON, field type or missing required field)"); }
        ValidateJournal(retained);
        return retained;
    }
    private static PublishingBlock InvalidJournal(string condition) => new($"Staging/current.json failed validation: {condition}. Keep Repository, Staging/current.json, Candidate and Rollback intact. The application maintainer must compare this field/invariant with the retained files and commit intent before recovery; do not edit the journal, discard or regenerate evidence to bypass the check.");
    private static bool Hex(string? value, int length) => value is not null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static void ValidateJournal(PublishingJournal retained)
    {
        // These are the existing writer's persisted invariants, not defaults for
        // repairing missing evidence. Partial preparation legitimately has no base
        // or candidate yet; replacement and committed phases never do.
        if (retained.Phase is not ("Preparing" or "Interrupted" or "Failed" or "Prepared" or "Replacing" or "Advancing" or "PendingPush" or "Published")) throw InvalidJournal("Phase is not a supported operation state");
        if (!Hex(retained.Configuration, 64)) throw InvalidJournal("Configuration must be a 64-character lowercase hexadecimal identity");
        if (string.IsNullOrWhiteSpace(retained.Renderer)) throw InvalidJournal("Renderer is missing or blank");
        if (retained.BaseCommit is null || (retained.BaseCommit.Length != 0 && !Hex(retained.BaseCommit, 40))) throw InvalidJournal("BaseCommit must be empty during incomplete preparation or a 40-character lowercase Git commit ID");
        if (retained.Files is null) throw InvalidJournal("Files is null");
        if (retained.Removed is null) throw InvalidJournal("Removed is null");
        if (retained.Preimages is null) throw InvalidJournal("Preimages is null");
        if (retained.Files.Any(x => string.IsNullOrEmpty(x.Key) || !Hex(x.Value, 64))) throw InvalidJournal("Files contains an empty path or a value other than a 64-character lowercase raw SHA-256");
        if (retained.Removed.Any(string.IsNullOrEmpty)) throw InvalidJournal("Removed contains an empty path");
        if (retained.Removed.Distinct(StringComparer.Ordinal).Count() != retained.Removed.Count) throw InvalidJournal("Removed contains duplicate paths");
        if (retained.Removed.Any(retained.Files.ContainsKey)) throw InvalidJournal("a path appears in both Removed and Files");
        if (retained.Preimages.Any(x => string.IsNullOrEmpty(x.Key) || (x.Value is not null && !Hex(x.Value, 64)))) throw InvalidJournal("Preimages contains an empty path or a hash other than null/64-character lowercase raw SHA-256");
        if (retained.IndexHash is not null && !Hex(retained.IndexHash, 64)) throw InvalidJournal("IndexHash must be null or a 64-character lowercase raw SHA-256");
        if (retained.IntendedTree is not null && !Hex(retained.IntendedTree, 40)) throw InvalidJournal("IntendedTree must be null or a 40-character lowercase Git tree ID");
        if (retained.Commit is not null && !Hex(retained.Commit, 40)) throw InvalidJournal("Commit must be null or a 40-character lowercase Git commit ID");
        if (retained.Phase is "Prepared" or "Replacing" or "Advancing" or "PendingPush" or "Published")
        {
            if (!Hex(retained.BaseCommit, 40)) throw InvalidJournal($"BaseCommit is required in phase {retained.Phase}");
            if (retained.Phase != "Advancing" && retained.Files.Count == 0) throw InvalidJournal($"Files must not be empty in phase {retained.Phase}");
        }
        if (retained.Phase is "Replacing" or "Advancing" or "PendingPush" or "Published")
        {
            if (!Hex(retained.Commit, 40)) throw InvalidJournal($"Commit is required in phase {retained.Phase}");
            if (!Hex(retained.IntendedTree, 40)) throw InvalidJournal($"IntendedTree is required in phase {retained.Phase}");
            if (!Hex(retained.IndexHash, 64)) throw InvalidJournal($"IndexHash is required in phase {retained.Phase}");
            if (retained.Phase != "Advancing" && retained.Preimages.Count == 0) throw InvalidJournal($"Preimages must not be empty in phase {retained.Phase}");
            if (retained.Phase != "Advancing" && string.IsNullOrWhiteSpace(retained.Message)) throw InvalidJournal($"Message must not be blank in phase {retained.Phase}");
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
            throw new PublishingBlock("A publication write destination is outside Publishing's owned workspace. No write is authorized to that destination. Keep Staging/current.json and generated files; have the application maintainer correct destination ownership before continuing.");
        CheckPath(path, Path.GetRelativePath(Root, path).Replace('\\', '/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Scratch belongs to this explicit storage instance, never a destination
        // sibling or part of Candidate. Collisions fail without opening old bytes.
        CheckPath(Staging, "Staging");
        Directory.CreateDirectory(Staging);
        temporary = Path.Combine(Staging, Guid.NewGuid().ToString("N"));
        CheckPath(temporary, "Staging/" + Path.GetFileName(temporary));
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
        Measured(PublishingMetric.Recheck, () => { CheckPath(temporary, "Staging/" + Path.GetFileName(temporary)); CheckPath(path, Path.GetRelativePath(Root, path).Replace('\\', '/')); return true; });
        Measured(PublishingMetric.Move, () => { File.Move(temporary, path, true); return true; });
        if (timing is not null) timing.Files = (timing.Files ?? 0) + 1;
        T Measured<T>(PublishingMetric metric, Func<T> work) => timing is null ? work() : timing.Measure(metric, work);
    }
    public void Validate(PublishingJournal journal) => Validate(journal, null);
    public IReadOnlyDictionary<string, long> ValidateForReview(PublishingJournal journal)
    {
        var lengths = new Dictionary<string, long>(journal.Files.Count, StringComparer.Ordinal);
        Validate(journal, lengths);
        return lengths;
    }
    private void Validate(PublishingJournal journal, Dictionary<string, long>? lengths)
    {
        CheckTree(Staging);
        var unowned = journal.Files.Keys.Concat(journal.Removed).Where(x => !PublishingGit.Owned(x)).Order(StringComparer.Ordinal).ToArray();
        if (unowned.Length != 0)
            throw new PublishingBlock("Candidate manifest includes paths outside generator ownership:\n" + string.Join("\n", unowned) + "\nKeep Staging/current.json, Candidate and Rollback; have the application maintainer compare these paths with the intended Git change set before continuing. Discard may be unsafe if replacement has begun.");
        var actual = Directory.EnumerateFiles(Candidate, "*", SearchOption.AllDirectories)
            .Select(x => Path.GetRelativePath(Candidate, x).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(journal.Files.Keys.Order(StringComparer.Ordinal)))
            throw new PublishingBlock($"Candidate file inventory differs from Staging/current.json in phase {journal.Phase}.\nMissing candidate paths:\n{string.Join("\n", journal.Files.Keys.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal))}\nUnexpected candidate paths:\n{string.Join("\n", actual.Except(journal.Files.Keys, StringComparer.Ordinal))}\nKeep Candidate, current.json and Rollback; compare these entries with the reviewed manifest before continuing. If replacement has begun, resolve rollback/commit evidence before any discard; otherwise discard the prepared changes and prepare/review again.");
        // Review sizes must describe the bytes hashed here, not a later path stat.
        Span<byte> buffer = lengths is null ? Span<byte>.Empty : stackalloc byte[4096];
        foreach (var (path, expected) in journal.Files)
        {
            var file = Resolve(Candidate, path);
            var (observed, length) = lengths is null ? (FileHash(file), 0L) : FileHashWithLength(file, buffer);
            if (observed != expected) throw new PublishingBlock($"Staging/Candidate/{path} differs from its manifest in phase {journal.Phase}.\nExpected raw SHA-256: {expected}\nObserved raw SHA-256: {observed}\nKeep this file, Staging/current.json and Rollback. If replacement has begun, compare the file with the intended Git blob and resolve rollback/commit evidence before discard; otherwise discard the prepared changes and prepare/review again. Do not edit the manifest to accept changed bytes.");
            lengths?.Add(path, length);
        }
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
