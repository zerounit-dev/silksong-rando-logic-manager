using Microsoft.EntityFrameworkCore;

namespace Silksong_Rando_Logic_Manager.Authentication;

public sealed partial class AccountsService
{
    // Same scalar account-name projection as publishing activity, but not its
    // PublishDocumentation capability and not a general account listing.
    internal async Task<IReadOnlyDictionary<Guid, string>> ReadEditorHistoryNamesAsync(Guid[] actorIds, CancellationToken token)
    {
        await access.RequireAsync(AccessPolicies.Normal, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.Users.AsNoTracking().Where(account => actorIds.Contains(account.Id) && account.UserName != null)
            .Select(account => new { account.Id, account.UserName })
            .ToDictionaryAsync(account => account.Id, account => account.UserName!, token);
    }
}
