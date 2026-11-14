using Silksong_Rando_Logic_Manager.Authentication;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Immutable attribution from the actual successful execution admission,
/// not a session/owner identity or a subsequent account lookup.</summary>
internal sealed record HistoryActor
{
    internal Guid AccountId { get; }
    internal bool Standalone { get; }
    private HistoryActor(Guid accountId, bool standalone) => (AccountId, Standalone) = (accountId, standalone);

    internal static HistoryActor From(ExecutionIdentity identity)
    {
        if (identity.Bypass) return new(Guid.Empty, false);
        if (!Guid.TryParse(identity.Account, out var id))
            throw new InvalidOperationException("An admitted account must have a GUID history identity.");
        return new(id, false);
    }

    // Only named operator-owned composition uses this permanent exclusion.
    internal static HistoryActor StandaloneOperator { get; } = new(Guid.Empty, true);
}
