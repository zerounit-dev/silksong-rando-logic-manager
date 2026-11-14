namespace Silksong_Rando_Logic_Manager.Services;

// Immutable, bound scenario constraints. Value is a Boolean, a lower bound, or an Act bit set.
public sealed record AuditCondition(string Kind, string Key, string Label, int Value, bool Manual = false)
{
    public bool Matches(AuditFactContext context) => Kind switch
    {
        "level" => context.Level(Key) >= Value,
        "act" => (Value & (1 << (context.Inputs.Act - 1))) != 0,
        "soul-mode" => context.Inputs.SoulRandomizer == (Value == 1),
        _ => context.Has(Key) == (Value == 1)
    };
    public string Summary => Kind switch
    {
        "level" => $"{Label} ≥ {RoomAuditEvaluator.DifficultyNames[Math.Clamp(Value, 0, 4)]}",
        "act" => "Act " + string.Join(" / ", Enumerable.Range(1, 3).Where(x => (Value & (1 << (x - 1))) != 0).Select(x => new[] { "I", "II", "III" }[x - 1])),
        "soul-mode" => $"Soul Randomizer {(Value == 1 ? "On" : "Off")}",
        _ => $"{Label}{(Key.StartsWith("setting:", StringComparison.Ordinal) ? Value == 1 ? " On" : " Off" : "")}"
    };
}
public sealed record AuditAnalysisIssue(AuditRow Row, string Reason);
