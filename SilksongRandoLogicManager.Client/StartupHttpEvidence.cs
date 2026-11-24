using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.JavaScript;

namespace SilksongRandoLogicManager.Client;

// Opt-in request observation. A failed call associates the observation with its
// original exception, never with a subsequent request or a process-wide last error.
internal sealed class StartupHttpEvidence
{
    internal static readonly HttpRequestOptionsKey<StartupHttpEvidence> Option = new("LogicRequiredStartup");
    private static readonly ConditionalWeakTable<Exception, StartupHttpEvidence> Failures = new();
    internal int? Status { get; set; }
    internal bool NoResponse { get; private set; }

    internal void ObserveTransportFailure(HttpRequestException error, CancellationToken token)
    {
        if (token.IsCancellationRequested || error.InnerException is not JSException) return;
        // .NET 10 BrowserHttpController.CallFetch awaits the Fetch promise in the
        // non-generic BrowserHttpInterop.CancellationHelper; that helper translates
        // its JS rejection to HttpRequestException. Startup sends only fixed HTTP
        // requests (the POST uses buffered JsonContent, not streaming upload).
        // Synchronous Fetch setup/serialization and response conversion do not
        // originate there, and body reads are outside this handler send boundary.
        // Use CLR method identity, not browser-specific exception message text.
        try
        {
            var site = error.TargetSite;
            var owner = site?.DeclaringType;
            if (owner?.DeclaringType?.FullName == "System.Net.Http.BrowserHttpInterop" && site!.Name == "MoveNext"
                && owner.Name.StartsWith("<CancellationHelper>d__", StringComparison.Ordinal)
                && !owner.IsGenericType && owner.Assembly == typeof(HttpClient).Assembly)
                NoResponse = true;
        }
        catch (Exception) { /* Unavailable stack metadata is not transport evidence; keep the original error. */ }
    }

    internal void Associate(Exception error)
    {
        if (Status is >= 500 and <= 599 || NoResponse)
            Failures.GetValue(error, _ => this);
    }

    internal static StartupHttpEvidence? ForFailure(Exception error) => Failures.TryGetValue(error, out var evidence) ? evidence : null;
}
