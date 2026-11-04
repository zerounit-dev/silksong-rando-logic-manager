using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components.RoomAudit;

// Presentation scope only: no inventory union, preferred origin, or structural expansion.
internal static class AuditComparisons
{
    internal static IReadOnlyList<AuditOriginComparison> For(RoomAuditEvaluator evaluator,
        AuditCurrentAccess access)
    {
        if (!access.Unstarted.Facts.Inputs.AutoCollect || !access.Scope.AnyEntrance) return [];
        return access.Origins.Keys
            .Select(key => new AuditOriginComparison(key, evaluator.Starts.First(x => x.Key == key).Label, access.Origins[key]))
            .ToArray();
    }

    internal static string? AutomaticReason(RoomAuditEvaluator evaluator, Guid id, AuditCurrentAccess access)
    {
        var origins = access.Origins.Where(x => x.Value.Facts.Collected.Contains(id))
            .Select(x => evaluator.Starts.First(start => start.Key == x.Key).Label).ToArray();
        return origins.Length == 0 ? null : "Automatically collected from: " + string.Join("; ", origins);
    }
}
