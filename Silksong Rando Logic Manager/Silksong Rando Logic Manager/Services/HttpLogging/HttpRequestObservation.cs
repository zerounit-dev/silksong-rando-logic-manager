using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Silksong_Rando_Logic_Manager.Authentication;

namespace Silksong_Rando_Logic_Manager.Services.HttpLogging;

// This feature belongs to the physical request, not a reexecuted DI scope. Only
// detached strings/scalars cross into the queue; callbacks never transfer context.
internal sealed class HttpRequestObservation
{
    private readonly object gate = new();
    private readonly HttpLogWriter sink;
    private readonly string requestId, method, path, query;
    private readonly DateTime startedUtc = DateTime.UtcNow;
    private readonly long entered = Stopwatch.GetTimestamp();
    private readonly HashSet<Exception> seen = new(ReferenceEqualityComparer.Instance);
    private readonly List<(Exception Error, DateTime Utc)> exceptions = [];
    private string authenticationState = "unobserved";
    private string? accountId;
    private bool identitySampled, aborted, unwound, sealedEnvelope, captureFailed, escapingStarted;
    private bool dispositionTaken;
    private long? starting;
    private int? lastStartedStatus;
    private CancellationTokenRegistration cancellation;

    internal HttpRequestObservation(HttpContext context, HttpLogWriter sink)
    {
        this.sink = sink;
        requestId = context.TraceIdentifier;
        method = context.Request.Method;
        (path, query) = OriginalTarget(context);
    }

    private static (string Path, string Query) OriginalTarget(HttpContext context)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw))
            return (context.Request.PathBase.ToUriComponent() + context.Request.Path.ToUriComponent(),
                context.Request.QueryString.ToUriComponent());
        // Strip only absolute-form scheme/authority. Uri.PathAndQuery would
        // canonicalize escapes and is deliberately not used.
        var scheme = raw.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0 && (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                           raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            var end = raw.IndexOfAny(['/', '?'], scheme + 3);
            raw = end < 0 ? "/" : raw[end] == '?' ? "/" + raw[end..] : raw[end..];
        }
        var split = raw.IndexOf('?');
        return split < 0 ? (raw, "") : (raw[..split], raw[split..]);
    }

    internal void RegisterCancellation(CancellationToken token)
    {
        // Register may synchronously call Abort. Do not hold gate or assume the
        // registration has already been assigned when that happens.
        var registration = token.UnsafeRegister(static state => ((HttpRequestObservation)state!).Abort(), this);
        bool terminal;
        lock (gate) { cancellation = registration; terminal = sealedEnvelope; }
        if (terminal) registration.Unregister();
    }

    internal void SampleIdentity(ClaimsPrincipal principal)
    {
        Safely(() =>
        {
            lock (gate)
            {
                if (sealedEnvelope || identitySampled) return;
                identitySampled = true;
                if (principal.Identity?.IsAuthenticated != true) authenticationState = "anonymous";
                else if (principal.Identity.AuthenticationType == "LogicManager")
                {
                    var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
                    if (principal.HasClaim(AccessPolicies.BypassClaim, "true") && id == "bypass")
                        authenticationState = "bypass";
                    else if (!principal.HasClaim(AccessPolicies.BypassClaim, "true") && Guid.TryParse(id, out var guid))
                    { authenticationState = "account"; accountId = guid.ToString("D"); }
                }
            }
        });
    }

    internal void Observe(Exception exception) => Safely(() =>
    {
        lock (gate)
        {
            if (!sealedEnvelope && !captureFailed && seen.Add(exception)) exceptions.Add((exception, DateTime.UtcNow));
        }
    });

    internal void Starting() => Safely(() =>
    {
        lock (gate) { if (!sealedEnvelope) starting ??= Stopwatch.GetTimestamp(); }
    });

    internal void Escaping(bool hasStarted) => Safely(() =>
    {
        lock (gate) { if (!sealedEnvelope) escapingStarted |= hasStarted; }
    });

    internal void Unwind(bool hasStarted, int status) => Safely(() =>
    {
        Frozen? frozen;
        lock (gate)
        {
            if (sealedEnvelope) return;
            if (hasStarted) lastStartedStatus = SafeStatus(status);
            unwound = true;
            frozen = aborted ? Seal(false, null) : null;
        }
        Handoff(frozen);
    });

    private void Abort() => Safely(() =>
    {
        Frozen? frozen;
        lock (gate)
        {
            if (sealedEnvelope) return;
            aborted = true;
            frozen = unwound ? Seal(false, null) : null;
        }
        Handoff(frozen);
    });

    internal void Completed(bool hasStarted, int status) => Safely(() =>
    {
        Frozen? frozen;
        lock (gate)
        {
            if (sealedEnvelope) return;
            if (hasStarted) lastStartedStatus = SafeStatus(status);
            frozen = Seal(!aborted && !escapingStarted, SafeStatus(status));
        }
        Handoff(frozen);
    });

    private static int? SafeStatus(int status) => status is >= 100 and <= 599 ? status : null;

    private sealed record Frozen(HttpLogEnvelope Envelope, (Exception Error, DateTime Utc)[] Errors, bool Failed);

    // gate is held. The winner fixes the cutoff BEFORE formatting or TryWrite.
    private Frozen Seal(bool completed, int? callbackStatus)
    {
        sealedEnvelope = true;
        var finishedUtc = DateTime.UtcNow;
        var terminal = Stopwatch.GetTimestamp();
        var total = Stopwatch.GetElapsedTime(entered, terminal).TotalMilliseconds;
        var start = starting is { } stamp ? Stopwatch.GetElapsedTime(entered, stamp).TotalMilliseconds : (double?)null;
        var completion = completed && starting is { } begin
            ? Stopwatch.GetElapsedTime(begin, terminal).TotalMilliseconds : (double?)null;
        var status = completed ? callbackStatus : lastStartedStatus;
        var frozen = new Frozen(new(requestId, startedUtc, finishedUtc, method, path, query,
            authenticationState, accountId, status, completed ? "completed" : "incomplete", total,
            start, completion, []), exceptions.ToArray(), captureFailed || completed && status is null);
        exceptions.Clear();
        seen.Clear();
        return frozen;
    }

    private void Handoff(Frozen? frozen)
    {
        if (frozen is null) return;
        CancellationTokenRegistration registration;
        bool taken;
        lock (gate)
        {
            registration = cancellation;
            taken = dispositionTaken;
            dispositionTaken = true;
        }
        registration.Unregister(); // Nonblocking; never Dispose/wait under gate.
        if (taken) return;
        if (frozen.Failed) { sink.DropCapture(); return; }
        try
        {
            var detached = ImmutableArray.CreateBuilder<HttpLogException>(frozen.Errors.Length);
            for (var i = 0; i < frozen.Errors.Length; i++)
                detached.Add(HttpExceptionDetail.Detach(frozen.Errors[i].Error, frozen.Errors[i].Utc, i + 1));
            sink.Submit(frozen.Envelope with { Exceptions = detached.MoveToImmutable() });
        }
        catch (Exception) { sink.DropCapture(); }
    }

    internal void Failed()
    {
        bool count = false;
        lock (gate)
        {
            captureFailed = true;
            if (!dispositionTaken) { dispositionTaken = true; count = true; }
        }
        if (count) sink.DropCapture();
    }
    private void Safely(Action action)
    {
        try { action(); }
        catch (Exception) { Failed(); }
    }
}
