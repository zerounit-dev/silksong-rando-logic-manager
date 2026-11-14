using System.Net;
using System.Text.Json;
using Microsoft.JSInterop;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

/// <summary>One irreversible application-runtime admission owner, not an auth state.</summary>
public sealed class ClientRuntime(IJSRuntime js)
{
    public bool Retired { get; private set; }
    public string? ServerVersion { get; private set; }
    public bool Installing { get; private set; }
    public bool Discarding { get; private set; }
    public enum UpdateFailure { Preparation, RequiredCode, IntentRead, ReloadStart }
    public UpdateFailure? InstallationFailure { get; private set; }
    public bool WorkAtRetirement { get; private set; } = true;
    private bool installIntentRead, installationStartup, intentStartedDocument, automaticAttempted, observingRetirement;
    private bool retainedMutationUncertainty;
    private readonly HashSet<MutationObservation> dispatchedMutations = [];
    public bool HasUncertainMutation => retainedMutationUncertainty || dispatchedMutations.Count != 0;
    public void RetainUncertainMutation() => retainedMutationUncertainty = true;
    public MutationObservation ObserveMutation() => new(this);
    // One document-owned account, independent of mounted components and error UI.
    // No-send preparation is harmless; a dispatched operation stays observed even
    // after its feature is disposed. Only a known outcome removes its live evidence.
    public sealed class MutationObservation(ClientRuntime runtime) : IDisposable
    {
        public void Dispatched() => runtime.dispatchedMutations.Add(this);
        public void OutcomeKnown() => runtime.dispatchedMutations.Remove(this);
        public void Dispose()
        {
            if (runtime.dispatchedMutations.Remove(this)) runtime.RetainUncertainMutation();
        }
    }
    public bool RetryRequired => installationStartup || InstallationFailure is { } failure
        && (failure != UpdateFailure.RequiredCode || intentStartedDocument);
    public bool UpdateAttemptObserved => intentStartedDocument || automaticAttempted;
    public bool CanAutomaticallyUpdate => Retired && !WorkAtRetirement && installIntentRead
        && !Installing && !RetryRequired && !automaticAttempted && !Discarding;
    public bool AutomaticUpdateInProgress => CanAutomaticallyUpdate
        || Discarding && automaticAttempted && !RetryRequired;
    // Synchronous loss-risk observation only. Owners must still be live/Current here.
    public event Func<bool>? ObserveUpdateRisk;
    public event Action? Changed;
    public void RequireActive() { if (Retired) throw new ClientRetiredException(); }
    public void Retire(string? serverVersion = null)
    {
        var changed = ApplicationVersion.Valid(serverVersion) && serverVersion != ServerVersion;
        if (changed) ServerVersion = serverVersion;
        if (Retired) { if (changed) Changed?.Invoke(); return; }
        if (observingRetirement) return;
        observingRetirement = true;
        try
        {
            WorkAtRetirement = HasUncertainMutation || ObserveUpdateRisk is null;
            if (ObserveUpdateRisk is { } observers)
                foreach (Func<bool> observe in observers.GetInvocationList())
                {
                    try { WorkAtRetirement |= observe(); }
                    catch (Exception error) { Console.Error.WriteLine(error); WorkAtRetirement = true; }
                }
        }
        finally { observingRetirement = false; Retired = true; }
        Changed?.Invoke();
    }
    public async Task<bool> ReadInstallIntentAsync()
    {
        RequireActive();
        try { Installing = await js.InvokeAsync<string?>("sessionStorage.getItem", "silksong.logic.clientUpdate.required") == "1"; }
        catch (JSException) { InstallationFailed(UpdateFailure.IntentRead); throw; }
        RequireActive(); installIntentRead = true; installationStartup = Installing; intentStartedDocument = Installing;
        return Installing;
    }
    // Keep the intent-derived retry guard through all post-install startup construction.
    public void CompleteStartup() { RequireActive(); installationStartup = false; }
    // Required feature imports can occur after shell startup clears the storage intent.
    // Keep their attempt provenance without reclassifying later verified mismatches.
    public void RequiredCodeFailed()
    {
        // Do not clear an already established retry-only failure on an ordinary
        // document if another required import finishes late after retirement.
        if (intentStartedDocument) InstallationFailure = UpdateFailure.RequiredCode;
        else InstallationFailure ??= UpdateFailure.RequiredCode;
        Retire();
    }
    public async Task FinishInstallationAsync()
    {
        RequireActive();
        await js.InvokeVoidAsync("sessionStorage.removeItem", "silksong.logic.clientUpdate.required");
        RequireActive(); Installing = false;
    }
    public void InstallationFailed(UpdateFailure failure = UpdateFailure.Preparation)
    {
        InstallationFailure = failure;
        Retire();
    }
    public async Task ReloadForUpdateAsync(Action? discardWarning = null, bool automatic = false)
    {
        if (Discarding) return;
        if (automatic)
        {
            if (!CanAutomaticallyUpdate) return;
            automaticAttempted = true;
        }
        Retire(); Discarding = true; Changed?.Invoke();
        try
        {
            await js.InvokeVoidAsync("sessionStorage.setItem", "silksong.logic.clientUpdate.required", "1");
            discardWarning?.Invoke();
            // Native full reload preserves the exact path, query and fragment.
            await js.InvokeVoidAsync("location.reload");
        }
        catch
        {
            Discarding = false;
            InstallationFailure = UpdateFailure.ReloadStart;
            Changed?.Invoke();
        }
    }
    public async Task CheckResponseAsync(HttpResponseMessage response, CancellationToken token)
    {
        // Real security denials keep their existing precedence and local owner.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return;
        if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase)) throw new HttpRequestException("An application response was expected; no operation was repeated.");
        if (!response.Headers.TryGetValues(ApplicationVersion.Header, out var versions)) throw new HttpRequestException("The application response identity could not be verified. No operation was repeated.");
        var values = versions.ToArray();
        if (values.Length != 1 || !ApplicationVersion.Valid(values[0])) throw new HttpRequestException("The application response identity could not be verified. No operation was repeated.");
        var version = values[0];
        if (version != ApplicationBuild.Version) { Retire(version); throw new ClientRetiredException(); }
        if (response.StatusCode == HttpStatusCode.Conflict && string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            // HttpContent buffers this read; domain409 consumers retain their body.
            var bytes = await response.Content.ReadAsByteArrayAsync(token);
            using var body = JsonDocument.Parse(bytes);
            if (body.RootElement.TryGetProperty("code", out var code) && code.GetString() == ApplicationVersion.MismatchCode)
            {
                if (!body.RootElement.TryGetProperty("serverVersion", out var server) || server.GetString() != version)
                    throw new HttpRequestException("The update response could not be verified.");
                Retire(version); throw new ClientRetiredException();
            }
        }
        RequireActive();
    }
}

public sealed class ClientRetiredException() : InvalidOperationException("This application runtime has retired. Update your client; no command was repeated.");
