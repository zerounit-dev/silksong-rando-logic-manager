using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components.RoomAudit;

// Display-only projection: retain the original item and full constraints for every action.
internal static class AuditRequirementGroups
{
    private static readonly string[] SettingKeys =
    [
        "setting:steel-soul", "setting:bellshrinesanity", "setting:bossanity",
        "setting:wishsanity", "setting:entrance-randomizer", "setting:advanced-knowledge"
    ];

    internal static bool IsFixedSetting(AuditCondition condition) => !condition.Manual && (condition switch
    {
        { Kind: "soul-mode", Key: "soul-mode", Value: 0 or 1 } => true,
        { Kind: "fact", Value: 0 or 1 } => SettingKeys.Contains(condition.Key, StringComparer.Ordinal),
        _ => false
    });

    internal static string ConditionClass(AuditCondition condition, AuditFactContext context) => condition.Matches(context)
        ? "audit-condition-met" : IsFixedSetting(condition) ? "audit-condition-setting-mismatch" : "audit-condition-missing";

}
