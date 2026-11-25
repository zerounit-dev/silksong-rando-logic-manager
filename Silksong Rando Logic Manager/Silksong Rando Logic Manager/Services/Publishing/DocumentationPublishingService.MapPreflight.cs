using Silksong_Rando_Logic_Manager.Authentication;

namespace Silksong_Rando_Logic_Manager.Services.Publishing;

internal sealed record DecideMapPreflight(Guid Decision, bool Proceed);

public sealed partial class DocumentationPublishingService
{
    private enum MapDecisionChoice { Proceed, Cancel, Shutdown }
    private sealed record PendingMapDecision(Guid Id, string Owner)
    {
        internal TaskCompletionSource<MapDecisionChoice> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private MapPreflightView? mapPreflight;
    private Guid? mapPreparation;
    private PendingMapDecision? pendingMapDecision;

    // Only for recovering a page that observed a denial. A normal status read can
    // still carry stale circuit claims, so it cannot lift that page's suppression.
    // This is a capability/stamp probe, not a reservation or decision admission.
    internal async Task ConfirmMapAccessAsync(LiveAccess.CircuitPrincipalObservation observation)
    {
        await using var scope = scopes.CreateAsyncScope();
        var detached = scope.ServiceProvider.GetRequiredService<LiveAccess>();
        await scope.ServiceProvider.GetRequiredService<DetachedCommandAdmission>()
            .AdmitAsync(detached, observation.Principal, observation.Owner);
    }

    internal async Task DecideMapAsync(LiveAccess access, DecideMapPreflight command)
    {
        var actor = await access.RequireAsync(AccessPolicies.PublishDocumentation);
        var presented = access.CapturePrincipal();
        await commandAdmission.WaitAsync();
        try
        {
            await access.RequireOwnerAsync(actor.Owner, AccessPolicies.PublishDocumentation);
            await using var scope = scopes.CreateAsyncScope();
            var detached = scope.ServiceProvider.GetRequiredService<LiveAccess>();
            await scope.ServiceProvider.GetRequiredService<DetachedCommandAdmission>().AdmitAsync(detached, presented, actor.Owner);
            lock (gate)
            {
                if (access.RequireInsideSynchronousGate(AccessPolicies.PublishDocumentation).Owner != actor.Owner)
                    throw new AccessDeniedException("not_found", 404, "This resource is not available to this account.");
                if (stopping || applicationStopping.IsCancellationRequested || pendingMapDecision is not { } pending ||
                    pending.Id != command.Decision || pending.Owner != actor.Owner ||
                    !pending.Completion.TrySetResult(command.Proceed ? MapDecisionChoice.Proceed : MapDecisionChoice.Cancel))
                    throw new PublishingBlock("There is no matching map decision available to this account. Read the current preparation before responding.");
                // Retire the token immediately, including for another page of this owner.
                pendingMapDecision = null;
                mapPreflight = mapPreflight! with { AwaitingDecision = false, Outcome = command.Proceed ? "Checking authorization to continue." : "Cancelling preparation." };
            }
            NotifyChanged();
        }
        finally { commandAdmission.Release(); }
    }

    // Called under gate at finalization, including failures before Prepare's
    // body starts. Distinct failure details keep their existing modal owner.
    private void SettleStoppedMapPreflight(bool failed)
    {
        var pending = pendingMapDecision;
        pendingMapDecision = null;
        pending?.Completion.TrySetResult(MapDecisionChoice.Shutdown);
        if (progress is not { } step) return;
        var current = mapPreflight;
        var rows = current?.Checks;
        if ((failed && rows is null) || rows?.Any(row => row.Status is MapCheckStatus.Waiting or MapCheckStatus.Checking) == true)
        {
            var reason = $"Preparation stopped at step {step.Step} of {step.Steps}: {step.Task} See the publishing details for the failure.";
            mapPreflight = new((rows ?? PublishingMapPreflight.NewChecks()).Select(row => row.Status is MapCheckStatus.Waiting or MapCheckStatus.Checking
                ? row with { Status = MapCheckStatus.Fail, NotRun = reason }
                : row).ToArray(), true, "Generation did not start.");
        }
        else if (current is not null && (current.AwaitingDecision || !current.Finished))
        {
            mapPreflight = current with
            {
                Finished = true, AwaitingDecision = false, Outcome = "Preparation stopped. Generation did not start."
            };
        }
    }

    private void PublishValidation(IReadOnlyList<string> findings)
    {
        lock (gate) validation = findings;
        NotifyChanged();
    }

    private static string DescribeGraphFailure(RoomGraphExportFailure failure) =>
        string.Join(" / ", new[] { failure.RoomName, failure.ChildName, failure.Cause }.Where(s => s is not null));

    private async Task ValidateMapAsync(LiveAccess execution, PreparedRoomGraph prepared,
        IReadOnlyList<string> documentIssues, List<string> hardReasons)
    {
        var rows = PublishingMapPreflight.NewChecks();
        lock (gate) mapPreflight = new(rows.ToArray());
        NotifyChanged();
        taskTiming!.Measure(PublishingMetric.Validation, () => new PublishingMapPreflight(prepared.Evidence).Run((i, status, report, notRun) =>
        {
            rows[i] = rows[i] with { Status = status, Report = report, NotRun = notRun };
            lock (gate) mapPreflight = new(rows.ToArray());
            NotifyChanged();
        }));
        taskTiming.Complete(); taskTiming.Dispose(); taskTiming = null; storage.Timing = null;

        var export = prepared.Export;
        var classified = prepared.Evidence.References.Where(r => r.HardFailure is not null).Select(r => r.HardFailure!).ToHashSet(ReferenceEqualityComparer.Instance);
        hardReasons.AddRange(export.Failures.Where(f => !classified.Contains(f)).Select(DescribeGraphFailure));
        if (!export.Succeeded || documentIssues.Count != 0 || hardReasons.Count != 0)
        {
            lock (gate)
            {
                validation = [.. documentIssues, .. hardReasons];
                mapPreflight = new(rows, true, "Generation is blocked by required publication validation or export failures. The checklist cannot override them.");
            }
            NotifyChanged();
            throw new PublishingBlock("Publication validation or export failed. Review the listed findings, correct the authored records, then discard and prepare again.");
        }
        if (rows.All(r => r.Status == MapCheckStatus.Pass))
        {
            lock (gate) mapPreflight = new(rows, true);
            NotifyChanged();
            return;
        }

        var admitted = execution.CapturePrincipal();
        var owner = execution.RequireInsideSynchronousGate(AccessPolicies.PublishDocumentation).Owner;
        var pending = new PendingMapDecision(Guid.NewGuid(), owner);
        lock (gate)
        {
            if (stopping || applicationStopping.IsCancellationRequested)
                pending.Completion.TrySetResult(MapDecisionChoice.Shutdown);
            else pendingMapDecision = pending;
            mapPreflight = new(rows, true, AwaitingDecision: pendingMapDecision is not null);
        }
        using var shutdown = applicationStopping.Register(() => pending.Completion.TrySetResult(MapDecisionChoice.Shutdown));
        NotifyChanged();
        MapDecisionChoice choice;
        using (var wait = timings!.Begin("Await map preflight decision"))
        {
            choice = await pending.Completion.Task;
            wait.Complete();
        }
        lock (gate)
        {
            pendingMapDecision = null;
            if (stopping || applicationStopping.IsCancellationRequested) choice = MapDecisionChoice.Shutdown;
            mapPreflight = new(rows, true, choice switch
            {
                MapDecisionChoice.Cancel => "Preparation cancelled. Generation did not start.",
                MapDecisionChoice.Shutdown => "Preparation interrupted by server shutdown. Generation did not start.",
                _ => "Checking authorization to continue."
            });
        }
        NotifyChanged();
        if (choice != MapDecisionChoice.Proceed)
            throw new PublishingBlock(choice == MapDecisionChoice.Cancel
                ? "Preparation cancelled before generation. Discard the frozen preparation before preparing again."
                : "Server shutdown interrupted preparation before generation.");

        // A fresh page of the same owner can submit the choice, but cannot grant
        // the parked Prepare job a new detached credential or security stamp.
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var resumed = scope.ServiceProvider.GetRequiredService<LiveAccess>();
            await scope.ServiceProvider.GetRequiredService<DetachedCommandAdmission>().AdmitAsync(resumed, admitted, owner);
            await resumed.RequireOwnerAsync(owner, AccessPolicies.PublishDocumentation);
            lock (gate)
            {
                if (stopping || applicationStopping.IsCancellationRequested)
                    throw new PublishingBlock("Server shutdown interrupted preparation before generation.");
                mapPreflight = new(rows, true, "Proceeding with the same frozen preparation.");
            }
        }
        catch (AccessDeniedException)
        {
            lock (gate) mapPreflight = new(rows, true, "The original preparation is no longer authorized. Generation did not start.");
            throw;
        }
        catch
        {
            lock (gate) mapPreflight = new(rows, true, "Unable to continue this preparation. Generation did not start.");
            throw;
        }
        finally { NotifyChanged(); }
    }
}
