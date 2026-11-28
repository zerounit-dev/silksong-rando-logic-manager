using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;

namespace SilksongRandoLogicManager.Contracts;

public static class ExplanationRules
{
    public static StringComparer ReferenceComparer => StringComparer.OrdinalIgnoreCase;

    // Count records rather than normalized labels; two equal authored References
    // are ambiguous even when only one has a valid Link.
    public static ILookup<Guid, string> IndexActiveReferences(IEnumerable<(Guid RoomId, string ReferenceId)> rows) =>
        rows.Where(row => !string.IsNullOrWhiteSpace(row.ReferenceId)).ToLookup(row => row.RoomId, row => row.ReferenceId.Trim());

    public static bool Resolves(ILookup<Guid, string> activeReferences, Guid roomId, string reference) =>
        FindUniqueReference(activeReferences, roomId, reference) is not null;

    public static string? FindUniqueReference(ILookup<Guid, string> activeReferences, Guid roomId, string reference)
    {
        var key = reference.Trim();
        if (key.Length == 0) return null;
        string? match = null;
        foreach (var value in activeReferences[roomId])
        {
            if (!ReferenceComparer.Equals(value.Trim(), key)) continue;
            if (match is not null) return null;
            match = value;
        }
        return match;
    }

    public static V2Severity ReferenceSeverity(string value, IReadOnlyDictionary<string, int> counts) =>
        string.IsNullOrWhiteSpace(value) ? V2Severity.Warning
        : counts.GetValueOrDefault(value.Trim()) > 1 ? V2Severity.Danger : V2Severity.Neutral;

    public static Dictionary<string, int> ReferenceCounts(IEnumerable<string> activeReferences) =>
        activeReferences.Where(x => !string.IsNullOrWhiteSpace(x)).GroupBy(x => x.Trim(), ReferenceComparer)
            .ToDictionary(group => group.Key, group => group.Count(), ReferenceComparer);

    public static Uri? ParseLink(string link) =>
        Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) && uri.IsWellFormedOriginalString()
            && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host)
        ? uri : null;

    public static V2Severity LinkSeverity(string link) =>
        ParseLink(link) is not null ? V2Severity.Neutral : V2Severity.Danger;
}
