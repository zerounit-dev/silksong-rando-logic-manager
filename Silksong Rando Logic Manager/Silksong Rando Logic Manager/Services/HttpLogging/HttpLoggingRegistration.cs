using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Silksong_Rando_Logic_Manager.Services.HttpLogging;

internal static class HttpLoggingRegistration
{
    private sealed record Classification;
    internal static void AddHttpRequestLogging(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<HttpLogWriter>();
        builder.Services.AddHostedService(services => services.GetRequiredService<HttpLogWriter>());
        builder.Services.AddSingleton<ILoggerProvider, HttpExceptionLoggerProvider>();
        builder.Services.PostConfigure<LoggerFilterOptions>(options =>
        {
            // These rules belong ONLY to this provider. Do not change console,
            // Debug or native framework logging levels/diagnostic suppression.
            var provider = typeof(HttpExceptionLoggerProvider).FullName!;
            for (var i = options.Rules.Count - 1; i >= 0; i--)
                if (options.Rules[i].ProviderName == provider) options.Rules.RemoveAt(i);
            options.Rules.Add(new(provider, null, LogLevel.Trace, (_, category, level) =>
                level != LogLevel.None && HttpExceptionLoggerProvider.CategoryAllowed(category)));
        });
    }

    internal static void UseHttpRequestLogging(this WebApplication app)
    {
        var sink = app.Services.GetRequiredService<HttpLogWriter>();
        app.Use(async (context, next) =>
        {
            if (context.Features.Get<Classification>() is not null)
            { await next(); return; } // Reentry is not the original invocation.
            HttpRequestObservation? observation = null;
            try
            {
                context.Features.Set(new Classification());
                var path = context.Request.Path;
                var eligible = path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWithSegments("/exports", StringComparison.OrdinalIgnoreCase);
                if (eligible && !V3CatalogueHttp.NativeDraftPreview(context.Request))
                {
                    observation = new(context, sink);
                    context.Features.Set(observation);
                    var owned = observation;
                    context.Response.OnStarting(() => { owned.Starting(); return Task.CompletedTask; });
                    context.Response.OnCompleted(() =>
                    {
                        try { owned.Completed(context.Response.HasStarted, context.Response.StatusCode); }
                        catch (Exception) { owned.Failed(); }
                        return Task.CompletedTask;
                    });
                    owned.RegisterCancellation(context.RequestAborted);
                }
            }
            catch (Exception)
            {
                if (observation is null) sink.DropCapture();
                else observation.Failed();
            }
            try { await next(); }
            catch (Exception exception)
            {
                observation?.Observe(exception);
                try { observation?.Escaping(context.Response.HasStarted); }
                catch (Exception) { observation?.Failed(); }
                throw;
            }
            finally
            {
                try { observation?.Unwind(context.Response.HasStarted, context.Response.StatusCode); }
                catch (Exception) { observation?.Failed(); }
            }
        });
    }

    internal static void ObserveHttpException(this HttpContext context, Exception exception)
    {
        try { context.Features.Get<HttpRequestObservation>()?.Observe(exception); }
        catch (Exception) { /* Observational failure must never replace application failure. */ }
    }

    internal static void SampleHttpLogIdentity(this HttpContext context)
    {
        try { context.Features.Get<HttpRequestObservation>()?.SampleIdentity(context.User); }
        catch (Exception) { /* No admission or identity change for diagnostics. */ }
    }
}

internal sealed class HttpExceptionLoggerProvider(IHttpContextAccessor http) : ILoggerProvider
{
    internal static bool CategoryAllowed(string? category) => category is not null &&
        ((Within(category, "Silksong_Rando_Logic_Manager.Services") &&
          !Within(category, "Silksong_Rando_Logic_Manager.Services.Publishing") &&
          !Within(category, "Silksong_Rando_Logic_Manager.Services.HttpLogging")) ||
         Within(category, "Silksong_Rando_Logic_Manager.Authentication") ||
         category == "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware" ||
         category == "Microsoft.AspNetCore.Server.Kestrel");

    private static bool Within(string value, string prefix) => value == prefix || value.StartsWith(prefix + ".", StringComparison.Ordinal);
    public ILogger CreateLogger(string categoryName) => new Observer(http, categoryName);
    public void Dispose() { }

    private sealed class Observer(IHttpContextAccessor http, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && CategoryAllowed(category);
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is null || !IsEnabled(level) ||
                category == "Microsoft.AspNetCore.Server.Kestrel" && eventId.Id != 13) return;
            // Never invoke formatter or inspect state/scopes. The existing accessor
            // supplies correlation only while an eligible physical request is active.
            try { http.HttpContext?.ObserveHttpException(exception); }
            catch (Exception) { }
        }
    }
}
