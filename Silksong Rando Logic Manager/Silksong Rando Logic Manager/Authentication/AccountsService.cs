using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Silksong_Rando_Logic_Manager.Authentication;

public sealed record AccountView(Guid Id, string Username, AccountPermissions Permissions, bool Enabled, bool ReplacementRequired, string ConcurrencyToken);
public sealed record AccountCommand(Guid Id, string ConcurrencyToken, string Username, AccountPermissions Permissions, bool Enabled);
public sealed record CredentialDisclosure(string Username, string TemporaryCredential);
public sealed class AccountCommandException(string message) : Exception(message);

public sealed partial class AccountsService(IDbContextFactory<AccountsDbContext> contexts, IPasswordHasher<Account> hasher, LiveAccess access, IServiceProvider services)
{
    [GeneratedRegex("^[A-Za-z0-9._-]{3,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
    private static string Username(string value)
    {
        var name = value.Trim();
        if (!UsernamePattern().IsMatch(name)) throw new AccountCommandException("Use 3–64 ASCII letters, digits, dots, underscores or hyphens for the username.");
        return name;
    }
    private static string Stamp() => Guid.NewGuid().ToString("N");
    private static string GenerateCredential() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
    // Publisher-only scalar projection, not managed account listing or account metadata.
    internal async Task<IReadOnlyDictionary<Guid, string>> ReadPublishingActivityNamesAsync(Guid[] ids, CancellationToken token)
    {
        await access.RequireAsync(AccessPolicies.PublishDocumentation, token);
        ids = ids.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, string>();
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id) && x.UserName != null)
            .Select(x => new { x.Id, x.UserName }).ToDictionaryAsync(x => x.Id, x => x.UserName!, token);
    }

    public async Task<IReadOnlyList<AccountView>> ListAsync()
    {
        await access.RequireAsync(AccessPolicies.ManageAccounts);
        await using var db = await contexts.CreateDbContextAsync();
        return await db.Users.AsNoTracking().OrderBy(x => x.NormalizedUserName)
            .Select(x => new AccountView(x.Id, x.UserName!, new(x.ManageAdmins, x.ManageUsers, x.EditLogic, x.PublishDocumentation), x.Enabled, x.MustReplacePassword, x.ConcurrencyStamp!)).ToListAsync();
    }

    public async Task<CredentialDisclosure> CreateAsync(string username, AccountPermissions permissions, bool enabled)
    {
        var name = Username(username);
        await using var db = await contexts.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await access.RequireAsync(permissions.ManageAdmins ? AccessPolicies.ManageAdmins : AccessPolicies.ManageUsers);
        var normalized = name.ToUpperInvariant();
        if (await db.Users.AnyAsync(x => x.NormalizedUserName == normalized)) throw new AccountCommandException("That username is already in use.");
        var credential = GenerateCredential();
        var account = new Account
        {
            Id = Guid.NewGuid(), UserName = name, NormalizedUserName = normalized, Enabled = enabled,
            MustReplacePassword = true, SecurityStamp = Stamp(), ConcurrencyStamp = Stamp(), LockoutEnabled = true
        };
        permissions.Apply(account);
        account.PasswordHash = hasher.HashPassword(account, credential);
        db.Users.Add(account);
        await SaveAsync(db); await transaction.CommitAsync();
        return new(name, credential);
    }

    public async Task<bool> UpdateAsync(AccountCommand command)
    {
        var name = Username(command.Username);
        await using var db = await contexts.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await access.RequireAsync(AccessPolicies.ManageAccounts);
        var account = await TargetAsync(db, command.Id, command.ConcurrencyToken);
        await AuthorizeTargetAsync(account);
        if (command.Permissions.ManageAdmins) await access.RequireAsync(AccessPolicies.ManageAdmins);
        var normalized = name.ToUpperInvariant();
        if (await db.Users.AnyAsync(x => x.Id != account.Id && x.NormalizedUserName == normalized)) throw new AccountCommandException("That username is already in use.");
        var changed = account.UserName != name || AccountPermissions.From(account) != command.Permissions || account.Enabled != command.Enabled;
        if (changed)
        {
            account.UserName = name; account.NormalizedUserName = normalized; command.Permissions.Apply(account); account.Enabled = command.Enabled;
            RotateStamps(account); await SaveAsync(db);
        }
        await transaction.CommitAsync();
        return changed;
    }

    public async Task<CredentialDisclosure> ResetAsync(Guid id, string concurrencyToken, string confirmedUsername)
    {
        await using var db = await contexts.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await access.RequireAsync(AccessPolicies.ManageAccounts);
        var account = await TargetAsync(db, id, concurrencyToken);
        await AuthorizeTargetAsync(account);
        Confirm(account, confirmedUsername);
        var credential = GenerateCredential();
        account.PasswordHash = hasher.HashPassword(account, credential);
        account.MustReplacePassword = true; account.AccessFailedCount = 0; account.LockoutEnd = null;
        RotateStamps(account); await SaveAsync(db); await transaction.CommitAsync();
        return new(account.UserName!, credential);
    }

    public async Task DeleteAsync(Guid id, string concurrencyToken, string confirmedUsername)
    {
        await using var db = await contexts.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await access.RequireAsync(AccessPolicies.ManageAccounts);
        var account = await TargetAsync(db, id, concurrencyToken);
        await AuthorizeTargetAsync(account);
        Confirm(account, confirmedUsername);
        db.Users.Remove(account); await SaveAsync(db); await transaction.CommitAsync();
    }

    public async Task<ClaimsPrincipal?> AuthenticateAsync(string username, string credential)
    {
        var normalized = username.Trim().ToUpperInvariant();
        await using var db = await contexts.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var account = await db.Users.SingleOrDefaultAsync(x => x.NormalizedUserName == normalized);
        using var manager = Manager(db);
        if (account is null || !account.Enabled || account.PasswordHash is null || await manager.IsLockedOutAsync(account)) return null;
        var verified = hasher.VerifyHashedPassword(account, account.PasswordHash, credential);
        if (verified == PasswordVerificationResult.Failed)
        {
            if (!(await manager.AccessFailedAsync(account)).Succeeded) return null;
            await transaction.CommitAsync(); return null;
        }
        if (!(await manager.ResetAccessFailedCountAsync(account)).Succeeded) return null;
        // Rehashing the same credential is not a password change and must not
        // revoke other native cookies for an unchanged password.
        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.PasswordHash = hasher.HashPassword(account, credential);
            account.ConcurrencyStamp = Stamp();
            await db.SaveChangesAsync();
        }
        await transaction.CommitAsync();
        // Same native factory as renewal, with the manager/store for this exact context.
        return await new AccountClaimsPrincipalFactory(manager,
            services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>()).CreateAsync(account);
    }

    private UserManager<Account> Manager(AccountsDbContext db) => new(
        new UserOnlyStore<Account, AccountsDbContext, Guid>(db),
        services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>(), hasher,
        services.GetServices<IUserValidator<Account>>(), services.GetServices<IPasswordValidator<Account>>(),
        services.GetRequiredService<ILookupNormalizer>(), services.GetRequiredService<IdentityErrorDescriber>(), services,
        services.GetRequiredService<ILogger<UserManager<Account>>>());

    public async Task ReplaceAsync(string credential, string confirmation)
    {
        if (credential.Length is < 12 or > 128 || credential != confirmation)
            throw new AccountCommandException("Use a new password of 12–128 characters and enter the same confirmation.");
        await using var db = await contexts.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var actor = await access.RequireAsync(AccessPolicies.Replacement);
        var id = Guid.Parse(actor.Account);
        var account = await db.Users.SingleAsync(x => x.Id == id);
        if (!account.MustReplacePassword) throw new AccessDeniedException("forbidden", 403, "Password replacement is not available.");
        if (hasher.VerifyHashedPassword(account, account.PasswordHash!, credential) != PasswordVerificationResult.Failed)
            throw new AccountCommandException("Choose a different password from the temporary password.");
        account.PasswordHash = hasher.HashPassword(account, credential);
        account.MustReplacePassword = false; account.AccessFailedCount = 0; account.LockoutEnd = null;
        RotateStamps(account); await SaveAsync(db); await transaction.CommitAsync();
    }

    private async Task AuthorizeTargetAsync(Account account)
    {
        await access.RequireAsync(account.ManageAdmins ? AccessPolicies.ManageAdmins : AccessPolicies.ManageUsers);
    }

    private static async Task<Account> TargetAsync(AccountsDbContext db, Guid id, string concurrencyToken)
    {
        var account = await db.Users.SingleOrDefaultAsync(x => x.Id == id);
        if (account is null || !string.Equals(account.ConcurrencyStamp, concurrencyToken, StringComparison.Ordinal))
            throw new AccountCommandException("The account changed or was deleted. Reload the current account list before trying again.");
        return account;
    }
    private static void Confirm(Account account, string confirmedUsername)
    {
        if (!string.Equals(account.UserName, confirmedUsername, StringComparison.Ordinal))
            throw new AccountCommandException("Type the account's exact current username to confirm this action.");
    }
    private static void RotateStamps(Account account)
    {
        account.SecurityStamp = Stamp(); account.ConcurrencyStamp = Stamp();
    }
    private static async Task SaveAsync(AccountsDbContext db)
    {
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { throw new AccountCommandException("The account changed. Reload before trying again."); }
        catch (DbUpdateException error) when (error.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        { throw new AccountCommandException("That username is already in use or the account changed. Reload before trying again."); }
    }
}
