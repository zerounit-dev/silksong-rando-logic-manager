using System.Security.Cryptography;
using System.Text;
using LibGit2Sharp;
using Silksong_Rando_Logic_Manager.Authentication;

namespace Silksong_Rando_Logic_Manager.Services.Publishing;

public sealed partial class DocumentationPublishingService
{
    private PublishingRoomReview? roomReview;
    private string? reviewError;
    private long reviewAdmissionVersion;

    private (PublishingRoomReview? Review, string? Error) BuildReview(Repository repo, PublishingJournal current,
        IReadOnlyList<PublishingChange> delta, PublishingTimingScope ready)
    {
        try
        {
            return (ready.Measure(PublishingMetric.ResponseBuild, () =>
            {
                var tree = repo.Lookup<Commit>(current.BaseCommit).Tree;
                byte[]? Old(string path) => tree[path]?.Target is Blob blob ? PublishingGit.Bytes(blob) : null;
                byte[]? New(string path) => current.Files.TryGetValue(path, out var expected) ? ReadVerifiedCandidate(path, expected) : null;
                const string geometry = "room_geometry_data.json", index = "README.md";
                var hasGeometry = delta.Any(x => PublishingRoomReview.GeometryId(x.Path) is not null);
                var oldGeometry = hasGeometry ? PublishingRoomReview.GeometryRooms(Old(geometry), Old(index), path => tree[path]?.Target is Blob) : [];
                var newGeometry = hasGeometry ? PublishingRoomReview.GeometryRooms(New(geometry), New(index), current.Files.ContainsKey) : [];
                var files = new List<PublishingReviewFile>(delta.Count);
                var locations = new Dictionary<int, (string Zone, string Slug)>();
                foreach (var item in delta)
                {
                    var entry = files.Count;
                    var text = PublishingTextDiff.IsTextPath(item.Path) ? PublishingTextDiff.Compare(Old(item.Path), New(item.Path), ready) : null;
                    var oldBlob = item.Kind == "Image" && item.Change != "Added" ? ((Blob)tree[item.Path].Target).Id.Sha : null;
                    var newHash = item.Kind == "Image" && item.Change != "Removed" ? current.Files[item.Path] : null;
                    files.Add(new(entry, item, text, oldBlob, newHash));
                    if (PublishingRoomReview.RoomPath(item.Path) is { } room) locations.Add(entry, room);
                    else if (PublishingRoomReview.GeometryId(item.Path) is { } guid)
                    {
                        bool oldKnown = oldGeometry.TryGetValue(guid, out var before);
                        bool newKnown = newGeometry.TryGetValue(guid, out var after);
                        if (oldBlob is null && newKnown) locations.Add(entry, after);
                        else if (newHash is null && oldKnown) locations.Add(entry, before);
                        // Conflicting or incomplete two-sided attribution must not imply historical identity.
                        else if (oldKnown && newKnown && before == after) locations.Add(entry, after);
                    }
                }
                return new PublishingRoomReview(files, locations);
            }), null);
        }
        catch (Exception ex)
        {
            // Review preparation is display-only, not a new commit or recovery invariant.
            logger.LogError(ex, "Building the prepared publication review failed; the exact delta remains available.");
            return (null, "The detailed review could not be built. The changed paths are listed below; see server logs before publishing.");
        }
    }

    private byte[] ReadVerifiedCandidate(string path, string expected)
    {
        var source = PublishingStorage.Resolve(storage.Candidate, path);
        var attributes = File.GetAttributes(source);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new PublishingBlock($"Staging/Candidate/{path} is not an ordinary candidate file.");
        var captured = File.ReadAllBytes(source);
        if (PublishingStorage.Hash(captured) != expected)
            throw new PublishingBlock($"Staging/Candidate/{path} no longer matches its manifest. Preserve the retained evidence; compare it before discarding or preparing again.");
        return captured;
    }

    internal async Task<PublishingReviewRoom?> ReadReviewRoomAsync(LiveAccess access, Guid generation, long epoch, int room, PublishingTimings timings)
    {
        using var timing = timings.Begin("Prepared room review admission");
        await timing.MeasureAsync(PublishingMetric.Authorization, () => access.RequireAsync(AccessPolicies.PublishDocumentation));
        using var wait = timing.Segment(PublishingMetric.GateWait);
        lock (gate)
        {
            wait.Complete();
            access.RequireInsideSynchronousGate(AccessPolicies.PublishDocumentation);
            if (!IsCurrentReview(generation, epoch)) return null;
            if (busy) throw new PublishingBlock("Review is unavailable while publishing work is running. Wait for its result, then reopen the room.");
            var payload = roomReview!.Rooms.GetValueOrDefault(room);
            timing.Complete(); return payload;
        }
    }

    // The process-unique generation distinguishes even repeated candidate IDs.
    private bool IsCurrentReview(Guid generation, long epoch) => !stopping && initialization?.IsCompletedSuccessfully == true
        && epoch == reviewAdmissionVersion && phase == "Prepared" && journal is not null && roomReview?.Generation == generation;

    internal async Task<bool> CheckReviewAsync(LiveAccess access, Guid generation, long epoch)
    {
        await access.RequireAsync(AccessPolicies.PublishDocumentation);
        lock (gate)
        {
            access.RequireInsideSynchronousGate(AccessPolicies.PublishDocumentation);
            return IsCurrentReview(generation, epoch) && !busy;
        }
    }

    // A slot covers capture AND response. Waiting callers hold no file, buffer,
    // checkout handle or publisher gate. Large valid images use transient disk,
    // not a new published-artifact size policy or a proportional heap allocation.
    private readonly SemaphoreSlim imageSlots = new(2, 2);
    internal async Task<IResult> ServeReviewImageAsync(LiveAccess access, Guid generation, long epoch, int entry, string side, HttpContext context)
    {
        var token = context.RequestAborted;
        using var timing = CreatePreviewTimings().Begin("Prepared review binary image side");
        await timing.MeasureAsync(PublishingMetric.Authorization, () => access.RequireAsync(AccessPolicies.PublishDocumentation, token));
        string path, identity;
        string? commit;
        long length;
        using (var wait = timing.Segment(PublishingMetric.GateWait))
        lock (gate)
        {
            wait.Complete();
            token.ThrowIfCancellationRequested();
            access.RequireInsideSynchronousGate(AccessPolicies.PublishDocumentation);
            if (!IsCurrentReview(generation, epoch)) return Results.NotFound();
            if (busy) throw new PublishingBlock("Review images are unavailable while publishing work is running.");
            if (entry < 0 || entry >= roomReview!.Files.Count) return Results.NotFound();
            var file = roomReview.Files[entry];
            path = file.Change.Path;
            if (file.Change.Kind != "Image" || !PublishingGit.Owned(path)) return Results.NotFound();
            if (side == "before" && file.OldBlob is { } old)
                (commit, identity, length) = (journal!.BaseCommit, old, file.Change.BeforeBytes);
            else if (side == "after" && file.NewHash is { } expected && journal!.Files.GetValueOrDefault(path) == expected)
                (commit, identity, length) = (null, expected, file.Change.AfterBytes);
            else return Results.NotFound();
        }
        await imageSlots.WaitAsync(token);
        try
        {
            if (!StillAdmitted()) return Results.NotFound();
            var options = new FileStreamOptions
            {
                Access = FileAccess.ReadWrite, Mode = FileMode.CreateNew, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var snapshot = new FileStream(Path.Combine(Path.GetTempPath(), $"logic-publisher-image-{Guid.NewGuid():N}"), options);
            var png = path.EndsWith(".png", StringComparison.Ordinal);
            try
            {
                using var capture = timing.Segment(commit is null ? PublishingMetric.AfterRead : PublishingMetric.BeforeRead);
                if (commit is null)
                {
                    var source = PublishingStorage.Resolve(storage.Candidate, path);
                    if ((File.GetAttributes(source) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                        throw new PublishingBlock("The admitted candidate image is not an ordinary file.");
                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                    if (input.Length != length) throw new PublishingBlock("The candidate image length no longer matches the verified preparation.");
                    await CaptureImageAsync(input, snapshot, length, identity, false, png, token);
                }
                else await CaptureBaseImageAsync(commit, path, identity, snapshot, length, png, token);
                capture.Complete();
            }
            catch
            {
                if (!StillAdmitted()) return Results.NotFound();
                throw;
            }
            if (!StillAdmitted()) return Results.NotFound();
            timing.InputBytes = timing.OutputBytes = length;
            snapshot.Position = 0;
            context.Response.ContentType = png ? "image/png" : "image/webp";
            context.Response.ContentLength = length;
            await snapshot.CopyToAsync(context.Response.Body, 64 * 1024, token);
            timing.Complete();
            return Results.Empty;
        }
        finally { imageSlots.Release(); }

        bool StillAdmitted()
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                access.RequireInsideSynchronousGate(AccessPolicies.PublishDocumentation);
                return IsCurrentReview(generation, epoch) && !busy;
            }
        }
    }

    private static async Task CaptureImageAsync(Stream input, FileStream snapshot, long expectedLength,
        string expectedHash, bool gitBlob, bool png, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(gitBlob ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);
        if (gitBlob) hash.AppendData(Encoding.ASCII.GetBytes(FormattableString.Invariant($"blob {expectedLength}\0")));
        var buffer = new byte[64 * 1024];
        var remaining = expectedLength;
        bool first = true;
        while (remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            var read = await input.ReadAtLeastAsync(buffer.AsMemory(0, count), count, false, token);
            if (read != count) throw new PublishingBlock("The image is shorter than its verified review length.");
            if (first && !HasImageSignature(buffer.AsSpan(0, read), png))
                throw new PublishingBlock("The admitted file does not have the declared image format.");
            first = false;
            hash.AppendData(buffer, 0, read);
            await snapshot.WriteAsync(buffer.AsMemory(0, read), token);
            remaining -= read;
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), token) != 0)
            throw new PublishingBlock("The image is longer than its verified review length.");
        if (first || Convert.ToHexStringLower(hash.GetHashAndReset()) != expectedHash)
            throw new PublishingBlock("The image bytes no longer match the verified review side.");
    }
    private static bool HasImageSignature(ReadOnlySpan<byte> bytes, bool png) => png
        ? bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
        : bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8);

    private async Task CaptureBaseImageAsync(string commit, string path, string expected, FileStream snapshot, long length, bool png, CancellationToken token)
    {
        // Full checkout admission already happened at readiness; this is a
        // read-only lookup of the exact reviewed blob, not an Open/write path.
        var metadata = Path.Combine(storage.Repository, ".git");
        PublishingStorage.CheckPath(metadata, "Repository/.git");
        if (!Directory.Exists(metadata) || new[] { "commondir", "gitdir", "objects/info/alternates" }.Any(marker =>
            {
                var file = Path.Combine(metadata, marker);
                PublishingStorage.CheckPath(file, "Repository/.git/" + marker);
                return File.Exists(file) || Directory.Exists(file);
            })) throw new PublishingBlock("The retained Git base is no longer available in the owned object store.");
        foreach (var child in new[] { "objects", "objects/pack", "objects/" + expected[..2], "objects/" + expected[..2] + "/" + expected[2..] })
            PublishingStorage.CheckPath(Path.Combine(metadata, child), "Repository/.git/" + child);
        using var repo = new Repository(metadata);
        if (!string.Equals(Path.TrimEndingDirectorySeparator(repo.Info.Path), metadata, StringComparison.Ordinal)
            || repo.Lookup<Commit>(commit)?.Tree[path] is not { Target: Blob blob, Mode: Mode.NonExecutableFile or Mode.ExecutableFile }
            || blob.Id.Sha != expected)
            throw new PublishingBlock("The exact reviewed image is no longer available at the recorded Git base.");
        if (blob.Size != length) throw new PublishingBlock("The Git blob length does not match the verified review side.");
        using var input = blob.GetContentStream();
        await CaptureImageAsync(input, snapshot, length, expected, true, png, token);
    }
}
