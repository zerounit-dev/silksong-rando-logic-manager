using System.Globalization;
using System.Text;

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

public sealed record MapRenderProjectionInput(Guid MapId, IReadOnlyList<MapRenderProjectionChunk> Chunks);
public sealed record MapRenderProjectionResult(string CacheKey, IReadOnlyList<MapRenderProjectionOwner> Owners, bool CacheHit, TimeSpan Elapsed);

// Optional invocation-owned numeric collector. No sink, retained measurements or data.
public sealed class MapProjectionObservation
{
    public int Chunks { get; internal set; }
    public int XBoundaries { get; internal set; }
    public int YBoundaries { get; internal set; }
    public int Slabs { get; internal set; }
    public long ActiveIntervalEvents { get; internal set; }
    public long WinningRuns { get; internal set; }
    public int Owners { get; internal set; }
    public long SparseSegments { get; internal set; }
}

public static class MapRenderProjection
{
    public static IReadOnlyList<MapRenderProjectionOwner> Project(MapRenderProjectionInput input, MapProjectionObservation? observation = null)
    {
        var source = input.Chunks.ToList();
        foreach (var chunk in source) Validate(chunk);
        return ProjectCore(source, observation);
    }

    private static IReadOnlyList<MapRenderProjectionOwner> ProjectCore(IReadOnlyList<MapRenderProjectionChunk> source, MapProjectionObservation? observation)
    {

        var xCoordinates = source.SelectMany(chunk => new[] { chunk.MapUnitMinX, chunk.MapUnitMaxX }).Distinct().Order().ToArray();
        var yCoordinates = source.SelectMany(chunk => new[] { chunk.MapUnitMinY, chunk.MapUnitMaxY }).Distinct().Order().ToArray();
        if (observation is not null)
        {
            observation.Chunks = source.Count; observation.XBoundaries = xCoordinates.Length; observation.YBoundaries = yCoordinates.Length;
            observation.Slabs = 0; observation.ActiveIntervalEvents = 0;
            observation.WinningRuns = 0; observation.SparseSegments = 0; observation.Owners = 0;
        }
        var xIndexes = xCoordinates.Select((value, index) => (value, index)).ToDictionary(x => x.value, x => x.index);
        var yIndexes = yCoordinates.Select((value, index) => (value, index)).ToDictionary(y => y.value, y => y.index);
        var owners = new Dictionary<string, OwnerBoundary>(StringComparer.Ordinal);
        // LINQ ordering is stable: equal keys retain source order. Priority belongs to
        // chunks, not room unions (a room can have chunks on either side of a rival).
        var ranked = source
                     .OrderBy(chunk => chunk.ActiveResolvedRoomId is null ? 0 : 1)
                     .ThenBy(chunk => chunk.MapUnitZ ?? double.PositiveInfinity)
            .ThenBy(chunk => chunk.StableIdentity, StringComparer.Ordinal).ToArray();
        var rectangles = new RankedRectangle[ranked.Length];
        var xEvents = new List<XEvent>();
        for (var rank = 0; rank < ranked.Length; rank++)
        {
            var chunk = ranked[rank];
            var key = chunk.ActiveResolvedRoomId is { } roomId ? $"room:{roomId:N}" : $"chunk:{chunk.ChunkId:N}";
            if (!owners.TryGetValue(key, out var owner))
            {
                owner = new(chunk.ActiveResolvedRoomId, chunk.ActiveResolvedRoomId is null ? chunk.ChunkId : null);
                owners.Add(key, owner);
            }
            rectangles[rank] = new(owner, yIndexes[chunk.MapUnitMinY], yIndexes[chunk.MapUnitMaxY]);
            xEvents.Add(new(xIndexes[chunk.MapUnitMinX], rank, true));
            xEvents.Add(new(xIndexes[chunk.MapUnitMaxX], rank, false));
        }

        xEvents.Sort((a, b) => a.X.CompareTo(b.X));
        var active = new HashSet<int>();
        var eventIndex = 0;
        List<WinnerRun> previous = [];
        for (var x = 0; x < xCoordinates.Length; x++)
        {
            while (eventIndex < xEvents.Count && xEvents[eventIndex].X == x)
            {
                var change = xEvents[eventIndex++];
                if (change.Start) active.Add(change.Rank); else active.Remove(change.Rank);
            }
            var current = x + 1 < xCoordinates.Length ? ResolveProfile(active, rectangles, observation) : [];
            CompareProfiles(x, previous, current);
            if (x + 1 < xCoordinates.Length)
            {
                if (observation is not null) observation.Slabs++;
                foreach (var run in current)
                {
                    run.Owner.Include(x, x + 1, run.MinY, run.MaxY);
                    run.Owner.AddSegment(new(new(x, run.MinY), new(x + 1, run.MinY)));
                    run.Owner.AddSegment(new(new(x + 1, run.MaxY), new(x, run.MaxY)));
                }
            }
            previous = current;
        }

        var result = owners.Values.Where(owner => owner.Visible)
            .Select(owner => new MapRenderProjectionOwner(owner.RoomId, owner.UnlinkedChunkId,
                CreatePath(NormalizeBoundary(owner.Segments), xCoordinates, yCoordinates, observation),
                new(xCoordinates[owner.MinX], yCoordinates[owner.MinY], xCoordinates[owner.MaxX], yCoordinates[owner.MaxY])))
            .OrderBy(owner => owner.ActiveResolvedRoomId is null ? 1 : 0)
            .ThenBy(owner => owner.ActiveResolvedRoomId ?? owner.UnlinkedChunkId)
            .ToList();
        if (observation is not null) observation.Owners = result.Count;
        return result;
    }

    private static List<WinnerRun> ResolveProfile(HashSet<int> active, RankedRectangle[] rectangles, MapProjectionObservation? observation)
    {
        var events = new List<YEvent>(active.Count * 2);
        foreach (var rank in active)
        {
            var rectangle = rectangles[rank];
            events.Add(new(rectangle.MinY, rank, true));
            events.Add(new(rectangle.MaxY, rank, false));
        }
        events.Sort((a, b) => a.Y.CompareTo(b.Y));
        var contenders = new SortedSet<int>();
        var runs = new List<WinnerRun>();
        var index = 0;
        while (index < events.Count)
        {
            var y = events[index].Y;
            do
            {
                var change = events[index++];
                if (change.Start) contenders.Add(change.Rank); else contenders.Remove(change.Rank);
                if (observation is not null) observation.ActiveIntervalEvents++;
            } while (index < events.Count && events[index].Y == y);
            if (index == events.Count || contenders.Count == 0) continue;
            var end = events[index].Y;
            var owner = rectangles[contenders.Min].Owner;
            if (runs.Count > 0 && ReferenceEquals(runs[^1].Owner, owner) && runs[^1].MaxY == y)
                runs[^1] = runs[^1] with { MaxY = end };
            else
            {
                runs.Add(new(y, end, owner));
                if (observation is not null) observation.WinningRuns++;
            }
        }
        return runs;
    }

    // Each profile is ordered, disjoint and maximal. Advance only at its real
    // endpoints, including gaps: no traversal of the global Y coordinate ranks.
    private static void CompareProfiles(int x, List<WinnerRun> left, List<WinnerRun> right)
    {
        var li = 0; var ri = 0;
        var y = Math.Min(left.Count == 0 ? int.MaxValue : left[0].MinY, right.Count == 0 ? int.MaxValue : right[0].MinY);
        while (y != int.MaxValue)
        {
            while (li < left.Count && left[li].MaxY <= y) li++;
            while (ri < right.Count && right[ri].MaxY <= y) ri++;
            var l = li < left.Count && left[li].MinY <= y ? left[li].Owner : null;
            var r = ri < right.Count && right[ri].MinY <= y ? right[ri].Owner : null;
            var next = Math.Min(li == left.Count ? int.MaxValue : l is null ? left[li].MinY : left[li].MaxY,
                ri == right.Count ? int.MaxValue : r is null ? right[ri].MinY : right[ri].MaxY);
            if (next == int.MaxValue) break;
            if (!ReferenceEquals(l, r))
            {
                l?.AddSegment(new(new(x, y), new(x, next)));
                r?.AddSegment(new(new(x, next), new(x, y)));
            }
            y = next;
        }
    }

    private static List<Edge> NormalizeBoundary(List<Edge> segments)
    {
        // Slab fragments have already been merged in sweep order. Split only at
        // actual boundary endpoints, not the discarded global-line intersections.
        var byX = new Dictionary<int, SortedSet<int>>();
        var byY = new Dictionary<int, SortedSet<int>>();
        foreach (var edge in segments)
        {
            AddPoint(edge.Start); AddPoint(edge.End);
        }
        void AddPoint(Point point)
        {
            if (!byX.TryGetValue(point.X, out var ys)) byX.Add(point.X, ys = []);
            if (!byY.TryGetValue(point.Y, out var xs)) byY.Add(point.Y, xs = []);
            ys.Add(point.Y); xs.Add(point.X);
        }
        var result = new List<Edge>();
        foreach (var edge in segments)
        {
            var horizontal = edge.Start.Y == edge.End.Y;
            var start = horizontal ? edge.Start.X : edge.Start.Y;
            var end = horizontal ? edge.End.X : edge.End.Y;
            var cuts = (horizontal ? byY[edge.Start.Y] : byX[edge.Start.X]).GetViewBetween(Math.Min(start, end), Math.Max(start, end));
            var positions = start < end ? cuts : cuts.Reverse();
            var previous = edge.Start;
            foreach (var position in positions)
            {
                var point = horizontal ? new Point(position, edge.Start.Y) : new Point(edge.Start.X, position);
                if (point != previous) result.Add(new(previous, point));
                previous = point;
            }
        }
        return result;
    }

    public static string CreateCacheKey(Guid mapId, IEnumerable<MapRenderProjectionChunk> chunks)
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

    private static string CreatePath(List<Edge> edges, double[] xCoordinates, double[] yCoordinates, MapProjectionObservation? observation)
    {
        if (observation is not null) observation.SparseSegments += edges.Count;
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

    public static void Validate(MapRenderProjectionChunk chunk)
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

    private sealed class OwnerBoundary(Guid? roomId, Guid? unlinkedChunkId)
    {
        public Guid? RoomId { get; } = roomId;
        public Guid? UnlinkedChunkId { get; } = unlinkedChunkId;
        public List<Edge> Segments { get; } = [];
        private readonly Dictionary<(int Direction, int Line), int> lastSegments = [];
        public void AddSegment(Edge edge)
        {
            // X slabs and each vertical comparison both arrive in increasing
            // coordinate order, so only the last segment on this directed line
            // can meet the new segment. Opposite directions never coalesce.
            var key = (Direction(edge), edge.Start.Y == edge.End.Y ? edge.Start.Y : edge.Start.X);
            if (lastSegments.TryGetValue(key, out var index))
            {
                var prior = Segments[index];
                if (prior.End == edge.Start) { Segments[index] = new(prior.Start, edge.End); return; }
                if (edge.End == prior.Start) { Segments[index] = new(edge.Start, prior.End); return; }
            }
            lastSegments[key] = Segments.Count;
            Segments.Add(edge);
        }
        public bool Visible { get; private set; }
        public int MinX { get; private set; } = int.MaxValue;
        public int MinY { get; private set; } = int.MaxValue;
        public int MaxX { get; private set; }
        public int MaxY { get; private set; }
        public void Include(int minX, int maxX, int minY, int maxY)
        {
            Visible = true;
            MinX = Math.Min(MinX, minX); MinY = Math.Min(MinY, minY);
            MaxX = Math.Max(MaxX, maxX); MaxY = Math.Max(MaxY, maxY);
        }
    }

    private sealed record RankedRectangle(OwnerBoundary Owner, int MinY, int MaxY);
    private readonly record struct XEvent(int X, int Rank, bool Start);
    private readonly record struct YEvent(int Y, int Rank, bool Start);
    private sealed record WinnerRun(int MinY, int MaxY, OwnerBoundary Owner);
    private readonly record struct Point(int X, int Y);
    private readonly record struct Edge(Point Start, Point End);

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
