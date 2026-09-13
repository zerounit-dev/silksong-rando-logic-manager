using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components;

public sealed class DiagnosticErrorBoundary : ErrorBoundary
{
    [Inject] private DiagnosticState Diagnostics { get; set; } = null!;

    protected override Task OnErrorAsync(Exception exception)
    {
        Diagnostics.Show(exception.ToString());
        return Task.CompletedTask;
    }
}
