using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Authentication;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services.Publishing;

public sealed record PublishingLatestActivity(bool Available, DateTime? RecordedUtc, string? User);
public sealed record PublishingActivityRow(string User, string RoomName, DateTime RecordedUtc, bool WithinLastHour);
public sealed record PublishingRecentActivity(bool Available, IReadOnlyList<PublishingActivityRow> Rows);

// This scoped page read is independent of publishing snapshots, jobs and commands.
public sealed class PublishingActivityService
{
    private readonly SharedPublicationCoordinator publications;
    private readonly RecordHistoryStore history;
    private readonly IDbContextFactory<LogicDbContext> contexts;
    private readonly AccountsService accounts;
    private readonly LiveAccess access;
    private readonly ILogger<PublishingActivityService> logger;

    internal PublishingActivityService(SharedPublicationCoordinator publications, RecordHistoryStore history,
        IDbContextFactory<LogicDbContext> contexts, AccountsService accounts, LiveAccess access,
        ILogger<PublishingActivityService> logger)
    {
        this.publications = publications; this.history = history; this.contexts = contexts;
        this.accounts = accounts; this.access = access; this.logger = logger;
    }

    internal static void Register(IServiceCollection services) => services.AddScoped(provider => new PublishingActivityService(
        provider.GetRequiredService<SharedPublicationCoordinator>(), provider.GetRequiredService<RecordHistoryStore>(),
        provider.GetRequiredService<IDbContextFactory<LogicDbContext>>(), provider.GetRequiredService<AccountsService>(),
        provider.GetRequiredService<LiveAccess>(), provider.GetRequiredService<ILogger<PublishingActivityService>>()));

    public async Task<PublishingLatestActivity> ReadLatestAsync(CancellationToken token = default)
    {
        try
        {
            using var admission = await publications.AcquireAsync(token);
            await access.RequireAsync(AccessPolicies.PublishDocumentation, token);
            var read = await history.ReadActivityAsync(null, token);
            if (read.LatestActorId is not Guid actorId) return new(read.Available, read.LatestUtc, null);
            var usernames = await accounts.ReadPublishingActivityNamesAsync([actorId], token);
            return new(read.Available, read.LatestUtc, ActivityUsername(actorId, usernames));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { LogFailure(exception); return new(false, null, null); }
    }

    public async Task<PublishingRecentActivity> ReadRecentAsync(CancellationToken token = default)
    {
        try
        {
            using var admission = await publications.AcquireAsync(token);
            await access.RequireAsync(AccessPolicies.PublishDocumentation, token);
            var now = DateTime.UtcNow; // One full-precision clock sample for both windows.
            var read = await history.ReadActivityAsync(now, token, async ids =>
            {
                await using var db = await contexts.CreateDbContextAsync(token);
                // Current names first, including archived rooms; stored fallback
                // is read only for missing rooms inside the same history snapshot.
                return await db.Rooms.AsNoTracking().Where(x => ids.Contains(x.Id))
                    .Select(x => new { x.Id, x.FriendlyName }).ToDictionaryAsync(x => x.Id, x => x.FriendlyName, token);
            });
            if (!read.Available) return new(false, []);
            if (read.Edits.Count == 0) return new(true, []);
            var usernames = await accounts.ReadPublishingActivityNamesAsync(read.Edits.Select(x => x.ActorId).ToArray(), token);
            var rows = read.Edits.OrderByDescending(x => x.RecordedUtc)
                .ThenBy(x => x.ActorId.ToString("D"), StringComparer.Ordinal).Select(edit =>
                {
                    return new PublishingActivityRow(ActivityUsername(edit.ActorId, usernames), edit.RoomName, edit.RecordedUtc,
                        edit.RecordedUtc >= now.AddHours(-1) && edit.RecordedUtc <= now);
                }).ToArray();
            return new(true, rows);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { LogFailure(exception); return new(false, []); }
    }

    internal const string LocalAdminLabel = "Local admin";

    private static string ActivityUsername(Guid actorId, IReadOnlyDictionary<Guid, string> usernames) =>
        actorId == Guid.Empty ? LocalAdminLabel : usernames.TryGetValue(actorId, out var name) ? name : "Unknown account";

    private void LogFailure(Exception exception)
    {
        try { logger.LogError(exception, "Publisher activity unavailable; publishing commands are independent."); }
        catch (Exception) { }
    }
}
