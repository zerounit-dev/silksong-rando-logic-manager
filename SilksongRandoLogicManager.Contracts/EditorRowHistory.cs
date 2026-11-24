using System.Text.Json.Serialization;

namespace SilksongRandoLogicManager.Contracts;

public enum EditorHistoryFamily { Subroom, Transition, Connection, Check, Room }
public enum EditorHistoryState { Active, Archived, Deleted }
public enum EditorHistoryStatus { Available, HistoryUnavailable }

public sealed record EditorHistoryCursor(long Ceiling, DateTime BeforeUtc, long BeforeRowId);
public sealed record EditorHistoryRequest([property: JsonRequired] EditorHistoryFamily Family, Guid RecordId, EditorHistoryCursor? Cursor);
public sealed record EditorHistoryPage(EditorHistoryStatus Status, EditorHistoryRevision[] Revisions, EditorHistoryCursor? Next);
// DetailsOnly means the revision changed only fields hidden by the child viewer's
// more details checkbox. It is false for the oldest row and for Rooms.
public sealed record EditorHistoryRevision(long RowId, DateTime ChangeUtc, Guid ActorId, string ChangedBy,
    bool DetailsOnly, EditorHistoryValues Values);

public sealed record EditorHistoryPoint(double? X, double? Y, double? Z = null);
public sealed record EditorHistoryRectangle(double? X, double? Y, double? Width, double? Height);
public sealed record EditorHistoryRoomDetails(string? Contributors, string? Comments, Guid? GroupId, string? GroupName = null);

// Only authored values displayed by at least one editor family, plus recorded state.
// Keep coordinates and nullable verification typed through comparison in the viewer.
public sealed record EditorHistoryValues(string? Name, string? Notes, bool AnnotationEnabled, EditorHistoryState State,
    string? Reference = null, string? Alias = null, string? Source = null, string? Destination = null,
    string? DestinationAlias = null, string? Type = null, bool UnsafeArrival = false, string? Requirements = null,
    bool Todo = false, bool? Verify = null, string? InGameId = null, EditorHistoryPoint? InGamePosition = null,
    EditorHistoryPoint? LocalPosition = null, EditorHistoryPoint? AnnotationPoint = null,
    EditorHistoryRectangle? AnnotationRectangle = null, EditorHistoryRoomDetails? RoomDetails = null);
