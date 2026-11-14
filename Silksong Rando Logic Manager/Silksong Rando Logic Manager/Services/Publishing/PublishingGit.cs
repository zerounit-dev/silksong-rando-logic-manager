using LibGit2Sharp;
using System.Text;

namespace Silksong_Rando_Logic_Manager.Services.Publishing;

internal sealed class PublishingGit(PublishingStorage storage, PublishingSettings settings)
{
    private Credentials Credentials(string _, string __, SupportedCredentialTypes ___) =>
        new UsernamePasswordCredentials { Username = settings.GitHubUsername, Password = settings.GitHubToken };
    private FetchOptions FetchOptions() => new() { CredentialsProvider = Credentials, TagFetchMode = TagFetchMode.None };

    public Repository Open()
    {
        PublishingStorage.CheckTree(storage.Repository);
        if (!Directory.Exists(Path.Combine(storage.Repository, ".git")))
            throw new PublishingBlock("The publishing checkout requires its own real .git directory. Preserve the workspace for operator recovery.");
        var gitDirectory = Path.Combine(storage.Repository, ".git");
        if (new[] { "commondir", "gitdir", "objects/info/alternates", "index.lock", "HEAD.lock" }
            .Any(x => File.Exists(Path.Combine(gitDirectory, x.Replace('/', Path.DirectorySeparatorChar)))) ||
            (Directory.Exists(Path.Combine(gitDirectory, "hooks")) && Directory.EnumerateFiles(Path.Combine(gitDirectory, "hooks")).Any(x => !x.EndsWith(".sample", StringComparison.Ordinal))))
            throw new PublishingBlock("Publishing checkout has external Git storage, active hooks or unresolved Git locks. Preserve it for operator recovery.");
        var repo = new Repository(storage.Repository);
        try
        {
            CheckConfiguration(repo);
            if (repo.Info.IsBare || repo.Info.IsHeadDetached || repo.Info.CurrentOperation != CurrentOperation.None || repo.Head.FriendlyName != settings.Branch ||
                Path.GetFullPath(repo.Info.Path).TrimEnd(Path.DirectorySeparatorChar) != Path.Combine(storage.Repository, ".git") ||
                Path.GetFullPath(repo.Info.WorkingDirectory).TrimEnd(Path.DirectorySeparatorChar) != storage.Repository ||
                repo.Network.Remotes["origin"]?.Url != settings.RepositoryUrl || repo.Submodules.Any() ||
                (repo.Network.Remotes["origin"]?.PushUrl is { Length: > 0 } pushUrl && pushUrl != settings.RepositoryUrl))
                throw new PublishingBlock("The publishing checkout does not match its configured repository/branch or contains unsupported Git extensions. Operator recovery is required.");
            ConfigureOwnedPaths(repo);
            ValidateTree(repo.Head.Tip.Tree);
            return repo;
        }
        catch { repo.Dispose(); throw; }
    }
    private void ValidateTree(Tree tree, string prefix = "", HashSet<string>? names = null)
    {
        names ??= new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in tree)
        {
            var path = prefix + entry.Name;
            PublishingStorage.Resolve(storage.Repository, path);
            if (!names.Add(path)) throw new PublishingBlock("Publication tree paths collide by case. Correct them outside the application before preparing.");
            if (entry.Mode is Mode.SymbolicLink or Mode.GitLink)
                throw new PublishingBlock("Publication repository trees cannot contain symbolic links or submodules.");
            if (entry.Target is Tree child) ValidateTree(child, path + "/", names);
            if (entry.Name == ".gitattributes" && entry.Target is Blob attributes &&
                System.Text.RegularExpressions.Regex.IsMatch(Encoding.UTF8.GetString(Bytes(attributes)), @"(?:^|\s)(?:filter|working-tree-encoding)(?:=|\s|$)", System.Text.RegularExpressions.RegexOptions.Multiline))
                throw new PublishingBlock("Publication checkout uses unsupported Git content filters/encodings. Remove them deliberately before preparing.");
        }
    }
    public void EnsureClean(Repository repo)
    {
        PublishingStorage.CheckTree(storage.Repository);
        if (repo.RetrieveStatus(new StatusOptions { IncludeIgnored = true, IncludeUntracked = true, RecurseUntrackedDirs = true }).Any())
            throw new PublishingBlock("The publication checkout is not clean, including untracked/ignored files. Preserve it and have the operator reconcile it; nothing was reset.");
    }
    public Commit Fetch(Repository repo)
    {
        // Also runs for the unborn first clone, before any transport invocation.
        CheckConfiguration(repo);
        Commands.Fetch(repo, "origin", [$"+refs/heads/{settings.Branch}:refs/remotes/origin/{settings.Branch}"], FetchOptions(), null);
        return repo.Branches[$"origin/{settings.Branch}"]?.Tip
            ?? throw new PublishingBlock("The configured remote branch does not exist. Create it deliberately outside the application, then prepare again.");
    }
    private void CheckConfiguration(Repository repo)
    {
        if (repo.Network.Remotes["origin"]?.Url != settings.RepositoryUrl ||
            (repo.Network.Remotes["origin"]?.PushUrl is { Length: > 0 } pushUrl && pushUrl != settings.RepositoryUrl))
            throw new PublishingBlock("Publishing Git remote does not match the configured repository. Operator correction is required before network access.");
        foreach (var entry in repo.Config)
        {
            var key = entry.Key;
            // Pinned libgit2 uses registered filter callbacks, not external Git
            // filter commands. This application registers none. Inactive host LFS
            // definitions are allowed; repository/application/unknown levels are not.
            var category = key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase) &&
                entry.Level is not (ConfigurationLevel.System or ConfigurationLevel.Global or ConfigurationLevel.Xdg or ConfigurationLevel.ProgramData)
                ? "repository or application content-filter definitions"
                : key.Equals("core.worktree", StringComparison.OrdinalIgnoreCase) ? "working-directory overrides"
                : key.Equals("core.hookspath", StringComparison.OrdinalIgnoreCase) ? "hook-path overrides"
                : key.Equals("core.attributesfile", StringComparison.OrdinalIgnoreCase) ? "external attribute files"
                : key.Equals("include.path", StringComparison.OrdinalIgnoreCase) || key.StartsWith("includeIf.", StringComparison.OrdinalIgnoreCase) ? "external configuration includes"
                : key.StartsWith("url.", StringComparison.OrdinalIgnoreCase) ? "URL rewriting"
                : null;
            if (category is not null)
                throw new PublishingBlock($"Publishing Git configuration contains unsupported {category}. Operator correction is required before network access.");
        }
    }
    private static void ConfigureOwnedPaths(Repository repo)
    {
        // Set before native checkout/status can cache the default MAX_PATH limit.
        // Only publisher-owned local config changes, never host Git settings.
        if (OperatingSystem.IsWindows())
            repo.Config.Set("core.longpaths", true, ConfigurationLevel.Local);
    }
    public Repository PrepareRepository(PublishingJournal journal)
    {
        if (!Directory.Exists(storage.Repository))
        {
            // No checkout before validating the remote tree. Native fetch never runs hooks/helpers.
            // Build in disposable, journal-owned staging. A failed/interrupted first
            // checkout cannot leave a half-adopted Repository directory.
            var clone = Path.Combine(storage.Staging, "Clone");
            Repository.Init(clone);
            using (var initial = new Repository(clone))
            {
                initial.Network.Remotes.Add("origin", settings.RepositoryUrl);
                CheckConfiguration(initial);
                ConfigureOwnedPaths(initial);
                var tip = Fetch(initial);
                ValidateTree(tip.Tree);
                initial.Branches.Add(settings.Branch, tip);
                Commands.Checkout(initial, settings.Branch);
            }
            PublishingStorage.CheckTree(clone);
            Directory.Move(clone, storage.Repository);
        }
        var repo = Open();
        try
        {
            EnsureClean(repo);
            var remote = Fetch(repo);
            if (remote.Id != repo.Head.Tip.Id)
            {
                if (repo.ObjectDatabase.FindMergeBase(repo.Head.Tip, remote)?.Id != repo.Head.Tip.Id)
                    throw new PublishingBlock("Publication branch diverged or contains unknown local commits. Stop publishing and reconcile it deliberately; no merge/reset was attempted.");
                ValidateTree(remote.Tree);
                Advance(repo, remote, journal);
                EnsureClean(repo);
            }
            return repo;
        }
        catch { repo.Dispose(); throw; }
    }

    private void Advance(Repository repo, Commit remote, PublishingJournal journal)
    {
        var before = Blobs(repo.Head.Tip.Tree).ToDictionary(x => x.Path, x => x.Blob, StringComparer.Ordinal);
        var after = Blobs(remote.Tree).ToDictionary(x => x.Path, x => x.Blob, StringComparer.Ordinal);
        journal.BaseCommit = repo.Head.Tip.Sha; journal.Commit = remote.Sha; journal.IntendedTree = remote.Tree.Sha;
        journal.Files = after.Where(x => !before.TryGetValue(x.Key, out var old) || old.Id != x.Value.Id || repo.Head.Tip.Tree[x.Key].Mode != remote.Tree[x.Key].Mode)
            .ToDictionary(x => x.Key, x => PublishingStorage.Hash(Bytes(x.Value)), StringComparer.Ordinal);
        journal.Removed = before.Keys.Except(after.Keys, StringComparer.Ordinal).ToList();
        SavePreimages(repo, journal);
        VerifyCapturedPreimages(repo, journal, true);
        journal.Phase = "Advancing"; storage.Save(journal);
        try
        {
            foreach (var path in journal.Preimages.Keys)
            {
                var target = PublishingStorage.Resolve(storage.Repository, path);
                if (!MatchesRecoveryFile(ReadRecoveryFile(repo, path, target), journal.Preimages[path], repo.Lookup<Commit>(journal.BaseCommit).Tree[path]))
                    throw new PublishingBlock("Unexpected checkout changes interrupted fast-forward. Preserve the workspace for reconciliation.");
                if (after.TryGetValue(path, out var blob))
                {
                    storage.Atomic(target, Bytes(blob)); SetMode(target, remote.Tree[path].Mode);
                    repo.Index.Add(blob, path, remote.Tree[path].Mode);
                }
                else { File.Delete(target); repo.Index.Remove(path); }
            }
            repo.Index.Write();
            if (repo.Index.WriteToTree().Id != remote.Tree.Id) throw new PublishingBlock("Fast-forward index did not match the fetched tree; preserve recovery evidence.");
            foreach (var path in journal.Preimages.Keys)
                if (!MatchesRecoveryFile(ReadRecoveryFile(repo, path, PublishingStorage.Resolve(storage.Repository, path)), journal.Files.GetValueOrDefault(path), remote.Tree[path]))
                    throw new PublishingBlock("Worktree bytes or mode changed before fast-forward. Preserve the workspace for reconciliation.");
            if (repo.Head.Tip.Sha != journal.BaseCommit ||
                repo.RetrieveStatus(new StatusOptions { IncludeIgnored = true, IncludeUntracked = true, RecurseUntrackedDirs = true }).Any(x => !journal.Preimages.ContainsKey(x.FilePath)))
                throw new PublishingBlock("Unexpected checkout changes prevent fast-forward. Preserve the workspace for reconciliation.");
            repo.Refs.UpdateTarget(repo.Refs["refs/heads/" + settings.Branch], remote.Id);
            journal.Phase = "Preparing"; storage.Save(journal);
            ClearRollback(journal);
        }
        catch (Exception original)
        {
            try { Recover(journal); }
            catch (Exception recovery) { throw new AggregateException("Publication advance failed and recovery also failed.", original, recovery); }
            throw;
        }
    }
    private static void SetMode(string path, Mode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode == Mode.ExecutableFile
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }
    private void SavePreimages(Repository repo, PublishingJournal journal)
    {
        EnsureClean(repo);
        var baseline = repo.Lookup<Commit>(journal.BaseCommit);
        var intended = repo.Lookup<Tree>(journal.IntendedTree);
        var index = Path.Combine(repo.Info.Path, "index");
        var indexBytes = File.ReadAllBytes(index);
        var indexHash = PublishingStorage.Hash(indexBytes);
        // A new handle reads the on-disk index, not this operation's cached index.
        using var captured = Open();
        if (baseline is null || intended is null || captured.Head.Tip.Id != baseline.Id || captured.Index.WriteToTree().Id != baseline.Tree.Id ||
            PublishingStorage.FileHash(index) != indexHash)
            throw new PublishingBlock("Publication HEAD or index changed before retaining rollback evidence. Nothing was replaced; preserve the workspace for reconciliation.");
        Directory.CreateDirectory(storage.Rollback);
        storage.Atomic(Path.Combine(storage.Rollback, "index"), indexBytes);
        journal.IndexHash = indexHash;
        journal.Preimages.Clear();
        foreach (var path in journal.Files.Keys.Concat(journal.Removed).Distinct(StringComparer.Ordinal))
        {
            var target = PublishingStorage.Resolve(storage.Repository, path);
            var entry = baseline.Tree[path];
            var before = entry?.Target as Blob;
            if (before?.Id == intended[path]?.Target.Id && entry?.Mode == intended[path]?.Mode) continue;
            var current = ReadRecoveryFile(repo, path, target);
            if (!MatchesRecoveryFile(current, current.Hash, entry))
                throw new PublishingBlock("Publication checkout changed while retaining rollback evidence. Nothing was replaced; reconcile it before trying again.");
            if (before is null) { journal.Preimages[path] = null; continue; }
            // Git identity and physical rollback identity are different: native
            // checkout can expand line endings/ident. Prove the captured bytes
            // clean to the baseline, but hash and retain those exact raw bytes.
            var bytes = File.ReadAllBytes(target);
            var hash = PublishingStorage.Hash(bytes);
            using var input = new MemoryStream(bytes, false);
            if (hash != current.Hash || repo.ObjectDatabase.CreateBlob(input, path).Id != before.Id ||
                !MatchesRecoveryFile(ReadRecoveryFile(repo, path, target), hash, entry))
                throw new PublishingBlock("Publication checkout changed while retaining rollback evidence. Nothing was replaced; reconcile it before trying again.");
            storage.Atomic(PublishingStorage.Resolve(Path.Combine(storage.Rollback, "files"), path), bytes);
            journal.Preimages[path] = hash;
        }
    }
    private void VerifyCapturedPreimages(Repository repo, PublishingJournal journal, bool advancing)
    {
        // Refreshes above own only these keys, not stale files from a failed
        // pre-intent capture. No replacement intent may use incomplete evidence.
        VerifyRecoveryEvidence(repo, journal, advancing);
        using var current = Open();
        EnsureClean(current);
        var baseline = repo.Lookup<Commit>(journal.BaseCommit);
        if (current.Head.Tip.Id != baseline.Id || current.Index.WriteToTree().Id != baseline.Tree.Id ||
            PublishingStorage.FileHash(Path.Combine(repo.Info.Path, "index")) != journal.IndexHash)
            throw new PublishingBlock("Publication HEAD or index changed while retaining rollback evidence. Nothing was replaced; preserve the workspace for reconciliation.");
        foreach (var (path, hash) in journal.Preimages)
            if (!MatchesRecoveryFile(ReadRecoveryFile(current, path, PublishingStorage.Resolve(storage.Repository, path)), hash, baseline.Tree[path]))
                throw new PublishingBlock("Publication checkout changed while retaining rollback evidence. Nothing was replaced; reconcile it before trying again.");
    }
    private void ClearRollback(PublishingJournal journal)
    {
        // Call only after a durably resolved outcome. Retain no run history.
        PublishingStorage.CheckTree(storage.Rollback);
        if (Directory.Exists(storage.Rollback)) Directory.Delete(storage.Rollback, true);
        journal.Preimages.Clear(); journal.IndexHash = null; journal.IntendedTree = null; journal.Commit = null;
        journal.Files.Clear(); journal.Removed.Clear(); storage.Save(journal);
    }

    // Inventory matches the generator's direct-zone Markdown and two dedicated
    // asset trees. LICENSE, workflows, other root files and nested prose stay intact.
    public static bool Owned(string path) => path is "README.md" or "MASTER.md" or "silksong-rando-logic.db" or
        "silksong-room-catalogue.json" or "silksong-requirement-catalogue.json" or "room_graph_data.json" or "room_geometry_data.json" ||
        path.StartsWith("00-annotations/", StringComparison.Ordinal) || path.StartsWith("01-geometry/", StringComparison.Ordinal) ||
        (path.Count(x => x == '/') == 1 && path.EndsWith(".md", StringComparison.Ordinal) && !path.StartsWith('.'));
    public static IEnumerable<(string Path, Blob Blob)> Blobs(Tree tree, string prefix = "")
    {
        foreach (var entry in tree)
            if (entry.Target is Tree child)
                foreach (var item in Blobs(child, prefix + entry.Name + "/")) yield return item;
            else if (entry.Target is Blob blob) yield return (prefix + entry.Name, blob);
    }
    public static byte[] Bytes(Blob blob)
    {
        using var input = blob.GetContentStream();
        using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
    }
    public void Inventory(Repository repo, PublishingJournal journal)
    {
        var existing = Blobs(repo.Head.Tip.Tree).ToDictionary(x => x.Path, x => x.Blob, StringComparer.Ordinal);
        foreach (var path in journal.Files.Keys)
        {
            if (!Owned(path)) throw new PublishingBlock("Generator produced a path outside its publication ownership.");
            if (existing.Keys.Any(x => x.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(x + "/", StringComparison.OrdinalIgnoreCase) ||
                (x.Equals(path, StringComparison.OrdinalIgnoreCase) && x != path)))
                throw new PublishingBlock("A candidate path collides with existing repository content. Operator correction is required.");
        }
        journal.Removed = existing.Keys.Where(Owned).Except(journal.Files.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }
    public void Commit(PublishingJournal journal, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new PublishingBlock("Enter a nonblank commit message.");
        if (!settings.CredentialsReady) throw new PublishingBlock("Configure the GitHub username and repository-scoped Contents read/write token on the server before committing.");
        using var repo = Open();
        EnsureClean(repo);
        storage.Validate(journal);
        if (journal.Renderer != PublishingRenderer.Version)
            throw new PublishingBlock("The candidate uses a different renderer version. Discard/reprepare and review the renderer change before committing.");
        if (journal.Configuration != settings.Identity || repo.Head.Tip.Sha != journal.BaseCommit || Fetch(repo).Sha != journal.BaseCommit)
            throw new PublishingBlock("Repository configuration or branch HEAD changed. Prepare and review a fresh candidate.");
        var definition = TreeDefinition.From(repo.Head.Tip.Tree);
        foreach (var path in journal.Removed) definition.Remove(path);
        foreach (var path in journal.Files.Keys)
        {
            using var input = File.OpenRead(PublishingStorage.Resolve(storage.Candidate, path));
            definition.Add(path, repo.ObjectDatabase.CreateBlob(input), repo.Head.Tip.Tree[path]?.Mode ?? Mode.NonExecutableFile);
        }
        var tree = repo.ObjectDatabase.CreateTree(definition);
        if (tree.Id == repo.Head.Tip.Tree.Id) throw new PublishingBlock("No changes; no commit was created.");
        var signature = new Signature(settings.AuthorName, settings.AuthorEmail, DateTimeOffset.UtcNow);
        // Object creation does not move a ref. Persist its exact identity BEFORE
        // replacing files, so ref-update/journal interruption never duplicates it.
        var commit = repo.ObjectDatabase.CreateCommit(signature, signature, message, tree, [repo.Head.Tip], false);
        journal.Message = message; journal.IntendedTree = tree.Sha; journal.Commit = commit.Sha;
        SavePreimages(repo, journal);
        VerifyCapturedPreimages(repo, journal, false);
        journal.Phase = "Replacing"; storage.Save(journal);
        try
        {
            foreach (var path in journal.Preimages.Keys)
            {
                var target = PublishingStorage.Resolve(storage.Repository, path);
                if (!MatchesRecoveryFile(ReadRecoveryFile(repo, path, target), journal.Preimages[path], repo.Lookup<Commit>(journal.BaseCommit).Tree[path]))
                    throw new PublishingBlock("Unexpected checkout edits interrupted replacement. Preserve the workspace for safe reconciliation.");
                if (journal.Files.ContainsKey(path)) { storage.Atomic(target, File.ReadAllBytes(PublishingStorage.Resolve(storage.Candidate, path))); SetMode(target, tree[path].Mode); }
                else File.Delete(target);
                // Direct blob-index insertion avoids attributes, filters and ignored-path rules.
                if (journal.Files.ContainsKey(path)) repo.Index.Add((Blob)tree[path].Target, path, tree[path].Mode);
                else repo.Index.Remove(path);
            }
            repo.Index.Write();
            if (repo.Index.WriteToTree().Id != tree.Id) throw new PublishingBlock("Staged bytes differ from the reviewed candidate. Recovery is required.");
            storage.Validate(journal);
            foreach (var path in journal.Preimages.Keys)
                if (!MatchesRecoveryFile(ReadRecoveryFile(repo, path, PublishingStorage.Resolve(storage.Repository, path)), journal.Files.GetValueOrDefault(path), tree[path]))
                    throw new PublishingBlock("Worktree bytes changed before commit. Preserve the workspace for reconciliation.");
            if (repo.RetrieveStatus(new StatusOptions { IncludeIgnored = true, IncludeUntracked = true, RecurseUntrackedDirs = true }).Any(x => !journal.Preimages.ContainsKey(x.FilePath)))
                throw new PublishingBlock("Unexpected checkout paths appeared before commit. Preserve the workspace for reconciliation.");
            if (repo.Head.Tip.Sha != journal.BaseCommit || Fetch(repo).Sha != journal.BaseCommit)
                throw new PublishingBlock("Remote/base HEAD moved before commit. Restore the verified precommit state, then prepare/review a fresh candidate.");
            repo.Refs.UpdateTarget(repo.Refs["refs/heads/" + settings.Branch], commit.Id);
            journal.Phase = "PendingPush"; storage.Save(journal);
        }
        catch (Exception original)
        {
            try { Recover(journal); }
            catch (Exception recovery) { throw new AggregateException("Publication commit failed and recovery also failed.", original, recovery); }
            throw;
        }
    }
    public void Recover(PublishingJournal journal)
    {
        if (journal.Phase is not ("Replacing" or "Advancing")) return;
        var advancing = journal.Phase == "Advancing";
        using var repo = Open();
        VerifyRecoveryEvidence(repo, journal, advancing);
        if (repo.Head.Tip.Sha == journal.Commit && repo.Head.Tip.Tree.Sha == journal.IntendedTree)
        {
            EnsureClean(repo);
            journal.Phase = advancing ? "Preparing" : "PendingPush"; storage.Save(journal);
            if (advancing) ClearRollback(journal);
            return;
        }
        if (repo.Head.Tip.Sha != journal.BaseCommit || journal.IndexHash is null || (!advancing && journal.Preimages.Count == 0))
            throw new PublishingBlock("Interrupted publication has uncertain commit/rollback evidence. Preserve the workspace; operator recovery is required. Discard is blocked.");
        var indexBackup = Path.Combine(storage.Rollback, "index");
        var indexBytes = File.ReadAllBytes(indexBackup);
        if (PublishingStorage.Hash(indexBytes) != journal.IndexHash)
            throw new PublishingBlock("Rollback index evidence changed. Preserve the workspace for operator recovery.");
        // A partially staged index may contain ONLY old or intended blobs/modes.
        var baseEntries = repo.Lookup<Commit>(journal.BaseCommit).Tree;
        var intendedEntries = repo.Lookup<Tree>(journal.IntendedTree);
        foreach (var item in repo.RetrieveStatus(new StatusOptions { IncludeIgnored = true, IncludeUntracked = true, RecurseUntrackedDirs = true }))
            if (!journal.Preimages.ContainsKey(item.FilePath)) throw new PublishingBlock("Unexpected checkout changes prevent safe rollback. No files were overwritten.");
        var indexTree = repo.Index.WriteToTree();
        if (indexTree.Id != baseEntries.Id && indexTree.Sha != journal.IntendedTree)
            throw new PublishingBlock("Unexpected index changes prevent safe rollback. Preserve the workspace.");
        var preimageBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, before) in journal.Preimages)
        {
            var target = PublishingStorage.Resolve(storage.Repository, path);
            var current = ReadRecoveryFile(repo, path, target);
            var after = journal.Files.GetValueOrDefault(path);
            // Bytes and executable mode must belong to the SAME allowed state.
            // In particular, content-only replacement never authorizes a chmod.
            if (!MatchesRecoveryFile(current, before, baseEntries[path]) && !MatchesRecoveryFile(current, after, intendedEntries[path]))
                throw new PublishingBlock("Unexpected file content, type or executable mode prevents safe rollback. No files were overwritten; preserve the workspace for operator recovery.");
            if (before is not null)
            {
                var bytes = File.ReadAllBytes(PublishingStorage.Resolve(Path.Combine(storage.Rollback, "files"), path));
                if (PublishingStorage.Hash(bytes) != before)
                    throw new PublishingBlock("Rollback preimage changed. Preserve the workspace for operator recovery.");
                preimageBytes[path] = bytes;
            }
        }
        foreach (var (path, before) in journal.Preimages)
        {
            var target = PublishingStorage.Resolve(storage.Repository, path);
            var current = ReadRecoveryFile(repo, path, target);
            if (!MatchesRecoveryFile(current, before, baseEntries[path]) &&
                !MatchesRecoveryFile(current, journal.Files.GetValueOrDefault(path), intendedEntries[path]))
                throw new PublishingBlock("Unexpected checkout changes interrupted rollback. Preserve the workspace for operator recovery.");
            if (before is null) File.Delete(target);
            else { storage.Atomic(target, preimageBytes[path]); SetMode(target, baseEntries[path].Mode); }
        }
        storage.Atomic(Path.Combine(repo.Info.Path, "index"), indexBytes);
        journal.Phase = advancing ? "Preparing" : "Prepared"; journal.Commit = null; storage.Save(journal);
        if (advancing) ClearRollback(journal);
    }
    private static (string? Hash, Mode? Mode) ReadRecoveryFile(Repository repo, string relative, string target)
    {
        PublishingStorage.CheckPath(target);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(target); }
        catch (FileNotFoundException) { return (null, null); }
        catch (DirectoryNotFoundException) { return (null, null); }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new PublishingBlock("Unexpected file type prevents safe rollback. Preserve the workspace; no files were overwritten.");
        // libgit2 excludes unsupported Unix filesystem types from its workdir
        // iterator. An existing path reported absent/deleted is not a regular blob;
        // reject it before opening it (e.g. a FIFO must not become a blocking read).
        var status = repo.RetrieveStatus(relative);
        if ((status & (FileStatus.Nonexistent | FileStatus.DeletedFromWorkdir | FileStatus.TypeChangeInWorkdir | FileStatus.Unreadable)) != 0)
            throw new PublishingBlock("Unexpected or unreadable file type prevents safe rollback. Preserve the workspace; no files were overwritten.");
        Mode? mode = OperatingSystem.IsWindows() ? null :
            (File.GetUnixFileMode(target) & UnixFileMode.UserExecute) != 0 ? Mode.ExecutableFile : Mode.NonExecutableFile;
        return (PublishingStorage.FileHash(target), mode);
    }
    private static bool MatchesRecoveryFile((string? Hash, Mode? Mode) current, string? hash, TreeEntry? entry) =>
        entry is null ? hash is null && current.Hash is null :
        entry.Target is Blob && entry.Mode is Mode.NonExecutableFile or Mode.ExecutableFile &&
        hash is not null && current.Hash == hash && (OperatingSystem.IsWindows() || current.Mode == entry.Mode);
    private void VerifyRecoveryEvidence(Repository repo, PublishingJournal journal, bool advancing)
    {
        var baseline = repo.Lookup<Commit>(journal.BaseCommit);
        var intended = repo.Lookup<Commit>(journal.Commit);
        if (baseline is null || intended is null || intended.Tree.Sha != journal.IntendedTree ||
            (advancing ? repo.ObjectDatabase.FindMergeBase(baseline, intended)?.Id != baseline.Id :
                intended.Parents.Count() != 1 || intended.Parents.Single().Id != baseline.Id))
            throw new PublishingBlock("Retained commit intent is inconsistent. Preserve the workspace for operator recovery.");
        ValidateTree(baseline.Tree);
        ValidateTree(intended.Tree);
        var before = Blobs(baseline.Tree).ToDictionary(x => x.Path, x => x.Blob, StringComparer.Ordinal);
        var after = Blobs(intended.Tree).ToDictionary(x => x.Path, x => x.Blob, StringComparer.Ordinal);
        var changed = before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).Where(path =>
            before.GetValueOrDefault(path)?.Id != after.GetValueOrDefault(path)?.Id || baseline.Tree[path]?.Mode != intended.Tree[path]?.Mode)
            .Order(StringComparer.Ordinal).ToArray();
        if (!changed.SequenceEqual(journal.Preimages.Keys.Order(StringComparer.Ordinal)) || (!advancing && changed.Any(x => !Owned(x))))
            throw new PublishingBlock("Rollback ownership differs from the intended commit. Preserve all evidence for operator recovery.");
        if (journal.IndexHash is null || PublishingStorage.FileHash(Path.Combine(storage.Rollback, "index")) != journal.IndexHash)
            throw new PublishingBlock("Rollback index evidence changed. Preserve the workspace for operator recovery.");
        foreach (var path in changed)
        {
            // The durable preimage hash describes physical bytes proven at capture,
            // not baseline blob bytes. Recovery-time attributes may have changed;
            // never clean the backup again or derive its expected hash from itself.
            if ((journal.Preimages[path] is null) != !before.ContainsKey(path) ||
                (before.ContainsKey(path) && baseline.Tree[path].Mode is not (Mode.NonExecutableFile or Mode.ExecutableFile)) ||
                (after.ContainsKey(path) && intended.Tree[path].Mode is not (Mode.NonExecutableFile or Mode.ExecutableFile)) ||
                journal.Files.GetValueOrDefault(path) != (after.TryGetValue(path, out var next) ? PublishingStorage.Hash(Bytes(next)) : null) ||
                journal.Removed.Contains(path, StringComparer.Ordinal) != !after.ContainsKey(path))
                throw new PublishingBlock("Rollback/candidate hashes do not match retained Git intent. Preserve the workspace for operator recovery.");
            if (journal.Preimages[path] is { } hash &&
                PublishingStorage.FileHash(PublishingStorage.Resolve(Path.Combine(storage.Rollback, "files"), path)) != hash)
                throw new PublishingBlock("Rollback preimage changed. Preserve the workspace for operator recovery.");
        }
    }
    public bool Push(PublishingJournal journal)
    {
        using var repo = Open();
        EnsureClean(repo);
        if (journal.Configuration != settings.Identity || journal.Commit is null || repo.Head.Tip.Sha != journal.Commit)
            throw new PublishingBlock("Pending commit/configuration does not match this checkout. Preserve all evidence for operator reconciliation.");
        var commit = repo.Lookup<Commit>(journal.Commit);
        var remote = Fetch(repo);
        if (remote.Id == commit.Id || repo.ObjectDatabase.FindMergeBase(commit, remote)?.Id == commit.Id) return true;
        if (repo.ObjectDatabase.FindMergeBase(commit, remote)?.Id != remote.Id)
            throw new PublishingBlock("The remote diverged from the pending commit. Stop attempts, preserve workspace and commit ID, and have the operator reconcile deliberately. No merge/rebase/force push was attempted.");
        if (!settings.CredentialsReady) throw new PublishingBlock("Configure GitHub credentials on the server, then retry this exact commit.");
        repo.Network.Push(repo.Network.Remotes["origin"], $"{commit.Sha}:refs/heads/{settings.Branch}",
            new PushOptions { CredentialsProvider = Credentials });
        var confirmed = Fetch(repo);
        return confirmed.Id == commit.Id || repo.ObjectDatabase.FindMergeBase(commit, confirmed)?.Id == commit.Id;
    }
}
