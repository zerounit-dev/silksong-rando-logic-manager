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
            throw new PublishingBlock("Repository/.git is not a directory. Publishing cannot adopt a worktree or external Git store. Keep Repository and Staging intact; have the server operator establish why the owned checkout lacks its local .git directory before attempting publication.");
        var gitDirectory = Path.Combine(storage.Repository, ".git");
        var markers = new[] { "commondir", "gitdir", "objects/info/alternates", "index.lock", "HEAD.lock" }
            .Where(x => File.Exists(Path.Combine(gitDirectory, x.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
        if (markers.Length != 0)
            throw new PublishingBlock("Repository/.git contains blocked storage markers or lock files:\n" +
                string.Join("\n", markers.Select(x => "Repository/.git/" + x + (x.EndsWith(".lock", StringComparison.Ordinal)
                    ? " — lock file present; this does not establish whether a writer is active or the lock is stale"
                    : " — external Git storage marker"))) +
                "\nKeep these files. The server operator must establish storage ownership and whether any writer still owns the locks before changing this checkout.");
        if (Directory.Exists(Path.Combine(gitDirectory, "hooks")))
        {
            var hooks = Directory.EnumerateFiles(Path.Combine(gitDirectory, "hooks")).Where(x => !x.EndsWith(".sample", StringComparison.Ordinal))
                .Select(x => Path.GetRelativePath(storage.Repository, x).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
            if (hooks.Length != 0) throw new PublishingBlock("The checkout contains non-sample Git hook files:\n" + string.Join("\n", hooks) +
                "\nPublishing does not adopt hooks. Have the repository owner review these files and arrange a hook-free owned checkout without deleting retained publication evidence.");
        }
        var repo = new Repository(storage.Repository);
        try
        {
            CheckConfiguration(repo);
            if (repo.Info.IsBare) throw new PublishingBlock("Repository is bare: it has no working checkout. Keep retained Staging evidence; the server operator must provide the owned non-bare publication checkout before publishing.");
            if (repo.Info.IsHeadDetached) throw new PublishingBlock($"Repository HEAD is detached at commit {repo.Head.Tip?.Sha ?? "(unborn)"}. Publishing requires branch {settings.Branch}. Have the repository owner compare that commit with the configured branch before changing refs; do not reset retained publication work.");
            if (repo.Info.CurrentOperation != CurrentOperation.None) throw new PublishingBlock($"Repository has an unfinished Git operation: {repo.Info.CurrentOperation}. Have its owner resolve that operation and verify the configured publication branch before publishing; the application will not complete or abort it.");
            if (repo.Head.FriendlyName != settings.Branch) throw new PublishingBlock($"Repository is on branch {repo.Head.FriendlyName}; configured branch is {settings.Branch}. Have the repository owner compare their commits and retained Staging evidence before selecting the publication branch.");
            if (Path.GetFullPath(repo.Info.Path).TrimEnd(Path.DirectorySeparatorChar) != Path.Combine(storage.Repository, ".git"))
                throw new PublishingBlock("Git resolved its metadata outside Repository/.git. Keep Repository and Staging intact; the server operator must resolve this external metadata ownership mismatch before publishing.");
            if (Path.GetFullPath(repo.Info.WorkingDirectory).TrimEnd(Path.DirectorySeparatorChar) != storage.Repository)
                throw new PublishingBlock("Git resolved its working directory somewhere other than Repository. The server operator must correct the checkout's working-directory ownership before publishing; retain Repository and Staging.");
            if (repo.Network.Remotes["origin"]?.Url != settings.RepositoryUrl) throw new PublishingBlock("Repository origin URL differs from DocumentationPublishing:RepositoryUrl. Have the server operator compare that setting with Repository/.git/config's origin fetch URL before network access. URLs are not echoed because a mismatched value may contain credentials.");
            if (repo.Submodules.Any()) throw new PublishingBlock("Repository declares submodules. Publishing does not adopt submodules; have the repository owner remove the submodule dependency from the publication branch deliberately before preparing again.");
            if (repo.Network.Remotes["origin"]?.PushUrl is { Length: > 0 } pushUrl && pushUrl != settings.RepositoryUrl)
                throw new PublishingBlock("Repository origin has a separate push URL differing from DocumentationPublishing:RepositoryUrl. Have the server operator review the origin push destination in Repository/.git/config before network access; no URL value is echoed.");
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
            if (!names.Add(path)) throw new PublishingBlock($"Publication tree paths collide by case:\n{names.First(x => x.Equals(path, StringComparison.OrdinalIgnoreCase))}\n{path}\nHave the repository owner choose distinct case-insensitive names on the publication branch before preparing again.");
            if (entry.Mode is Mode.SymbolicLink or Mode.GitLink)
                throw new PublishingBlock($"Publication tree path {path} has unsupported mode {entry.Mode}. Symbolic links and submodules cannot be checked out for publication. Have the repository owner replace this entry with ordinary publication content before preparing again.");
            if (entry.Target is Tree child) ValidateTree(child, path + "/", names);
            if (entry.Name == ".gitattributes" && entry.Target is Blob attributes)
            {
                var token = System.Text.RegularExpressions.Regex.Match(Encoding.UTF8.GetString(Bytes(attributes)), @"(?:^|\s)(?<token>filter|working-tree-encoding)(?:=|\s|$)", System.Text.RegularExpressions.RegexOptions.Multiline);
                if (token.Success) throw new PublishingBlock($"Publication attributes file {path} contains an unsupported {token.Groups["token"].Value} token. Have the repository owner review that token and remove the unsupported content conversion from the publication branch before preparing again; attribute bodies are not displayed.");
            }
        }
    }
    public void EnsureClean(Repository repo)
    {
        PublishingStorage.CheckTree(storage.Repository);
        var status = repo.RetrieveStatus(new StatusOptions { IncludeIgnored = true, IncludeUntracked = true, RecurseUntrackedDirs = true }).ToArray();
        if (status.Length != 0)
            throw new PublishingBlock($"The publication checkout is not clean.\nBranch: {repo.Head.FriendlyName}\nHEAD commit: {repo.Head.Tip?.Sha ?? "(unborn)"}\nRepository-relative paths and native Git statuses:\n{StatusFacts(status)}\nKeep Repository and Staging intact. Have the server operator compare these paths with HEAD and the index to establish which differences belong to the checkout before publishing again. Status alone does not establish the cause, even on a fresh install; do not reset or delete files to bypass it. Native Git is bundled; installing Git CLI is not required.");
    }
    private static string StatusFacts(IEnumerable<StatusEntry> entries) => string.Join("\n", entries.OrderBy(x => x.FilePath, StringComparer.Ordinal)
        .Select(x => $"{x.FilePath} — {x.State} ({string.Join("; ", Enum.GetValues<FileStatus>().Where(flag => flag != FileStatus.Unaltered && x.State.HasFlag(flag)).Select(flag => flag switch
        {
            FileStatus.Nonexistent => "absent from Git and working files",
            FileStatus.NewInIndex => "staged addition",
            FileStatus.ModifiedInIndex => "staged modification",
            FileStatus.DeletedFromIndex => "staged deletion",
            FileStatus.RenamedInIndex => "staged rename",
            FileStatus.TypeChangeInIndex => "staged file-type change",
            FileStatus.NewInWorkdir => "untracked working file",
            FileStatus.ModifiedInWorkdir => "working file differs from index",
            FileStatus.DeletedFromWorkdir => "working file missing",
            FileStatus.TypeChangeInWorkdir => "working file type differs",
            FileStatus.RenamedInWorkdir => "working file renamed",
            FileStatus.Unreadable => "working file unreadable",
            FileStatus.Ignored => "ignored by Git exclude rules",
            FileStatus.Conflicted => "unresolved index conflict",
            _ => flag.ToString()
        }))})"));
    public Commit Fetch(Repository repo)
    {
        // Also runs for the unborn first clone, before any transport invocation.
        CheckConfiguration(repo);
        Commands.Fetch(repo, "origin", [$"+refs/heads/{settings.Branch}:refs/remotes/origin/{settings.Branch}"], FetchOptions(), null);
        return repo.Branches[$"origin/{settings.Branch}"]?.Tip
            ?? throw new PublishingBlock($"Fetch completed but origin/{settings.Branch} has no commit. Check DocumentationPublishing:Branch against the existing remote branch name; the repository owner must create the branch outside the application if it does not exist, then prepare again.");
    }
    private void CheckConfiguration(Repository repo)
    {
        if (repo.Network.Remotes["origin"]?.Url != settings.RepositoryUrl)
            throw new PublishingBlock("Git origin fetch URL is missing or differs from DocumentationPublishing:RepositoryUrl. Have the server operator compare the configured destination with the owned checkout's origin before network access; mismatched URL values are not displayed.");
        if (repo.Network.Remotes["origin"]?.PushUrl is { Length: > 0 } pushUrl && pushUrl != settings.RepositoryUrl)
            throw new PublishingBlock("Git origin has a push URL differing from DocumentationPublishing:RepositoryUrl. Have the server operator compare the push destination with the configured repository before network access; mismatched URL values are not displayed.");
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
                throw new PublishingBlock($"Publishing Git configuration contains unsupported {category} at level {entry.Level}. Have the server operator review that category at that configuration level and remove its effect on this owned checkout before publishing. Inherited inactive filter definitions are allowed; configuration keys/values are not displayed because they may contain private URLs or paths.");
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
                    throw new PublishingBlock($"Branch {settings.Branch} cannot fast-forward: local HEAD {repo.Head.Tip.Sha} is not an ancestor of fetched commit {remote.Sha}. The local branch is ahead or the histories diverge. Have the repository owner compare these two commits and decide which publication history to retain outside the application; keep Repository and Staging, and do not reset, merge or force-push to bypass this block.");
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
                var current = ReadRecoveryFile(repo, path, target);
                var expected = repo.Lookup<Commit>(journal.BaseCommit).Tree[path];
                if (!MatchesRecoveryFile(current, journal.Preimages[path], expected))
                    throw new PublishingBlock($"Fast-forward stopped at {path}: the checkout no longer matches its captured preimage.\n{FileFacts(current, journal.Preimages[path], expected)}\nKeep Repository/{path} and Staging/Rollback/files/{path}; compare them with base commit {journal.BaseCommit} before resolving the interrupted advance. Earlier paths may already have changed.");
                if (after.TryGetValue(path, out var blob))
                {
                    storage.Atomic(target, Bytes(blob)); SetMode(target, remote.Tree[path].Mode);
                    repo.Index.Add(blob, path, remote.Tree[path].Mode);
                }
                else { File.Delete(target); repo.Index.Remove(path); }
            }
            repo.Index.Write();
            var staged = repo.Index.WriteToTree();
            if (staged.Id != remote.Tree.Id) throw new PublishingBlock($"Fast-forward index tree differs from the fetched tree.\nExpected Git tree: {remote.Tree.Sha}\nObserved index tree: {staged.Sha}\nKeep Repository/.git/index and Staging/Rollback/index; compare their entries with fetched commit {remote.Sha} before resolving the advance.");
            foreach (var path in journal.Preimages.Keys)
            {
                var current = ReadRecoveryFile(repo, path, PublishingStorage.Resolve(storage.Repository, path));
                if (!MatchesRecoveryFile(current, journal.Files.GetValueOrDefault(path), remote.Tree[path]))
                    throw new PublishingBlock($"Fast-forward postimage differs at {path}.\n{FileFacts(current, journal.Files.GetValueOrDefault(path), remote.Tree[path])}\nKeep this Repository path and its Staging/Rollback evidence; compare it with fetched commit {remote.Sha} before resolving the advance.");
            }
            if (repo.Head.Tip.Sha != journal.BaseCommit)
                throw new PublishingBlock($"HEAD moved during fast-forward.\nExpected base commit: {journal.BaseCommit}\nObserved HEAD commit: {repo.Head.Tip.Sha}\nKeep Repository and Staging/Rollback; compare these commits and the intended fetched commit {remote.Sha} before changing refs or files.");
            var unexpected = repo.RetrieveStatus(new StatusOptions { IncludeIgnored = true, IncludeUntracked = true, RecurseUntrackedDirs = true }).Where(x => !journal.Preimages.ContainsKey(x.FilePath)).ToArray();
            if (unexpected.Length != 0)
                throw new PublishingBlock($"Paths outside the captured fast-forward changes have Git differences:\n{StatusFacts(unexpected)}\nKeep these Repository paths and Staging/Rollback; establish their ownership against base commit {journal.BaseCommit} before resolving the advance.");
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
        if (baseline is null) throw new PublishingBlock($"Cannot retain rollback: base Git commit {journal.BaseCommit} is missing. Keep Repository and Staging/current.json; the repository owner must establish that base commit's availability before publication.");
        if (intended is null) throw new PublishingBlock($"Cannot retain rollback: intended Git tree {journal.IntendedTree} is missing. Keep Repository and Staging/current.json; compare the intended commit/tree evidence before publication.");
        if (captured.Head.Tip.Id != baseline.Id) throw new PublishingBlock($"HEAD changed before rollback capture.\nExpected base commit: {baseline.Sha}\nObserved HEAD commit: {captured.Head.Tip.Sha}\nCompare these commits and retained candidate evidence before preparing a new review; no replacement has started in this capture.");
        var capturedTree = captured.Index.WriteToTree();
        if (capturedTree.Id != baseline.Tree.Id) throw new PublishingBlock($"Index differs from the base before rollback capture.\nExpected base Git tree: {baseline.Tree.Sha}\nObserved index tree: {capturedTree.Sha}\nKeep Repository/.git/index; compare its entries with the base before publishing.");
        var observedIndexHash = PublishingStorage.FileHash(index);
        if (observedIndexHash != indexHash) throw new PublishingBlock($"Repository/.git/index changed during rollback capture.\nCaptured raw SHA-256: {indexHash}\nObserved raw SHA-256: {observedIndexHash}\nEstablish which writer changed the index before publishing; keep Repository and Staging intact.");
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
                throw new PublishingBlock($"Cannot capture rollback for {path}: file presence, type or executable mode differs from the base entry.\nExpected Git entry: {(entry is null ? "(absent)" : $"{entry.TargetType}, {entry.Mode}")}\nObserved regular file present: {current.Hash is not null}\nObserved executable mode: {(OperatingSystem.IsWindows() ? "not checked on Windows" : current.Mode?.ToString() ?? "(absent)")}\nCompare Repository/{path} with base commit {baseline.Sha} before publishing; keep existing rollback evidence.");
            if (before is null) { journal.Preimages[path] = null; continue; }
            // Git identity and physical rollback identity are different: native
            // checkout can expand line endings/ident. Prove the captured bytes
            // clean to the baseline, but hash and retain those exact raw bytes.
            var bytes = File.ReadAllBytes(target);
            var hash = PublishingStorage.Hash(bytes);
            using var input = new MemoryStream(bytes, false);
            if (hash != current.Hash)
                throw new PublishingBlock($"Repository/{path} changed while reading its rollback bytes.\nFirst raw SHA-256: {current.Hash}\nCaptured raw SHA-256: {hash}\nEstablish which writer changed this file before publishing; retain Repository and Staging.");
            var normalized = repo.ObjectDatabase.CreateBlob(input, path).Id;
            if (normalized != before.Id)
                throw new PublishingBlock($"Captured checkout bytes for {path} do not normalize to the base Git blob.\nExpected base blob ID: {before.Id.Sha}\nObserved normalized blob ID: {normalized.Sha}\nCompare Repository/{path} and the publication branch's attributes with base commit {baseline.Sha}; do not replace rollback evidence or assume a line-ending cause.");
            var rechecked = ReadRecoveryFile(repo, path, target);
            if (!MatchesRecoveryFile(rechecked, hash, entry))
                throw new PublishingBlock($"Repository/{path} changed after its rollback bytes were captured.\n{FileFacts(rechecked, hash, entry)}\nEstablish which writer changed this path before publishing; keep retained rollback evidence.");
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
        if (current.Head.Tip.Id != baseline.Id) throw new PublishingBlock($"HEAD changed after rollback capture.\nExpected base commit: {baseline.Sha}\nObserved HEAD commit: {current.Head.Tip.Sha}\nCompare both commits with Staging/current.json before publishing; retain rollback files.");
        var currentTree = current.Index.WriteToTree();
        if (currentTree.Id != baseline.Tree.Id) throw new PublishingBlock($"Index changed after rollback capture.\nExpected base Git tree: {baseline.Tree.Sha}\nObserved index tree: {currentTree.Sha}\nCompare Repository/.git/index with Staging/Rollback/index and the base tree before publishing.");
        var currentIndexHash = PublishingStorage.FileHash(Path.Combine(repo.Info.Path, "index"));
        if (currentIndexHash != journal.IndexHash) throw new PublishingBlock($"Repository/.git/index bytes changed after rollback capture.\nExpected raw SHA-256: {journal.IndexHash}\nObserved raw SHA-256: {currentIndexHash}\nKeep the index and Staging/Rollback/index; establish which writer changed it before publishing.");
        foreach (var (path, hash) in journal.Preimages)
        {
            var file = ReadRecoveryFile(current, path, PublishingStorage.Resolve(storage.Repository, path));
            if (!MatchesRecoveryFile(file, hash, baseline.Tree[path]))
                throw new PublishingBlock($"Captured preimage no longer matches Repository/{path}.\n{FileFacts(file, hash, baseline.Tree[path])}\nCompare this file with Staging/Rollback/files/{path} and base commit {baseline.Sha} before publishing.");
        }
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
            if (!Owned(path)) throw new PublishingBlock($"Generated path {path} is outside publication ownership. Keep Staging/Candidate/{path}; report this generator output to the application maintainer rather than copying it into Repository.");
            var collisions = existing.Keys.Where(x => x.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(x + "/", StringComparison.OrdinalIgnoreCase) ||
                (x.Equals(path, StringComparison.OrdinalIgnoreCase) && x != path)).Order(StringComparer.Ordinal).ToArray();
            if (collisions.Length != 0)
                throw new PublishingBlock($"Candidate path {path} collides with existing repository paths:\n{string.Join("\n", collisions)}\nHave the repository owner choose distinct file/directory names on the publication branch, then prepare and review again; do not overwrite the colliding content.");
        }
        journal.Removed = existing.Keys.Where(Owned).Except(journal.Files.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }
    public void Commit(PublishingJournal journal, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new PublishingBlock("Enter a nonblank commit message.");
        if (!settings.CredentialsReady) throw new PublishingBlock(settings.MissingCredentials + " Configure these server keys with a repository-scoped Contents read/write token before committing. Publishing settings were captured at application registration; editing a file does not update this process's captured settings.");
        using var repo = Open();
        EnsureClean(repo);
        storage.Validate(journal);
        if (journal.Renderer != PublishingRenderer.Version)
            throw new PublishingBlock($"Prepared renderer version differs.\nCandidate: {journal.Renderer}\nCurrent application: {PublishingRenderer.Version}\nDiscard the prepared changes, prepare again and review the renderer change before committing.");
        if (journal.Configuration != settings.Identity) throw new PublishingBlock("The prepared repository/branch configuration differs from the current server configuration. Compare DocumentationPublishing:RepositoryUrl and :Branch with the retained operation's destination before deciding whether to discard and prepare again; configuration identities are not displayed.");
        if (repo.Head.Tip.Sha != journal.BaseCommit) throw new PublishingBlock($"Prepared base commit differs from HEAD.\nPrepared base: {journal.BaseCommit}\nCurrent HEAD: {repo.Head.Tip.Sha}\nCompare these commits; after confirming the checkout history is intended, discard the prepared changes and prepare/review against the new base.");
        var fetchedBase = Fetch(repo);
        if (fetchedBase.Sha != journal.BaseCommit) throw new PublishingBlock($"Remote branch {settings.Branch} moved since preparation.\nPrepared base commit: {journal.BaseCommit}\nFetched commit: {fetchedBase.Sha}\nDiscard the prepared changes and prepare/review a fresh candidate against the intended remote history.");
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
                var current = ReadRecoveryFile(repo, path, target);
                var expected = repo.Lookup<Commit>(journal.BaseCommit).Tree[path];
                if (!MatchesRecoveryFile(current, journal.Preimages[path], expected))
                    throw new PublishingBlock($"Replacement stopped at {path}: checkout differs from its captured preimage.\n{FileFacts(current, journal.Preimages[path], expected)}\nCompare Repository/{path} with Staging/Rollback/files/{path} and base commit {journal.BaseCommit}; keep the evidence. Earlier files may already have been replaced.");
                if (journal.Files.ContainsKey(path)) { storage.Atomic(target, File.ReadAllBytes(PublishingStorage.Resolve(storage.Candidate, path))); SetMode(target, tree[path].Mode); }
                else File.Delete(target);
                // Direct blob-index insertion avoids attributes, filters and ignored-path rules.
                if (journal.Files.ContainsKey(path)) repo.Index.Add((Blob)tree[path].Target, path, tree[path].Mode);
                else repo.Index.Remove(path);
            }
            repo.Index.Write();
            var staged = repo.Index.WriteToTree();
            if (staged.Id != tree.Id) throw new PublishingBlock($"Staged index differs from the reviewed Git tree.\nExpected Git tree: {tree.Sha}\nObserved index tree: {staged.Sha}\nKeep Repository/.git/index, Staging/current.json and Staging/Rollback/index; compare their entries before resolving replacement.");
            storage.Validate(journal);
            foreach (var path in journal.Preimages.Keys)
            {
                var current = ReadRecoveryFile(repo, path, PublishingStorage.Resolve(storage.Repository, path));
                if (!MatchesRecoveryFile(current, journal.Files.GetValueOrDefault(path), tree[path]))
                    throw new PublishingBlock($"Replacement postimage differs at {path}.\n{FileFacts(current, journal.Files.GetValueOrDefault(path), tree[path])}\nCompare this Repository path with Staging/Candidate/{path} and its rollback preimage before resolving replacement; do not discard recovery evidence.");
            }
            var unexpected = repo.RetrieveStatus(new StatusOptions { IncludeIgnored = true, IncludeUntracked = true, RecurseUntrackedDirs = true }).Where(x => !journal.Preimages.ContainsKey(x.FilePath)).ToArray();
            if (unexpected.Length != 0) throw new PublishingBlock($"Paths outside the reviewed replacement have Git differences:\n{StatusFacts(unexpected)}\nKeep these Repository paths and Staging/Rollback; compare them with base commit {journal.BaseCommit} and establish ownership before resolving replacement.");
            if (repo.Head.Tip.Sha != journal.BaseCommit) throw new PublishingBlock($"HEAD moved during replacement.\nExpected base commit: {journal.BaseCommit}\nObserved HEAD commit: {repo.Head.Tip.Sha}\nCompare these commits with intended commit {journal.Commit} and retained rollback files before changing refs or discarding anything.");
            var remoteBeforeCommit = Fetch(repo);
            if (remoteBeforeCommit.Sha != journal.BaseCommit) throw new PublishingBlock($"Remote moved during replacement.\nExpected base commit: {journal.BaseCommit}\nFetched commit: {remoteBeforeCommit.Sha}\nKeep Staging/Rollback and Staging/current.json. Resolve the verified precommit state against these commits before preparing/reviewing again; do not force-push.");
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
        if (repo.Head.Tip.Sha != journal.BaseCommit)
            throw new PublishingBlock($"Interrupted {journal.Phase} cannot roll back: HEAD is neither the base nor the exact intended commit/tree.\nBase commit: {journal.BaseCommit}\nIntended commit: {journal.Commit}\nObserved HEAD commit: {repo.Head.Tip.Sha}\nKeep Repository and Staging; have the repository owner compare these commits and Staging/current.json before choosing the retained history. Discard is blocked.");
        if (journal.IndexHash is null) throw new PublishingBlock($"Interrupted {journal.Phase} has no IndexHash in Staging/current.json. Keep Repository/.git/index and Staging/Rollback/index; their original identity must be established before any rollback or discard.");
        if (!advancing && journal.Preimages.Count == 0) throw new PublishingBlock("Interrupted replacement has no Preimages in Staging/current.json. Keep Staging/Candidate, Staging/Rollback and Repository; establish the intended changed paths and their original bytes before any rollback or discard.");
        var indexBackup = Path.Combine(storage.Rollback, "index");
        var indexBytes = File.ReadAllBytes(indexBackup);
        var savedIndexHash = PublishingStorage.Hash(indexBytes);
        if (savedIndexHash != journal.IndexHash)
            throw new PublishingBlock($"Staging/Rollback/index differs from its retained identity.\nExpected raw SHA-256: {journal.IndexHash}\nObserved raw SHA-256: {savedIndexHash}\nKeep this index and Repository/.git/index; the original index identity must be established from trusted evidence before rollback. Do not edit the journal to accept these bytes.");
        // A partially staged index may contain ONLY old or intended blobs/modes.
        var baseEntries = repo.Lookup<Commit>(journal.BaseCommit).Tree;
        var intendedEntries = repo.Lookup<Tree>(journal.IntendedTree);
        var unexpected = repo.RetrieveStatus(new StatusOptions { IncludeIgnored = true, IncludeUntracked = true, RecurseUntrackedDirs = true }).Where(x => !journal.Preimages.ContainsKey(x.FilePath)).ToArray();
        if (unexpected.Length != 0) throw new PublishingBlock($"Rollback found Git differences outside its captured paths:\n{StatusFacts(unexpected)}\nKeep these Repository paths and Staging/Rollback; establish ownership of these differences against base commit {journal.BaseCommit} before rollback or discard. Earlier operation changes may remain.");
        var indexTree = repo.Index.WriteToTree();
        if (indexTree.Id != baseEntries.Id && indexTree.Sha != journal.IntendedTree)
            throw new PublishingBlock($"Rollback index is neither the base nor the intended tree.\nBase Git tree: {baseEntries.Sha}\nIntended Git tree: {journal.IntendedTree}\nObserved index tree: {indexTree.Sha}\nKeep Repository/.git/index and Staging/Rollback/index; compare their entries with both trees before resolving recovery.");
        var preimageBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, before) in journal.Preimages)
        {
            var target = PublishingStorage.Resolve(storage.Repository, path);
            var current = ReadRecoveryFile(repo, path, target);
            var after = journal.Files.GetValueOrDefault(path);
            // Bytes and executable mode must belong to the SAME allowed state.
            // In particular, content-only replacement never authorizes a chmod.
            if (!MatchesRecoveryFile(current, before, baseEntries[path]) && !MatchesRecoveryFile(current, after, intendedEntries[path]))
                throw new PublishingBlock($"Rollback cannot classify Repository/{path} as the complete before or after state.\nBefore state:\n{FileFacts(current, before, baseEntries[path])}\nAfter state:\n{FileFacts(current, after, intendedEntries[path])}\nKeep this file, Staging/Rollback/files/{path} and Staging/Candidate/{path} where present. Compare bytes AND executable mode with the two retained states before recovery; do not discard or mix a before hash with an after mode.");
            if (before is not null)
            {
                var bytes = File.ReadAllBytes(PublishingStorage.Resolve(Path.Combine(storage.Rollback, "files"), path));
                var backupHash = PublishingStorage.Hash(bytes);
                if (backupHash != before)
                    throw new PublishingBlock($"Rollback file Staging/Rollback/files/{path} differs from its captured raw bytes.\nExpected raw SHA-256: {before}\nObserved raw SHA-256: {backupHash}\nKeep this backup and Repository/{path}; locate trusted original bytes matching the retained hash before rollback. Do not redefine the expected hash from this backup.");
                preimageBytes[path] = bytes;
            }
        }
        foreach (var (path, before) in journal.Preimages)
        {
            var target = PublishingStorage.Resolve(storage.Repository, path);
            var current = ReadRecoveryFile(repo, path, target);
            if (!MatchesRecoveryFile(current, before, baseEntries[path]) &&
                !MatchesRecoveryFile(current, journal.Files.GetValueOrDefault(path), intendedEntries[path]))
                throw new PublishingBlock($"Repository/{path} changed during rollback and matches neither retained state.\nBefore state:\n{FileFacts(current, before, baseEntries[path])}\nAfter state:\n{FileFacts(current, journal.Files.GetValueOrDefault(path), intendedEntries[path])}\nStop publishing attempts and compare this file with its retained before/after evidence. Keep Staging/Rollback; earlier files may already have been restored.");
            if (before is null) File.Delete(target);
            else { storage.Atomic(target, preimageBytes[path]); SetMode(target, baseEntries[path].Mode); }
        }
        storage.Atomic(Path.Combine(repo.Info.Path, "index"), indexBytes);
        journal.Phase = advancing ? "Preparing" : "Prepared"; journal.Commit = null; storage.Save(journal);
        if (advancing) ClearRollback(journal);
    }
    private static (string? Hash, Mode? Mode) ReadRecoveryFile(Repository repo, string relative, string target)
    {
        PublishingStorage.CheckPath(target, "Repository/" + relative);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(target); }
        catch (FileNotFoundException) { return (null, null); }
        catch (DirectoryNotFoundException) { return (null, null); }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new PublishingBlock($"Repository/{relative} is not an ordinary file; observed attributes: {attributes}. Keep this path and its Staging/Rollback evidence; establish its intended file type before capture, replacement or rollback. Earlier operation changes may remain.");
        // libgit2 excludes unsupported Unix filesystem types from its workdir
        // iterator. An existing path reported absent/deleted is not a regular blob;
        // reject it before opening it (e.g. a FIFO must not become a blocking read).
        var status = repo.RetrieveStatus(relative);
        if ((status & (FileStatus.Nonexistent | FileStatus.DeletedFromWorkdir | FileStatus.TypeChangeInWorkdir | FileStatus.Unreadable)) != 0)
            throw new PublishingBlock($"Repository/{relative} exists but native Git reports {status}; it cannot be read as a regular rollback file. Keep this path and its Staging/Rollback evidence; have the server operator establish its type/readability and compare with the base entry before recovery.");
        Mode? mode = OperatingSystem.IsWindows() ? null :
            (File.GetUnixFileMode(target) & UnixFileMode.UserExecute) != 0 ? Mode.ExecutableFile : Mode.NonExecutableFile;
        return (PublishingStorage.FileHash(target), mode);
    }
    private static bool MatchesRecoveryFile((string? Hash, Mode? Mode) current, string? hash, TreeEntry? entry) =>
        entry is null ? hash is null && current.Hash is null :
        entry.Target is Blob && entry.Mode is Mode.NonExecutableFile or Mode.ExecutableFile &&
        hash is not null && current.Hash == hash && (OperatingSystem.IsWindows() || current.Mode == entry.Mode);
    private static string FileFacts((string? Hash, Mode? Mode) current, string? hash, TreeEntry? entry) =>
        $"Expected raw SHA-256: {hash ?? "(absent)"}\nObserved raw SHA-256: {current.Hash ?? "(absent)"}\nExpected Git entry: {(entry is null ? "(absent)" : $"{entry.TargetType}, {entry.Mode}")}\nObserved executable mode: {(current.Hash is null ? "(absent)" : OperatingSystem.IsWindows() ? "not checked on Windows" : current.Mode?.ToString() ?? "(unknown)")}";
    private void VerifyRecoveryEvidence(Repository repo, PublishingJournal journal, bool advancing)
    {
        var baseline = repo.Lookup<Commit>(journal.BaseCommit);
        var intended = repo.Lookup<Commit>(journal.Commit);
        if (baseline is null) throw new PublishingBlock($"Recovery base commit {journal.BaseCommit} cannot be found in Repository. Keep Staging/current.json and Rollback; establish this commit's availability and history before recovery or discard.");
        if (intended is null) throw new PublishingBlock($"Recovery intended commit {journal.Commit} cannot be found in Repository. Keep Staging/current.json and Rollback; establish whether that exact commit was created or published before changing files or refs.");
        if (intended.Tree.Sha != journal.IntendedTree) throw new PublishingBlock($"Retained intended commit has a different tree.\nCommit: {intended.Sha}\nJournal Git tree: {journal.IntendedTree}\nCommit Git tree: {intended.Tree.Sha}\nKeep Staging/current.json and Repository; compare this inconsistent intent with trusted operation evidence before recovery. Do not edit the journal to make it pass.");
        if (advancing ? repo.ObjectDatabase.FindMergeBase(baseline, intended)?.Id != baseline.Id :
            intended.Parents.Count() != 1 || intended.Parents.Single().Id != baseline.Id)
            throw new PublishingBlock($"Retained {journal.Phase} history is inconsistent.\nBase commit: {baseline.Sha}\nIntended commit: {intended.Sha}\nRequired relationship: {(advancing ? "base must be an ancestor of intended" : "intended must have exactly one parent, the base")}\nKeep Repository and Staging/current.json; have the repository owner compare these commits' ancestry before recovery or discard.");
        ValidateTree(baseline.Tree);
        ValidateTree(intended.Tree);
        var before = Blobs(baseline.Tree).ToDictionary(x => x.Path, x => x.Blob, StringComparer.Ordinal);
        var after = Blobs(intended.Tree).ToDictionary(x => x.Path, x => x.Blob, StringComparer.Ordinal);
        var changed = before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).Where(path =>
            before.GetValueOrDefault(path)?.Id != after.GetValueOrDefault(path)?.Id || baseline.Tree[path]?.Mode != intended.Tree[path]?.Mode)
            .Order(StringComparer.Ordinal).ToArray();
        if (!changed.SequenceEqual(journal.Preimages.Keys.Order(StringComparer.Ordinal)))
            throw new PublishingBlock("Rollback path ownership differs from the intended commit.\nChanged Git paths missing from Preimages:\n" + string.Join("\n", changed.Except(journal.Preimages.Keys, StringComparer.Ordinal)) +
                "\nPreimages not in the Git change set:\n" + string.Join("\n", journal.Preimages.Keys.Except(changed, StringComparer.Ordinal).Order(StringComparer.Ordinal)) +
                $"\nCompare base commit {baseline.Sha} and intended commit {intended.Sha} with Staging/current.json; retain all rollback files and do not discard or invent missing preimages.");
        if (!advancing && changed.Any(x => !Owned(x))) throw new PublishingBlock("Replacement intent changes paths outside generator ownership:\n" + string.Join("\n", changed.Where(x => !Owned(x))) + "\nKeep Repository and Staging/current.json; have the application maintainer compare this commit intent with the generator's allowed paths before recovery.");
        if (journal.IndexHash is null) throw new PublishingBlock("Staging/current.json has no rollback IndexHash. Keep Repository/.git/index and Staging/Rollback/index; establish the original index identity before recovery. Do not derive a replacement expected hash from untrusted backup bytes.");
        var backupIndexHash = PublishingStorage.FileHash(Path.Combine(storage.Rollback, "index"));
        if (backupIndexHash != journal.IndexHash) throw new PublishingBlock($"Staging/Rollback/index failed its captured identity check.\nExpected raw SHA-256: {journal.IndexHash}\nObserved raw SHA-256: {backupIndexHash}\nKeep both indexes and Staging/current.json; obtain trusted original index evidence before recovery or discard.");
        foreach (var path in changed)
        {
            // The durable preimage hash describes physical bytes proven at capture,
            // not baseline blob bytes. Recovery-time attributes may have changed;
            // never clean the backup again or derive its expected hash from itself.
            if ((journal.Preimages[path] is null) != !before.ContainsKey(path)) throw new PublishingBlock($"Preimage presence for {path} disagrees with base commit {baseline.Sha}.\nBase entry present: {before.ContainsKey(path)}\nPreimage hash present: {journal.Preimages[path] is not null}\nKeep Staging/current.json and Rollback/files/{path}; establish the original file presence from the base before recovery.");
            if (before.ContainsKey(path) && baseline.Tree[path].Mode is not (Mode.NonExecutableFile or Mode.ExecutableFile)) throw new PublishingBlock($"Base path {path} has unsupported Git mode {baseline.Tree[path].Mode}. Keep base commit {baseline.Sha} and Staging evidence; the repository owner must establish a regular-file rollback state before recovery.");
            if (after.ContainsKey(path) && intended.Tree[path].Mode is not (Mode.NonExecutableFile or Mode.ExecutableFile)) throw new PublishingBlock($"Intended path {path} has unsupported Git mode {intended.Tree[path].Mode}. Keep intended commit {intended.Sha} and Staging evidence; the repository owner must establish the intended regular-file state before recovery.");
            var intendedHash = after.TryGetValue(path, out var next) ? PublishingStorage.Hash(Bytes(next)) : null;
            if (journal.Files.GetValueOrDefault(path) != intendedHash) throw new PublishingBlock($"Candidate identity for {path} disagrees with intended commit {intended.Sha}.\nJournal raw SHA-256: {journal.Files.GetValueOrDefault(path) ?? "(absent)"}\nIntended blob raw SHA-256: {intendedHash ?? "(absent)"}\nKeep Staging/Candidate/{path} and Staging/current.json; compare the candidate with the intended Git blob before recovery, without rewriting expected hashes.");
            if (journal.Removed.Contains(path, StringComparer.Ordinal) != !after.ContainsKey(path)) throw new PublishingBlock($"Removal evidence for {path} disagrees with intended commit {intended.Sha}.\nListed in Removed: {journal.Removed.Contains(path, StringComparer.Ordinal)}\nPresent in intended tree: {after.ContainsKey(path)}\nKeep Staging/current.json; compare its removal list with this commit's tree before recovery or discard.");
            if (journal.Preimages[path] is { } hash)
            {
                var observed = PublishingStorage.FileHash(PublishingStorage.Resolve(Path.Combine(storage.Rollback, "files"), path));
                if (observed != hash) throw new PublishingBlock($"Rollback file Staging/Rollback/files/{path} failed its captured identity check.\nExpected raw SHA-256: {hash}\nObserved raw SHA-256: {observed}\nKeep this backup and Repository/{path}; obtain trusted original bytes matching the expected hash before recovery. Do not accept this backup by changing the journal.");
            }
        }
    }
    public bool Push(PublishingJournal journal)
    {
        using var repo = Open();
        EnsureClean(repo);
        if (journal.Configuration != settings.Identity) throw new PublishingBlock($"Pending commit {journal.Commit ?? "(missing)"} belongs to different repository/branch settings. Compare DocumentationPublishing:RepositoryUrl and :Branch with Staging/current.json's retained destination before attempting a push; keep the exact commit and do not discard it.");
        if (journal.Commit is null) throw new PublishingBlock("PendingPush has no Commit in Staging/current.json. Keep Repository and the journal; establish which exact publication commit exists locally/remotely before retry or cleanup.");
        if (repo.Head.Tip.Sha != journal.Commit) throw new PublishingBlock($"HEAD differs from the retained pending commit.\nPending commit: {journal.Commit}\nObserved HEAD commit: {repo.Head.Tip.Sha}\nHave the repository owner compare these commits and their remote reachability before pushing; do not reset or create a replacement commit.");
        var commit = repo.Lookup<Commit>(journal.Commit);
        var remote = Fetch(repo);
        if (remote.Id == commit.Id || repo.ObjectDatabase.FindMergeBase(commit, remote)?.Id == commit.Id) return true;
        if (repo.ObjectDatabase.FindMergeBase(commit, remote)?.Id != remote.Id)
            throw new PublishingBlock($"Remote branch {settings.Branch} and pending commit have incompatible history.\nPending commit: {commit.Sha}\nFetched remote commit: {remote.Sha}\nNeither contains the other. Keep this exact pending commit and Staging/current.json; have the repository owner compare the two histories and decide the publication destination outside the application before any further push. Blind retry, merge, rebase or force push is not a resolution.");
        if (!settings.CredentialsReady) throw new PublishingBlock(settings.MissingCredentials + $" Configure these server keys with repository-scoped Contents read/write access, then retry only retained commit {commit.Sha}. Settings are captured at application registration, not hot-swapped by editing a file.");
        repo.Network.Push(repo.Network.Remotes["origin"], $"{commit.Sha}:refs/heads/{settings.Branch}",
            new PushOptions { CredentialsProvider = Credentials });
        var confirmed = Fetch(repo);
        return confirmed.Id == commit.Id || repo.ObjectDatabase.FindMergeBase(commit, confirmed)?.Id == commit.Id;
    }
}
