namespace Silksong_Rando_Logic_Manager.Components.RoomEditorV2;

// Closed presentation identities. Wire spelling belongs at the browser boundary,
// not in markup selection or a JavaScript copy of the domain field layout.
internal enum ChildTableField
{
    Alias, Name, Reference, Source, Destination, DestinationRoom,
    DestinationAlias, Subroom, Requirements, Notes, Todo, Verification, Type, UnsafeArrival
}

internal enum ChildTableDirection { Left, Right, Up, Down }
internal enum ChildTableKind { Subroom, Transition, Connection, Check }
internal enum ChildTablePartition { Active, Archived }
internal enum ChildTableSection { Checks, Enemies }
internal enum ChildTableAction { Archive, Restore, OpenDelete, CancelDelete, Delete }

internal sealed record ChildTableRegistration(string Method, string Blur, string Drop, string? Control,
    string? Action, string Row, string Table, string Grip, string Field, string Client,
    string DirectionPrefix, string Before, string Tail, string TailRow, string? Section = null)
{
    public IReadOnlyList<string> Actions => Action is null ? [] : ["archive", "restore", "open-delete"];
}

internal static class ChildTableBrowserFacts
{
    internal static ChildTableRegistration Registration(ChildTableKind kind) => kind switch
    {
        ChildTableKind.Subroom => new(nameof(SubroomTablePresentation.NavigateSubroomAsync), nameof(SubroomTablePresentation.BlurSubroomAsync), nameof(SubroomTablePresentation.DropSubroomAsync), null,
            nameof(SubroomTablePresentation.ChildActionSubroomAsync), "v2SubroomRow", "v2SubroomTable", "v2SubroomGrip", "v2SubroomField", "v2SubroomClientRow", "v2Subroom", "v2-drop-before", "v2-drop-tail", "v2SubroomTail"),
        ChildTableKind.Transition => new(nameof(TransitionTablePresentation.NavigateTransitionAsync), nameof(TransitionTablePresentation.BlurTransitionAsync), nameof(TransitionTablePresentation.DropTransitionAsync), nameof(TransitionTablePresentation.ControlTransitionAsync),
            null, "v2TransitionRow", "v2TransitionTable", "v2TransitionGrip", "v2TransitionField", "v2TransitionClientRow", "v2Transition", "v2-transition-drop-before", "v2-transition-drop-tail", "v2TransitionTail"),
        ChildTableKind.Connection => new(nameof(ConnectionTablePresentation.NavigateConnectionAsync), nameof(ConnectionTablePresentation.BlurConnectionAsync), nameof(ConnectionTablePresentation.DropConnectionAsync), nameof(ConnectionTablePresentation.ControlConnectionAsync),
            null, "v2ConnectionRow", "v2ConnectionTable", "v2ConnectionGrip", "v2ConnectionField", "v2ConnectionClientRow", "v2Connection", "v2-connection-drop-before", "v2-connection-drop-tail", "v2ConnectionTail"),
        ChildTableKind.Check => new(nameof(CheckTablePresentation.NavigateCheckAsync), nameof(CheckTablePresentation.BlurCheckAsync), nameof(CheckTablePresentation.DropCheckAsync), nameof(CheckTablePresentation.ControlCheckAsync),
            nameof(CheckTablePresentation.ChildActionCheckAsync), "v2CheckRow", "v2CheckTable", "v2CheckGrip", "v2CheckField", "v2CheckClientRow", "v2Check", "v2-check-drop-before", "v2-check-drop-tail", "v2CheckTail", "v2CheckSection"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static bool TryField(string wire, out ChildTableField field)
    {
        foreach (var candidate in Enum.GetValues<ChildTableField>())
            if (candidate.Wire() == wire) { field = candidate; return true; }
        field = default; return false;
    }
    internal static bool TryDirection(string wire, out ChildTableDirection direction)
    {
        foreach (var candidate in Enum.GetValues<ChildTableDirection>())
            if (candidate.Wire() == wire) { direction = candidate; return true; }
        direction = default; return false;
    }
    internal static bool TryPartition(string wire, out ChildTablePartition partition)
    {
        partition = wire == "archived" ? ChildTablePartition.Archived : ChildTablePartition.Active;
        return wire is "active" or "archived";
    }
    internal static bool TrySection(string wire, out ChildTableSection section)
    {
        section = wire == "enemies" ? ChildTableSection.Enemies : ChildTableSection.Checks;
        return wire is "checks" or "enemies";
    }
    internal static bool TryAction(string wire, out ChildTableAction action)
    {
        action = wire switch { "archive" => ChildTableAction.Archive, "restore" => ChildTableAction.Restore,
            "open-delete" => ChildTableAction.OpenDelete, "cancel-delete" => ChildTableAction.CancelDelete,
            "delete" => ChildTableAction.Delete, _ => (ChildTableAction)(-1) };
        return Enum.IsDefined(action);
    }
    internal static string Wire(this ChildTableField field) => field switch
    {
        ChildTableField.Alias => "alias",
        ChildTableField.Name => "name",
        ChildTableField.Reference => "reference",
        ChildTableField.Source => "source",
        ChildTableField.Destination => "destination",
        ChildTableField.DestinationRoom => "destination-room",
        ChildTableField.DestinationAlias => "destination-alias",
        ChildTableField.Subroom => "subroom",
        ChildTableField.Requirements => "requirements",
        ChildTableField.Notes => "notes",
        ChildTableField.Todo => "todo",
        ChildTableField.Verification => "verification",
        ChildTableField.Type => "type",
        ChildTableField.UnsafeArrival => "unsafe-arrival",
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    internal static string Wire(this ChildTableDirection direction) => direction switch
    {
        ChildTableDirection.Left => "left",
        ChildTableDirection.Right => "right",
        ChildTableDirection.Up => "up",
        ChildTableDirection.Down => "down",
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    // Attribute facts only: no HTML, event receivers, draft ownership or persistence.
    internal static Dictionary<string, object> FieldAttributes(
        string table, Guid clientId, ChildTableField field, string label, bool changed,
        Guid latentId, Func<ChildTableDirection, ChildTableFocusTarget?> target)
    {
        var attributes = new Dictionary<string, object>
        {
            ["id"] = ChildTableInteractionPrimitives.FieldId(clientId, field.Wire()),
            ["aria-label"] = label,
            [$"data-v2-{table}-field"] = field.Wire(),
            [$"data-v2-{table}-client-row"] = clientId,
            ["data-v2-child-row-changed"] = changed ? "true" : "false"
        };
        foreach (var direction in Enum.GetValues<ChildTableDirection>())
        {
            if (target(direction) is not { } destination) continue;
            var name = $"data-v2-{table}-{direction.Wire()}";
            attributes[name] = $"{destination.ClientRowId}|{destination.Field}";
            if (destination.ClientRowId == latentId) attributes[$"{name}-latent"] = "true";
        }
        return attributes;
    }
}
