namespace Silksong_Rando_Logic_Manager.Services;

public static class SceneImportMatching
{
    public static SceneImportObjectMatch MatchObject(SceneDumpObject node, IEnumerable<SceneImportSelectableRecord> transitions,
        IEnumerable<SceneImportSelectableRecord> checks)
    {
        var gameId = SceneDumpIdentity.GameId(node);
        var matches = transitions.Concat(checks).Where(record => Matches(record.InGameId, gameId))
            .Select(record => new SceneImportRecordMatch(record.Id, record.RoomId, record.Classification)).ToArray();
        return new(node.Id, node.Classification, gameId, matches);
    }

    private static bool Matches(string? left, string? right) => !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
