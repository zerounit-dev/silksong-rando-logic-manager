using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components.RoomEditorV2;

// Pure, typed draft installation. No markup, command dispatch or persistence.
internal static class ChildTableDraftAdapters
{
    internal static bool IsText(ChildTableField field) => field is not
        (ChildTableField.Type or ChildTableField.Todo or ChildTableField.Verification or ChildTableField.UnsafeArrival);

    internal static bool TryVerification(string? value, out bool? verified)
    {
        verified = value == "true" ? true : value == "false" ? false : null;
        return value is "true" or "false" or "" or null;
    }
    internal static bool TryTodo(string? value, out bool todo)
    {
        todo = value == "true";
        return value is "true" or "false";
    }

    internal static SubroomDraft Install(SubroomDraft draft, ChildTableField field, string? value, bool creation)
        => value is null ? draft : field switch
        {
            ChildTableField.Name => draft with { FriendlyName = value, ReferenceId = creation ? value : draft.ReferenceId },
            ChildTableField.Reference => draft with { ReferenceId = value },
            ChildTableField.Notes => draft with { Notes = value },
            _ => draft
        };
    internal static TransitionDraft Install(TransitionDraft draft, ChildTableField field, string? value)
        => field switch
        {
            ChildTableField.Alias when value is not null => draft with { Alias = value },
            ChildTableField.Name when value is not null => draft with { FriendlyName = value },
            ChildTableField.Source when value is not null => draft with { SourceSubroomReferenceText = value },
            ChildTableField.DestinationRoom when value is not null => draft with { DestinationRoomReferenceText = value },
            ChildTableField.DestinationAlias when value is not null => draft with { DestinationTransitionAliasText = value },
            ChildTableField.UnsafeArrival when TryTodo(value, out var unsafeArrival) => draft with { IsUnsafeArrival = unsafeArrival },
            ChildTableField.Requirements when value is not null => draft with { Requirements = value },
            ChildTableField.Notes when value is not null => draft with { Notes = value },
            ChildTableField.Todo when TryTodo(value, out var todo) => draft with { IsTodo = todo },
            ChildTableField.Verification when TryVerification(value, out var verified) => draft with { IsVerified = verified },
            _ => draft
        };
    internal static ConnectionDraft Install(ConnectionDraft draft, ChildTableField field, string? value)
        => field switch
        {
            ChildTableField.Alias when value is not null => draft with { Alias = value },
            ChildTableField.Name when value is not null => draft with { FriendlyName = value },
            ChildTableField.Source when value is not null => draft with { SourceSubroomReferenceText = value },
            ChildTableField.Destination when value is not null => draft with { DestinationSubroomReferenceText = value },
            ChildTableField.Requirements when value is not null => draft with { Requirements = value },
            ChildTableField.Notes when value is not null => draft with { Notes = value },
            ChildTableField.Todo when TryTodo(value, out var todo) => draft with { IsTodo = todo },
            ChildTableField.Verification when TryVerification(value, out var verified) => draft with { IsVerified = verified },
            _ => draft
        };
    internal static CheckDraft Install(CheckDraft draft, ChildTableField field, string? value)
        => field switch
        {
            ChildTableField.Name when value is not null => draft with { FriendlyName = value },
            ChildTableField.Subroom when value is not null => draft with { SubroomReferenceText = value },
            ChildTableField.Requirements when value is not null => draft with { Requirements = value },
            ChildTableField.Notes when value is not null => draft with { Notes = value },
            ChildTableField.Type => draft with { LocationType = string.IsNullOrEmpty(value) ? null : value },
            ChildTableField.Todo when TryTodo(value, out var todo) => draft with { IsTodo = todo },
            ChildTableField.Verification when TryVerification(value, out var verified) => draft with { IsVerified = verified },
            _ => draft
        };

    internal static bool InstallGenuineEdit<TBaseline, TDraft>(ChildTableRowInteraction<TBaseline, TDraft> row, TDraft installed)
    {
        if (row.IsArchived || EqualityComparer<TDraft>.Default.Equals(row.Draft, installed)) return false;
        row.ReplaceDraft(installed);
        row.BeginRetryFromEdit();
        return true;
    }

    internal static SubroomDraft Rebase(SubroomDurableBaseline origin, SubroomDraft local, SubroomDraft fresh) => Rebase(From(origin, local.ClientDraftId), local, fresh);
    internal static SubroomDraft Rebase(SubroomDraft origin, SubroomDraft local, SubroomDraft fresh) => fresh with
    {
        FriendlyName = origin.FriendlyName != local.FriendlyName ? local.FriendlyName : fresh.FriendlyName,
        ReferenceId = origin.ReferenceId != local.ReferenceId ? local.ReferenceId : fresh.ReferenceId,
        Notes = origin.Notes != local.Notes ? local.Notes : fresh.Notes,
        SceneUnitX = origin.SceneUnitX != local.SceneUnitX ? local.SceneUnitX : fresh.SceneUnitX,
        SceneUnitY = origin.SceneUnitY != local.SceneUnitY ? local.SceneUnitY : fresh.SceneUnitY,
        SceneUnitWidth = origin.SceneUnitWidth != local.SceneUnitWidth ? local.SceneUnitWidth : fresh.SceneUnitWidth,
        SceneUnitHeight = origin.SceneUnitHeight != local.SceneUnitHeight ? local.SceneUnitHeight : fresh.SceneUnitHeight
    };
    internal static SubroomDraft From(SubroomDurableBaseline value, Guid clientId) => new(clientId, value.FriendlyName, value.ReferenceId, value.Notes, value.SceneUnitX, value.SceneUnitY, value.SceneUnitWidth, value.SceneUnitHeight);
    internal static TransitionDraft From(TransitionDurableBaseline value, Guid clientId) => new(clientId, value.Alias, value.FriendlyName, value.InGameId, value.InGamePositionX, value.InGamePositionY, value.InGamePositionZ, value.LocalPositionX, value.LocalPositionY, value.LocalPositionZ, value.AnnotationSceneUnitX, value.AnnotationSceneUnitY, value.SourceSubroomReferenceText, value.DestinationRoomReferenceText, value.DestinationTransitionAliasText, value.Requirements, value.Notes, value.IsTodo, value.IsVerified, value.IsUnsafeArrival);
    internal static ConnectionDraft From(ConnectionDurableBaseline value, Guid clientId) => new(clientId, value.Alias, value.FriendlyName, value.SourceSubroomReferenceText, value.DestinationSubroomReferenceText, value.Requirements, value.Notes, value.EnableAnnotation, value.SceneUnitX, value.SceneUnitY, value.IsTodo, value.IsVerified);
    internal static TransitionDraft Rebase(TransitionDurableBaseline origin, TransitionDraft local, TransitionDraft fresh) => Rebase(From(origin, local.ClientDraftId), local, fresh);
    internal static TransitionDraft Rebase(TransitionDraft origin, TransitionDraft local, TransitionDraft fresh) => fresh with
    {
        IsUnsafeArrival = origin.IsUnsafeArrival != local.IsUnsafeArrival ? local.IsUnsafeArrival : fresh.IsUnsafeArrival,
        Alias = origin.Alias != local.Alias ? local.Alias : fresh.Alias,
        FriendlyName = origin.FriendlyName != local.FriendlyName ? local.FriendlyName : fresh.FriendlyName,
        SourceSubroomReferenceText = origin.SourceSubroomReferenceText != local.SourceSubroomReferenceText ? local.SourceSubroomReferenceText : fresh.SourceSubroomReferenceText,
        DestinationRoomReferenceText = origin.DestinationRoomReferenceText != local.DestinationRoomReferenceText ? local.DestinationRoomReferenceText : fresh.DestinationRoomReferenceText,
        DestinationTransitionAliasText = origin.DestinationTransitionAliasText != local.DestinationTransitionAliasText ? local.DestinationTransitionAliasText : fresh.DestinationTransitionAliasText,
        Requirements = origin.Requirements != local.Requirements ? local.Requirements : fresh.Requirements,
        Notes = origin.Notes != local.Notes ? local.Notes : fresh.Notes,
        IsTodo = origin.IsTodo != local.IsTodo ? local.IsTodo : fresh.IsTodo,
        IsVerified = origin.IsVerified != local.IsVerified ? local.IsVerified : fresh.IsVerified,
        InGameId = origin.InGameId != local.InGameId ? local.InGameId : fresh.InGameId,
        InGamePositionX = origin.InGamePositionX != local.InGamePositionX ? local.InGamePositionX : fresh.InGamePositionX,
        InGamePositionY = origin.InGamePositionY != local.InGamePositionY ? local.InGamePositionY : fresh.InGamePositionY,
        InGamePositionZ = origin.InGamePositionZ != local.InGamePositionZ ? local.InGamePositionZ : fresh.InGamePositionZ,
        LocalPositionX = origin.LocalPositionX != local.LocalPositionX ? local.LocalPositionX : fresh.LocalPositionX,
        LocalPositionY = origin.LocalPositionY != local.LocalPositionY ? local.LocalPositionY : fresh.LocalPositionY,
        LocalPositionZ = origin.LocalPositionZ != local.LocalPositionZ ? local.LocalPositionZ : fresh.LocalPositionZ,
        AnnotationSceneUnitX = origin.AnnotationSceneUnitX != local.AnnotationSceneUnitX ? local.AnnotationSceneUnitX : fresh.AnnotationSceneUnitX,
        AnnotationSceneUnitY = origin.AnnotationSceneUnitY != local.AnnotationSceneUnitY ? local.AnnotationSceneUnitY : fresh.AnnotationSceneUnitY
    };
    internal static ConnectionDraft Rebase(ConnectionDurableBaseline origin, ConnectionDraft local, ConnectionDraft fresh) => Rebase(From(origin, local.ClientDraftId), local, fresh);
    internal static ConnectionDraft Rebase(ConnectionDraft origin, ConnectionDraft local, ConnectionDraft fresh) => fresh with
    {
        Alias = origin.Alias != local.Alias ? local.Alias : fresh.Alias,
        FriendlyName = origin.FriendlyName != local.FriendlyName ? local.FriendlyName : fresh.FriendlyName,
        SourceSubroomReferenceText = origin.SourceSubroomReferenceText != local.SourceSubroomReferenceText ? local.SourceSubroomReferenceText : fresh.SourceSubroomReferenceText,
        DestinationSubroomReferenceText = origin.DestinationSubroomReferenceText != local.DestinationSubroomReferenceText ? local.DestinationSubroomReferenceText : fresh.DestinationSubroomReferenceText,
        Requirements = origin.Requirements != local.Requirements ? local.Requirements : fresh.Requirements,
        Notes = origin.Notes != local.Notes ? local.Notes : fresh.Notes,
        IsTodo = origin.IsTodo != local.IsTodo ? local.IsTodo : fresh.IsTodo,
        IsVerified = origin.IsVerified != local.IsVerified ? local.IsVerified : fresh.IsVerified,
        EnableAnnotation = origin.EnableAnnotation != local.EnableAnnotation ? local.EnableAnnotation : fresh.EnableAnnotation,
        SceneUnitX = origin.SceneUnitX != local.SceneUnitX ? local.SceneUnitX : fresh.SceneUnitX,
        SceneUnitY = origin.SceneUnitY != local.SceneUnitY ? local.SceneUnitY : fresh.SceneUnitY
    };

    internal static ChildTableBoundaryAdapter<SubroomDurableBaseline, SubroomDraft> SubroomBoundaries { get; } = new(
        From, Rebase, Compare, baseline => baseline.UpdatedUtc, RoomEditorV2CommandService.IsPotentialSubroomReferencePreparation);
    internal static ChildTableBoundaryAdapter<TransitionDurableBaseline, TransitionDraft> TransitionBoundaries { get; } = new(
        From, Rebase, Compare, baseline => baseline.UpdatedUtc, RoomEditorV2CommandService.IsPotentialInversePreparation);
    internal static ChildTableBoundaryAdapter<ConnectionDurableBaseline, ConnectionDraft> ConnectionBoundaries { get; } = new(
        From, Rebase, Compare, baseline => baseline.UpdatedUtc, (_, _) => false);

    private static SubroomDurableBaseline Compare(SubroomDurableBaseline fresh, SubroomDraft origin, SubroomDraft authored, DateTime? observed) => fresh with
    {
        UpdatedUtc = observed ?? fresh.UpdatedUtc,
        FriendlyName = origin.FriendlyName != authored.FriendlyName ? origin.FriendlyName : fresh.FriendlyName,
        ReferenceId = origin.ReferenceId != authored.ReferenceId ? origin.ReferenceId : fresh.ReferenceId,
        Notes = origin.Notes != authored.Notes ? origin.Notes : fresh.Notes
    };
    private static TransitionDurableBaseline Compare(TransitionDurableBaseline fresh, TransitionDraft origin, TransitionDraft authored, DateTime? observed) => fresh with
    {
        IsUnsafeArrival = origin.IsUnsafeArrival != authored.IsUnsafeArrival ? origin.IsUnsafeArrival : fresh.IsUnsafeArrival,
        UpdatedUtc = observed ?? fresh.UpdatedUtc,
        Alias = origin.Alias != authored.Alias ? origin.Alias : fresh.Alias,
        FriendlyName = origin.FriendlyName != authored.FriendlyName ? origin.FriendlyName : fresh.FriendlyName,
        SourceSubroomReferenceText = origin.SourceSubroomReferenceText != authored.SourceSubroomReferenceText ? origin.SourceSubroomReferenceText : fresh.SourceSubroomReferenceText,
        DestinationRoomReferenceText = origin.DestinationRoomReferenceText != authored.DestinationRoomReferenceText ? origin.DestinationRoomReferenceText : fresh.DestinationRoomReferenceText,
        DestinationTransitionAliasText = origin.DestinationTransitionAliasText != authored.DestinationTransitionAliasText ? origin.DestinationTransitionAliasText : fresh.DestinationTransitionAliasText,
        Requirements = origin.Requirements != authored.Requirements ? origin.Requirements : fresh.Requirements,
        Notes = origin.Notes != authored.Notes ? origin.Notes : fresh.Notes,
        IsTodo = origin.IsTodo != authored.IsTodo ? origin.IsTodo : fresh.IsTodo,
        IsVerified = origin.IsVerified != authored.IsVerified ? origin.IsVerified : fresh.IsVerified
    };
    private static ConnectionDurableBaseline Compare(ConnectionDurableBaseline fresh, ConnectionDraft origin, ConnectionDraft authored, DateTime? observed) => fresh with
    {
        UpdatedUtc = observed ?? fresh.UpdatedUtc,
        Alias = origin.Alias != authored.Alias ? origin.Alias : fresh.Alias,
        FriendlyName = origin.FriendlyName != authored.FriendlyName ? origin.FriendlyName : fresh.FriendlyName,
        SourceSubroomReferenceText = origin.SourceSubroomReferenceText != authored.SourceSubroomReferenceText ? origin.SourceSubroomReferenceText : fresh.SourceSubroomReferenceText,
        DestinationSubroomReferenceText = origin.DestinationSubroomReferenceText != authored.DestinationSubroomReferenceText ? origin.DestinationSubroomReferenceText : fresh.DestinationSubroomReferenceText,
        Requirements = origin.Requirements != authored.Requirements ? origin.Requirements : fresh.Requirements,
        Notes = origin.Notes != authored.Notes ? origin.Notes : fresh.Notes,
        IsTodo = origin.IsTodo != authored.IsTodo ? origin.IsTodo : fresh.IsTodo,
        IsVerified = origin.IsVerified != authored.IsVerified ? origin.IsVerified : fresh.IsVerified
    };

    internal static string? NativeValue(SubroomDraft draft, ChildTableField field) => field switch
    { ChildTableField.Name => draft.FriendlyName, ChildTableField.Reference => draft.ReferenceId, ChildTableField.Notes => draft.Notes, _ => null };
    internal static string? NativeValue(TransitionDraft draft, ChildTableField field) => field switch
    {
        ChildTableField.UnsafeArrival => draft.IsUnsafeArrival ? "true" : "false",
        ChildTableField.Alias => draft.Alias, ChildTableField.Name => draft.FriendlyName,
        ChildTableField.Source => draft.SourceSubroomReferenceText ?? "", ChildTableField.DestinationRoom => draft.DestinationRoomReferenceText ?? "",
        ChildTableField.DestinationAlias => draft.DestinationTransitionAliasText ?? "", ChildTableField.Requirements => draft.Requirements,
        ChildTableField.Notes => draft.Notes, ChildTableField.Todo => draft.IsTodo ? "true" : "false",
        ChildTableField.Verification => draft.IsVerified == true ? "true" : draft.IsVerified == false ? "false" : "", _ => null
    };

    internal static ChildTableBoundaryCompletion<SubroomDurableBaseline> Completion(V2SubroomCommandOutcome result) => new(result.Status switch
    { V2SubroomCommandStatus.Committed => ChildTableBoundaryResult.Committed, V2SubroomCommandStatus.Unchanged => ChildTableBoundaryResult.Unchanged, V2SubroomCommandStatus.Proposal => ChildTableBoundaryResult.Proposal, V2SubroomCommandStatus.Unexpected => ChildTableBoundaryResult.Unexpected, _ => ChildTableBoundaryResult.Failed }, result.FreshBaseline, result.CreatedEntityId, result.Message, result.PresentationRefreshed, result.RequiresRoomReload);
    internal static ChildTableBoundaryCompletion<TransitionDurableBaseline> Completion(V2TransitionCommandOutcome result) => new(result.Status switch
    { V2TransitionCommandStatus.Committed => ChildTableBoundaryResult.Committed, V2TransitionCommandStatus.Unchanged => ChildTableBoundaryResult.Unchanged, V2TransitionCommandStatus.Proposal => ChildTableBoundaryResult.Proposal, V2TransitionCommandStatus.Unexpected => ChildTableBoundaryResult.Unexpected, _ => ChildTableBoundaryResult.Failed }, result.FreshBaseline, result.CreatedEntityId, result.Message, result.PresentationRefreshed, result.RequiresRoomReload);
    internal static ChildTableBoundaryCompletion<ConnectionDurableBaseline> Completion(V2ConnectionCommandOutcome result) => new(result.Status switch
    { V2ConnectionCommandStatus.Committed => ChildTableBoundaryResult.Committed, V2ConnectionCommandStatus.Unchanged => ChildTableBoundaryResult.Unchanged, V2ConnectionCommandStatus.Unexpected => ChildTableBoundaryResult.Unexpected, _ => ChildTableBoundaryResult.Failed }, result.FreshBaseline, result.CreatedEntityId, result.Message, result.PresentationRefreshed, result.RequiresRoomReload);
}
