using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class SceneEvidenceStaleException : Exception;

public sealed partial class SceneImportService
{
    public async Task<SceneReviewEvidence> PreviewV3Async(ScenePreviewRequest request, CancellationToken token)
    {
        var owner = (await admission.AuthorAsync(token)).Owner;
        await using var db = await dbContextFactory.CreateDbContextAsync(token);
        await using var snapshot = await db.Database.BeginTransactionAsync(token);
        return await ReadV3EvidenceAsync(db, request.SceneName, request.IdentityKeys, request.DestinationKeys, owner, token);
    }

    private async Task<SceneReviewEvidence> ReadV3EvidenceAsync(LogicDbContext db, string sceneName,
        IReadOnlyList<string> keys, IReadOnlyList<string> destinationKeys, string owner, CancellationToken token)
    {
        // Exact retained comparison happens in .NET, not SQLite's ASCII-only NOCASE.
        var identities = await db.Rooms.AsNoTracking().Where(r => !r.IsArchived).Select(r => new { r.Id, r.InGameId }).ToListAsync(token);
        var ids = identities.Where(r => Matches(r.InGameId, sceneName)).Select(r => r.Id).ToArray();
        var rooms = await db.Rooms.AsNoTracking().Where(r => ids.Contains(r.Id)).OrderBy(r => r.Id)
            .Select(r => new { View = new SceneImportRoom(r.Id, r.FriendlyName, r.ReferenceId, r.InGameId, r.SceneMaxX, r.SceneMaxY)
            {
                SceneMinX = r.SceneMinX, SceneMinY = r.SceneMinY, ScaleX = r.SceneImagePixelsPerUnitX, ScaleY = r.SceneImagePixelsPerUnitY,
                OffsetX = r.SceneImagePixelOffsetX, OffsetY = r.SceneImagePixelOffsetY, UseSceneImage = r.UseSceneImage,
                IsStale = r.IsSceneImageStale, ImageVersion = r.UpdatedUtc.Ticks
            }, r.SortOrder, r.CreatedUtc, r.UpdatedUtc }).ToListAsync(token);
        var transitions = await db.RoomTransitions.AsNoTracking().Where(t => !t.IsArchived && ids.Contains(t.RoomId))
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Id).Select(t => new
            {
                View = new SceneImportSelectableRecord(t.Id, t.RoomId, SceneDumpClassification.Exit, t.FriendlyName, t.InGameId)
                { X = t.InGamePositionX, Y = t.InGamePositionY, Z = t.InGamePositionZ },
                t.SortOrder, t.CreatedUtc, t.UpdatedUtc
            }).ToListAsync(token);
        var enemyTypes = CheckLocationTypeCatalogue.Definitions.Where(t => t.Group == "enemies").Select(t => t.OutputValue).ToArray();
        var checks = await db.CheckLocations.AsNoTracking().Where(t => !t.IsArchived && ids.Contains(t.RoomId))
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Id).Select(t => new
            {
                View = new SceneImportSelectableRecord(t.Id, t.RoomId, SceneDumpClassification.Check, t.FriendlyName, t.InGameId)
                {
                    X = t.InGamePositionX, Y = t.InGamePositionY, Z = t.InGamePositionZ, LocationType = t.LocationType,
                    Requirements = enemyTypes.Contains(t.LocationType!) ? t.Requirements : string.Empty,
                    Notes = enemyTypes.Contains(t.LocationType!) ? t.Notes : string.Empty,
                    EnemyEquivalence = new(t.RequirementsParseSucceeded, t.IsTodo, t.IsVerified, t.EnableAnnotation,
                        t.AnnotationSceneUnitX, t.AnnotationSceneUnitY, t.SubroomReferenceText, t.ResolvedSubroomId,
                        t.LocalPositionX, t.LocalPositionY, t.LocalPositionZ)
                }, t.SortOrder, t.CreatedUtc, t.UpdatedUtc
            }).ToListAsync(token);
        using var hash = SHA256.Create();
        await using (var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write, leaveOpen: true))
        {
            await JsonSerializer.SerializeAsync(stream, new { Owner = owner, Rooms = rooms, Transitions = transitions, Checks = checks }, cancellationToken: token);
            await stream.FlushFinalBlockAsync(token);
        }
        var fingerprint = Convert.ToHexString(hash.Hash!);
        var foreign = new List<SceneForeignMatch>();
        if (keys.Count > 0)
        {
            // Stream only scalar identities. Matching must use the retained .NET
            // trim/OrdinalIgnoreCase algebra, not SQLite's ASCII-only NOCASE.
            var submitted = keys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await foreach (var t in db.RoomTransitions.AsNoTracking().Where(t => !t.IsArchived && !t.Room.IsArchived && !ids.Contains(t.RoomId) && t.InGameId != null)
                .Select(t => new { t.InGameId, t.Id, t.RoomId }).AsAsyncEnumerable().WithCancellation(token))
                if (submitted.Contains(t.InGameId!.Trim())) foreign.Add(new(t.InGameId, t.Id, t.RoomId, SceneDumpClassification.Exit));
            await foreach (var t in db.CheckLocations.AsNoTracking().Where(t => !t.IsArchived && !t.Room.IsArchived && !ids.Contains(t.RoomId) && t.InGameId != null)
                .Select(t => new { t.InGameId, t.Id, t.RoomId }).AsAsyncEnumerable().WithCancellation(token))
                if (submitted.Contains(t.InGameId!.Trim())) foreign.Add(new(t.InGameId, t.Id, t.RoomId, SceneDumpClassification.Check));
        }
        var names = await db.RequirementSouls.AsNoTracking().Select(s => new SceneImportEnemyName(s.Id, s.OutputValue, s.Name)).ToListAsync(token);
        var roomViews = rooms.Select(r => r.View with { ImageUrl = r.View.ImageEligible && sceneImages?.Exists(r.View.Id) == true
            ? $"/data/scenes/{r.View.Id:D}.webp?v={r.View.ImageVersion}" : null }).ToArray();
        return new(new(roomViews, [], transitions.Select(t => t.View).ToArray(), checks.Select(c => c.View).ToArray(), names),
            foreign.Distinct().ToArray(), fingerprint, await ReadDestinationsAsync(db, destinationKeys, token));
    }

    private static async Task<SceneDestinationObservation> ReadDestinationsAsync(LogicDbContext db, IReadOnlyList<string> keys, CancellationToken token)
    {
        var all = await db.Rooms.AsNoTracking().Where(r => !r.IsArchived).OrderBy(r => r.Id)
            .Select(r => new SceneDestinationRoom(r.Id, r.InGameId, r.ReferenceId)).ToListAsync(token);
        var rooms = all.Where(r => keys.Any(k => Matches(r.InGameId, k))).ToArray();
        var ids = rooms.Select(r => r.Id).ToArray();
        var transitions = await db.RoomTransitions.AsNoTracking().Where(t => !t.IsArchived && ids.Contains(t.RoomId))
            .OrderBy(t => t.Id).Select(t => new SceneDestinationTransition(t.Id, t.RoomId, t.InGameId, t.Alias)).ToArrayAsync(token);
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Rooms = rooms, Transitions = transitions })));
        return new(keys, rooms, transitions, fingerprint);
    }
}
