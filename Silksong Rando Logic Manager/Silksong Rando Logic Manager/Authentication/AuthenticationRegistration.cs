using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SilksongRandoLogicManager.Contracts;
using Silksong_Rando_Logic_Manager.Services.HttpLogging;

namespace Silksong_Rando_Logic_Manager.Authentication;

public static class AuthenticationRegistration
{
    public static void AddConfiguredAuthentication(this WebApplicationBuilder builder, AuthenticationMode mode, Services.ApplicationStoragePaths paths)
    {
        builder.Services.AddSingleton(mode);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddDbContextFactory<AccountsDbContext>(options => options.UseSqlite(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(paths.DataRoot, "accounts.db") }.ToString()));
        builder.Services.AddIdentityCore<Account>(options =>
        {
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.AllowedForNewUsers = true;
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version1;
        }).AddEntityFrameworkStores<AccountsDbContext>().AddSignInManager()
            .AddClaimsPrincipalFactory<AccountClaimsPrincipalFactory>();
        builder.Services.AddScoped<ISecurityStampValidator, AccountSecurityStampValidator>();
        builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(30));
        builder.Services.AddDataProtection()
            .SetApplicationName("SilksongRandoLogicManager.Authentication.v1")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(paths.DataRoot, "authentication", "keys")));
        builder.Services.AddScoped<DetachedCommandAdmission>();
        builder.Services.AddScoped<LiveAccess>();
        builder.Services.AddScoped<CircuitCallbackAccess>();
        builder.Services.AddScoped<AccountsService>();
        builder.Services.AddScoped<CircuitAccessState>();
        builder.Services.AddCascadingValue<ExecutionIdentity?>(services => services.GetRequiredService<CircuitAccessState>().IdentitySource);
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
        builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, AccessCircuitHandler>();
        builder.Services.AddAuthentication(options =>
        {
            // Multiple native schemes remain registered, but bypass has no default
            // authenticate scheme: even WebApplication's implicit middleware cannot
            // read an account cookie in that startup mode.
            if (!mode.Bypass) options.DefaultScheme = IdentityConstants.ApplicationScheme;
        }).AddIdentityCookies();
        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "Silksong.LogicManager.Auth";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = mode.Bypass ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.LoginPath = "/account/login";
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromDays(400);
            options.Events.OnRedirectToLogin = context => Deny(context.HttpContext, new("authentication_required", 401, "Sign in to continue."));
            options.Events.OnRedirectToAccessDenied = context => Deny(context.HttpContext, new("forbidden", 403, "Your current access does not permit this operation."));
        });
        builder.Services.AddAuthorization(options =>
        {
            foreach (var policy in new[] { AccessPolicies.Normal, AccessPolicies.Author, AccessPolicies.ManageAccounts,
                AccessPolicies.ManageAdmins, AccessPolicies.ManageUsers, AccessPolicies.PublishDocumentation, AccessPolicies.Replacement })
                options.AddPolicy(policy, rule => rule.AddRequirements(new LiveAccessRequirement(policy)));
        });
        builder.Services.AddScoped<IAuthorizationHandler, LiveAccessHandler>();
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, LiveAccessResultHandler>();
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "Silksong.LogicManager.Antiforgery";
            options.Cookie.SecurePolicy = mode.Bypass ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy("account-login", _ => RateLimitPartition.GetFixedWindowLimiter("global", _ => new FixedWindowRateLimiterOptions
            { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
            options.OnRejected = async (context, token) =>
            {
                AccountHttp.NoStore(context.HttpContext);
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsync("Too many sign-in requests. Try again in a minute.", token);
            };
        });
    }

    public static async Task InitializeAccountsAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        await db.Database.MigrateAsync();
        if (app.Services.GetRequiredService<AuthenticationMode>().Bypass)
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            foreach (var account in await users.Users.ToListAsync())
            {
                var result = await users.UpdateSecurityStampAsync(account);
                if (!result.Succeeded) throw new InvalidOperationException("Bypass recovery could not invalidate account logins.");
            }
        }
    }

    public static void UseConfiguredAccess(this WebApplication app)
    {
        var paths = app.Services.GetRequiredService<Services.ApplicationStoragePaths>();
        // Bypass never reads account cookies, including malformed/old native ones.
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            var mode = context.RequestServices.GetRequiredService<AuthenticationMode>();
            if (mode.Bypass)
            {
                const string cookieName = "Silksong.LogicManager.BypassOwner";
                var protector = context.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("LogicManager.BypassOwner.v1");
                string? owner = null;
                if (context.Request.Cookies.TryGetValue(cookieName, out var encoded))
                {
                    try { owner = protector.Unprotect(encoded); }
                    catch (CryptographicException) { }
                }
                if (owner is null || owner.Length != 64 || !owner.All(Uri.IsHexDigit))
                {
                    owner = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                    context.Response.Cookies.Append(cookieName, protector.Protect(owner), new CookieOptions
                    { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = context.Request.IsHttps, IsEssential = true });
                }
                context.User = LiveAccess.BypassPrincipal(owner);
            }
            var access = context.RequestServices.GetRequiredService<LiveAccess>();
            access.Capture(context.User);
            context.SampleHttpLogIdentity();
            try
            {
                if (PrivatePath(context.Request.Path) || paths.IsPrivateWebPath(context.Request.Path, app.Environment.WebRootPath))
                { context.Response.StatusCode = 404; return; }
                var path = context.Request.Path;
                if (IsFrameworkDisconnect(context.Request))
                {
                    // A23: both framework disposal paths send a protected circuitId
                    // form, not the SignalR-configured antiforgery header. Only the
                    // framework endpoint may consume that secret and terminate its
                    // circuit. Cleanup remains available after session revocation.
                    if (!context.Request.HasFormContentType || !HasDisconnectOrigin(context.Request, mode))
                    {
                        await Deny(context, new("forbidden", 403, "The circuit cleanup request could not be verified."));
                        return;
                    }
                    await next();
                    return;
                }
                var account = path.StartsWithSegments("/account");
                var initialCacheSession = Services.V3CatalogueHttp.InitialCacheSession(context.Request);
                var session = path.Equals("/api/auth/session") || initialCacheSession;
                if (context.Request.Headers.TryGetValue(WorkspaceBinding.Header, out var workspace))
                    await WorkspaceBinding.RequireAsync(context, workspace);
                else if (!initialCacheSession && path.StartsWithSegments("/api/v3") &&
                    !Services.V3CatalogueHttp.NativeDraftPreview(context.Request))
                    await WorkspaceBinding.RequireAsync(context, default);
                if (account)
                    context.Items[typeof(ExecutionIdentity)] = await access.CurrentAsync(context.RequestAborted);
                if (path.Equals("/account/logout") && HttpMethods.IsPost(context.Request.Method))
                {
                    var form = context.Request.HasFormContentType
                        ? await context.Request.ReadFormAsync(context.RequestAborted) : null;
                    await WorkspaceBinding.RequireAsync(context, form?[WorkspaceBinding.FormField] ?? default);
                }
                if (path.StartsWithSegments("/_blazor") && context.Request.Headers.TryGetValue("Origin", out var origin) &&
                    (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var originUri) ||
                     !string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase) ||
                     originUri.Scheme is not ("https" or "http")))
                    throw new AccessDeniedException("forbidden", 403, "The server connection origin is not allowed.");
                if (!account && !session && context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>().Count is not > 0)
                    await access.RequireAsync(path.StartsWithSegments("/tools/publishing") ? AccessPolicies.PublishDocumentation : AccessPolicies.Normal, context.RequestAborted);
                if (account && (!KnownAccountPath(path) || !KnownAccountMethod(path, context.Request.Method))) { context.Response.StatusCode = 404; return; }
                if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
                {
                    try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                    catch (AntiforgeryValidationException exception)
                    {
                        context.ObserveHttpException(exception);
                        await Deny(context, new("antiforgery_required", 400, "The request could not be verified. Reopen the form or sign in again before trying a new action."));
                        return;
                    }
                }
                if (context.GetEndpoint()?.Metadata.GetMetadata<Services.V3CatalogueHttp.EndpointAdmission>() is not null
                    && context.Request.Method is "POST" or "PATCH" or "DELETE"
                    && context.Request.Headers.Origin is { Count: > 0 } mutationOrigin
                    && (!Uri.TryCreate(mutationOrigin.ToString(), UriKind.Absolute, out var mutationUri)
                        || !string.Equals(mutationUri.GetLeftPart(UriPartial.Authority), $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase)))
                {
                    await Services.V3CatalogueHttp.ErrorAsync(context, 403, "origin", "Cross-origin mutations are not permitted.");
                    return;
                }
                await next();
            }
            catch (AccessDeniedException denied) when (!context.Response.HasStarted)
            {
                context.ObserveHttpException(denied);
                await Deny(context, denied);
            }
        });
    }

    internal static bool IsFrameworkDisconnect(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) && string.Equals(request.Path.Value, "/_blazor/disconnect", StringComparison.Ordinal);

    private static bool HasDisconnectOrigin(HttpRequest request, AuthenticationMode mode)
    {
        var origins = request.Headers.Origin;
        if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") || origin.UserInfo.Length != 0 ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            return false;
        if (mode.Bypass ? !string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) : origin.Scheme != "https")
            return false;
        if (!Uri.TryCreate(origin.Scheme + "://" + request.Host.Value + "/", UriKind.Absolute, out var expected) ||
            expected.UserInfo.Length != 0 || !string.Equals(origin.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            origin.Port != expected.Port)
            return false;
        // Fetch metadata is additional rejection evidence, never a substitute for
        // the mandatory Origin or the framework's protected secret validation.
        return !request.Headers["Sec-Fetch-Site"].Any(value =>
            value?.Split(',').Any(site => string.Equals(site.Trim(), "cross-site", StringComparison.OrdinalIgnoreCase)) == true);
    }

    private static bool KnownAccountPath(PathString path) => path.Value is "/account/login" or "/account/logout" or "/account/replace-password" or "/account/admin"
        or "/account/admin/create" or "/account/admin/update" or "/account/admin/reset" or "/account/admin/delete";
    private static bool KnownAccountMethod(PathString path, string method) => path.Value switch
    {
        "/account/login" or "/account/replace-password" => HttpMethods.IsGet(method) || HttpMethods.IsPost(method),
        "/account/admin" => HttpMethods.IsGet(method),
        _ => HttpMethods.IsPost(method)
    };
    private static bool PrivatePath(PathString path)
    {
        var parts = (path.Value ?? "").Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var privateData = parts.Length > 1 && parts[0].Equals("data", StringComparison.OrdinalIgnoreCase) &&
            (parts[1].Equals("Publishing", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("history", StringComparison.OrdinalIgnoreCase)
                || parts[1].Equals("authentication", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("backups", StringComparison.OrdinalIgnoreCase)
                || parts[1].Equals("logs", StringComparison.OrdinalIgnoreCase));
        privateData |= parts.Length > 1 && parts[0].Equals("data", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("scene-image-staging", StringComparison.OrdinalIgnoreCase);
        return privateData || parts.Any(part => part.Equals("Config", StringComparison.OrdinalIgnoreCase)
            || part.Equals("Storage", StringComparison.OrdinalIgnoreCase)
            || part.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase)
            || part.Equals("authentication.json", StringComparison.OrdinalIgnoreCase)
            || part.StartsWith("accounts.db", StringComparison.OrdinalIgnoreCase)
            || part.Contains(".db", StringComparison.OrdinalIgnoreCase));
    }
    internal static async Task Deny(HttpContext context, AccessDeniedException denied)
    {
        AccountHttp.NoStore(context);
        var request = context.Request;
        var destination = request.Headers["Sec-Fetch-Dest"];
        var fetchMode = request.Headers["Sec-Fetch-Mode"];
        // An explicit programmatic/account-bound signal wins even with HTML Accept.
        var programmatic = request.Headers.ContainsKey(WorkspaceBinding.Header) ||
            destination.Count > 0 && (destination.Count != 1 || destination[0] is not ("document" or "iframe")) ||
            fetchMode.Count > 0 && (fetchMode.Count != 1 || fetchMode[0] != "navigate");
        var documentSignal = destination.Count == 1 && destination[0] is "document" or "iframe" ||
            fetchMode.Count == 1 && fetchMode[0] == "navigate" ||
            request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
        var nativeApiDownload = request.Path.Equals(Services.LocalRequirementCatalogueDownloadEndpoints.ExportPath) ||
            request.Path.Equals(Services.LocalRoomGraphDownloadEndpoints.ExportPath) ||
            request.Path.Equals(Services.LocalRoomGeometryDownloadEndpoints.ExportPath);
        var document = !programmatic &&
            (HttpMethods.IsGet(request.Method) && documentSignal && (!request.Path.StartsWithSegments("/api") || nativeApiDownload) ||
             request.Path.StartsWithSegments("/account"));
        if (document && denied.Code is "authentication_required" or "password_replacement_required")
        {
            context.Response.Redirect(AccountHttp.LoginDestination(context.Request.Path));
            return;
        }
        context.Response.StatusCode = denied.Status;
        if (document)
        {
            await AccountHttp.DenialPage(denied).ExecuteAsync(context);
            return;
        }
        await context.Response.WriteAsJsonAsync(new AccessDenial(denied.Code, denied.Message));
    }
}
