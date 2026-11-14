using System.Collections.Immutable;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

/// <summary>Purpose-shaped immutable views from complete local SQL reads. No HTTP,
/// resolver, saved parser, publication or feature/draft admission lives here.</summary>
internal static class LocalLogicMaterializer
{
    internal static RequirementCatalogueReferenceView Reference(RequirementCatalogueManagerView source) => new(
        source.Predicates.Select(x => new RequirementPredicateReferenceView(x.Id, x.Name, x.Category, x.InputSyntax, x.Notes, x.Aliases, x.ParsedAliases)).ToImmutableArray(),
        source.Crests.Select(x => new RequirementCrestReferenceView(x.Id, x.Name, x.Category, x.Notes, x.Aliases, x.ParsedAliases)).ToImmutableArray(),
        source.Methods.Select(x => new RequirementMethodReferenceView(x.Id, x.Name, x.Category, x.Notes, x.Aliases, x.ParsedAliases)).ToImmutableArray(),
        source.Items.Select(x => new RequirementItemReferenceView(x.Id, x.Name, x.Category, x.Notes, x.Aliases, x.ParsedAliases)).ToImmutableArray(),
        CheckLocationTypeCatalogue.Definitions,
        source.Countables.Select(x => new RequirementCountableReferenceView(x.Id, x.Name, x.Category, x.Notes, x.Aliases, x.ParsedAliases)).ToImmutableArray(),
        source.Souls.Select(x => new RequirementSoulReferenceView(x.Id, x.Name, x.Category, x.Notes, x.Aliases, x.ParsedAliases)).ToImmutableArray());
    // Keep the owning checkpoint callback: startup checks synchronously, ordinary
    // work crosses its browser task. Final generation admission stays with the caller.
    private static async Task<ImmutableArray<T>> PrepareAsync<TSource, T>(IReadOnlyList<TSource> source, LogicCacheTable table,
        Func<TSource, T> map, Func<Task> yield, CancellationToken token, LocalLoadWork? observation = null)
    {
        var result = ImmutableArray.CreateBuilder<T>();
        foreach (var row in source)
        {
            token.ThrowIfCancellationRequested();
            observation?.Row(table);
            result.Add(map(row));
            if (result.Count % 128 == 0) { await yield(); token.ThrowIfCancellationRequested(); }
        }
        return result.ToImmutable();
    }
    private static ImmutableArray<T> Reuse<T>(ImmutableArray<T> next, IReadOnlyList<T>? previous)
    {
        if (previous is ImmutableArray<T> old && old.SequenceEqual(next)) return old;
        return next;
    }
    internal static async Task<RequirementCatalogueManagerView> VocabularyAsync(LocalVocabularySource read,
        RequirementCatalogueManagerView? previous, Func<Task> yield, CancellationToken token, LocalLoadWork? observation = null)
    {
        var oldPredicates = previous?.Predicates.ToDictionary(x => x.Id);
        var predicates = await PrepareAsync(read.Predicates, LogicCacheTable.RequirementPredicates, r =>
        {
            var id = r.Id; var name = r.Name; var category = r.Category;
            var input = r.InputSyntax!; var output = r.Output; var notes = r.Notes;
            var order = r.SortOrder; var aliases = r.Aliases;
            if (oldPredicates?.TryGetValue(id, out var old) == true && old.Name == name && old.Category == category &&
                old.InputSyntax == input && old.OutputSyntax == output && old.Notes == notes && old.SortOrder == order && old.Aliases == aliases)
                return old;
            return new RequirementPredicateView(id, name, category, input, output, notes, order, aliases,
                RequirementCatalogueLanguage.ParseAliases(aliases));
        }, yield, token, observation);
        var crests = await ValuesAsync(read.Crests, LogicCacheTable.RequirementCrests, previous?.Crests,
            x => new(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, null, x.OutputValue, x.Notes),
            (v, a) => new RequirementCrestView(v.Id, v.Name, v.Category, v.Output, v.Notes, v.SortOrder, v.Aliases, a), yield, token, observation);
        var methods = await ValuesAsync(read.Methods, LogicCacheTable.RequirementMethods, previous?.Methods,
            x => new(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, null, x.OutputValue, x.Notes),
            (v, a) => new RequirementMethodView(v.Id, v.Name, v.Category, v.Output, v.Notes, v.SortOrder, v.Aliases, a), yield, token, observation);
        var items = await ValuesAsync(read.Items, LogicCacheTable.RequirementItems, previous?.Items,
            x => new(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, null, x.OutputValue, x.Notes),
            (v, a) => new RequirementItemView(v.Id, v.Name, v.Category, v.Output, v.Notes, v.SortOrder, v.Aliases, a), yield, token, observation);
        var countables = await ValuesAsync(read.Countables, LogicCacheTable.RequirementCountables, previous?.Countables,
            x => new(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, null, x.OutputValue, x.Notes),
            (v, a) => new RequirementCountableView(v.Id, v.Name, v.Category, v.Output, v.Notes, v.SortOrder, v.Aliases, a), yield, token, observation);
        var souls = await ValuesAsync(read.Souls, LogicCacheTable.RequirementSouls, previous?.Souls,
            x => new(x.Id, x.Name, x.Category, x.SortOrder, x.Aliases, null, x.OutputValue, x.Notes),
            (v, a) => new RequirementSoulView(v.Id, v.Name, v.Category, v.Output, v.Notes, v.SortOrder, v.Aliases, a), yield, token, observation);
        var orderedPredicates = Reuse(predicates.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToImmutableArray(), previous?.Predicates);
        if (previous is not null && previous.Predicates.SequenceEqual(orderedPredicates) && previous.Crests.SequenceEqual(crests) &&
            previous.Methods.SequenceEqual(methods) && previous.Items.SequenceEqual(items) && previous.Countables.SequenceEqual(countables) &&
            previous.Souls.SequenceEqual(souls)) return previous;
        return new(orderedPredicates, crests, methods, items, countables, souls);
    }
    private static async Task<ImmutableArray<T>> ValuesAsync<T>(IReadOnlyList<VocabularyDefinition> read, LogicCacheTable table,
        IReadOnlyList<T>? previous, Func<T, VocabularyDefinition> source, Func<VocabularyDefinition, IReadOnlyList<string>, T> create,
        Func<Task> yield, CancellationToken token, LocalLoadWork? observation = null)
    {
        var old = previous?.ToDictionary(x => source(x).Id, x => (Fact: source(x), View: x));
        var next = await PrepareAsync(read, table, value =>
        {
            return old?.TryGetValue(value.Id, out var prior) == true && prior.Fact == value ? prior.View
                : create(value, RequirementCatalogueLanguage.ParseAliases(value.Aliases));
        }, yield, token, observation);
        return Reuse(next.OrderBy(x => source(x).SortOrder).ThenBy(x => source(x).Id).ToImmutableArray(), previous);
    }

    internal static async Task<MapDataSnapshot> MapsAsync(LocalMapsSource read, MapDataSnapshot? previous,
        Func<Task> yield, CancellationToken token, LocalLoadWork? observation = null)
    {
        var maps = await PrepareAsync(read.Maps, LogicCacheTable.Maps, r => r, yield, token, observation);
        var zones = await PrepareAsync(read.Zones, LogicCacheTable.MapZones, r => r, yield, token, observation);
        var scenes = await PrepareAsync(read.Scenes, LogicCacheTable.MapScenes, r => r, yield, token, observation);
        var chunks = await PrepareAsync(read.Chunks, LogicCacheTable.MapChunks, r => r, yield, token, observation);
        var assets = new MapOverlayAssetCatalog();
        var overlays = await PrepareAsync(read.Overlays, LogicCacheTable.MapOverlays, r =>
        {
            var key = r.ImageAssetKey;
            var asset = assets.TryGet(key, out var known) ? new MapDataAsset(known.Url, known.AspectRatio) : null;
            return (Order: r.SortOrder, View: new MapDataOverlay(r.Id, r.MapId, key, asset,
                r.ScaleXPercent, r.ScaleYPercent, r.LeftOffsetPercent, r.BottomOffsetPercent));
        }, yield, token, observation);
        // First persisted row by SortOrder, BEFORE validating its key/placement.
        // Do not add an Id tie-break or fall through to a later usable overlay.
        var first = overlays.GroupBy(x => x.View.MapId).Select(g => g.OrderBy(x => x.Order).First().View).ToImmutableArray();
        if (previous is not null && previous.Maps.SequenceEqual(maps) && previous.Zones.SequenceEqual(zones) &&
            previous.Scenes.SequenceEqual(scenes) && previous.Chunks.SequenceEqual(chunks) && previous.FirstOverlays.SequenceEqual(first)) return previous;
        return new(Reuse(maps, previous?.Maps), Reuse(zones, previous?.Zones), Reuse(scenes, previous?.Scenes),
            Reuse(chunks, previous?.Chunks), Reuse(first, previous?.FirstOverlays));
    }
}
