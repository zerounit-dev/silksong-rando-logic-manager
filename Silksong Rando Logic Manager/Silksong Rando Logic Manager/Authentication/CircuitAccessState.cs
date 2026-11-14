namespace Silksong_Rando_Logic_Manager.Authentication;

public sealed class CircuitAccessState(ILogger<CircuitAccessState> logger)
{
    public Microsoft.AspNetCore.Components.CascadingValueSource<ExecutionIdentity?> IdentitySource { get; } = new((ExecutionIdentity?)null, isFixed: false);
    private ExecutionIdentity? identity;
    public void Accept(ExecutionIdentity? value)
    {
        if (identity == value) return;
        identity = value;
        _ = NotifyIdentityAsync(value);
    }
    private async Task NotifyIdentityAsync(ExecutionIdentity? value)
    {
        try { await IdentitySource.NotifyChangedAsync(value); }
        catch (Exception exception) { logger.LogError(exception, "Circuit access presentation notification failed."); }
    }
}
