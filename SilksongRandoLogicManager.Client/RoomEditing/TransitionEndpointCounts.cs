using static Silksong_Rando_Logic_Manager.Services.RoomPresentationRules;

namespace SilksongRandoLogicManager.Client.RoomEditing;

/// <summary>Editor matching is pairwise, not an equivalence relation. Disjoint
/// candidate ID-presence partitions each contribute exactly one projection count.</summary>
internal sealed class TransitionEndpointCounts
{
    private readonly record struct Endpoint(Guid? FirstId, string? FirstText, Guid? SecondId, string? SecondText);
    private sealed class EndpointComparer : IEqualityComparer<Endpoint>
    {
        public bool Equals(Endpoint a, Endpoint b) => a.FirstId == b.FirstId && a.SecondId == b.SecondId
            && StringComparer.OrdinalIgnoreCase.Equals(a.FirstText, b.FirstText)
            && StringComparer.OrdinalIgnoreCase.Equals(a.SecondText, b.SecondText);
        public int GetHashCode(Endpoint value) => HashCode.Combine(value.FirstId, value.SecondId,
            value.FirstText is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(value.FirstText),
            value.SecondText is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(value.SecondText));
    }
    // Masks: 1 = first ID, 2 = second ID. Exactly 1+2+2+4 maps.
    private readonly Dictionary<Endpoint, int>?[,] maps = new Dictionary<Endpoint, int>?[4, 4];
    public TransitionEndpointCounts()
    {
        for (var partition = 0; partition < 4; partition++)
            for (var projection = 0; projection < 4; projection++)
                if ((projection & partition) == projection) maps[partition, projection] = new(new EndpointComparer());
    }
    public void Add(string? first, Guid? firstId, string? second, Guid? secondId)
    {
        var partition = Presence(firstId, secondId);
        for (var projection = 0; projection < 4; projection++)
        {
            if (maps[partition, projection] is not { } map || !TryKey(projection, first, firstId, second, secondId, out var key)) continue;
            map[key] = map.GetValueOrDefault(key) + 1;
        }
    }
    public int Count(string? first, Guid? firstId, string? second, Guid? secondId)
    {
        var query = Presence(firstId, secondId);
        var count = 0;
        for (var partition = 0; partition < 4; partition++)
        {
            var projection = partition & query;
            if (TryKey(projection, first, firstId, second, secondId, out var key))
                count += maps[partition, projection]!.GetValueOrDefault(key);
        }
        return count;
    }
    private static int Presence(Guid? first, Guid? second) => (first.HasValue ? 1 : 0) | (second.HasValue ? 2 : 0);
    private static bool TryKey(int projection, string? first, Guid? firstId, string? second, Guid? secondId, out Endpoint key)
    {
        key = default;
        if ((projection & 1) == 0 && !Text(first) || (projection & 2) == 0 && !Text(second)) return false;
        key = new((projection & 1) != 0 ? firstId : null, (projection & 1) == 0 ? first!.Trim() : null,
            (projection & 2) != 0 ? secondId : null, (projection & 2) == 0 ? second!.Trim() : null);
        return true;
    }
}
