using System.Collections.Immutable;
using System.Text.Json;

namespace Silksong_Rando_Logic_Manager.Services.HttpLogging;

internal sealed record HttpLogEnvelope(string RequestId, DateTime StartedUtc, DateTime FinishedUtc,
    string Method, string Path, string QueryString, string AuthenticationState, string? AccountId,
    int? ObservedStatusCode, string Outcome, double TotalElapsedMs, double? ResponseStartElapsedMs,
    double? ResponseCompletionElapsedMs, ImmutableArray<HttpLogException> Exceptions);

internal sealed record HttpLogException(DateTime OccurredUtc, int Ordinal, string ExceptionType,
    string Message, string ExceptionText);

internal static class HttpExceptionDetail
{
    // An explicit graph representation, not Exception.ToString(), Data, or arbitrary
    // exception serialization. Node references retain every aggregate branch and
    // shared inner object without recursive traversal/stack overflow.
    private sealed record Node(int Id, string Type, string Message, string? StackTrace, int[] InnerExceptions);

    internal static HttpLogException Detach(Exception root, DateTime occurredUtc, int ordinal)
    {
        var ids = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance) { [root] = 0 };
        var pending = new List<Exception> { root };
        var nodes = new List<Node>();
        for (var i = 0; i < pending.Count; i++)
        {
            var exception = pending[i];
            IEnumerable<Exception> children = exception is AggregateException aggregate
                ? aggregate.InnerExceptions
                : exception.InnerException is { } inner ? [inner] : [];
            var links = new List<int>();
            foreach (var child in children)
            {
                if (!ids.TryGetValue(child, out var id))
                {
                    id = pending.Count;
                    ids.Add(child, id);
                    pending.Add(child);
                }
                links.Add(id);
            }
            nodes.Add(new(i, exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message, exception.StackTrace, links.ToArray()));
        }
        return new(occurredUtc, ordinal, nodes[0].Type, nodes[0].Message, JsonSerializer.Serialize(nodes));
    }
}
