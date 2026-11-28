using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

internal static class ExplanationReferenceReader
{
    // Lexical only: each parser still owns operand validity, diagnostics and recovery.
    internal static (int End, bool Repeated, bool Balanced) ScanSuffix(string source, int start,
        Func<string, string, int, bool> wordAt)
    {
        var depth = 0;
        var repeated = false;
        var end = start;
        for (; end < source.Length; end++)
        {
            var character = source[end];
            if (depth == 0)
            {
                if (character == ')' || wordAt(source, "AND", end) || wordAt(source, "OR", end)) break;
                if (wordAt(source, "SEE", end)) repeated = true;
            }
            if (character == '(') depth++;
            else if (character == ')') depth--;
        }
        return (end, repeated, depth == 0);
    }

    // Use the caller's existing SQLite snapshot. Only the authored key is
    // needed for recognition; Name/Link/Notes never affect lookup.
    internal static IQueryable<RequirementExplanation> ActiveRows(LogicDbContext db) =>
        db.RequirementExplanations.AsNoTracking().Where(x => !x.IsArchived && !x.Room.IsArchived);

    internal static async Task<ILookup<Guid, string>> LoadAsync(LogicDbContext db, Guid? owningRoomId, CancellationToken token)
    {
        var rows = ActiveRows(db);
        if (owningRoomId is { } id) rows = rows.Where(x => x.RoomId == id);
        var references = await rows.Select(x => new { x.RoomId, x.ReferenceId }).ToArrayAsync(token);
        return ExplanationRules.IndexActiveReferences(references.Select(x => (x.RoomId, x.ReferenceId)));
    }
}
