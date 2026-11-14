namespace Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;

/// <summary>Closed pane-local scene identity. It is transient and never a persistence key or DOM marker.</summary>
public enum V2SceneSelectedItemKind { Subroom, Transition, Check, Connection }
/// <summary>One pane-local selection survives a non-renderable durable state; renderability is derived from SceneLayoutView.</summary>
public sealed record V2SceneSelectedItem(V2SceneSelectedItemKind Kind, Guid EntityId);
/// <summary>Browser callback evidence for one mounted scene owner; never persisted.</summary>
public sealed record V2SceneSelectionCallback(Guid RoomId, long OwnerGeneration, V2SceneSelectedItem? Selection);
/// <summary>Page-to-scene transient placement request; it is never persisted or a UI identity.</summary>
public sealed record SceneAnnotationPlacementRequest(string Kind, Guid EntityId, string InitiatorId, Guid? GroupMarkerEntityId = null);
/// <summary>Transient, page-derived availability for the fixed scene annotation action box.</summary>
public enum V2SceneAction { Rearm, Reset, Clear, ToggleVisibility, Dismiss }
public sealed record V2SceneActionBoxView(bool RearmEnabled, bool ResetEnabled, bool ClearEnabled, bool VisibilityEnabled,
    bool DismissEnabled, string RearmLabel, string ResetLabel, string ClearLabel, string VisibilityLabel, string DismissLabel,
    bool AnnotationIsVisible, string StatusText, bool AnnotationMatchesGamePosition);
