using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

// Invocation-private interop inputs. Only Freeze methods construct durable immutable
// leaves; no receiving object is retained by a candidate, native owner or pinned view.
internal sealed class LocalCatalogueReply
{
    public required LocalCatalogueState? State { get; set; }
    public bool Conflict { get; set; }
    public LocalCatalogueInventory[]? Inventory { get; set; }
    public LocalCatalogueRoom[]? Rooms { get; set; }
    public LocalCatalogueTransition[]? Transitions { get; set; }
    public LocalCatalogueConnection[]? Connections { get; set; }
    public LocalCatalogueCheck[]? Checks { get; set; }
    public LocalCatalogueGroup[]? Groups { get; set; }
    public LocalVocabularySource<LocalCatalogueDefinition>? Vocabulary { get; set; }
}

internal sealed class LocalCatalogueState
{
    public required string StateId { get; set; }
    public required LocalCatalogueCheckpoint Checkpoint { get; set; }
    internal LocalCacheState Freeze()
    {
        LocalCatalogueValidation.Require(StateId is not null && Checkpoint is not null);
        LocalCatalogueValidation.Require(Checkpoint.Schema is not null);
        return new(StateId, new(Checkpoint.Format, Checkpoint.RunId, Checkpoint.StartedUtc, Checkpoint.ThroughUtc, Checkpoint.Schema));
    }
}
internal sealed class LocalCatalogueCheckpoint
{
    public required int Format { get; set; }
    public required Guid RunId { get; set; }
    public required DateTime StartedUtc { get; set; }
    public required DateTime ThroughUtc { get; set; }
    public required string Schema { get; set; }
}
internal sealed class LocalCatalogueInventory
{
    public required Guid Id { get; set; } // Native JSON GUID grammar, not persisted Guid.Parse.
    [JsonIgnore] public long CacheFlagTicks { get; private set; }
    [JsonRequired, JsonPropertyName("cacheFlagUtc")]
    public string CacheFlagText { get => CacheFlagTicks.ToString(CultureInfo.InvariantCulture); set => CacheFlagTicks = long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture); }
    public required int CompletionStatus { get; set; }
}
internal sealed class LocalCatalogueObservation
{
    [JsonIgnore] public Guid RoomId { get; private set; }
    [JsonRequired, JsonPropertyName("roomId")]
    public string RoomIdText { get => RoomId.ToString(); set => RoomId = Guid.Parse(value); }
    [JsonIgnore] public DateTime CacheFlagUtc { get; private set; }
    [JsonRequired, JsonPropertyName("cacheFlagUtc")]
    public string CacheFlagText { get => CacheFlagUtc.Ticks.ToString(CultureInfo.InvariantCulture); set => CacheFlagUtc = new(long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), DateTimeKind.Utc); }
    internal RoomDocumentObservation Freeze() => new(RoomId, CacheFlagUtc);
}
internal sealed class LocalCatalogueIdentity
{
    [JsonIgnore] public Guid EntityId { get; private set; }
    [JsonRequired, JsonPropertyName("entityId")]
    public string EntityIdText { get => EntityId.ToString(); set => EntityId = Guid.Parse(value); }
    public required bool IsArchived { get; set; }
    internal RoomLookupIdentity Freeze() => new(EntityId, IsArchived);
}
internal sealed class LocalCatalogueRoomText
{
    public required string FriendlyName { get; set; }
    public required string ReferenceId { get; set; }
    public required string? InGameId { get; set; }
    internal RoomLookupText Freeze() => new(FriendlyName, ReferenceId, InGameId);
}
internal sealed class LocalCatalogueImage
{
    public required bool UseSceneImage { get; set; }
    public required bool UseUploadedSceneImage { get; set; }
    public required bool Stale { get; set; }
    [JsonIgnore] public double? PixelsPerUnitX { get; private set; }
    [JsonIgnore] public double? PixelsPerUnitY { get; private set; }
    [JsonRequired, JsonPropertyName("pixelsPerUnitX")]
    public string? PixelsPerUnitXText { get => PixelsPerUnitX?.ToString(CultureInfo.InvariantCulture); set => PixelsPerUnitX = LocalCatalogueScalars.Number(value); }
    [JsonRequired, JsonPropertyName("pixelsPerUnitY")]
    public string? PixelsPerUnitYText { get => PixelsPerUnitY?.ToString(CultureInfo.InvariantCulture); set => PixelsPerUnitY = LocalCatalogueScalars.Number(value); }
    internal RoomImageIndicatorFacts Freeze() => new(UseSceneImage, UseUploadedSceneImage, Stale, PixelsPerUnitX, PixelsPerUnitY);
}
internal sealed class LocalCatalogueRoom
{
    public required LocalCatalogueObservation Observation { get; set; }
    public required LocalCatalogueIdentity Identity { get; set; }
    public required LocalCatalogueRoomText Values { get; set; }
    [JsonIgnore] public Guid? RoomGroupId { get; private set; }
    [JsonRequired, JsonPropertyName("roomGroupId")]
    public string? RoomGroupIdText { get => RoomGroupId?.ToString(); set => RoomGroupId = LocalCatalogueScalars.Id(value); }
    public required int SortOrder { get; set; }
    public required int CompletionStatus { get; set; }
    public required LocalCatalogueImage SceneImage { get; set; }
}
internal sealed class LocalCatalogueTransitionText
{
    public required string Alias { get; set; }
    public required string? DestinationRoomReferenceText { get; set; }
    public required string? DestinationTransitionAliasText { get; set; }
    internal TransitionLookupText Freeze() => new(Alias, DestinationRoomReferenceText, DestinationTransitionAliasText);
}
internal sealed class LocalCatalogueTransitionFacts
{
    public required LocalCatalogueIdentity Observation { get; set; }
    public required LocalCatalogueTransitionText Values { get; set; }
    [JsonIgnore] public Guid? ResolvedDestinationRoomId { get; private set; }
    [JsonRequired, JsonPropertyName("resolvedDestinationRoomId")]
    public string? ResolvedDestinationRoomIdText { get => ResolvedDestinationRoomId?.ToString(); set => ResolvedDestinationRoomId = LocalCatalogueScalars.Id(value); }
    [JsonIgnore] public Guid? ResolvedDestinationTransitionId { get; private set; }
    [JsonRequired, JsonPropertyName("resolvedDestinationTransitionId")]
    public string? ResolvedDestinationTransitionIdText { get => ResolvedDestinationTransitionId?.ToString(); set => ResolvedDestinationTransitionId = LocalCatalogueScalars.Id(value); }
    internal TransitionLookupFacts Freeze() => new(Observation.Freeze(), Values.Freeze(), ResolvedDestinationRoomId, ResolvedDestinationTransitionId);
}
internal sealed class LocalCatalogueTransition
{
    [JsonIgnore] public Guid RoomId { get; private set; }
    [JsonRequired, JsonPropertyName("roomId")]
    public string RoomIdText { get => RoomId.ToString(); set => RoomId = Guid.Parse(value); }
    public required LocalCatalogueTransitionFacts Facts { get; set; }
    public required string? Requirements { get; set; }
}
internal sealed class LocalCatalogueConnection
{
    [JsonIgnore] public Guid RoomId { get; private set; }
    [JsonRequired, JsonPropertyName("roomId")]
    public string RoomIdText { get => RoomId.ToString(); set => RoomId = Guid.Parse(value); }
    public required LocalCatalogueIdentity Identity { get; set; }
    public required string? Requirements { get; set; }
}
internal sealed class LocalCatalogueCheckText
{
    public required string FriendlyName { get; set; }
}
internal sealed class LocalCatalogueCheckFacts
{
    public required LocalCatalogueIdentity Observation { get; set; }
    public required LocalCatalogueCheckText Values { get; set; }
    internal CheckLookupFacts Freeze() => new(Observation.Freeze(), new(Values.FriendlyName));
}
internal sealed class LocalCatalogueCheck
{
    [JsonIgnore] public Guid RoomId { get; private set; }
    [JsonRequired, JsonPropertyName("roomId")]
    public string RoomIdText { get => RoomId.ToString(); set => RoomId = Guid.Parse(value); }
    public required LocalCatalogueCheckFacts Facts { get; set; }
    public required string? Requirements { get; set; }
}
internal sealed class LocalCatalogueGroup
{
    [JsonIgnore] public Guid Id { get; private set; }
    [JsonRequired, JsonPropertyName("id")]
    public string IdText { get => Id.ToString(); set => Id = Guid.Parse(value); }
    public required string FriendlyName { get; set; }
    public required int SortOrder { get; set; }
    public required string? ZoneReferenceText { get; set; }
    [JsonIgnore] public Guid? ResolvedMapZoneId { get; private set; }
    [JsonRequired, JsonPropertyName("resolvedMapZoneId")]
    public string? ResolvedMapZoneIdText { get => ResolvedMapZoneId?.ToString(); set => ResolvedMapZoneId = LocalCatalogueScalars.Id(value); }
    public required bool IsVirtual { get; set; }
    [JsonIgnore] public DateTime UpdatedUtc { get; private set; }
    [JsonRequired, JsonPropertyName("updatedUtc")]
    public string UpdatedUtcText { get => UpdatedUtc.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture); set => UpdatedUtc = DateTime.SpecifyKind(DateTime.ParseExact(value,
        ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Utc); }
    internal CatalogueGroup Freeze() => new(Id, FriendlyName, SortOrder, ZoneReferenceText, ResolvedMapZoneId, IsVirtual, UpdatedUtc);
}
internal sealed class LocalCatalogueDefinition
{
    [JsonIgnore] public Guid Id { get; private set; }
    [JsonRequired, JsonPropertyName("id")]
    public string IdText { get => Id.ToString(); set => Id = Guid.Parse(value); }
    public required string Name { get; set; }
    public required string? Category { get; set; }
    public required int SortOrder { get; set; }
    public required string Aliases { get; set; }
    public required string? InputSyntax { get; set; }
    public required string Output { get; set; }
    public required string Notes { get; set; }
    internal VocabularyDefinition Freeze() => new(Id, Name, Category, SortOrder, Aliases, InputSyntax, Output, Notes);
}
internal static class LocalCatalogueScalars
{
    internal static Guid? Id(string? value) => value is null ? null : Guid.Parse(value);
    internal static double? Number(string? value) => value is null ? null : double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
}

// Presence is native required-property metadata; explicit null and domain integrity
// are checked after header admission, independently of setter encounter order.
internal static class LocalCatalogueValidation
{
    internal static void Require([DoesNotReturnIf(false)] bool valid) { if (!valid) throw new JsonException("Invalid named local SQLite result."); }
    internal static RoomStatus Status(int value) => value switch {
        0 => RoomStatus.Neutral, 1 => RoomStatus.Success, 2 => RoomStatus.Warning, 3 => RoomStatus.Danger,
        _ => throw new JsonException("Invalid stored completion.") };
    private static void Unique(HashSet<Guid> ids, Guid id) => Require(id != Guid.Empty && ids.Add(id));
    private static void Identity([NotNull] LocalCatalogueIdentity? value) => Require(value is not null);
    private static void Requirements(string? value, bool archived) => Require(archived ? value is null : value is not null);
    internal static void Success(LocalCatalogueReply value)
    {
        Require(value.Inventory is not null && value.Rooms is not null && value.Transitions is not null &&
            value.Connections is not null && value.Checks is not null && value.Groups is not null && value.Vocabulary is not null);
        foreach (var row in value.Inventory!) Require(row is not null);
        var selected = new HashSet<Guid>();
        foreach (var row in value.Rooms!)
        {
            Require(row is not null && row.Observation is not null && row.Identity is not null && row.Values is not null && row.SceneImage is not null);
            Unique(selected, row.Observation.RoomId);
            Require(row.Identity.EntityId == row.Observation.RoomId && row.Values.FriendlyName is not null && row.Values.ReferenceId is not null);
            _ = Status(row.CompletionStatus);
        }
        var ids = new HashSet<Guid>();
        foreach (var row in value.Transitions!)
        {
            Require(row is not null && row.Facts is not null && row.Facts.Values is not null);
            Identity(row.Facts.Observation); Unique(ids, row.Facts.Observation.EntityId);
            Require(selected.Contains(row.RoomId) && row.Facts.Values.Alias is not null);
            Requirements(row.Requirements, row.Facts.Observation.IsArchived);
        }
        ids.Clear();
        foreach (var row in value.Connections!)
        {
            Require(row is not null); Identity(row.Identity); Unique(ids, row.Identity.EntityId);
            Require(selected.Contains(row.RoomId)); Requirements(row.Requirements, row.Identity.IsArchived);
        }
        ids.Clear();
        foreach (var row in value.Checks!)
        {
            Require(row is not null && row.Facts is not null && row.Facts.Values is not null);
            Identity(row.Facts.Observation); Unique(ids, row.Facts.Observation.EntityId);
            Require(selected.Contains(row.RoomId) && row.Facts.Values.FriendlyName is not null);
            Requirements(row.Requirements, row.Facts.Observation.IsArchived);
        }
        ids.Clear();
        foreach (var row in value.Groups!)
        {
            Require(row is not null && row.FriendlyName is not null); Unique(ids, row.Id);
        }
        var vocabulary = value.Vocabulary!;
        void Definitions(IReadOnlyList<LocalCatalogueDefinition>? rows, bool predicate = false)
        {
            Require(rows is not null); ids.Clear();
            foreach (var row in rows!)
            {
                Require(row is not null && row.Name is not null && row.Aliases is not null && row.Output is not null && row.Notes is not null);
                Unique(ids, row.Id); Require(predicate ? row.InputSyntax is not null : row.InputSyntax is null);
            }
        }
        Definitions(vocabulary.Predicates, true); Definitions(vocabulary.Crests); Definitions(vocabulary.Methods);
        Definitions(vocabulary.Items); Definitions(vocabulary.Countables); Definitions(vocabulary.Souls);
    }
}
