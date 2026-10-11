using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using System.Globalization;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class RoomGeometryExportService(IDbContextFactory<LogicDbContext> contexts) : IRoomGeometryExportService
{
    public async Task<RoomGeometryExportResult> GenerateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var snapshot = await RoomExportStructure.LoadSnapshotAsync(db, false, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var globalFailures = new List<RoomGraphExportFailure>();
        RoomExportStructure.BuildTypeLegend(globalFailures);
        var structure = RoomExportStructure.Project(snapshot, globalFailures);
        if (structure.Failures.Count != 0) return new(null, structure.Failures, structure.Warnings);
        var groupNames = snapshot.Groups.ToDictionary(group => group.Id, group => group.Name);
        var rooms = structure.Rooms.Select(room => ProjectRoom(room, groupNames[room.Row.GroupId!.Value])).ToArray();
        return new(RoomExportStructure.Serialize(new RoomGeometryDocument(2, rooms)), [], structure.Warnings);
    }

    private static RoomGeometryRoom ProjectRoom(RoomExportStructure.ProjectedRoom room, string roomGroupName)
    {
        var valid = SceneBounds.TryGetSize(room.Row.MinX, room.Row.MinY, room.Row.MaxX, room.Row.MaxY, out var wScene, out var hScene);
        double? width = valid ? wScene : null, height = valid ? hScene : null;
        var pixels = OutputDimensions(width, height, room.Row.PixelsPerUnitX, room.Row.PixelsPerUnitY);
        var minX = room.Row.MinX; var minY = room.Row.MinY;
        var nodes = room.Nodes.Select(node =>
        {
            var row = node.Row;
            var x = Finite(row?.X);
            var y = Finite(row?.Y);
            var w = Finite(row?.Width);
            var h = Finite(row?.Height);
            var image = Rectangle(x, y, w, h, minX, minY, room.Row.MaxX, room.Row.MaxY, width, height);
            var pixelX = image.X * pixels.Width;
            var pixelY = image.Y * pixels.Height;
            var pixelWidth = image.Width * pixels.Width;
            var pixelHeight = image.Height * pixels.Height;
            return new RoomGeometryNode(node.Id, node.Name, x, y, w, h, row?.Enabled ?? false,
                image.X, image.Y, image.Width, image.Height,
                image.X, image.Y, image.X + image.Width, image.Y + image.Height,
                pixelX, pixelY, pixelWidth, pixelHeight,
                pixelX, pixelY, pixelX + pixelWidth, pixelY + pixelHeight);
        }).ToArray();
        var transitions = room.Transitions.Select(transition =>
        {
            var row = transition.Row;
            var x = Finite(row.X);
            var y = Finite(row.Y);
            var image = Point(Finite(row.GameX), Finite(row.GameY), minX, minY, room.Row.MaxX, room.Row.MaxY, width, height);
            return new RoomGeometryTransition(transition.Id, row.Alias, row.Name, row.InGameId, row.IsUnsafeArrival,
                transition.SourceNodeId, transition.Target, x, y, row.Enabled, Finite(row.GameX), Finite(row.GameY), Finite(row.GameZ),
                image.X, image.Y, image.X * pixels.Width, image.Y * pixels.Height, image.OutOfBounds);
        }).ToArray();
        // Effective pathway state uses database identity ties, not export-ID ties.
        // The projected directed-edge array retains its independent export ordering.
        var effective = room.Connections.GroupBy(x => x.Row.Alias.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderBy(row => row.Row.SortOrder).ThenBy(row => row.Row.Id).First().Row,
                StringComparer.OrdinalIgnoreCase);
        var connections = room.Connections.Select(connection =>
        {
            var row = connection.Row;
            var geometry = effective[row.Alias.Trim()];
            var x = Finite(geometry.X);
            var y = Finite(geometry.Y);
            var image = Point(x, y, minX, minY, room.Row.MaxX, room.Row.MaxY, width, height);
            return new RoomGeometryConnection(connection.Id, row.Alias, row.Name, connection.SourceNodeId, connection.TargetNodeId,
                x, y, geometry.Enabled, image.X, image.Y, image.X * pixels.Width, image.Y * pixels.Height, image.OutOfBounds);
        }).ToArray();
        var locations = room.Locations.Select(location =>
        {
            var row = location.Row;
            var x = Finite(row.X);
            var y = Finite(row.Y);
            var image = Point(Finite(row.GameX), Finite(row.GameY), minX, minY, room.Row.MaxX, room.Row.MaxY, width, height);
            return new RoomGeometryLocation(location.Id, row.Name, row.InGameId, location.Type, location.NodeId,
                x, y, row.Enabled, Finite(row.GameX), Finite(row.GameY), Finite(row.GameZ),
                image.X, image.Y, image.X * pixels.Width, image.Y * pixels.Height, image.OutOfBounds);
        }).ToArray();
        return new(room.Id, room.Row.Id.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant(), room.Row.Name,
            roomGroupName, room.Row.InGameId, room.IsMetadataOnly, Finite(minX), Finite(minY), Finite(room.Row.MaxX), Finite(room.Row.MaxY),
            width, height, pixels.Width, pixels.Height, nodes, transitions, connections, locations);
    }

    private static (double? Width, double? Height) OutputDimensions(double? width, double? height, double? scaleX, double? scaleY)
    {
        if (width is not > 0 || height is not > 0 || !SceneBounds.HasUsableScales(scaleX, scaleY)) return (null, null);
        var pixelWidth = OutputAxis(width.Value * scaleX!.Value);
        var pixelHeight = OutputAxis(height.Value * scaleY!.Value);
        // A usable image context requires both axes; normalized geometry is independent.
        return pixelWidth is null || pixelHeight is null ? (null, null) : (pixelWidth, pixelHeight);
    }

    private static double? OutputAxis(double value)
    {
        // Match capture generation without allocation, file reads or mapping defaults.
        // Positive-product underflow to zero still yields the generation minimum of one.
        var rounded = Math.Max(1d, Math.Round(value, MidpointRounding.ToEven));
        return double.IsFinite(value) && double.IsFinite(rounded) && rounded <= uint.MaxValue ? rounded : null;
    }

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;

    private static (double? X, double? Y, bool? OutOfBounds) Point(double? x, double? y, double minX, double minY, double? maxX, double? maxY, double? width, double? height)
    {
        if (x is not { } px || y is not { } py || width is not > 0 || height is not > 0) return (null, null, null);
        var dx = px < minX ? minX - px : px > maxX!.Value ? px - maxX.Value : 0;
        var dy = py < minY ? minY - py : py > maxY!.Value ? py - maxY.Value : 0;
        // Reject distant axes before squaring so even huge finite points cannot overflow.
        // Corners use Euclidean distance, with exactly one scene unit still outside.
        if (dx >= 1 || dy >= 1 || Math.Sqrt(dx * dx + dy * dy) >= 1) return (null, null, true);
        return ((Math.Clamp(px, minX, maxX!.Value) - minX) / width.Value,
            (maxY!.Value - Math.Clamp(py, minY, maxY.Value)) / height.Value, false);
    }

    private static (double? X, double? Y, double? Width, double? Height) Rectangle(
        double? x, double? y, double? width, double? height, double minX, double minY, double? maxX, double? maxY, double? sceneWidth, double? sceneHeight)
    {
        if (x is not { } left || y is not { } bottom || width is not > 0 || height is not > 0 ||
            sceneWidth is not > 0 || sceneHeight is not > 0) return (null, null, null, null);
        var right = left + width.Value;
        var top = bottom + height.Value;
        // Overflow is an invalid derived rectangle, not an infinite edge to clamp.
        if (!double.IsFinite(right) || !double.IsFinite(top)) return (null, null, null, null);
        left = Math.Clamp(left, minX, maxX!.Value);
        right = Math.Clamp(right, minX, maxX.Value);
        bottom = Math.Clamp(bottom, minY, maxY!.Value);
        top = Math.Clamp(top, minY, maxY.Value);
        return ((left - minX) / sceneWidth.Value, (maxY.Value - top) / sceneHeight.Value,
            (right - left) / sceneWidth.Value, (top - bottom) / sceneHeight.Value);
    }
}
