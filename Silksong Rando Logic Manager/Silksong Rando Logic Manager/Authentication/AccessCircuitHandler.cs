using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Silksong_Rando_Logic_Manager.Authentication;

public sealed class AccessCircuitHandler(AuthenticationStateProvider authentication, LiveAccess access,
    ILogger<AccessCircuitHandler> logger) : CircuitHandler, IDisposable
{
    private bool subscribed, disposed;
    private readonly object lifetimeBoundary = new();
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        authentication.AuthenticationStateChanged += AuthenticationChanged;
        subscribed = true;
        await ObserveAsync(authentication.GetAuthenticationStateAsync());
    }

    // Connection-up precedes SetCircuitUser. Its notification below, rather than
    // this callback's old principal, supplies the refreshed credential.
    private void AuthenticationChanged(Task<AuthenticationState> value) => _ = ObserveAsync(value);
    private async Task ObserveAsync(Task<AuthenticationState> value)
    {
        // Runs synchronously through Begin before its first await, including for
        // framework SetCircuitUser notifications after connection-up. Never wait
        // synchronously or throw from the provider notification into the circuit.
        long revision;
        lock (lifetimeBoundary)
        {
            if (disposed) return;
            revision = access.BeginCircuitObservation();
        }
        try
        {
            var observed = await value;
            lock (lifetimeBoundary)
                if (!disposed) access.CompleteCircuitObservation(revision, observed.User);
        }
        catch (Exception exception)
        {
            lock (lifetimeBoundary)
                if (!disposed) access.FailCircuitObservation(revision, exception);
            logger.LogError(exception, "Circuit authentication state could not be observed.");
        }
    }
    public void Dispose()
    {
        lock (lifetimeBoundary)
        {
            disposed = true;
            if (subscribed) authentication.AuthenticationStateChanged -= AuthenticationChanged;
            access.EndCircuitObservations();
        }
    }
}
