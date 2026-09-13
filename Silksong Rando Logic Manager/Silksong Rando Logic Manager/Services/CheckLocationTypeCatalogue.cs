namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>The single application-owned catalogue for persisted check-location type values.</summary>
public static class CheckLocationTypeCatalogue
{
    private static readonly IReadOnlyList<CheckLocationTypeDefinition> definitions = Array.AsReadOnly(
    new CheckLocationTypeDefinition[]
    {
        new("collectible", "Collectible", "items", "A persistent acquisition, rescue, upgrade, or reward received by the player, regardless of whether it is found, purchased, earned, or granted.", "Rosary string, shard cache, tool or skill spot, shop item, needle upgrade, rescued flea, wish reward", 0, true),
        new("boss", "Boss", "enemies", "An individually tracked, non-respawning enemy formally considered a boss.", "Moss Mother, Fourth Chorus", 1, true),
        new("miniboss", "Miniboss", "enemies", "An individually tracked, non-respawning enemy not formally considered a boss.", "Craggler, Pilgrim's Rest Rhinogrund", 2, true),
        new("gauntlet", "Gauntlet", "enemies", "A combat challenge consisting of multiple enemies, waves, or an encounter as a whole rather than one individually tracked enemy.", "Underworks arena fight, Whiteward Sherma gauntlet", 3, true),
        new("enemy", "Enemy", "enemies", "An individually tracked enemy that respawns.", "A respawning room enemy, a respawning enemy used as a logic target", 4, false),
        new("resource", "Resource", "items", "A repeatable source of an item or currency tied to an area or spot. The resource opportunity, rather than a particular supplying enemy, is what the row represents.", "Silk, shell shards, rosaries, wish items", 5, false),
        new("switch", "Switch", "environment", "A triggerable physical or environmental actuator tracked independently for logic or room randomization.", "Lever, pressure plate, door switch, elevator switch, interactable mechanism", 6, true),
        new("blockade", "Blockade", "environment", "A persistent, non-switch-controlled obstruction that can be removed or passed through an appropriate interaction.", "Silk blockade, breakable wall, breakable vines, blast-rock obstruction", 7, true),
        new("lock", "Lock", "environment", "A persistent lock whose unlocking is itself the tracked location, normally requiring a key, currency, or another consumable.", "Keyed door, rosary lock, paid gate", 8, true),
        new("bench", "Bench", "environment", "A bench or the persistent availability of a bench facility. It remains a Bench even when payment or another prerequisite is required.", "Whiteward Bench, paid bench unlock", 9, true),
        new("travel", "Travel", "environment", "A travel-network access point or the persistent availability of a travel facility. It remains Travel even when payment or another prerequisite is required.", "Bellway access point, Ventrica access point", 10, true),
        new("lore", "Lore", "environment", "A readable or inspectable world spot whose interaction conveys lore or knowledge rather than awarding a collectible.", "Lore tablet, plaque, inscription, readable record", 11, false),
        new("event", "Event", "other", "A persistent progression or world-state milestone that has no more specific category.", "Wish start, wish progress, wish completion, rewardless turn-in", 12, true),
        new("logic-point", "Logic Point", "other", "A virtual, geographically anchored reference used by logic. Reaching or using it does not inherently imply persistent state, an acquisition, combat completion, or a farmable resource.", "Temporary spawn position, location-only prerequisite, virtual room reference marker", 13, false)
    });

    public static IReadOnlyList<CheckLocationTypeDefinition> Definitions => definitions;

    public static bool IsRecognized(string? outputValue) => outputValue is not null &&
        definitions.Any(definition => string.Equals(definition.OutputValue, outputValue, StringComparison.Ordinal));
}

public sealed record CheckLocationTypeDefinition(
    string OutputValue,
    string Name,
    string Group,
    string Description,
    string Examples,
    int SortOrder,
    bool HasPersistedState);
