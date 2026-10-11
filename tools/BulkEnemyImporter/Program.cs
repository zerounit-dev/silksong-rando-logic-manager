using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Silksong_Rando_Logic_Manager.Services;

return await BulkEnemyOperator.RunAsync(args);

internal static class BulkEnemyOperator
{
    internal static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var logs = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true)
            .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace));
        var logger = logs.CreateLogger("BI1.Operator");
        try
        {
            var options = Options.Parse(args);
            var input = options.Path("input");
            var database = options.Path("database");
            var planPath = options.Path("plan");
            ExternalArtifact(planPath, input, database);
            var service = new BulkEnemyIngestService(database, logs.CreateLogger<RequirementValidationService>());
            if (options.Command == "plan")
            {
                var reportPath = options.Path("report");
                ExternalArtifact(reportPath, input, database);
                Distinct(planPath, reportPath);
                if (File.Exists(planPath) || File.Exists(reportPath)) throw new ArgumentException("Plan/report must be new artifact paths.");
                var plan = await service.PlanAsync(input, cancellation.Token);
                var bytes = BulkEnemyIngestService.PlanBytes(plan);
                var digest = BulkEnemyIngestService.Digest(bytes);
                await WriteNewAsync(planPath, bytes, cancellation.Token);
                await WriteNewAsync(reportPath, Encoding.UTF8.GetBytes(Report(plan, digest, planPath)), cancellation.Token);
                Console.WriteLine($"PLAN only. SHA256 {digest}\nMachine plan: {planPath}\nReport: {reportPath}\nBlockers: {plan.Blockers}. No database rows written.");
                return plan.Blockers == 0 ? 0 : 1;
            }

            if (!options.Flag("no-concurrent-editing"))
                throw new ArgumentException("Apply requires --no-concurrent-editing: operator must prevent editing for the entire operation. The tool does not stop or lock the app.");
            var approved = options.Value("approved-sha256");
            var approvedBytes = await File.ReadAllBytesAsync(planPath, cancellation.Token);
            var approvedDigest = BulkEnemyIngestService.Digest(approvedBytes);
            if (!string.Equals(approved, approvedDigest, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Explicit approved SHA256 does not match this artifact; no writes allowed.");
            var approvedPlan = JsonSerializer.Deserialize<BulkEnemyPlan>(approvedBytes, BulkEnemyIngestService.ArtifactJson)
                ?? throw new ArgumentException("Empty plan artifact.");
            if (!approvedBytes.AsSpan().SequenceEqual(BulkEnemyIngestService.PlanBytes(approvedPlan)))
                throw new ArgumentException("Plan is not the canonical generated artifact; regenerate and review.");
            if (approvedPlan.DatabasePath != database || approvedPlan.InputRoot != input)
                throw new ArgumentException("Explicit database/input paths differ from the approved plan; regenerate and review.");
            var receiptPath = options.Path("receipt");
            var backupDirectory = options.Path("backup-directory");
            ExternalArtifact(receiptPath, input, database);
            ExternalArtifact(backupDirectory, input, database);
            Distinct(planPath, receiptPath);
            if (!Directory.Exists(backupDirectory)) throw new DirectoryNotFoundException("Operator backup directory must already exist.");
            var backupPath = System.IO.Path.Combine(backupDirectory, $"BI1-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.db");
            Distinct(backupPath, receiptPath);
            using var receipt = new Receipt(receiptPath);
            receipt.Append(new { Event = "ApplyStarted", Utc = DateTime.UtcNow, PlanPath = planPath, ApprovedSha256 = approvedDigest,
                DatabasePath = database, InputRoot = input, approvedPlan.Blockers, NoConcurrentEditingAsserted = true,
                Recovery = "No retry/resume. Fresh plan and operator review required after any partial apply." });
            // Includes complete replan comparison and SQLite-consistent verified exact-target backup.
            BulkEnemyBackup backup;
            try { backup = await service.PrepareApplyAsync(approvedPlan, backupPath, cancellation.Token); }
            catch (Exception e)
            {
                receipt.Append(new { Event = "ApplyRejectedBeforeWrites", Utc = DateTime.UtcNow,
                    Error = e.ToString(), BackupAttemptPath = backupPath, Cancelled = e is OperationCanceledException });
                throw;
            }
            receipt.Append(new { Event = "BackupVerified", Utc = DateTime.UtcNow, Backup = backup });
            var failures = 0;
            var created = 0;
            var updated = 0;
            var reused = 0;
            var cancelled = false;
            var units = approvedPlan.Captures.Where(c => c.Outcome == "Covered" && c.Instructions.Count != 0)
                .Select(c => new Unit("Scene", c.Path, c.RoomId!.Value, ct => service.ApplySceneAsync(c, ct)))
                .Concat(approvedPlan.Placeholders.Select(p => new Unit("Placeholder", p.SceneName, p.RoomId, ct => service.ApplyPlaceholderAsync(p, ct))));
            foreach (var unit in units)
            {
                if (cancellation.IsCancellationRequested) { cancelled = true; break; }
                // Persist intent before entering the unit. Any append failure aborts further writes.
                receipt.Append(new { Event = "UnitStarted", Utc = DateTime.UtcNow, unit.Kind, unit.Identity, unit.RoomId });
                BulkEnemyUnitResult result;
                try
                {
                    result = await unit.Apply(cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    receipt.Append(new { Event = "UnitCancelledNotConfirmed", Utc = DateTime.UtcNow, unit.Kind, unit.Identity,
                        unit.RoomId, Guidance = "Uncommitted transaction is disposed. Inspect fresh durable state before recovery; earlier commits retained." });
                    break;
                }
                catch (BulkEnemyBaselineException e)
                {
                    // Global drift gate is not an independent unit failure. Stop the whole session.
                    receipt.Append(new { Event = "UnitRejectedBeforeTransaction", Utc = DateTime.UtcNow,
                        unit.Kind, unit.Identity, unit.RoomId, Error = e.Message });
                    throw;
                }
                catch (Exception e)
                {
                    failures++;
                    logger.LogError(e, "BI1 unit failed: {Kind} {Identity}; commit not confirmed", unit.Kind, unit.Identity);
                    receipt.Append(new { Event = "UnitFailedNotConfirmed", Utc = DateTime.UtcNow, unit.Kind, unit.Identity, unit.RoomId,
                        Error = e.ToString(), Guidance = "No automatic retry. Inspect fresh database/receipt before planning recovery." });
                    continue;
                }
                created += result.Created;
                updated += result.Updated;
                reused += result.Reused;
                receipt.Append(new { Event = "UnitCommitted", Utc = DateTime.UtcNow, unit.Kind, unit.Identity, unit.RoomId, Result = result });
            }
            receipt.Append(new { Event = "ApplyEnded", Utc = DateTime.UtcNow, Created = created, Updated = updated, Reused = reused,
                Failures = failures, Cancelled = cancelled, approvedPlan.Blockers,
                Success = !cancelled && failures == 0 && approvedPlan.Blockers == 0 });
            Console.WriteLine($"Confirmed commits: Created {created}, Updated {updated}, Reused {reused}. Failures {failures}; blockers {approvedPlan.Blockers}; cancelled {cancelled}. Receipt: {receiptPath}");
            return cancelled ? 130 : failures != 0 || approvedPlan.Blockers != 0 ? 1 : 0;
        }
        catch (ReceiptException e)
        {
            Console.Error.WriteLine($"RECEIPT FAILURE: no further writes. Last unit outcome may be uncertain in the receipt; SQLite and receipt are not atomic. Inspect durable state and backup, then obtain fresh plan approval. {e}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled. No further writes; prior unit commits/backup/artifacts retained. Inspect receipt and durable state before fresh planning.");
            return 130;
        }
        catch (Exception e)
        {
            logger.LogError(e, "BI1 operation stopped; do not rerun a stale plan");
            Console.Error.WriteLine("Operation stopped. No further writes. An incomplete receipt may have a started unit without outcome; inspect durable state. Recovery requires a fresh plan and review. See README for usage.");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await stream.WriteAsync(bytes, token);
        stream.Flush(flushToDisk: true);
    }

    private static string Report(BulkEnemyPlan p, string sha, string machinePath)
    {
        static string Text(string? value) => JsonSerializer.Serialize(value);
        var b = new StringBuilder();
        b.AppendLine("BI1 PLAN — NO DATABASE WRITES");
        b.AppendLine($"Machine artifact: {Text(machinePath)}\nApproved-artifact SHA256: {sha}\nDatabase: {Text(p.DatabasePath)}\nLogical baseline: {p.DatabaseFingerprint}\nInput: {Text(p.InputRoot)}");
        b.AppendLine($"Blockers: {p.Blockers}; uncertain coverage: {p.CoverageUncertain}");
        b.AppendLine($"Enemies planned Create: {p.Captures.Sum(c => c.Instructions.Count(i => i.Mode == SceneImportMode.Create))}, Update: {p.Captures.Sum(c => c.Instructions.Count(i => i.Mode == SceneImportMode.Update))}; missing placeholders: Create {p.Placeholders.Count(x => x.ExistingRecordId is null)}, Reuse {p.Placeholders.Count(x => x.ExistingRecordId is not null)}");
        b.AppendLine("Exclusions/policies: sceneLoad/manifest/all non-sceneReady files excluded; no new rooms, Exit/Check selection, dimensions/group/image-stale changes, images, seed/catalogue edits, deduplication, placeholder removal or authored-field overwrites. Normal resolver may update navigation metadata elsewhere. No concurrent editing during apply. Per-unit commits, no retry/resume; fresh review required after partial work.");
        b.AppendLine("\nCAPTURES (ordinal filesystem order; instructions preorder source traversal)");
        foreach (var c in p.Captures)
        {
            b.AppendLine($"{Text(c.Path)} SHA256={c.Sha256 ?? "unavailable"} scene={Text(c.SceneName)} outcome={c.Outcome} room={c.RoomId} error={Text(c.Error)}");
            foreach (var w in c.Warnings) b.AppendLine($"  WARNING {Text(w.HierarchyPath)}: {Text(w.Message)}");
            foreach (var i in c.Instructions) b.AppendLine($"  ordinal={i.Ordinal} hierarchy={Text(i.HierarchyPath)} source={Text(i.SourceName)} gameID={Text(i.GameId)} {i.Mode} target={i.ExistingRecordId} name={Text(i.FriendlyName)} Requirements={Text(i.Requirements)} Notes={Text(i.Notes)} incomingCount={i.IncomingOccurrences} durableMatches={i.DurableMatches} world={i.World}");
            if (c.Outcome == "Covered" && c.Instructions.Count == 0) b.AppendLine("  Covered, zero enemies: no import transaction.");
        }
        b.AppendLine("\nALL ACTIVE ROOM COVERAGE");
        foreach (var r in p.Rooms) b.AppendLine($"{r.RoomId} name={Text(r.FriendlyName)} scene={Text(r.InGameId)} {r.Outcome} {Text(r.Detail)}");
        b.AppendLine("\nMISSING PLACEHOLDER UNITS — Unknown import enemies, TODO=true, verified=false, blank Requirements/null parse status, no identity/coordinates, annotation=false");
        foreach (var x in p.Placeholders) b.AppendLine($"{x.RoomId} scene={Text(x.SceneName)} {(x.ExistingRecordId is null ? "Create" : "Reuse")} existing={x.ExistingRecordId}");
        b.AppendLine("\nEXCLUDED FILES (suffix not .sceneReady.json; not parsed)");
        foreach (var file in p.ExcludedFiles) b.AppendLine(Text(file));
        b.AppendLine("\nReview this report AND the exact machine artifact; explicitly supply its digest only after approval. This command never invokes apply.");
        return b.ToString();
    }

    private static void ExternalArtifact(string path, string input, string database)
    {
        static bool Within(string path, string directory) => path.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (Within(path, input) || Within(path, System.IO.Path.GetDirectoryName(database)!))
            throw new ArgumentException("Operator artifacts must be outside the input tree and live database directory.");
    }
    private static void Distinct(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Operator artifact paths must be distinct.");
    }
    private sealed record Unit(string Kind, string Identity, Guid RoomId, Func<CancellationToken, Task<BulkEnemyUnitResult>> Apply);

    private sealed class Receipt(string path) : IDisposable
    {
        private readonly FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        public void Append<T>(T value)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
                stream.Write(bytes);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }
            catch (Exception e) { throw new ReceiptException("Append-only receipt write/flush failed.", e); }
        }
        public void Dispose() => stream.Dispose();
    }
    private sealed class ReceiptException(string message, Exception inner) : Exception(message, inner);

    private sealed class Options(string command, Dictionary<string, string> values)
    {
        public string Command => command;
        public string Value(string name) => values.TryGetValue(name, out var value) && value.Length != 0 ? value : throw new ArgumentException($"Missing --{name}.");
        public string Path(string name) => System.IO.Path.GetFullPath(Value(name));
        public bool Flag(string name) => values.ContainsKey(name);
        public static Options Parse(string[] args)
        {
            if (args.Length == 0 || args[0] is not ("plan" or "apply")) throw new ArgumentException("Command must be plan or apply; see README.");
            var allowed = args[0] == "plan" ? new[] { "input", "database", "plan", "report" }
                : new[] { "input", "database", "plan", "approved-sha256", "backup-directory", "receipt", "no-concurrent-editing" };
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected named option.");
                var name = args[i][2..];
                if (!allowed.Contains(name) || values.ContainsKey(name)) throw new ArgumentException($"Unknown/duplicate option --{name}.");
                if (name == "no-concurrent-editing") { values.Add(name, "true"); continue; }
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for --{name}.");
                values.Add(name, args[i]);
            }
            return new(args[0], values);
        }
    }
}
