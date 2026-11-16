namespace SilksongRandoLogicManager.Client;

// Only a proved checkpoint mismatch or admitted producer invalidation authorizes
// preparation again. I/O, malformed payloads and invariant failures are not retries.
internal sealed class LocalReadInvalidatedException() : InvalidOperationException("Local Room preparation was invalidated by a confirmed-state change.");

public sealed partial class ConfirmedCatalogue
{
    private TaskCompletionSource? mutationSettlement;

    private async Task<T?> PrepareRoomReadAsync<T>(Func<CancellationToken, Task<T?>> read,
        Func<bool> owns, CancellationToken token, PreparationContext context, bool refreshCommon = true) where T : class
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        void RuntimeChanged() { if (runtime.Retired) linked.Cancel(); }
        runtime.Changed += RuntimeChanged;
        bool Current() => !disposed && !runtime.Retired && owns() && !linked.IsCancellationRequested;
        try
        {
            // All producer waits are outside admission. Register then recheck runtime;
            // the captured completion tasks cannot miss an intervening settlement.
            while (Current())
            {
                await browser.WaitForLocalReadinessAsync(linked.Token);
                if (!Current()) return null;
                if (mutationSettlement is { } changing) await changing.Task.WaitAsync(linked.Token);
                if (!Current()) return null;
                if (refreshCommon || browser.Current?.StateId != appliedLocalStateId)
                    await RefreshAsync(context).WaitAsync(linked.Token);
                if (!Current()) return null;
                if (mutation || !browser.CanAdoptLocal) continue;
                // A joined/deferred flight is not an adoption witness. A moved
                // observation requires another actual common preparation, not a read.
                if (browser.Current?.StateId != appliedLocalStateId) { refreshCommon = true; continue; }
                var startedEpoch = epoch;
                try
                {
                    var value = await read(linked.Token);
                    if (!Current()) return null;
                    if (value is null && (epoch != startedEpoch || !browser.CanAdoptLocal))
                        throw new LocalReadInvalidatedException();
                    return value;
                }
                catch (LocalReadInvalidatedException) when (Current()) { refreshCommon = true; }
            }
            return null;
        }
        catch when (!Current()) { return null; }
        finally { runtime.Changed -= RuntimeChanged; }
    }

    private void RequireRoomLocalCheckpoint(LocalCacheState? state)
    {
        if (state is null) throw new InvalidOperationException("The confirmed local database is unavailable. Use explicit Refresh to synchronize it.");
        if (!browser.CanAdoptLocal || state.StateId != appliedLocalStateId) throw new LocalReadInvalidatedException();
    }
}
