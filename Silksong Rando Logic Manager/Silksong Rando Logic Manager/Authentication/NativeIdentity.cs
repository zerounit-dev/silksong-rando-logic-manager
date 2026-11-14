using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Silksong_Rando_Logic_Manager.Authentication;

public sealed class AccountClaimsPrincipalFactory(UserManager<Account> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<Account>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(Account user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        var claims = Options.ClaimsIdentity;
        // Base includes AspNetUserClaims. None of those rows may supply application
        // authority or replace native credential/owner evidence on issue or renewal.
        var reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            claims.UserIdClaimType, claims.UserNameClaimType, claims.SecurityStampClaimType, claims.RoleClaimType,
            AccessPolicies.ReplacementClaim, AccessPolicies.BypassClaim, AccessPolicies.BypassOwnerClaim,
            AccessPolicies.ManageAdminsClaim, AccessPolicies.ManageUsersClaim,
            AccessPolicies.EditLogicClaim, AccessPolicies.PublishDocumentationClaim
        };
        foreach (var claim in identity.Claims.Where(claim => reserved.Contains(claim.Type)).ToArray()) identity.RemoveClaim(claim);
        identity.AddClaim(new(claims.UserIdClaimType, await UserManager.GetUserIdAsync(user)));
        identity.AddClaim(new(claims.UserNameClaimType, (await UserManager.GetUserNameAsync(user))!));
        if (UserManager.SupportsUserSecurityStamp)
            identity.AddClaim(new(claims.SecurityStampClaimType, (await UserManager.GetSecurityStampAsync(user))!));
        if (user.ManageAdmins) identity.AddClaim(new(AccessPolicies.ManageAdminsClaim, "true"));
        if (user.ManageUsers) identity.AddClaim(new(AccessPolicies.ManageUsersClaim, "true"));
        if (user.EditLogic) identity.AddClaim(new(AccessPolicies.EditLogicClaim, "true"));
        if (user.PublishDocumentation) identity.AddClaim(new(AccessPolicies.PublishDocumentationClaim, "true"));
        if (user.MustReplacePassword) identity.AddClaim(new(AccessPolicies.ReplacementClaim, "true"));
        return identity;
    }
}

// Extend the native due-validation path, reusing the account it already loaded.
public sealed class AccountSecurityStampValidator(IOptions<SecurityStampValidatorOptions> options,
    SignInManager<Account> signIn, ILoggerFactory logger) : SecurityStampValidator<Account>(options, signIn, logger)
{
    protected override async Task<Account?> VerifySecurityStamp(ClaimsPrincipal? principal)
    {
        var user = await base.VerifySecurityStamp(principal);
        return user is { Enabled: true } ? user : null;
    }
}

// Standard server template pattern: fresh scope on each interval; success does not
// refresh claims or an HTTP cookie. The framework publishes anonymous on failure.
internal sealed class IdentityRevalidatingAuthenticationStateProvider(ILoggerFactory logger,
    IServiceScopeFactory scopes, AuthenticationMode mode, IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(logger)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken token)
    {
        if (mode.Bypass) return LiveAccess.IsBypassPrincipal(state.User);
        if (state.User.HasClaim(AccessPolicies.BypassClaim, "true")) return false;
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        token.ThrowIfCancellationRequested();
        var user = await users.GetUserAsync(state.User);
        return user is { Enabled: true } && users.SupportsUserSecurityStamp &&
            state.User.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType) is { } stamp &&
            stamp == await users.GetSecurityStampAsync(user);
    }
}

// Only detached command entry uses this fresh native check. Ordinary reads, waits,
// progress and file callbacks continue using accepted-principal policy checks.
internal sealed class DetachedCommandAdmission(SignInManager<Account> signIn, AuthenticationMode mode)
{
    internal async Task AdmitAsync(LiveAccess execution, ClaimsPrincipal presented, string owner)
    {
        if (mode.Bypass)
        {
            if (!LiveAccess.IsBypassPrincipal(presented)) throw Denied();
            execution.Capture(presented);
        }
        else
        {
            if (presented.HasClaim(AccessPolicies.BypassClaim, "true")) throw Denied();
            var user = await signIn.ValidateSecurityStampAsync(presented);
            if (user is not { Enabled: true, PublishDocumentation: true, MustReplacePassword: false }) throw Denied();
            execution.Capture(await signIn.CreateUserPrincipalAsync(user));
        }
        await execution.RequireOwnerAsync(owner, AccessPolicies.PublishDocumentation);
    }

    private static AccessDeniedException Denied() => new("authentication_required", 401, "Sign in again before starting a new publishing command.");
}
