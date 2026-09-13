namespace Silksong_Rando_Logic_Manager.Services;

public sealed class DiagnosticState
{
    private readonly object sync = new();
    private string? errorDetails;

    public event Action? StateChanged;

    public string? ErrorDetails
    {
        get
        {
            lock (sync)
            {
                return errorDetails;
            }
        }
    }

    public void Show(string errorDetails)
    {
        ArgumentNullException.ThrowIfNull(errorDetails);

        Action? stateChanged;
        lock (sync)
        {
            this.errorDetails = errorDetails;
            stateChanged = StateChanged;
        }

        stateChanged?.Invoke();
    }

    public void Close()
    {
        Action? stateChanged;
        lock (sync)
        {
            errorDetails = null;
            stateChanged = StateChanged;
        }

        stateChanged?.Invoke();
    }
}
