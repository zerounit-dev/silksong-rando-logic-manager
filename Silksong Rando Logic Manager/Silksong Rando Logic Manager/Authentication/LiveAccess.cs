using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Silksong_Rando_Logic_Manager.Authentication;

public static class AccessPolicies
{
    public const string Normal = "NormalAccess";
    public const string Author = "EditLogic";
    public const string ManageAccounts = "ManageAccounts";
    public const string ManageAdmins = "ManageAdmins";
    public const string ManageUsers = "ManageUsers";
    public const string PublishDocumentation = "PublishDocumentation";
    public const string Replacement = "ReplacePasswordOnly";
    internal const string BypassOwnerClaim = "logic:bypass-owner";
    internal const string ReplacementClaim = "logic:replacement";
    internal const string BypassClaim = "logic:bypass";
    internal const string ManageAdminsClaim = "logic:manage-admins";
    internal const string ManageUsersClaim = "logic:manage-users";
    internal const string EditLogicClaim = "logic:edit-logic";
    internal const string PublishDocumentationClaim = "logic:publish-documentation";
}

public sealed class AccessDeniedException(string code, int status, string message) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed record ExecutionIdentity(string Account, string Username, AccountPermissions Permissions, bool Replacement, bool Bypass, string? BrowserOwner = null)
{
    public string Owner => Bypass ? Account + ":" + BrowserOwner : Account;
    public bool CanAuthor => !Replacement && Permissions.EditLogic;
    public bool CanManageAdmins => !Replacement && Permissions.ManageAdmins;
    public bool CanManageUsers => !Replacement && Permissions.ManageUsers;
    public bool CanManageAccounts => CanManageAdmins || CanManageUsers;
    public bool CanPublishDocumentation => !Replacement && Permissions.PublishDocumentation;
    public bool CanManage(AccountPermissions target) => target.ManageAdmins ? CanManageAdmins : CanManageUsers;
    public string WorkspaceKey => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("workspace:" + Owner)));
}

// Thin policy/owner projection over framework-accepted HTTP/circuit principals.
// This adapter never checks credentials or reads the accounts database.
public sealed class LiveAccess(AuthenticationMode mode, IHttpContextAccessor http, CircuitAccessState state, IOptions<IdentityOptions> options)
{
    private ClaimsPrincipal captured = http.HttpContext?.User ?? new();
    private readonly object circuitBoundary = new();
    private bool circuit;
    private long observation;
    private string? originatingOwner;
    private TaskCompletionSource? pendingObservation;
    private Exception? observationFailure;
    public void Capture(ClaimsPrincipal principal)
    {
        lock (circuitBoundary)
        {
            // HTTP/detached scopes keep their actual credential. Circuit updates
            // belong exclusively to the revision-bound framework notification.
            if (!circuit) captured = principal.Clone();
        }
    }

    internal ClaimsPrincipal CapturePrincipal()
    {
        lock (circuitBoundary)
        {
            RequireCircuitObservation();
            return captured.Clone();
        }
    }
    internal static ClaimsPrincipal BypassPrincipal(string owner) => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, "bypass"), new Claim(ClaimTypes.Name, "Standalone administrator"),
        new Claim(AccessPolicies.BypassClaim, "true"),
        new Claim(AccessPolicies.BypassOwnerClaim, owner)
    }, "LogicManager.Bypass"));
    internal static bool IsBypassPrincipal(ClaimsPrincipal principal) =>
        principal.Identity is ClaimsIdentity { IsAuthenticated: true, AuthenticationType: "LogicManager.Bypass" } &&
        principal.FindFirstValue(ClaimTypes.NameIdentifier) == "bypass" &&
        principal.HasClaim(AccessPolicies.BypassClaim, "true") &&
        principal.FindFirstValue(AccessPolicies.BypassOwnerClaim) is { Length: 64 } owner && owner.All(Uri.IsHexDigit);

    private ExecutionIdentity? Project(ClaimsPrincipal principal)
    {
        if (mode.Bypass) return IsBypassPrincipal(principal)
            ? new("bypass", "Standalone administrator", AccountPermissions.Admin, false, true, principal.FindFirstValue(AccessPolicies.BypassOwnerClaim)) : null;
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(AccessPolicies.BypassClaim, "true")) return null;
        var claims = options.Value.ClaimsIdentity;
        if (!Guid.TryParse(principal.FindFirstValue(claims.UserIdClaimType), out var id)) return null;
        var permissions = new AccountPermissions(principal.HasClaim(AccessPolicies.ManageAdminsClaim, "true"),
            principal.HasClaim(AccessPolicies.ManageUsersClaim, "true"), principal.HasClaim(AccessPolicies.EditLogicClaim, "true"),
            principal.HasClaim(AccessPolicies.PublishDocumentationClaim, "true"));
        return new(id.ToString("D"), principal.FindFirstValue(claims.UserNameClaimType) ?? "", permissions,
            principal.HasClaim(AccessPolicies.ReplacementClaim, "true"), false);
    }

    internal long BeginCircuitObservation()
    {
        lock (circuitBoundary)
        {
            circuit = true;
            pendingObservation?.TrySetResult(); // Wake waiters to inspect the newer revision.
            pendingObservation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            observationFailure = null;
            return ++observation;
        }
    }
    internal void CompleteCircuitObservation(long revision, ClaimsPrincipal? principal)
    {
        lock (circuitBoundary)
        {
            if (revision != observation) return;
            ObserveCircuitPrincipal(principal);
            pendingObservation?.TrySetResult();
            pendingObservation = null;
        }
    }
    internal void FailCircuitObservation(long revision, Exception failure)
    {
        lock (circuitBoundary)
        {
            if (revision != observation) return;
            observationFailure = failure;
            pendingObservation?.TrySetResult();
            pendingObservation = null;
        }
    }
    internal void EndCircuitObservations()
    {
        lock (circuitBoundary)
        {
            ++observation;
            observationFailure = new ObjectDisposedException(nameof(AccessCircuitHandler));
            pendingObservation?.TrySetResult();
            pendingObservation = null;
        }
    }
    private void ObserveCircuitPrincipal(ClaimsPrincipal? principal)
    {
        captured = principal?.Clone() ?? new();
        var identity = Project(captured);
        originatingOwner ??= identity?.Owner;
        state.Accept(identity is not null && identity.Owner == originatingOwner ? identity : null);
    }
    private static AccessDeniedException CircuitIdentityLoss() => new("authentication_required", 401,
        "Sign-in or account changed. Sign in again to continue.");
    private void RequireCircuitObservation()
    {
        if (observationFailure is { } failure)
            throw new InvalidOperationException("Circuit authentication state could not be verified.", failure);
        if (circuit && pendingObservation is not null)
            throw new InvalidOperationException("Circuit authentication state is being checked. No operation was admitted.");
    }

    public async Task<ExecutionIdentity?> CurrentAsync(CancellationToken token = default)
    {
        return (await ReadCurrentAsync(token)).Identity;
    }

    private async Task<(ExecutionIdentity? Identity, long Revision)> ReadCurrentAsync(CancellationToken token)
    {
        while (true)
        {
            Task? pending; ClaimsPrincipal principal; long revision;
            lock (circuitBoundary)
            {
                pending = pendingObservation?.Task;
                if (pending is null) RequireCircuitObservation();
                principal = captured;
                revision = observation;
            }
            if (pending is not null) { await pending.WaitAsync(token); continue; }
            token.ThrowIfCancellationRequested();
            var current = Project(principal);
            lock (circuitBoundary)
            {
                if (revision != observation) continue;
                RequireCircuitObservation();
                if (circuit && current is not null && current.Owner != originatingOwner) throw CircuitIdentityLoss();
                return (current, revision);
            }
        }
    }

    public async Task<ExecutionIdentity> RequireAsync(string policy = AccessPolicies.Normal, CancellationToken token = default)
    {
        while (true)
        {
            var current = await ReadCurrentAsync(token);
            lock (circuitBoundary)
            {
                if (current.Revision != observation) continue;
                return RequireIdentity(current.Identity, policy);
            }
        }
    }

    // Existing synchronous locks retain cheap policy/owner checks, not SQL.
    public ExecutionIdentity RequireInsideSynchronousGate(string policy)
    {
        lock (circuitBoundary)
        {
        RequireCircuitObservation();
        return RequireIdentity(Project(captured), policy);
        }
    }

    private ExecutionIdentity RequireIdentity(ExecutionIdentity? current, string policy)
    {
        lock (circuitBoundary)
        {
        RequireCircuitObservation();
        var identity = current ?? throw Denied("authentication_required", 401, "Sign in to continue. Submitted work may already have committed.");
        if (circuit && identity.Owner != originatingOwner) throw CircuitIdentityLoss();
        if (identity.Replacement && policy != AccessPolicies.Replacement)
            throw Denied("password_replacement_required", 403, "Replace the temporary password before using the application.");
        var allowed = policy switch
        {
            AccessPolicies.Normal => !identity.Replacement,
            AccessPolicies.Author => identity.CanAuthor,
            AccessPolicies.ManageAccounts => identity.CanManageAccounts,
            AccessPolicies.ManageAdmins => identity.CanManageAdmins,
            AccessPolicies.ManageUsers => identity.CanManageUsers,
            AccessPolicies.PublishDocumentation => identity.CanPublishDocumentation,
            AccessPolicies.Replacement => identity.Replacement && !identity.Bypass,
            _ => false
        };
        state.Accept(identity);
        RequireCircuitObservation();
        if (!allowed) throw Denied("forbidden", 403, "Your current access does not permit this operation.");
        return identity;
        }
    }
    private AccessDeniedException Denied(string code, int status, string message)
    {
        var denial = new AccessDeniedException(code, status, message);
        return denial;
    }

    public async Task<ExecutionIdentity> RequireOwnerAsync(string owner, string policy = AccessPolicies.Normal, CancellationToken token = default)
    {
        var identity = await RequireAsync(policy, token);
        if (!string.Equals(identity.Owner, owner, StringComparison.Ordinal))
            throw new AccessDeniedException("not_found", 404, "This resource is not available to this account.");
        return identity;
    }
}

public sealed record LiveAccessRequirement(string Policy) : IAuthorizationRequirement;
public sealed class LiveAccessHandler(LiveAccess access) : AuthorizationHandler<LiveAccessRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, LiveAccessRequirement requirement)
    {
        if (requirement.Policy == AccessPolicies.Normal && context.Resource is HttpContext http &&
            Services.V3CatalogueHttp.InitialCacheSession(http.Request))
        {
            context.Succeed(requirement);
            return;
        }
        try { await access.RequireAsync(requirement.Policy); context.Succeed(requirement); }
        catch (AccessDeniedException denial) { context.Fail(new LiveAccessFailure(this, denial)); }
    }
}

internal sealed class LiveAccessFailure(IAuthorizationHandler handler, AccessDeniedException denial)
    : AuthorizationFailureReason(handler, denial.Message)
{
    internal AccessDeniedException Denial { get; } = denial;
}

public sealed class LiveAccessResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        var denial = result.AuthorizationFailure?.FailureReasons.OfType<LiveAccessFailure>().FirstOrDefault()?.Denial;
        if (denial is not null) await AuthenticationRegistration.Deny(context, denial);
        else if (result.Challenged && policy.Requirements.OfType<LiveAccessRequirement>().Any())
            // Native PolicyEvaluator deliberately omits failure reasons on an
            // unauthenticated challenge. No role/replacement denial is inferred.
            await AuthenticationRegistration.Deny(context, new("authentication_required", 401, "Sign in to continue. Submitted work may already have committed."));
        else await fallback.HandleAsync(next, context, policy, result);
    }
}
