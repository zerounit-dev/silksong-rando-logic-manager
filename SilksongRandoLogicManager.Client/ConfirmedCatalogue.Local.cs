using System.Collections.Immutable;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Contracts;
using SilksongRandoLogicManager.Contracts.Diagnostics;

namespace SilksongRandoLogicManager.Client;

public sealed partial class ConfirmedCatalogue
{
    private string? appliedLocalStateId;
    private ImmutableDictionary<Guid, bool>? appliedImageEvidence;
    private RequirementCatalogueManagerView? localDefinitions;
    private bool cacheEventsAttached;
    private bool localSettlementPending;
    public event Action<Exception>? BackgroundFailed;
    public event Action? LocalSettled;
    public bool CanAdoptLocal => !disposed && !runtime.Retired && browser.CanAdoptLocal;

    // Warm preparation is wholly local and returns before the combined live check.
    // The shell starts that background check only after its usable local reveal.
    public async Task<bool> InitializeAsync(Func<InitialCatalogueProgress, Task> progress, bool installedCurrentRuntime = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var discovery = OperationTiming.Start("local-initial-state-discovery");
        var state = await browser.ReadStateAsync(lifetime.Token);
        discovery.End();
        var warm = state is not null;
        using var cold = OperationTiming.Start("local-initial-cold-install");
        if (!warm)
        {
            await progress(new(InitialLoadingPhase.Neutral));
            await browser.StartupAsync(requiredForSplash: true, token: lifetime.Token, initialProgress: progress);
            await progress(new(InitialLoadingPhase.Neutral));
        }
        cold.End(warm ? TimingOutcome.Skipped : TimingOutcome.Returned);
        await ReadLocalAsync(progress, PreparationContext.Startup);
        if (!disposed && !runtime.Retired && !cacheEventsAttached)
        {
            browser.LocalChanged += LocalCheckpointChanged;
            browser.ImagesChanged += LocalImagesChanged;
            cacheEventsAttached = true;
        }
        return warm && !installedCurrentRuntime;
    }
    private Task LocalCheckpointChanged(LocalCacheState? state)
    {
        if (!disposed && !runtime.Retired && Initialized)
        {
            localSettlementPending = true;
            _ = PrepareBackgroundLocalAsync();
        }
        // Never make an admitted cache synchronization wait for catalogue admission
        // or its own Refresh flight. RAM preparation has independent ownership.
        return Task.CompletedTask;
    }
    private void LocalImagesChanged()
    {
        if (!disposed && !runtime.Retired && Initialized) _ = PrepareBackgroundLocalAsync();
    }
    private async Task PrepareBackgroundLocalAsync()
    {
        try
        {
            await RefreshAsync();
            // The existing local completion notification also owns derived map
            // input. Common values can be unchanged for a map-only publication.
            // Do not require an initiating feature to remain mounted to finish it.
            await PrepareRetainedMapAsync();
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception error) { if (!disposed && !runtime.Retired) BackgroundFailed?.Invoke(error); }
    }
    private async Task ReadLocalAsync(Func<InitialCatalogueProgress, Task>? progress = null,
        PreparationContext context = PreparationContext.Ordinary)
    {
        using var load = OperationTiming.Start("local-catalogue-prepare-install");
        var work = new LocalLoadWork();
        var prepared = false;
        var ownerEpoch = epoch; long ownerGeneration = 0;
        bool Current() => Owns(ownerEpoch, ownerGeneration) && !mutation && !lifetime.IsCancellationRequested;
        async Task YieldCurrent()
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (!Current()) throw new OperationCanceledException();
            if (context == PreparationContext.Ordinary)
            {
                using (work.MeasureYield()) await browser.YieldAsync(lifetime.Token);
            }
            lifetime.Token.ThrowIfCancellationRequested();
            if (!Current()) throw new OperationCanceledException();
        }
        using var wait = OperationTiming.Start("local-catalogue-admission-wait");
        await admission.WaitAsync(lifetime.Token);
        wait.End();
        try
        {
            if (disposed) return;
            if (ownerEpoch != epoch || mutation || !browser.CanAdoptLocal) { DeferRefresh(false); return; }
            ownerGeneration = ++generation;
            using var stateCheck = OperationTiming.Start("local-catalogue-state-check");
            var state = await browser.ReadStateAsync(lifetime.Token);
            stateCheck.End(TimingOutcome.Returned);
            if (!Current()) { DeferRefresh(false); return; }
            if (state is null) throw new InvalidOperationException("The confirmed local database is unavailable. Use explicit Refresh to synchronize it.");
            if (Initialized && state.StateId == appliedLocalStateId && ReferenceEquals(appliedImageEvidence, browser.Images))
            { prepared = true; work.Report(load, reused: true, items: Rooms.Count); load.End(TimingOutcome.Skipped); return; }
            if (progress is not null) await progress(new(InitialLoadingPhase.GlobalLookups));
            if (!Current()) return;
            var before = CaptureCandidate();
            using var query = OperationTiming.Start("local-catalogue-query-return-including-managed-decode");
            var read = await browser.ReadCatalogueAsync(before.Rooms, lifetime.Token);
            query.End(TimingOutcome.Returned);
            if (!Current()) { DeferRefresh(false); return; }
            var evidence = browser.Images;
            using var materialize = OperationTiming.Start("local-catalogue-record-materialization");
            var facts = await LocalCatalogueMaterializer.MergeAsync(read, new(before.Rooms, before.Statuses, before.Order, before.Groups),
                evidence, YieldCurrent, lifetime.Token, work);
            work.Report(materialize, ReferenceEquals(facts.Rooms, before.Rooms), facts.Rooms.Count);
            materialize.End();
            if (!Current()) { DeferRefresh(false); return; }
            using var definitionWork = OperationTiming.Start("local-catalogue-vocabulary-materialization");
            var definitions = await LocalLogicMaterializer.VocabularyAsync(read.Vocabulary!, r => r.Freeze(), localDefinitions,
                YieldCurrent, lifetime.Token, work);
            work.Report(definitionWork, ReferenceEquals(definitions, localDefinitions));
            definitionWork.End();
            if (!Current()) { DeferRefresh(false); return; }
            using var vocabularyWork = OperationTiming.Start("local-catalogue-vocabulary-preparation");
            var vocabulary = ReferenceEquals(definitions, localDefinitions) && before.Vocabulary is not null
                ? before.Vocabulary : PrepareLocalVocabulary(definitions, before.Vocabulary);
            work.Report(vocabularyWork, ReferenceEquals(vocabulary, before.Vocabulary));
            vocabularyWork.End();
            using var identityWork = OperationTiming.Start("local-catalogue-identity-preparation");
            var identities = await PrepareLookupIdentitiesAsync(before.Identities, before.Rooms, facts.Rooms, facts.ChangedRooms, YieldCurrent);
            work.Report(identityWork, ReferenceEquals(identities, before.Identities), facts.ChangedRooms.Count);
            identityWork.End();
            using var diagnosticWork = OperationTiming.Start("local-catalogue-diagnostic-preparation");
            var diagnostics = await RoomEditing.RoomDiagnosticIndex.PrepareAsync(before.Diagnostics, facts.Rooms, facts.ChangedRooms, YieldCurrent);
            work.Report(diagnosticWork, ReferenceEquals(diagnostics, before.Diagnostics), facts.ChangedRooms.Count);
            diagnosticWork.End();
            using var imageWork = OperationTiming.Start("local-catalogue-image-preparation");
            var images = ReferenceEquals(facts.Rooms, before.Rooms) ? before.Images
                : await PrepareImagesAsync(before, facts.Rooms, facts.ChangedRooms, YieldCurrent);
            work.Report(imageWork, ReferenceEquals(images, before.Images));
            imageWork.End();
            using var sidebarWork = OperationTiming.Start("local-catalogue-sidebar-preparation");
            var (sidebar, presentation) = before.Initialized && ReferenceEquals(facts.Rooms, before.Rooms) && facts.Groups == before.Groups &&
                facts.Order == before.Order && ReferenceEquals(facts.Statuses, before.Statuses) && ReferenceEquals(images, before.Images)
                ? (before.Sidebar, before.Presentation!)
                : await PrepareSidebarAsync(before, facts.Rooms, facts.Groups, facts.Order, facts.Statuses, images, YieldCurrent);
            work.Report(sidebarWork, ReferenceEquals(presentation, before.Presentation));
            sidebarWork.End();
            using var handoff = OperationTiming.Start("local-catalogue-existing-task-yield");
            if (context == PreparationContext.Ordinary)
            {
                using (work.MeasureYield()) await browser.YieldAsync(lifetime.Token);
            }
            work.Report(handoff); handoff.End(context == PreparationContext.Startup ? TimingOutcome.Skipped : TimingOutcome.Complete);
            if (!Current() || !browser.CanAdoptLocal) { DeferRefresh(false); return; }
            using var searchWork = OperationTiming.Start("local-catalogue-search-preparation");
            var search = before.Search is not null && ReferenceEquals(facts.Rooms, before.Rooms) ? before.Search
                : await SidebarSearchIndex.PrepareAsync(facts.Rooms, before.Rooms, before.Search, facts.ChangedRooms, YieldCurrent);
            work.Report(searchWork, ReferenceEquals(search, before.Search));
            searchWork.End();
            if (!Current() || !browser.CanAdoptLocal) { DeferRefresh(false); return; }
            using var managerWork = OperationTiming.Start("local-catalogue-manager-preparation");
            var manager = ReferenceEquals(vocabulary, before.Vocabulary) ? before.Manager : PrepareManager(vocabulary);
            work.Report(managerWork, ReferenceEquals(manager, before.Manager)); managerWork.End();
            var candidate = new Candidate(facts.Rooms, facts.Groups, facts.Order, facts.Statuses, images, vocabulary,
                manager,
                 sidebar, presentation, search, true) { Reads = ReadObservations.Complete(), Identities = identities, Diagnostics = diagnostics, ChangedRooms = facts.ChangedRooms };
            // Local DB coverage, not either retired global timestamp scope. No
            // generated freshness marker certifies an independently retained result.
            using var install = OperationTiming.Start("local-catalogue-candidate-install");
            var priorStateId = appliedLocalStateId;
            var priorEvidence = appliedImageEvidence;
            var priorDefinitions = localDefinitions;
            try
            {
                appliedLocalStateId = read.State!.StateId;
                appliedImageEvidence = evidence;
                localDefinitions = definitions;
                Install(candidate);
            }
            catch
            {
                // Preserve callbacks' existing in-install witness, but a failed
                // install cannot certify reuse on the next explicit read attempt.
                appliedLocalStateId = priorStateId;
                appliedImageEvidence = priorEvidence;
                localDefinitions = priorDefinitions;
                throw;
            }
            prepared = true;
            install.End(TimingOutcome.Incorporated);
            load.End(TimingOutcome.Incorporated);
        }
        catch when (!Current()) { if (!disposed) DeferRefresh(false); }
        finally
        {
            admission.Release();
            // Settlement is not a fact change. Forward only coherent successful
            // local preparation, including reuse, after releasing admission.
            if (prepared && localSettlementPending && !disposed && !mutation && browser.CanAdoptLocal)
            {
                localSettlementPending = false;
                LocalSettled?.Invoke();
            }
        }
    }
    private static ConfirmedVocabulary PrepareLocalVocabulary(RequirementCatalogueManagerView source, ConfirmedVocabulary? prior)
    {
        IEnumerable<(VocabularyDefinition Value, IReadOnlyList<string> Aliases)>[] kinds = [
            source.Predicates.Select(x => (new VocabularyDefinition(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, x.InputSyntax, x.OutputSyntax, x.Notes), x.ParsedAliases)),
            source.Crests.Select(x => (new VocabularyDefinition(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, null, x.OutputValue, x.Notes), x.ParsedAliases)),
            source.Methods.Select(x => (new VocabularyDefinition(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, null, x.OutputValue, x.Notes), x.ParsedAliases)),
            source.Items.Select(x => (new VocabularyDefinition(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, null, x.OutputValue, x.Notes), x.ParsedAliases)),
            source.Countables.Select(x => (new VocabularyDefinition(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, null, x.OutputValue, x.Notes), x.ParsedAliases)),
            source.Souls.Select(x => (new VocabularyDefinition(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, null, x.OutputValue, x.Notes), x.ParsedAliases))];
        var prepared = ImmutableArray.CreateBuilder<ImmutableArray<ConfirmedDefinition>>(6);
        for (var index = 0; index < kinds.Length; index++)
        {
            var old = prior?.Kinds[index] ?? [];
            var byId = old.ToDictionary(x => x.Value.Id);
            var next = kinds[index].Select(x => byId.TryGetValue(x.Value.Id, out var retained) && retained.Value == x.Value
                ? retained : new ConfirmedDefinition(x.Value, x.Aliases.ToImmutableArray())).ToImmutableArray();
            prepared.Add(old.SequenceEqual(next) ? old : next);
        }
        return prior is not null && prepared.Select((kind, index) => kind == prior.Kinds[index]).All(x => x)
            ? prior : new(prepared.ToImmutable());
    }
}
