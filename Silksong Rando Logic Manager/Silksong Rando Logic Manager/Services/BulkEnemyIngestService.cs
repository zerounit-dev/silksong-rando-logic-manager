using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed record BulkEnemyInstruction(int Ordinal, string HierarchyPath, string SourceName, string? GameId,
    SceneImportMode Mode, Guid? ExistingRecordId, string FriendlyName, string Requirements, string Notes,
    int IncomingOccurrences, int DurableMatches, ScenePlacementWorldAvailability World);
public sealed record BulkEnemyCapturePlan(string Path, string? Sha256, string? SceneName, string Outcome,
    Guid? RoomId, string? Error, IReadOnlyList<SceneDumpWarning> Warnings, IReadOnlyList<BulkEnemyInstruction> Instructions)
{
    public int CreateCount => Instructions.Count(i => i.Mode == SceneImportMode.Create);
    public int UpdateCount => Instructions.Count(i => i.Mode == SceneImportMode.Update);
}
public sealed record BulkEnemyRoomCoverage(Guid RoomId, string FriendlyName, string? InGameId, string Outcome, string? Detail);
public sealed record BulkEnemyPlaceholder(Guid RoomId, string SceneName, Guid? ExistingRecordId);
public sealed record BulkEnemyPlan(int Format, string DatabasePath, string InputRoot, string DatabaseFingerprint,
    IReadOnlyList<string> ExcludedFiles, IReadOnlyList<BulkEnemyCapturePlan> Captures,
    IReadOnlyList<BulkEnemyRoomCoverage> Rooms, IReadOnlyList<BulkEnemyPlaceholder> Placeholders,
    bool CoverageUncertain, int Blockers)
{
    public int EnemyCreateCount => Captures.Sum(c => c.CreateCount);
    public int EnemyUpdateCount => Captures.Sum(c => c.UpdateCount);
    public int PlaceholderCreateCount => Placeholders.Count(p => p.ExistingRecordId is null);
    public int PlaceholderReuseCount => Placeholders.Count(p => p.ExistingRecordId is not null);
    public IReadOnlyList<string> PolicyExclusions =>
    [
        "Non-sceneReady files are not parsed; sceneLoad and map manifests excluded.",
        "No new rooms or dump Exit/Check selection; inactive/repeated Enemy actors retained, not deduplicated.",
        "No room dimension/image-stale/group changes or generated images.",
        "No authored Update fields overwritten; covered-room placeholders are not removed or changed.",
        "No seed/catalogue/schema/migration/refresh/inverse changes.",
        "No automatic retry/resume/restore/delete/batch rollback; operator prevents concurrent editing."
    ];
}
public sealed record BulkEnemyBackup(string Path, string Sha256, string DatabaseFingerprint);
public sealed record BulkEnemyUnitResult(int Created, int Updated, int Reused, IReadOnlyList<Guid> RecordIds);
public sealed class BulkEnemyBaselineException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One-off operational workflow. Owns all SQLite/domain access; no web host or migrations.</summary>
public sealed class BulkEnemyIngestService
{
    public static readonly JsonSerializerOptions ArtifactJson = new() { WriteIndented = true };
    private readonly string databasePath;
    private readonly BulkEnemyContextFactory contexts;
    private readonly SceneImportService importer;
    private BulkEnemyPlan? preparedPlan;
    private bool firstWriteChecked;

    public BulkEnemyIngestService(string databasePath, ILogger<RequirementValidationService> validationLogger)
    {
        this.databasePath = Path.GetFullPath(databasePath);
        contexts = new(this.databasePath);
        importer = new(contexts, new RequirementValidationService(contexts, validationLogger));
    }

    public static byte[] PlanBytes(BulkEnemyPlan plan) => JsonSerializer.SerializeToUtf8Bytes(plan, ArtifactJson);
    public static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public async Task<BulkEnemyPlan> PlanAsync(string inputRoot, CancellationToken token = default)
    {
        RequireSource();
        inputRoot = Path.GetFullPath(inputRoot);
        var paths = Inventory(inputRoot);
        var admitted = paths.Where(IsAdmitted).ToArray();
        await using var connection = BulkEnemyDatabase.Connection(databasePath, true);
        await connection.OpenAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: true);
        // The first fingerprint read pins the snapshot for every subsequent preview/coverage read.
        var fingerprint = BulkEnemyDatabase.Fingerprint(connection, transaction, token);
        await using var db = BulkEnemyDatabase.Context(connection);
        await db.Database.UseTransactionAsync(transaction, token);
        var rooms = await db.Rooms.AsNoTracking().Where(r => !r.IsArchived)
            .Select(r => new RoomIdentity(r.Id, r.FriendlyName, r.InGameId)).ToListAsync(token);
        rooms = rooms.OrderBy(r => r.Id).ToList();
        var retained = await db.CheckLocations.AsNoTracking().Where(c => !c.IsArchived && !c.Room.IsArchived)
            .Select(c => new { c.Id, c.Requirements, c.Notes }).ToDictionaryAsync(c => c.Id, token);
        var captures = new List<BulkEnemyCapturePlan>();
        foreach (var path in admitted)
        {
            token.ThrowIfCancellationRequested();
            var (review, sha, error) = await ReadCaptureAsync(path, token);
            if (review is null)
            {
                captures.Add(new(path, sha, null, "FatalInput", null, error, [], []));
                continue;
            }
            var matches = rooms.Where(r => Matches(r.InGameId, review.SceneName)).ToArray();
            var outcome = matches.Length switch { 0 => "Unmatched", 1 => "Covered", _ => "AmbiguousRooms" };
            var instructions = new List<BulkEnemyInstruction>();
            if (matches.Length == 1)
            {
                var preview = await SceneImportService.PreviewAsync(db, review, token);
                var projection = new SceneReviewProjectionService();
                var all = Flatten(review.RootObjects).ToArray();
                var counts = projection.CountCheckIdentities(all);
                var selected = all.Where(n => n.Classification == SceneDumpClassification.Enemy).ToArray();
                var drafts = selected.ToDictionary(n => n.Id, n => projection.CreateDraft(n, preview, matches[0].Id, counts));
                projection.AllocateNames(selected, preview, matches[0].Id, drafts);
                for (var ordinal = 0; ordinal < all.Length; ordinal++)
                {
                    var node = all[ordinal];
                    if (!drafts.TryGetValue(node.Id, out var draft)) continue;
                    var id = SceneDumpIdentity.GameId(node);
                    instructions.Add(new(ordinal, node.HierarchyPath, node.Name, id, draft.Mode, draft.ExistingRecordId,
                        draft.FriendlyName, draft.Mode == SceneImportMode.Update ? retained[draft.ExistingRecordId!.Value].Requirements
                            : SceneEnemySpawnNotesResolver.EnemyRequirements(node),
                        draft.Mode == SceneImportMode.Update ? retained[draft.ExistingRecordId!.Value].Notes
                            : SceneEnemySpawnNotesResolver.EnemyNotes(node),
                        string.IsNullOrWhiteSpace(id) ? 0 : counts.GetValueOrDefault(id.Trim()),
                        preview.Checks.Count(c => c.RoomId == matches[0].Id && Matches(c.InGameId, id)), node.PlacementWorld));
                }
            }
            captures.Add(new(path, sha, review.SceneName, outcome, matches.Length == 1 ? matches[0].Id : null,
                matches.Length > 1 ? "Multiple active rooms match the embedded scene name." : null, review.Warnings, instructions));
        }
        // Distinct admitted paths claiming one scene conflict, even with equal hashes.
        var duplicates = captures.Where(c => c.SceneName is not null).GroupBy(c => c.SceneName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        captures = captures.Select(c => c.SceneName is not null && duplicates.Contains(c.SceneName.Trim())
            ? c with { Outcome = "ConflictingCaptures", Error = "Distinct admitted files claim this scene; no winner selected.", Instructions = [] }
            : c).ToList();
        var uncertain = captures.Any(c => c.Outcome == "FatalInput");
        var coverage = new List<BulkEnemyRoomCoverage>();
        var placeholders = new List<BulkEnemyPlaceholder>();
        foreach (var room in rooms)
        {
            string outcome;
            string? detail = null;
            if (string.IsNullOrWhiteSpace(room.InGameId)) outcome = "BlankSceneId";
            else if (rooms.Count(r => Matches(r.InGameId, room.InGameId)) != 1) outcome = "AmbiguousRooms";
            else
            {
                var corresponding = captures.Where(c => Matches(c.SceneName, room.InGameId)).ToArray();
                if (corresponding.Length != 0)
                    outcome = corresponding.Length == 1 && corresponding[0].Outcome == "Covered" ? "Covered" : "ConflictingCaptures";
                else if (uncertain) outcome = "MissingBlockedByUncertainCoverage";
                else
                {
                    var namesakes = await Namesakes(db, room.Id, token);
                    if (namesakes.Count > 1 || namesakes.Count == 1 && !RequiredShape(namesakes[0]))
                    {
                        outcome = "PlaceholderConflict";
                        detail = "Active exact-name import enemies row(s) do not uniquely match the required placeholder shape.";
                    }
                    else
                    {
                        outcome = namesakes.Count == 0 ? "MissingPlaceholderCreate" : "MissingPlaceholderReuse";
                        placeholders.Add(new(room.Id, room.InGameId!.Trim(), namesakes.SingleOrDefault()?.Id));
                    }
                }
            }
            coverage.Add(new(room.Id, room.FriendlyName, room.InGameId, outcome, detail));
        }
        var blockers = captures.Count(c => c.Outcome is "FatalInput" or "ConflictingCaptures" or "AmbiguousRooms")
            + coverage.Count(c => c.Outcome is "AmbiguousRooms" or "MissingBlockedByUncertainCoverage" or "PlaceholderConflict");
        return new(1, databasePath, inputRoot, fingerprint, paths.Where(p => !IsAdmitted(p)).ToArray(), captures, coverage, placeholders, uncertain, blockers);
    }

    /// <summary>Approval is CLI-owned; this gate validates its contents and takes the actual-target backup.</summary>
    public async Task<BulkEnemyBackup> PrepareApplyAsync(BulkEnemyPlan plan, string backupPath, CancellationToken token = default)
    {
        if (preparedPlan is not null) throw new InvalidOperationException("An apply session cannot be prepared twice.");
        if (plan.Format != 1 || !string.Equals(databasePath, plan.DatabasePath, StringComparison.Ordinal))
            throw new InvalidOperationException("Wrong target/plan format; generate a new plan for this exact path.");
        var current = await PlanAsync(plan.InputRoot, token);
        if (!PlanBytes(current).AsSpan().SequenceEqual(PlanBytes(plan)))
            throw new InvalidOperationException("Inputs, database or planned instructions changed; regenerate and review a new plan.");
        if (plan.Captures.Any(c => c.Sha256 is null))
            throw new InvalidOperationException("An admitted input could not be hashed; exact input binding cannot be confirmed. Resolve that blocker before apply.");
        backupPath = Path.GetFullPath(backupPath);
        if (string.Equals(backupPath, databasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backup must be distinct from the source database.");
        RequireSource();
        // Reserve a new operator artifact; never replace any previous backup.
        using (new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        await using (var source = BulkEnemyDatabase.Connection(databasePath, true))
        await using (var destination = BulkEnemyDatabase.Connection(backupPath, false))
        {
            await source.OpenAsync(token);
            await destination.OpenAsync(token);
            token.ThrowIfCancellationRequested();
            source.BackupDatabase(destination);
        }
        string backupFingerprint;
        await using (var backup = BulkEnemyDatabase.Connection(backupPath, true))
        {
            await backup.OpenAsync(token);
            await using var transaction = backup.BeginTransaction(deferred: true);
            using var command = backup.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA integrity_check";
            using (var reader = await command.ExecuteReaderAsync(token))
            {
                var results = new List<string>();
                while (await reader.ReadAsync(token)) results.Add(reader.GetString(0));
                if (results.Count != 1 || results[0] != "ok") throw new InvalidOperationException("Actual-target backup failed integrity_check.");
            }
            backupFingerprint = BulkEnemyDatabase.Fingerprint(backup, transaction, token);
        }
        if (backupFingerprint != plan.DatabaseFingerprint)
            throw new InvalidOperationException("Backup differs from the approved baseline; no writes allowed. Regenerate the plan.");
        var sha = await FileHashAsync(backupPath, token);
        preparedPlan = plan;
        return new(backupPath, sha, backupFingerprint);
    }

    public async Task<BulkEnemyUnitResult> ApplySceneAsync(BulkEnemyCapturePlan unit, CancellationToken token = default)
    {
        var plan = preparedPlan ?? throw new InvalidOperationException("Verified backup and plan preparation required.");
        if (!plan.Captures.Contains(unit) || unit.Outcome != "Covered" || unit.RoomId is null || unit.Instructions.Count == 0)
            throw new InvalidOperationException("Scene is not an admitted nonempty approved unit.");
        var (review, sha, error) = await ReadCaptureAsync(unit.Path, token);
        if (review is null || sha != unit.Sha256 || review.SceneName != unit.SceneName)
            throw new InvalidOperationException($"Capture changed or cannot be read; regenerate plan. {error}");
        var all = Flatten(review.RootObjects).ToArray();
        var instructions = new List<SceneImportInstruction>();
        foreach (var instruction in unit.Instructions)
        {
            if (instruction.Ordinal < 0 || instruction.Ordinal >= all.Length) throw new InvalidOperationException("Invalid approved traversal ordinal.");
            var node = all[instruction.Ordinal];
            if (node.Classification != SceneDumpClassification.Enemy || node.HierarchyPath != instruction.HierarchyPath ||
                node.Name != instruction.SourceName || SceneDumpIdentity.GameId(node) != instruction.GameId ||
                instruction.Mode == SceneImportMode.Create &&
                    (SceneEnemySpawnNotesResolver.EnemyRequirements(node) != instruction.Requirements ||
                     SceneEnemySpawnNotesResolver.EnemyNotes(node) != instruction.Notes))
                throw new InvalidOperationException("Approved source traversal no longer maps to the Enemy object.");
            instructions.Add(new(node.Id, instruction.Mode, instruction.ExistingRecordId, instruction.FriendlyName));
        }
        await BeforeFirstWriteAsync(token);
        var result = await importer.ImportEnemiesIntoExistingRoomAsync(review, unit.RoomId.Value, instructions, token);
        return new(result.CheckIds.Count, instructions.Count(i => i.Mode == SceneImportMode.Update), 0, result.CheckIds
            .Concat(instructions.Where(i => i.Mode == SceneImportMode.Update).Select(i => i.ExistingRecordId!.Value)).ToArray());
    }

    public async Task<BulkEnemyUnitResult> ApplyPlaceholderAsync(BulkEnemyPlaceholder unit, CancellationToken token = default)
    {
        var plan = preparedPlan ?? throw new InvalidOperationException("Verified backup and plan preparation required.");
        if (!plan.Placeholders.Contains(unit)) throw new InvalidOperationException("Placeholder is not an approved unit.");
        await BeforeFirstWriteAsync(token);
        await using var db = contexts.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var rooms = await db.Rooms.AsNoTracking().Where(r => !r.IsArchived)
            .Select(r => new RoomIdentity(r.Id, r.FriendlyName, r.InGameId)).ToListAsync(token);
        var matching = rooms.Where(r => Matches(r.InGameId, unit.SceneName)).ToArray();
        if (matching.Length != 1 || matching[0].Id != unit.RoomId)
            throw new InvalidOperationException("Missing room is no longer the unique active scene-name match.");
        var namesakes = await Namesakes(db, unit.RoomId, token);
        if (namesakes.Count > 1 || namesakes.Count == 1 && (!RequiredShape(namesakes[0]) || namesakes[0].Id != unit.ExistingRecordId) ||
            namesakes.Count == 0 && unit.ExistingRecordId is not null)
            throw new InvalidOperationException("Placeholder recognition changed; no authored fields were overwritten.");
        if (namesakes.Count == 1)
        {
            await transaction.CommitAsync(token);
            return new(0, 0, 1, [namesakes[0].Id]);
        }
        var next = (await db.CheckLocations.Where(c => c.RoomId == unit.RoomId && !c.IsArchived)
            .Select(c => (int?)c.SortOrder).MaxAsync(token) ?? -1) + 1;
        var row = new CheckLocation
        {
            RoomId = unit.RoomId, FriendlyName = "import enemies", IsTodo = true, IsVerified = false,
            EnableAnnotation = false, SortOrder = next
        };
        db.CheckLocations.Add(row);
        await db.SaveChangesAsync(token);
        await new LogicReferenceResolver(db).ResolveAsync(token);
        // Blank Requirements have the normal null validation result; no authored timestamp rewrite.
        using (db.SuppressAuditMetadata()) await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(1, 0, 0, [row.Id]);
    }

    private async Task BeforeFirstWriteAsync(CancellationToken token)
    {
        try { await CheckBaselineBeforeFirstWriteAsync(token); }
        catch (Exception e) when (e is not OperationCanceledException && e is not BulkEnemyBaselineException)
        {
            throw new BulkEnemyBaselineException("Pre-write baseline could not be verified; stop and regenerate plan.", e);
        }
    }

    private async Task CheckBaselineBeforeFirstWriteAsync(CancellationToken token)
    {
        if (firstWriteChecked) return;
        var plan = preparedPlan!;
        RequireSource();
        // Recheck after backup and receipt preparation, immediately before the first unit transaction.
        var paths = Inventory(plan.InputRoot);
        if (!paths.SequenceEqual(plan.Captures.Select(c => c.Path).Concat(plan.ExcludedFiles).Order(StringComparer.Ordinal)))
            throw new BulkEnemyBaselineException("Input inventory changed after backup; regenerate plan.");
        foreach (var input in plan.Captures)
        {
            if (input.Sha256 is null || await FileHashAsync(input.Path, token) != input.Sha256)
                throw new BulkEnemyBaselineException("Input baseline cannot be confirmed after backup; regenerate plan.");
        }
        await using var connection = BulkEnemyDatabase.Connection(databasePath, true);
        await connection.OpenAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: true);
        if (BulkEnemyDatabase.Fingerprint(connection, transaction, token) != plan.DatabaseFingerprint)
            throw new BulkEnemyBaselineException("Database changed after backup; regenerate plan.");
        firstWriteChecked = true;
    }

    private void RequireSource()
    {
        if (!File.Exists(databasePath)) throw new FileNotFoundException("Selected source database is absent; it will not be created.", databasePath);
    }
    private static string[] Inventory(string inputRoot) => Directory.EnumerateFiles(inputRoot, "*", SearchOption.AllDirectories)
        .Select(Path.GetFullPath).Order(StringComparer.Ordinal).ToArray();
    private static bool IsAdmitted(string path) => path.EndsWith(".sceneReady.json", StringComparison.OrdinalIgnoreCase);
    private static bool Matches(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<SceneDumpObject> Flatten(IEnumerable<SceneDumpObject> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
    private static async Task<string> FileHashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
    private static async Task<(SceneDumpReview? Review, string? Sha, string? Error)> ReadCaptureAsync(string path, CancellationToken token)
    {
        string? sha = null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            stream.Position = 0;
            var review = await new SceneDumpParser().ParseAsync(stream, token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(review.SceneName)) throw new SceneDumpParseException("Embedded scene name is blank; coverage cannot be determined.");
            new SceneClassificationService().Apply(review);
            SceneFillerVisibility.Apply(review);
            return (review, sha, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SceneDumpParseException or SceneDumpTooLargeException)
        {
            return (null, sha, $"{e.GetType().Name}: {e.Message}");
        }
    }
    private static async Task<List<PlaceholderShape>> Namesakes(LogicDbContext db, Guid roomId, CancellationToken token) =>
        // SQLite's default Name collation is binary; filter again ordinal to make exact spelling explicit.
        (await db.CheckLocations.AsNoTracking().Where(c => c.RoomId == roomId && !c.IsArchived && c.FriendlyName == "import enemies")
            .Select(c => new PlaceholderShape(c.Id, c.FriendlyName, c.LocationType, c.IsTodo, c.IsVerified, c.Requirements,
                c.RequirementsParseSucceeded, c.InGameId, c.InGamePositionX, c.InGamePositionY, c.InGamePositionZ,
                c.LocalPositionX, c.LocalPositionY, c.LocalPositionZ, c.AnnotationSceneUnitX, c.AnnotationSceneUnitY, c.EnableAnnotation))
            .ToListAsync(token)).Where(c => c.Name == "import enemies").ToList();
    private static bool RequiredShape(PlaceholderShape c) => c.Type is null && c.Todo && c.Verified == false &&
        string.IsNullOrWhiteSpace(c.Requirements) && c.ParseSucceeded is null && c.GameId is null &&
        c.X is null && c.Y is null && c.Z is null && c.LocalX is null && c.LocalY is null && c.LocalZ is null &&
        c.AnnotationX is null && c.AnnotationY is null && !c.EnableAnnotation;
    private sealed record RoomIdentity(Guid Id, string FriendlyName, string? InGameId);
    private sealed record PlaceholderShape(Guid Id, string Name, string? Type, bool Todo, bool? Verified, string Requirements,
        bool? ParseSucceeded, string? GameId, double? X, double? Y, double? Z, double? LocalX, double? LocalY, double? LocalZ,
        double? AnnotationX, double? AnnotationY, bool EnableAnnotation);
}
