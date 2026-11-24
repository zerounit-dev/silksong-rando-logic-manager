using System.Net;
using System.Text.Json;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

public sealed class SessionAccess
{
    private readonly TaskCompletionSource initial = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task InitialSession => initial.Task;
    public AuthenticationSession? Session { get; private set; }
    public bool CanAuthor => Session is { ReplacementRequired: false, EditLogic: true };
    public bool CanManageAccounts => Session is { ReplacementRequired: false } session && (session.ManageAdmins || session.ManageUsers);
    public bool CanPublishDocumentation => Session is { ReplacementRequired: false, PublishDocumentation: true };
    public event Action? Changed;

    internal void FailInitial(Exception error) => initial.TrySetException(error);

    internal void Accept(AuthenticationSession session)
    {
        if (Session is { SignedIn: true } prior && prior.WorkspaceKey != session.WorkspaceKey)
        {
            throw new SessionAccessException(new("authentication_required", "The account changed. Reload to continue."), HttpStatusCode.Unauthorized);
        }
        if (!session.SignedIn) throw new SessionAccessException(new("authentication_required", "Sign in to use the application."), HttpStatusCode.Unauthorized);
        if (session.ReplacementRequired) throw new SessionAccessException(new("password_replacement_required", "Replace your temporary password before using the application."), HttpStatusCode.Forbidden);
        Session = session;
        initial.TrySetResult();
        Changed?.Invoke();
    }
}

public sealed class SessionAccessException(AccessDenial denial, HttpStatusCode status) : HttpRequestException(denial.Message, null, status)
{
    public AccessDenial Denial { get; } = denial;
}

public sealed class SessionHttpHandler(SessionAccess access, ClientRuntime runtime) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        runtime.RequireActive();
        var versioned = request.RequestUri?.AbsolutePath.StartsWith("/api/v3/", StringComparison.Ordinal) == true;
        if (versioned)
        {
            if (request.Headers.Contains(ApplicationVersion.Header)) throw new InvalidOperationException("The common transport owns application identity.");
            request.Headers.Add(ApplicationVersion.Header, ApplicationBuild.Version);
        }
        var sessionRead = request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath is "/api/auth/session" or "/api/v3/cache/startup";
        // Warm views are local-first. Genuine server tracker/semantic operations
        // wait for the SAME combined initial session, not another session request.
        if (!sessionRead && access.Session is null) await access.InitialSession.WaitAsync(token);
        if (access.Session?.WorkspaceKey is { Length: > 0 } workspace)
        {
            if (request.Headers.Contains("X-Logic-Workspace"))
                throw new InvalidOperationException("The common transport owns workspace binding.");
            request.Headers.Add("X-Logic-Workspace", workspace);
        }
        else if (!sessionRead || access.Session is not null)
            throw new InvalidOperationException("Authentication session must be acquired before sending application requests.");
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head && request.Method != HttpMethod.Options)
        {
            if (access.Session?.AntiforgeryToken is not { Length: > 0 } csrf)
                throw new InvalidOperationException("Authentication session must be acquired before sending application commands.");
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
        }
        runtime.RequireActive();
        request.Options.TryGetValue(StartupHttpEvidence.Option, out var startup);
        HttpResponseMessage response;
        try { response = await base.SendAsync(request, token); }
        catch (HttpRequestException error) when (startup is not null)
        {
            startup.ObserveTransportFailure(error, token);
            throw;
        }
        if (startup is not null) startup.Status = (int)response.StatusCode;
        try { if (versioned) await runtime.CheckResponseAsync(response, token); }
        catch { response.Dispose(); throw; }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            AccessDenial? denial = null;
            try
            {
                using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
                if (body.RootElement.TryGetProperty("code", out var code) && body.RootElement.TryGetProperty("message", out var message))
                    denial = new(code.GetString()!, message.GetString()!);
            }
            catch (JsonException) { }
            // An unverified proxy/error document is not confirmed credential loss.
            // Keep it in the existing ordinary transport-error path, not navigation.
            if (denial is null)
            {
                response.Dispose();
                throw new HttpRequestException("The access response could not be verified. No operation was automatically repeated.", null, response.StatusCode);
            }
            response.Dispose();
            throw new SessionAccessException(denial, response.StatusCode);
        }
        return response;
    }
}
