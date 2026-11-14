namespace Silksong_Rando_Logic_Manager.Authentication;

/// <summary>
/// Application callback admission, never circuit protocol admission. A false
/// result means that the callback must stop without touching its draft or running
/// its operation. Denial uses the ordinary diagnostic surface, not forced navigation.
/// This is not an operation result or a reusable command admission;
/// shared services still validate independently when execution reaches their gate.
/// </summary>
public sealed class CircuitCallbackAccess(LiveAccess access, Services.DiagnosticState diagnostics)
{
    public async Task<bool> AdmitAsync(string policy = AccessPolicies.Normal)
    {
        try { await access.RequireAsync(policy); return true; }
        catch (AccessDeniedException denial) { diagnostics.Show(denial.Message); return false; }
    }
}
