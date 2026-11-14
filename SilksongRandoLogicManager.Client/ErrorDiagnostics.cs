using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace SilksongRandoLogicManager.Client;

// Single-dialog, RAM-only disclosure model. Not an exception serialization framework.
public sealed record DiagnosticFrame(string Source, string? Method, int? Line = null, int? Column = null);
public sealed record DiagnosticCause(string Type, string? Method, ImmutableArray<DiagnosticFrame> Frames,
    int? SqliteCode = null, int? HttpStatus = null, string? Code = null, string? Trace = null, bool Unavailable = false);
public sealed record DiagnosticSnapshot(int Version, string Origin, string Stage, string Operation, string Query,
    long? RequestId, ImmutableArray<DiagnosticCause> Causes, bool Truncated = false);
internal interface IDiagnosticFailure { DiagnosticSnapshot Diagnostic { get; } }
internal sealed class LocalCacheFailure(DiagnosticSnapshot diagnostic, Exception? inner = null)
    : Exception("The local confirmed-cache operation failed. Authoring work was not replayed.", inner), IDiagnosticFailure
{
    public DiagnosticSnapshot Diagnostic { get; } = ErrorDiagnostics.Validate(diagnostic);
}
internal static class ErrorDiagnostics
{
    private static readonly HashSet<string> Origins = ["Owner", "Broker", "Tab", "Managed", "Shell", "Unknown"];
    private static readonly HashSet<string> Stages = ["request", "initialize", "postMessage", "messageerror", "error", "timeout", "retirement", "visibility", "interop", "feature", "http", "Unknown"];
    private static readonly HashSet<string> Operations = ["state", "query", "install", "patch", "inspect"];
    private static readonly HashSet<string> Queries = ["catalogue", "inventory", "vocabulary", "maps", "room"];
    private static readonly HashSet<string> Sources = ["Client", "Contracts", "logicCacheOwner.mjs", "logicCacheQueries.mjs", "logicCacheBroker.mjs", "logicCache.mjs", "catalogue.js", "sqlite3.mjs"];
    private static readonly HashSet<string> JsTypes = ["Error", "TypeError", "RangeError", "SyntaxError", "ReferenceError", "URIError", "EvalError", "DOMException", "SQLite3Error", "UnknownError", "UnknownThrownValue"];
    private static readonly IReadOnlyDictionary<string, HashSet<string>> JsMethods = new Dictionary<string, HashSet<string>>
    {
        ["logicCacheQueries.mjs"] = ["extractNamedQuery", "select", "index", "type", "invalid", "text", "integer", "definitions", "selected"],
        ["logicCacheOwner.mjs"] = ["enqueue", "openDatabase", "state", "schema", "validateIntegrity", "assertNoTriggers", "writeState", "conflict", "install", "typedCell", "tableRows", "sameRow", "patch", "inspect", "query", "operate", "retire"],
        ["logicCacheBroker.mjs"] = ["fail", "retireOwner", "dispatch", "retire"],
        ["logicCache.mjs"] = ["rejectAll", "retire", "connect", "requestRaw", "request", "yieldTask", "visibleAgain", "pageHide", "pageShow", "initialize", "dispose"],
        ["catalogue.js"] = ["showError", "closeError", "restoreErrorFocus", "restoreFocus", "eligibleFocus", "visible", "showDialog", "copyDiagnosticDetails"],
        ["sqlite3.mjs"] = []
    };
    private static string? JsMethod(string source, string? method) => method is not null && JsMethods.TryGetValue(source, out var names) && names.Contains(method) ? method : null;
    // Existing AUTH1/V3 error codes only. Unknown response codes are not displayed.
    private static readonly HashSet<string> Codes = ["authentication_required", "password_replacement_required", "forbidden", "antiforgery_required", "not_found", "invalid", "route", "media", "failure", "missing", "conflict", "tracking-unavailable"];
    private static string Token(HashSet<string> set, string? value) => value is not null && set.Contains(value) ? value : "Unknown";
    internal static string? Trace(string? value) => value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-') ? value : null;
    private static string? Metadata(string? value) => value is { Length: > 0 and <= 256 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '`' or '<' or '>') ? value : null;
    private static int? Position(int? value) => value > 0 ? value : null;
    internal static DiagnosticSnapshot Unavailable(string origin = "Managed", string stage = "interop") =>
        new(1, Token(Origins, origin), Token(Stages, stage), "Unknown", "Unknown", null,
            [new("UnknownError", null, [], Unavailable: true)]);

    internal static DiagnosticSnapshot Validate(DiagnosticSnapshot? snapshot)
    {
        try
        {
            if (snapshot is not { Version: 1 } || snapshot.Causes.IsDefaultOrEmpty) return Unavailable();
            var causes = ImmutableArray.CreateBuilder<DiagnosticCause>();
            var remaining = 16;
            var truncated = snapshot.Truncated || snapshot.Causes.Length > 4;
            var managed = snapshot.Origin is "Managed" or "Shell";
            foreach (var cause in snapshot.Causes.Take(4))
            {
                var frames = ImmutableArray.CreateBuilder<DiagnosticFrame>();
                if (!cause.Frames.IsDefault)
                {
                    if (cause.Frames.Length > remaining) truncated = true;
                    foreach (var frame in cause.Frames.Take(remaining))
                    {
                        if (!Sources.Contains(frame.Source)) continue;
                        var clr = frame.Source is "Client" or "Contracts";
                        if (clr && !managed) continue;
                        frames.Add(new(frame.Source, clr ? Metadata(frame.Method) : JsMethod(frame.Source, frame.Method),
                            clr ? null : Position(frame.Line), clr ? null : Position(frame.Column)));
                    }
                }
                remaining -= frames.Count;
                causes.Add(new(managed ? Metadata(cause.Type) ?? "UnknownError" : JsTypes.Contains(cause.Type) ? cause.Type : "UnknownError",
                    managed ? Metadata(cause.Method) : null,
                    frames.ToImmutable(), cause.SqliteCode, cause.HttpStatus is >= 100 and <= 599 ? cause.HttpStatus : null,
                    Codes.Contains(cause.Code ?? "") ? cause.Code : null, Trace(cause.Trace), cause.Unavailable || frames.Count == 0));
            }
            return new(1, Token(Origins, snapshot.Origin), Token(Stages, snapshot.Stage), Token(Operations, snapshot.Operation), Token(Queries, snapshot.Query),
                snapshot.RequestId is >= 0 and <= 9007199254740991 ? snapshot.RequestId : null, causes.ToImmutable(), truncated);
        }
        catch { return Unavailable(); }
    }
    internal static DiagnosticSnapshot FromLocal(DiagnosticSnapshot? snapshot) => snapshot is null ? Unavailable() :
        Validate(snapshot with { Origin = snapshot.Origin is "Owner" or "Broker" or "Tab" ? snapshot.Origin : "Unknown" });
    internal static DiagnosticSnapshot RequestContext(DiagnosticSnapshot snapshot, string operation, string query) =>
        Validate(snapshot with { Operation = snapshot.Operation == "Unknown" ? Token(Operations, operation) : snapshot.Operation,
            Query = snapshot.Query == "Unknown" ? Token(Queries, query) : snapshot.Query });
    private static string? TypeName(Type? type) => type is null ? null : Metadata((type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName);
    private static DiagnosticFrame? Method(MethodBase? method)
    {
        var type = method?.DeclaringType;
        var assembly = type?.Assembly.GetName().Name;
        var source = assembly == "SilksongRandoLogicManager.Client" ? "Client" : assembly == "SilksongRandoLogicManager.Contracts" ? "Contracts" : null;
        var name = TypeName(type);
        var member = Metadata(method?.Name);
        return source is not null && name is not null && member is not null ? new(source, Metadata(name + "." + member)) : null;
    }
    internal static DiagnosticSnapshot Capture(Exception exception, string stage = "feature", string? trace = null, int? status = null, string? code = null)
    {
        try
        {
            Exception? current = exception;
            var causes = ImmutableArray.CreateBuilder<DiagnosticCause>();
            var remaining = 16;
            var examined = 0;
            var truncated = false;
            for (var i = 0; i < 4 && current is not null; ++i)
            {
                // Preserve the originating immutable snapshot across finite wrappers.
                if (current is IDiagnosticFailure existing) return WithHttp(existing.Diagnostic, trace, status, code);
                var frames = ImmutableArray.CreateBuilder<DiagnosticFrame>();
                try
                {
                    var stack = new StackTrace(current, false);
                    var at = 0;
                    for (; at < stack.FrameCount && remaining > 0 && examined < 16; ++at, ++examined)
                        if (Method(stack.GetFrame(at)?.GetMethod()) is { } frame) { frames.Add(frame); --remaining; }
                    if (at < stack.FrameCount) truncated = true;
                    if (frames.Count == 0 && remaining > 0 && Method(current.TargetSite) is { } target) { frames.Add(target); --remaining; }
                }
                catch { /* WASM/AOT metadata can be unavailable. Never use raw StackTrace. */ }
                var http = current as HttpRequestException;
                var denied = current as SessionAccessException;
                causes.Add(new(TypeName(current.GetType()) ?? "UnknownError", frames.FirstOrDefault()?.Method, frames.ToImmutable(),
                    HttpStatus: http?.StatusCode is { } s ? (int)s : null, Code: denied?.Denial.Code, Unavailable: frames.Count == 0 || current is Microsoft.JSInterop.JSException));
                current = current.InnerException;
            }
            return WithHttp(new(1, "Managed", Token(Stages, stage), "Unknown", "Unknown", null, causes.ToImmutable(), truncated || current is not null || remaining == 0), trace, status, code);
        }
        catch { return Unavailable("Managed", stage); }
    }
    private static DiagnosticSnapshot WithHttp(DiagnosticSnapshot snapshot, string? trace, int? status, string? code)
    {
        var value = Validate(snapshot);
        if (trace is null && status is null && code is null) return value;
        var first = value.Causes[0];
        return Validate(value with { Causes = value.Causes.SetItem(0, first with { Trace = Trace(trace), HttpStatus = status ?? first.HttpStatus, Code = code ?? first.Code }) });
    }
    internal static DiagnosticSnapshot HttpFailure(Type type, string? trace, int? status, string? code) =>
        WithHttp(new(1, "Managed", "http", "Unknown", "Unknown", null, [new(TypeName(type) ?? "UnknownError", null, [], Unavailable: true)]), trace, status, code);
    internal static string Render(DiagnosticSnapshot snapshot)
    {
        var value = Validate(snapshot);
        var text = new StringBuilder("Diagnostic version: 1\n");
        text.AppendLine($"Origin: {value.Origin}; stage: {value.Stage}; operation: {value.Operation}; query: {value.Query}");
        if (value.RequestId is { } id) text.AppendLine($"Local request: {id}");
        for (var i = 0; i < value.Causes.Length; ++i)
        {
            var cause = value.Causes[i];
            text.AppendLine($"Cause {i + 1}: {cause.Type}");
            if (cause.SqliteCode is { } sqlite) text.AppendLine($"SQLite resultCode: {sqlite}");
            if (cause.HttpStatus is { } status) text.AppendLine($"HTTP status: {status}");
            if (cause.Code is { } code) text.AppendLine($"Code: {code}");
            if (cause.Trace is { } trace) text.AppendLine($"Log reference: {trace}");
            if (cause.Method is { } method) text.AppendLine($"Method: {method}");
            foreach (var frame in cause.Frames)
                text.AppendLine($"  {frame.Source}{(frame.Line is { } line ? ":" + line : "")}{(frame.Column is { } column ? ":" + column : "")} — {frame.Method ?? "method unavailable"}");
            if (cause.Unavailable) text.AppendLine("Additional originating/stack metadata unavailable.");
        }
        const string marker = "\n[details truncated]";
        if (value.Truncated) text.Append(marker);
        return text.Length <= 8192 ? text.ToString() : text.ToString(0, 8192 - marker.Length) + marker;
    }
}
