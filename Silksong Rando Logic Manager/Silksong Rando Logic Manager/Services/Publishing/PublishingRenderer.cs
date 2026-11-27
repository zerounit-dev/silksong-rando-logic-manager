using ImageMagick;
using ImageMagick.Drawing;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services.Publishing;

internal sealed class PublishingImageResult
{
    public Dictionary<string, string> Paths { get; } = [];
    public HashSet<Guid> Annotated { get; } = [];
}

internal static class PublishingRenderer
{
    internal const string FontHash = "c976e4b1b99edc88775377fcc21692ca4bfa46b6d6ca6522bfda505b28ff9d6a";
    internal const string Version = "native-png-1;Magick.NET-Q8-14.16.0;NotoSans-Bold-" + FontHash;
    private sealed record Rect(double L, double B, double R, double T);
    private sealed record Marker(string Kind, Guid[] Ids, double X, double Y, string Text, Guid?[] Endpoints, bool Enemy);
    private sealed record Label(Marker Marker, double X, double Y, Rect Box, double TextWidth, double Ascent, double Descent);
    private static bool Finite(params double?[] values) => values.All(x => x is { } n && double.IsFinite(n));
    private static bool Same(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && a.Trim().Equals(b?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool Endpoint(SubroomConnection a, bool sourceA, SubroomConnection b, bool sourceB)
    {
        var idA = sourceA ? a.ResolvedSourceSubroomId : a.ResolvedDestinationSubroomId;
        var idB = sourceB ? b.ResolvedSourceSubroomId : b.ResolvedDestinationSubroomId;
        return idA is not null && idB is not null ? idA == idB : Same(sourceA ? a.SourceSubroomReferenceText : a.DestinationSubroomReferenceText,
            sourceB ? b.SourceSubroomReferenceText : b.DestinationSubroomReferenceText);
    }
    internal static PublishingImageResult Render(PublishingStorage storage, PublishingDocuments data, Room room, string captures, string destination, string font, PublishingTimingScope timing)
    {
        var result = new PublishingImageResult();
        var capture = Path.Combine(captures, $"{room.Id:D}.webp");
        if (!File.Exists(capture)) { timing.SkipReason = "No eligible frozen capture"; return result; }
        var actualFontHash = timing.Measure(PublishingMetric.Font, () => PublishingStorage.FileHash(font));
        if (actualFontHash != FontHash) throw new PublishingBlock($"Assets/Publishing/NotoSans-Bold.ttf differs from the font pinned by this application.\nExpected raw SHA-256: {FontHash}\nObserved raw SHA-256: {actualFontHash}\nRestore that asset from the matching application distribution before preparing again; do not substitute a host font.");
        // Existing eligible captures must decode. Never downgrade corruption to an unavailable notice.
        using var decode = timing.Segment(PublishingMetric.Decode);
        using var source = new MagickImage(capture);
        source.BackgroundColor = new MagickColor(22, 25, 31);
        source.Alpha(AlphaOption.Remove);
        source.ColorSpace = ColorSpace.sRGB;
        decode.Complete();
        using var geometryTime = timing.Segment(PublishingMetric.Geometry);
        var frames = new Dictionary<Guid, Rect>();
        var subs = data.SubroomsByRoom[room.Id].ToArray();
        var subroomNumbers = new Dictionary<Guid, int>();
        for (var i = 0; i < subs.Length; i++) subroomNumbers.TryAdd(subs[i].Id, i + 1);
        foreach (var s in subs)
            if (s.EnableAnnotation && Finite(s.SceneUnitX, s.SceneUnitY, s.SceneUnitWidth, s.SceneUnitHeight) && s.SceneUnitWidth > 0 && s.SceneUnitHeight > 0 &&
                Finite(s.SceneUnitX + s.SceneUnitWidth, s.SceneUnitY + s.SceneUnitHeight))
                frames[s.Id] = new(s.SceneUnitX!.Value, s.SceneUnitY!.Value, (s.SceneUnitX + s.SceneUnitWidth)!.Value, (s.SceneUnitY + s.SceneUnitHeight)!.Value);
        var markers = new List<Marker>();
        foreach (var t in data.TransitionsByRoom[room.Id].Where(x => x.EnableAnnotation && Finite(x.AnnotationSceneUnitX, x.AnnotationSceneUnitY)))
            markers.Add(new("transitions", [t.Id], t.AnnotationSceneUnitX!.Value, t.AnnotationSceneUnitY!.Value, t.Alias, [t.ResolvedSourceSubroomId], false));
        var checks = data.ChecksByRoom[room.Id].ToArray();
        foreach (var (c, i) in checks.Select((c, i) => (c, i)))
            if (c.EnableAnnotation && Finite(c.AnnotationSceneUnitX, c.AnnotationSceneUnitY))
                markers.Add(new("checks", [c.Id], c.AnnotationSceneUnitX!.Value, c.AnnotationSceneUnitY!.Value, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), [c.ResolvedSubroomId],
                    new[] { "boss", "miniboss", "gauntlet", "enemy" }.Contains(c.LocationType?.Trim().ToLowerInvariant())));
        var connections = data.ConnectionsByRoom[room.Id].ToArray();
        if (subs.Length > 0)
            foreach (var grouping in connections.GroupBy(x => x.Alias.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var group = grouping.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToArray(); var first = group[0];
                if (group.Length is < 1 or > 2 || first.Alias.Length is < 1 or > 3 ||
                    group.Any(x => !Same(x.FriendlyName, first.FriendlyName)) ||
                    connections.Where(x => Same(x.FriendlyName, first.FriendlyName)).Select(x => x.Alias.Trim().ToUpperInvariant()).Distinct().Count() != 1 ||
                    !Endpoint(first, true, first, true) || !Endpoint(first, false, first, false) ||
                    (group.Length == 2 && ((Endpoint(first, true, group[1], true) && Endpoint(first, false, group[1], false)) ||
                        !(Endpoint(first, true, group[1], false) && Endpoint(first, false, group[1], true)))) ||
                    !first.EnableAnnotation || !Finite(first.SceneUnitX, first.SceneUnitY)) continue;
                markers.Add(new("connections", group.Select(x => x.Id).ToArray(), first.SceneUnitX!.Value, first.SceneUnitY!.Value, first.Alias,
                    group.SelectMany(x => new[] { x.ResolvedSourceSubroomId, x.ResolvedDestinationSubroomId }).Distinct().ToArray(), false));
            }
        var left = room.SceneMinX; var right = room.SceneMaxX!.Value;
        var bottom = room.SceneMinY; var top = room.SceneMaxY!.Value;
        var width = right - left; var height = top - bottom;
        foreach (var f in frames.Values) { left = Math.Min(left, f.L); right = Math.Max(right, f.R); bottom = Math.Min(bottom, f.B); top = Math.Max(top, f.T); }
        foreach (var m in markers)
        {
            var rx = m.Kind == "checks" ? 3 : 7; var ry = m.Kind == "checks" ? 3 : 4;
            left = Math.Min(left, m.X - rx); right = Math.Max(right, m.X + rx); bottom = Math.Min(bottom, m.Y - ry); top = Math.Max(top, m.Y + ry);
        }
        var spanX = right - left; var spanY = top - bottom;
        if (!Finite(spanX, spanY)) throw new PublishingBlock($"{PublishingDocuments.Title(room)} (room {room.Id:D}): annotation extent is not finite.\nObserved span X: {spanX}; span Y: {spanY}\nInspect this room's scene bounds, subroom frames and annotation coordinates in the editor, correct the nonfinite extent, then prepare again.");
        var scale = Math.Min(Math.Min(source.Width / width, source.Height / height), Math.Min(3500 / spanX, 3500 / spanY));
        (double X, double Y) Project(double x, double y) => ((x - left) * scale, (top - y) * scale);
        Dictionary<string, List<Label>> labels = [];
        double minX = 0, minY = 0, maxX = 0, maxY = 0;
        int canvasWidth = 0, canvasHeight = 0;
        double fontSize = 0;
        var fit = false;
        geometryTime.Complete();
        using var layoutTime = timing.Segment(PublishingMetric.Layout);
        // Keep only scalar results, never native metric objects, for this room/font.
        var fontMetrics = new Dictionary<(string Text, double Size), (double TextWidth, double Ascent, double Descent)>();
        for (var attempt = 0; attempt < 12; attempt++)
        {
            timing.Attempts = (timing.Attempts ?? 0) + 1;
            fontSize = Math.Max(1, Math.Round(5 * scale, MidpointRounding.ToEven));
            labels = new() { ["connections"] = [], ["checks"] = [] };
            minX = -spanX * scale * .1; minY = -spanY * scale * .1; maxX = spanX * scale * 1.1; maxY = spanY * scale * 1.1;
            foreach (var kind in new[] { "connections", "checks" })
            {
                var visible = markers.Where(x => (x.Kind == "checks") == (kind == "checks")).ToArray();
                var occupied = new List<(Guid Id, Rect Box)>();
                foreach (var marker in visible)
                {
                    var (x, y) = Project(marker.X, marker.Y); var rx = (marker.Kind == "checks" ? 3 : 7) * scale; var ry = (marker.Kind == "checks" ? 3 : 4) * scale;
                    occupied.Add((marker.Ids[0], new(x - rx, y - ry, x + rx, y + ry)));
                }
                var entries = frames.Select(f => new Marker("frame", [f.Key], (f.Value.L + f.Value.R) / 2, (f.Value.B + f.Value.T) / 2,
                    "S" + subroomNumbers[f.Key], [], false)).Concat(visible);
                foreach (var entry in entries)
                {
                    var (px, py) = Project(entry.X, entry.Y);
                    timing.Labels = (timing.Labels ?? 0) + 1;
                    if (!fontMetrics.TryGetValue((entry.Text, fontSize), out var metrics))
                    {
                        var nativeMetrics = timing.Measure(PublishingMetric.FontMetrics, () => new Drawables().Font(font).FontPointSize(fontSize).FontTypeMetrics(entry.Text))
                            ?? throw new PublishingBlock($"{PublishingDocuments.Title(room)} (room {room.Id:D}): the pinned font returned no metrics for a {entry.Kind} label on record {entry.Ids[0]:D}, at font size {fontSize}. Check the matching application's Assets/Publishing/NotoSans-Bold.ttf and native rendering dependencies before preparing again; this does not establish an image-decoding failure.");
                        metrics = (nativeMetrics.TextWidth, nativeMetrics.Ascent, nativeMetrics.Descent);
                        fontMetrics.Add((entry.Text, fontSize), metrics);
                    }
                    var tw = metrics.TextWidth + 6; var th = metrics.Ascent - metrics.Descent + 6;
                    if (entry.Kind == "checks") tw = th = Math.Max(Math.Max(tw, th), 6 * scale);
                    var obstacles = occupied.Where(x => x.Id != entry.Ids[0]).Select(x => x.Box).ToArray();
                    var xs = new SortedSet<double> { px }; var ys = new SortedSet<double> { py };
                    Rect? frame = null;
                    if (entry.Kind == "frame")
                    {
                        var f = frames[entry.Ids[0]]; var a = Project(f.L, f.T); var c = Project(f.R, f.B);
                        frame = new(a.X, a.Y, c.X, c.Y);
                        xs.Add(a.X + tw / 2 + 3); xs.Add(c.X - tw / 2 - 3); ys.Add(a.Y + th / 2 + 3); ys.Add(c.Y - th / 2 - 3);
                    }
                    foreach (var o in obstacles) { xs.Add(o.L - tw / 2 - 3); xs.Add(o.R + tw / 2 + 3); ys.Add(o.B - th / 2 - 3); ys.Add(o.T + th / 2 + 3); }
                    var candidates = xs.SelectMany(x => ys.Select(y => (X: x, Y: y)))
                        .OrderBy(p => frame is not null && !(frame.L <= p.X - tw / 2 && p.X + tw / 2 <= frame.R && frame.B <= p.Y - th / 2 && p.Y + th / 2 <= frame.T))
                        .ThenBy(p => Math.Pow(p.X - px, 2) + Math.Pow(p.Y - py, 2)).ThenBy(p => p.Y).ThenBy(p => p.X).ToArray();
                    timing.Candidates = (timing.Candidates ?? 0) + candidates.LongLength;
                    var selected = candidates.FirstOrDefault(p => obstacles.All(o => p.X + tw / 2 + 2 < o.L || p.X - tw / 2 - 2 > o.R || p.Y + th / 2 + 2 < o.B || p.Y - th / 2 - 2 > o.T), candidates[^1]);
                    var box = new Rect(selected.X - tw / 2, selected.Y - th / 2, selected.X + tw / 2, selected.Y + th / 2);
                    occupied.Add((entry.Ids[0], box));
                    minX = Math.Min(minX, box.L - 8); minY = Math.Min(minY, box.B - 8); maxX = Math.Max(maxX, box.R + 8); maxY = Math.Max(maxY, box.T + 8);
                    labels[kind].Add(new(entry, selected.X, selected.Y, box, metrics.TextWidth, metrics.Ascent, metrics.Descent));
                }
            }
            var outputWidth = Math.Ceiling(maxX - minX); var outputHeight = Math.Ceiling(maxY - minY);
            if (!Finite(outputWidth, outputHeight) || outputWidth <= 0 || outputHeight <= 0)
                throw new PublishingBlock($"{PublishingDocuments.Title(room)} (room {room.Id:D}): annotation canvas dimensions are nonfinite or nonpositive.\nObserved width: {outputWidth}; height: {outputHeight}\nInspect this room's bounds and annotation coordinates for invalid extents before preparing again.");
            if (Math.Max(outputWidth, outputHeight) <= 4096)
            { canvasWidth = (int)outputWidth; canvasHeight = (int)outputHeight; fit = true; break; }
            scale *= 4094d / Math.Max(outputWidth, outputHeight);
        }
        if (!fit) throw new PublishingBlock($"{PublishingDocuments.Title(room)} (room {room.Id:D}): annotation layout did not fit the 4096-pixel per-axis limit after 12 attempts.\nLast width: {Math.Ceiling(maxX - minX)}; height: {Math.Ceiling(maxY - minY)}\nReview this room's bounds and annotation placement before preparing again; if those are intended, report this room and these dimensions to the application maintainer. The limit does not by itself prove dense or corrupt geometry.");
        layoutTime.Complete();
        (double X, double Y) Point(double x, double y) { var p = Project(x, y); return (p.X - minX, p.Y - minY); }
        source.FilterType = FilterType.Lanczos;
        // Map both capture axes onto the actual scene bounds, even when their
        // aspect ratios differ. The width/height overload only fits inside this box.
        timing.Measure(PublishingMetric.Resize, () => source.Resize(new MagickGeometry((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale)))
        { IgnoreAspectRatio = true }));
        using (var backgroundTime = timing.Segment(PublishingMetric.SharedBackgroundDraw))
        using (var canvas = new MagickImage(new MagickColor(22, 25, 31), (uint)canvasWidth, (uint)canvasHeight))
        {
            var background = Point(room.SceneMinX, room.SceneMaxY.Value);
            canvas.Composite(source, (int)Math.Round(background.X), (int)Math.Round(background.Y), CompositeOperator.Over);
            backgroundTime.Complete();
            source.Dispose(); // No native source image is needed after composition; the using also covers earlier failures.

            WriteVariant(canvas, "scene", PublishingMetric.SceneDraw, PublishingMetric.SceneEncode, clone: true);
            if (frames.Count != 0)
            {
                using var frameTime = timing.Segment(PublishingMetric.SharedFrameDraw);
                var outlines = BeginAnnotationDraw();
                foreach (var f in frames.Values) { var a = Point(f.L, f.T); var c = Point(f.R, f.B); outlines.Rectangle(a.X, a.Y, c.X, c.Y); }
                outlines.Draw(canvas);
                frameTime.Complete();
            }
            WriteVariant(canvas, "connections", PublishingMetric.ConnectionsDraw, PublishingMetric.ConnectionsEncode, clone: true);
            // Connections is disposed. Checks starts at the same framed pixels
            // and is the last consumer, so it can take the base in place.
            WriteVariant(canvas, "checks", PublishingMetric.ChecksDraw, PublishingMetric.ChecksEncode, clone: false);
        }
        foreach (var id in frames.Keys.Concat(markers.SelectMany(x => x.Ids))) result.Annotated.Add(id);
        // Machine-facing geometry never passes through the decoder/encoder.
        var rawDestination = PublishingStorage.Resolve(destination, $"01-geometry/{room.Id:D}.webp");
        var raw = timing.Measure(PublishingMetric.RawRead, () => File.ReadAllBytes(capture));
        storage.AtomicGeneratedCandidate(rawDestination, raw);
        return result;

        static IDrawables<byte> BeginAnnotationDraw() =>
            new Drawables().StrokeWidth(2).FillColor(MagickColors.Transparent).StrokeColor(new MagickColor(73, 214, 225));

        void WriteVariant(MagickImage basis, string kind, PublishingMetric drawMetric, PublishingMetric encodeMetric, bool clone)
        {
            using var drawTime = timing.Segment(drawMetric);
            using var branch = clone ? basis.Clone() : null;
            var canvas = branch ?? basis;
            if (kind != "scene")
            {
                // A fresh native draw must restore the frame state, including the
                // transparent fill and 2px width inherited by connector lines.
                var draw = BeginAnnotationDraw();
                foreach (var label in labels[kind].Where(x => x.Marker.Kind != "frame"))
                {
                    var m = label.Marker; var p = Point(m.X, m.Y);
                    foreach (var id in m.Endpoints)
                        if (id is Guid key && frames.TryGetValue(key, out var f) && !(f.L <= m.X && m.X <= f.R && f.B <= m.Y && m.Y <= f.T))
                        { var end = Point(Math.Clamp(m.X, f.L, f.R), Math.Clamp(m.Y, f.B, f.T)); draw.StrokeColor(new MagickColor(153, 139, 93)).Line(p.X, p.Y, end.X, end.Y); }
                }
                foreach (var label in labels[kind])
                {
                    var m = label.Marker; var p = Point(m.X, m.Y); var lx = label.X - minX; var ly = label.Y - minY;
                    var color = m.Kind switch { "frame" => new MagickColor(73, 214, 225), "checks" when m.Enemy => new MagickColor(210, 69, 72), "checks" => new MagickColor(48, 119, 208), "connections" => new MagickColor(135, 79, 202), _ => new MagickColor(220, 100, 39) };
                    draw.StrokeColor(color).FillColor(color).StrokeWidth(2);
                    if (m.Kind != "frame" && m.Kind != "checks") draw.Rectangle(p.X - 7 * scale, p.Y - 4 * scale, p.X + 7 * scale, p.Y + 4 * scale);
                    if (m.Kind == "checks" && (lx != p.X || ly != p.Y))
                    { var radius = Math.Max(2, .5 * scale); draw.Ellipse(p.X, p.Y, radius, radius, 0, 360); }
                    if (lx != p.X || ly != p.Y) draw.Line(p.X, p.Y, lx, ly);
                    var box = label.Box;
                    if (m.Kind == "checks")
                    { draw.Ellipse(lx, ly, (box.R - box.L) / 2, (box.T - box.B) / 2, 0, 360); }
                    else draw.Rectangle(box.L - minX, box.B - minY, box.R - minX, box.T - minY);
                    draw.StrokeColor(MagickColors.Transparent).FillColor(MagickColors.White).Font(font).FontPointSize(fontSize)
                        .Text(lx - label.TextWidth / 2, ly + (label.Ascent + label.Descent) / 2, m.Text);
                }
                draw.Draw(canvas);
            }
            canvas.Strip(); canvas.Depth = 8; canvas.ColorType = ColorType.TrueColor; canvas.Format = MagickFormat.Png24;
            canvas.Settings.SetDefine(MagickFormat.Png, "exclude-chunks", "all");
            canvas.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
            canvas.Settings.SetDefine(MagickFormat.Png, "compression-filter", "5");
            canvas.Settings.SetDefine(MagickFormat.Png, "compression-strategy", "1");
            var path = "00-annotations/" + data.Paths[room.Id][..^3] + "-" + kind + ".png";
            drawTime.Complete();
            // Preserve left-to-right evaluation: destination resolution precedes encoding.
            var destinationPath = PublishingStorage.Resolve(destination, path);
            var png = timing.Measure(encodeMetric, () => canvas.ToByteArray());
            storage.AtomicGeneratedCandidate(destinationPath, png);
            result.Paths[kind] = path;
        }
    }
}
