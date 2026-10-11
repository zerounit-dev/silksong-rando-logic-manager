using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

// Invocation-local, read-only scalar lookup. SQLite/JavaScript case conversion
// must not replace the approved .NET comparison (including Unicode whitespace).
public sealed class ArchipelagoRoomLookup(IDbContextFactory<LogicDbContext> contexts)
{
    public static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static bool Same(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    public async Task<Guid?> FindAsync(string value)
    {
        var key = Normalize(value);
        if (key is null) return null;
        await using var db = await contexts.CreateDbContextAsync();
        var candidates = await db.Rooms.AsNoTracking().Where(room => !room.IsArchived && room.InGameId != null)
            .Select(room => new { room.Id, room.InGameId }).ToListAsync();
        var matches = candidates.Where(room => Same(room.InGameId, key)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Id : null;
    }
}
