using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Silksong_Rando_Logic_Manager.Authentication;
using Silksong_Rando_Logic_Manager.Services.HttpLogging;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Operator-owned scheme recognition, independent of authentication mode.</summary>
public sealed class HttpHostingOptions
{
    public bool ForwardingEnabled { get; set; }
    public string[] TrustedProxies { get; set; } = [];
}

public static class HttpIntegration
{
    public static void AddHttpIntegration(this WebApplicationBuilder builder)
    {
        // Match .NET 10 ForwardedHeadersStartupFilter/OptionsSetup's effective-key
        // predicate. The filter runs at host Start, not builder construction/Build.
        // Reject now, before Directory.CreateDirectory, migrations or key creation.
        if (string.Equals(builder.Configuration["ForwardedHeaders_Enabled"], "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ForwardedHeaders_Enabled=true (including ASPNETCORE_FORWARDEDHEADERS_ENABLED) conflicts with explicit HTTP hosting. Unset or disable that setting; configure HttpHosting:ForwardingEnabled, HttpHosting:TrustedProxies and AllowedHosts instead.");

        var hosting = builder.Configuration.GetSection("HttpHosting").Get<HttpHostingOptions>() ?? new();
        var proxies = new List<IPAddress>();
        if (hosting.ForwardingEnabled)
        {
            if (hosting.TrustedProxies is not { Length: > 0 })
                throw new InvalidOperationException("HttpHosting:TrustedProxies must contain explicit immediate proxy IP addresses when ForwardingEnabled is true.");
            foreach (var text in hosting.TrustedProxies)
            {
                if (string.IsNullOrWhiteSpace(text) || !text.Contains('.') && !text.Contains(':') ||
                    !IPAddress.TryParse(text, out var address) || !SpecificUnicast(address))
                    throw new InvalidOperationException("HttpHosting:TrustedProxies accepts only specific unicast or loopback IP literals, not hostnames, subnets, wildcard, multicast or unspecified addresses.");
                proxies.Add(address);
            }
            var hosts = builder.Configuration["AllowedHosts"]?.Split(';');
            if (hosts is not { Length: > 0 } || hosts.Any(host => string.IsNullOrWhiteSpace(host) || host.Contains('*')))
                throw new InvalidOperationException("Forwarding requires explicit AllowedHosts names without wildcard. Include the intended public and local host names.");
        }
        builder.Services.AddSingleton(hosting);
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = hosting.ForwardingEnabled ? ForwardedHeaders.XForwardedProto : ForwardedHeaders.None;
            options.ForwardLimit = 1;
            if (hosting.ForwardingEnabled)
            {
                // Never clear defaults and leave enabled forwarding with empty trust.
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
                foreach (var address in proxies) options.KnownProxies.Add(address);
            }
        });
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.RespectRequiredConstructorParameters = true);
        builder.Services.AddExceptionHandler<HttpFailureHandler>();
    }

    private static bool SpecificUnicast(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast) return false;
        var bytes = address.GetAddressBytes();
        return bytes.Length != 4 || bytes[0] is > 0 and < 224;
    }

    public static void UseHttpIntegration(this WebApplication app)
    {
        var exceptions = new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => false };
        if (!app.Environment.IsDevelopment())
        {
            exceptions.ExceptionHandlingPath = "/tools/error";
            exceptions.CreateScopeForErrors = true;
        }
        else
        {
            // Native middleware requires a fallback even with IExceptionHandler.
            // Unmapped document failures still escape to the Development host;
            // only the approved API/denial paths below are replaced.
            exceptions.ExceptionHandler = context => Task.FromException(context.Features.Get<IExceptionHandlerFeature>()!.Error);
        }
        app.UseExceptionHandler(exceptions);
        app.Use(async (context, next) =>
        {
            try { await next(); }
            catch (Exception exception) { context.ObserveHttpException(exception); throw; }
        });
        if (app.Services.GetRequiredService<HttpHostingOptions>().ForwardingEnabled) app.UseForwardedHeaders();
        if (!app.Environment.IsDevelopment()) app.UseHsts();
        app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), api =>
        {
            api.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                await next();
            });
            // Native status pages invoke this only for an empty, unstarted response.
            api.UseStatusCodePages(async status => await BasicApiErrorAsync(status.HttpContext));
        });
    }

    internal static Task BasicApiErrorAsync(HttpContext context)
    {
        var (code, message) = context.Response.StatusCode switch
        {
            404 => ("route", "This API route does not exist."),
            405 => ("method", "This HTTP method is not supported for this API route."),
            400 or 415 => ("invalid", "The request is malformed or uses an unsupported media type."),
            _ => ("failure", "The request could not be completed. The outcome could not be confirmed.")
        };
        return V3CatalogueHttp.ErrorAsync(context, context.Response.StatusCode, code, message);
    }
}

internal sealed class HttpFailureHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken token)
    {
        if (context.Response.HasStarted) return false; // Native middleware rethrows started failures before calling handlers.
        // Native path handling changes Request.Path before invoking handlers. Use
        // its captured original for API classification and document/download denial.
        var handlingPath = context.Request.Path;
        if (context.Features.Get<IExceptionHandlerFeature>() is { } feature) context.Request.Path = feature.Path;
        try
        {
            return await HandleAsync(context, exception);
        }
        finally { context.Request.Path = handlingPath; }
    }

    private static async Task<bool> HandleAsync(HttpContext context, Exception exception)
    {
        if (exception is AccessDeniedException denial)
        {
            await AuthenticationRegistration.Deny(context, denial);
            return true;
        }
        if (!context.Request.Path.StartsWithSegments("/api")) return false;
        context.Response.Headers.CacheControl = "no-store";
        var error = exception switch
        {
            BadHttpRequestException bad => (bad.StatusCode, "invalid", "The request could not be read."),
            V3CatalogueHttp.RequestBindingException => (400, "invalid", "The request is malformed or incomplete."),
            RoomTrackingUnavailableException => (410, "tracking-unavailable", "Room tracking is no longer available. Some submitted work may already be saved. Copy pending work before refreshing."),
            RoomTrackingProtocolException => (400, "invalid", "The room request does not match its retained visit or operation evidence."),
            V3CatalogueHttp.ScenePayloadTooLargeException => (413, "scene-payload-too-large", "Scene request exceeds 256 MiB. No facts were truncated."),
            SceneTransportException => (400, "invalid", "The scene request is malformed or incomplete."),
            SceneEvidenceStaleException => (409, "scene-stale", context.Features.Get<IExceptionHandlerFeature>()?.Endpoint?.Metadata.GetMetadata<V3CatalogueHttp.InverseSceneOperation>() is not null
                ? "Inverse proposal changed or is no longer eligible. No inverse fields were written; the import remains committed."
                : "Current scene records changed. Your choices are retained; restart evidence review explicitly before applying."),
            SceneWorkbenchValidationException validation => (422, "scene-ineligible", validation.Message),
            _ => (500, "failure", "An unexpected error occurred. The outcome could not be confirmed.")
        };
        await V3CatalogueHttp.ErrorAsync(context, error.Item1, error.Item2, error.Item3);
        return true;
    }
}
