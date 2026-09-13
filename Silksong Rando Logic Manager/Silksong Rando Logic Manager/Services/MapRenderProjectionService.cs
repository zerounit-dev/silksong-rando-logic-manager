using System.Globalization;
using System.Text;
using System.Diagnostics;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed record MapRenderProjectionChunk(
    Guid ChunkId,
    string StableIdentity,
    Guid? ActiveResolvedRoomId,
    double MapUnitMinX,
    double MapUnitMinY,
    double MapUnitMaxX,
    double MapUnitMaxY,
    double? MapUnitZ);

public sealed record MapRenderProjectionBounds(double MinX, double MinY, double MaxX, double MaxY);
public sealed record MapRenderProjectionInput(Guid MapId, IReadOnlyList<MapRenderProjectionChunk> Chunks);
public sealed record MapRenderProjectionResult(string CacheKey, IReadOnlyList<MapRenderProjectionOwner> Owners, bool CacheHit, TimeSpan Elapsed);

public sealed record MapRenderProjectionOwner(Guid? ActiveResolvedRoomId, Guid? UnlinkedChunkId, string SvgPathData, MapRenderProjectionBounds Bounds)
{
    public bool IsLinked => ActiveResolvedRoomId is not null;
}

public sealed class MapRenderProjectionService
{
    private readonly Lock cacheLock = new();
    private ProjectionCacheEntry? cache;

    public MapRenderProjectionResult Project(MapRenderProjectionInput input)
    {
        var started = Stopwatch.GetTimestamp();
        var source = input.Chunks.ToList();
        foreach (var chunk in source) Validate(chunk);
        var cacheKey = CreateCacheKey(input.MapId, source);
        lock (cacheLock)
        {
            if (cache?.Key == cacheKey) return new(cacheKey, cache.Owners, true, Stopwatch.GetElapsedTime(started));
        }

        var owners = ProjectCore(source);
        lock (cacheLock) cache = new(cacheKey, owners);
        return new(cacheKey, owners, false, Stopwatch.GetElapsedTime(started));
    }

    private static IReadOnlyList<MapRenderProjectionOwner> ProjectCore(IReadOnlyList<MapRenderProjectionChunk> source)
    {

        var xCoordinates = source.SelectMany(chunk => new[] { chunk.MapUnitMinX, chunk.MapUnitMaxX }).Distinct().Order().ToArray();
        var yCoordinates = source.SelectMany(chunk => new[] { chunk.MapUnitMinY, chunk.MapUnitMaxY }).Distinct().Order().ToArray();
        var xIndexes = xCoordinates.Select((value, index) => (value, index)).ToDictionary(x => x.value, x => x.index);
        var yIndexes = yCoordinates.Select((value, index) => (value, index)).ToDictionary(y => y.value, y => y.index);
        var cellsByOwner = new Dictionary<string, OwnerCells>(StringComparer.Ordinal);
        var claimedCells = new HashSet<Cell>();

        // Earlier chunks own their complete coordinate ranges, so each cell is compared once.
        foreach (var chunk in source
                     .OrderBy(chunk => chunk.ActiveResolvedRoomId is null ? 0 : 1)
                     .ThenBy(chunk => chunk.MapUnitZ ?? double.PositiveInfinity)
            .ThenBy(chunk => chunk.StableIdentity, StringComparer.Ordinal))
        {
            var key = chunk.ActiveResolvedRoomId is { } roomId ? $"room:{roomId:N}" : $"chunk:{chunk.ChunkId:N}";
            cellsByOwner.TryGetValue(key, out var ownerCells);

            for (var x = xIndexes[chunk.MapUnitMinX]; x < xIndexes[chunk.MapUnitMaxX]; x++)
            for (var y = yIndexes[chunk.MapUnitMinY]; y < yIndexes[chunk.MapUnitMaxY]; y++)
            {
                var cell = new Cell(x, y);
                if (!claimedCells.Add(cell)) continue;
                ownerCells ??= new(chunk.ActiveResolvedRoomId, chunk.ActiveResolvedRoomId is null ? chunk.ChunkId : null);
                cellsByOwner.TryAdd(key, ownerCells);
                ownerCells.Cells.Add(cell);
            }
        }

        return cellsByOwner.Values
            .Select(owner => new MapRenderProjectionOwner(owner.RoomId, owner.UnlinkedChunkId, CreatePath(owner.Cells, xCoordinates, yCoordinates), CreateBounds(owner.Cells, xCoordinates, yCoordinates)))
            .OrderBy(owner => owner.ActiveResolvedRoomId is null ? 1 : 0)
            .ThenBy(owner => owner.ActiveResolvedRoomId ?? owner.UnlinkedChunkId)
            .ToList();
    }

    private static string CreateCacheKey(Guid mapId, IEnumerable<MapRenderProjectionChunk> chunks)
    {
        var key = new StringBuilder(mapId.ToString("N", CultureInfo.InvariantCulture)).Append(';');
        foreach (var chunk in chunks.OrderBy(chunk => chunk.ChunkId).ThenBy(chunk => chunk.StableIdentity, StringComparer.Ordinal))
        {
            key.Append(chunk.ChunkId).Append('|').Append(chunk.StableIdentity).Append('|').Append(chunk.ActiveResolvedRoomId).Append('|')
                .Append(Number(chunk.MapUnitMinX)).Append('|').Append(Number(chunk.MapUnitMinY)).Append('|').Append(Number(chunk.MapUnitMaxX)).Append('|').Append(Number(chunk.MapUnitMaxY)).Append('|')
                .Append(chunk.MapUnitZ is { } z ? Number(z) : "null").Append(';');
        }

        return key.ToString();
    }

    private static string CreatePath(HashSet<Cell> cells, double[] xCoordinates, double[] yCoordinates)
    {
        var edges = new HashSet<Edge>();
        foreach (var cell in cells)
        {
            if (!cells.Contains(new(cell.X, cell.Y - 1))) edges.Add(new(new(cell.X, cell.Y), new(cell.X + 1, cell.Y)));
            if (!cells.Contains(new(cell.X + 1, cell.Y))) edges.Add(new(new(cell.X + 1, cell.Y), new(cell.X + 1, cell.Y + 1)));
            if (!cells.Contains(new(cell.X, cell.Y + 1))) edges.Add(new(new(cell.X + 1, cell.Y + 1), new(cell.X, cell.Y + 1)));
            if (!cells.Contains(new(cell.X - 1, cell.Y))) edges.Add(new(new(cell.X, cell.Y + 1), new(cell.X, cell.Y)));
        }

        var remaining = new SortedSet<Edge>(edges, EdgeComparer.Instance);
        var outgoing = edges.GroupBy(edge => edge.Start).ToDictionary(group => group.Key, group => group.ToList());
        var paths = new List<string>();
        while (remaining.Count > 0)
        {
            var first = remaining.Min!;
            var points = new List<Point> { first.Start };
            var current = first;
            while (true)
            {
                remaining.Remove(current);
                points.Add(current.End);
                if (current.End == first.Start) break;
                current = NextEdge(current, outgoing, remaining);
            }

            paths.Add(ToSvgPath(Simplify(points), xCoordinates, yCoordinates));
        }

        return string.Join(" ", paths);
    }

    private static MapRenderProjectionBounds CreateBounds(HashSet<Cell> cells, double[] xCoordinates, double[] yCoordinates)
    {
        var minX = cells.Min(cell => xCoordinates[cell.X]);
        var minY = cells.Min(cell => yCoordinates[cell.Y]);
        var maxX = cells.Max(cell => xCoordinates[cell.X + 1]);
        var maxY = cells.Max(cell => yCoordinates[cell.Y + 1]);
        return new(minX, minY, maxX, maxY);
    }

    private static Edge NextEdge(Edge current, IReadOnlyDictionary<Point, List<Edge>> outgoing, IReadOnlySet<Edge> remaining) => outgoing[current.End]
        .Where(remaining.Contains)
        .OrderBy(edge => (Direction(edge) - Direction(current) + 4) % 4 == 1 ? 0 : (Direction(edge) - Direction(current) + 4) % 4)
        .First();

    private static List<Point> Simplify(List<Point> points)
    {
        points.RemoveAt(points.Count - 1);
        var simplified = new List<Point>();
        for (var index = 0; index < points.Count; index++)
        {
            var previous = points[(index - 1 + points.Count) % points.Count];
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            if ((previous.X == current.X && current.X == next.X) || (previous.Y == current.Y && current.Y == next.Y)) continue;
            simplified.Add(current);
        }

        return simplified;
    }

    private static string ToSvgPath(List<Point> points, double[] xCoordinates, double[] yCoordinates) =>
        $"M {Number(xCoordinates[points[0].X])} {Number(yCoordinates[points[0].Y])}" +
        string.Concat(points.Skip(1).Select(point => $" L {Number(xCoordinates[point.X])} {Number(yCoordinates[point.Y])}")) + " Z";

    private static int Direction(Edge edge) => edge.End.X > edge.Start.X ? 0 : edge.End.Y > edge.Start.Y ? 1 : edge.End.X < edge.Start.X ? 2 : 3;
    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    private static void Validate(MapRenderProjectionChunk chunk)
    {
        if (string.IsNullOrWhiteSpace(chunk.StableIdentity) ||
            !double.IsFinite(chunk.MapUnitMinX) || !double.IsFinite(chunk.MapUnitMinY) ||
            !double.IsFinite(chunk.MapUnitMaxX) || !double.IsFinite(chunk.MapUnitMaxY) ||
            chunk.MapUnitMinX >= chunk.MapUnitMaxX || chunk.MapUnitMinY >= chunk.MapUnitMaxY ||
            chunk.MapUnitZ is { } z && !double.IsFinite(z))
        {
            throw new ArgumentException("Map render chunks require a stable identity and complete finite rectangle.", nameof(chunk));
        }
    }

    private sealed class OwnerCells(Guid? roomId, Guid? unlinkedChunkId)
    {
        public Guid? RoomId { get; } = roomId;
        public Guid? UnlinkedChunkId { get; } = unlinkedChunkId;
        public HashSet<Cell> Cells { get; } = [];
    }

    private readonly record struct Cell(int X, int Y);
    private readonly record struct Point(int X, int Y);
    private readonly record struct Edge(Point Start, Point End);
    private sealed record ProjectionCacheEntry(string Key, IReadOnlyList<MapRenderProjectionOwner> Owners);

    private sealed class EdgeComparer : IComparer<Edge>
    {
        public static EdgeComparer Instance { get; } = new();
        public int Compare(Edge left, Edge right)
        {
            var comparison = left.Start.X.CompareTo(right.Start.X);
            if (comparison != 0) return comparison;
            comparison = left.Start.Y.CompareTo(right.Start.Y);
            if (comparison != 0) return comparison;
            comparison = left.End.X.CompareTo(right.End.X);
            return comparison != 0 ? comparison : left.End.Y.CompareTo(right.End.Y);
        }
    }
}
