using System.Diagnostics;

namespace Silksong_Rando_Logic_Manager.Services.Publishing;

// Deliberately scalar, immutable and payload-free. Missing spans remain null.
public sealed record PublishingTiming(Guid Operation, long Sequence, string Stage, string? Room,
    string Outcome, double? ElapsedMs)
{
    public double? FontMs { get; init; }
    public double? DecodeMs { get; init; }
    public double? GeometryMs { get; init; }
    public double? LayoutMs { get; init; }
    public double? FontMetricsMs { get; init; }
    public double? ResizeMs { get; init; }
    public double? SharedBackgroundDrawMs { get; init; }
    public double? SharedFrameDrawMs { get; init; }
    public double? SceneDrawMs { get; init; }
    public double? ConnectionsDrawMs { get; init; }
    public double? ChecksDrawMs { get; init; }
    public double? SceneEncodeMs { get; init; }
    public double? ConnectionsEncodeMs { get; init; }
    public double? ChecksEncodeMs { get; init; }
    public double? RawReadMs { get; init; }
    public double? MarkdownMs { get; init; }
    public double? MasterMs { get; init; }
    public double? TextEncodingMs { get; init; }
    public double? AtomicPrepareMs { get; init; }
    public double? WriteMs { get; init; }
    public double? FlushMs { get; init; }
    public double? CloseMs { get; init; }
    public double? RecheckMs { get; init; }
    public double? MoveMs { get; init; }
    public double? LifecycleWaitMs { get; init; }
    public double? PublicationWaitMs { get; init; }
    public double? BackupMs { get; init; }
    public double? CaptureCopyMs { get; init; }
    public double? CaptureFlushMs { get; init; }
    public double? CaptureValidationMs { get; init; }
    public double? LoadMs { get; init; }
    public double? ValidationMs { get; init; }
    public double? HashingMs { get; init; }
    public double? InventoryMs { get; init; }
    public double? ReviewMs { get; init; }
    public double? SaveMs { get; init; }
    public double? CleanupMs { get; init; }
    public double? AuthorizationMs { get; init; }
    public double? GateWaitMs { get; init; }
    public double? GitOpenMs { get; init; }
    public double? BeforeReadMs { get; init; }
    public double? AfterReadMs { get; init; }
    public double? ResponseBuildMs { get; init; }
    public double? ProjectionMs { get; init; }
    public double? RoomLoadMs { get; init; }
    public double? GroupLoadMs { get; init; }
    public double? AreaMapLoadMs { get; init; }
    public double? JsonSerializationMs { get; init; }
    public long? Attempts { get; init; }
    public long? Labels { get; init; }
    public long? Candidates { get; init; }
    public long? Files { get; init; }
    public long? Bytes { get; init; }
    public long? InputBytes { get; init; }
    public long? OutputBytes { get; init; }
    public long? BeforeLines { get; init; }
    public long? AfterLines { get; init; }
    public long? FailedSpans { get; init; }
    public string? SkipReason { get; init; }
}

internal enum PublishingMetric
{
    Font, Decode, Geometry, Layout, FontMetrics, Resize, SharedBackgroundDraw, SharedFrameDraw,
    SceneDraw, ConnectionsDraw, ChecksDraw,
    SceneEncode, ConnectionsEncode, ChecksEncode, RawRead, Markdown, Master, TextEncoding,
    AtomicPrepare, Write, Flush, Close, Recheck, Move, LifecycleWait, PublicationWait, Backup,
    CaptureCopy, CaptureFlush, CaptureValidation, Load, Validation, Hashing, Inventory, Review,
    Save, Cleanup, Authorization, GateWait, GitOpen, BeforeRead, AfterRead, ResponseBuild, Projection,
    RoomLoad, GroupLoad, AreaMapLoad, JsonSerialization
}

public sealed class PublishingTimings
{
    private readonly object gate = new();
    private readonly List<PublishingTiming> records = [];
    private readonly ILogger logger;
    private readonly Action? changed;
    private readonly Guid operation = Guid.NewGuid();
    private long sequence;
    private int boundaries, roomRecords, roomLimit;
    private bool overflow;
    public Guid Operation => operation;
    internal PublishingTimings(ILogger logger, Action? changed = null) { this.logger = logger; this.changed = changed; }
    internal void Rooms(int count) { lock (gate) roomLimit = checked(count * 2); }
    public IReadOnlyList<PublishingTiming> Read(Guid? deliveredOperation = null, long after = 0)
    {
        lock (gate) return records.Where(x => deliveredOperation != operation || x.Sequence > after).ToArray();
    }
    internal PublishingTimingScope Begin(string stage, string? room = null, PublishingTimingScope? parent = null, long? started = null) => new(this, stage, room, parent, started);
    internal void Emit(PublishingTimingScope span, string outcome, double? elapsed)
    {
        // Diagnostics may not change operation outcome, even if a logger/subscriber fails.
        try
        {
            PublishingTiming record;
            lock (gate)
            {
                if ((span.Room is null ? ++boundaries > 128 : ++roomRecords > roomLimit))
                {
                    if (overflow) return;
                    overflow = true;
                    record = new(operation, ++sequence, "Diagnostic record budget exceeded", null, "diagnostic-failure", null);
                }
                else record = span.Snapshot(operation, ++sequence, outcome, elapsed);
                records.Add(record);
            }
            try { logger.LogInformation("[publishing] {Timing}", System.Text.Json.JsonSerializer.Serialize(record,
                new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })); } catch { }
            try { changed?.Invoke(); } catch { }
        }
        catch { /* Timing must never replace the original work/failure. */ }
    }
}

internal sealed class PublishingTimingScope : IDisposable
{
    private readonly PublishingTimings owner;
    private readonly PublishingTimingScope? parent;
    private readonly long started;
    private readonly double?[] metrics = new double?[Enum.GetValues<PublishingMetric>().Length];
    internal string Stage { get; }
    internal string? Room { get; }
    internal long? Attempts, Labels, Candidates, Files, Bytes, InputBytes, OutputBytes, BeforeLines, AfterLines, FailedSpans;
    internal string? SkipReason;
    private bool completed, disposed;
    internal PublishingTimingScope(PublishingTimings owner, string stage, string? room, PublishingTimingScope? parent, long? started)
    { this.owner = owner; this.parent = parent; this.started = started ?? Stopwatch.GetTimestamp(); Stage = stage; Room = room; owner.Emit(this, "started", null); }
    internal void Complete() => completed = true;
    internal SegmentTime Segment(PublishingMetric metric) => new(this, metric);
    internal sealed class SegmentTime(PublishingTimingScope owner, PublishingMetric metric) : IDisposable
    {
        private readonly long start = Stopwatch.GetTimestamp();
        private bool disposed;
        internal void Complete() { Finish(true); }
        private void Finish(bool success) { if (disposed) return; disposed = true; owner.Add(metric, start, success); }
        public void Dispose() => Finish(false);
    }
    internal T Measure<T>(PublishingMetric metric, Func<T> work)
    {
        var start = Stopwatch.GetTimestamp(); var success = false;
        try { var value = work(); success = true; return value; }
        finally { Add(metric, start, success); }
    }
    internal void Measure(PublishingMetric metric, Action work) => Measure(metric, () => { work(); return true; });
    internal async Task<T> MeasureAsync<T>(PublishingMetric metric, Func<Task<T>> work)
    {
        var start = Stopwatch.GetTimestamp(); var success = false;
        try { var value = await work(); success = true; return value; }
        finally { Add(metric, start, success); }
    }
    internal async Task MeasureAsync(PublishingMetric metric, Func<Task> work) =>
        await MeasureAsync(metric, async () => { await work(); return true; });
    internal void Add(PublishingMetric metric, long start, bool success)
    {
        metrics[(int)metric] = (metrics[(int)metric] ?? 0) + Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (!success) FailedSpans = (FailedSpans ?? 0) + 1;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (parent is not null)
        {
            for (var i = 0; i < metrics.Length; i++)
                if (metrics[i] is { } value) parent.metrics[i] = (parent.metrics[i] ?? 0) + value;
            if (Files is { } files) parent.Files = (parent.Files ?? 0) + files;
            if (Bytes is { } bytes) parent.Bytes = (parent.Bytes ?? 0) + bytes;
            if (FailedSpans is { } failed) parent.FailedSpans = (parent.FailedSpans ?? 0) + failed;
        }
        owner.Emit(this, completed ? "completed" : "failed", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
    internal PublishingTiming Snapshot(Guid operation, long sequence, string outcome, double? elapsed)
    {
        double? M(PublishingMetric metric) => metrics[(int)metric];
        return new(operation, sequence, Stage, Room, outcome, elapsed)
        {
            FontMs = M(PublishingMetric.Font), DecodeMs = M(PublishingMetric.Decode), GeometryMs = M(PublishingMetric.Geometry),
            LayoutMs = M(PublishingMetric.Layout), FontMetricsMs = M(PublishingMetric.FontMetrics), ResizeMs = M(PublishingMetric.Resize),
            SharedBackgroundDrawMs = M(PublishingMetric.SharedBackgroundDraw), SharedFrameDrawMs = M(PublishingMetric.SharedFrameDraw),
            SceneDrawMs = M(PublishingMetric.SceneDraw), ConnectionsDrawMs = M(PublishingMetric.ConnectionsDraw), ChecksDrawMs = M(PublishingMetric.ChecksDraw),
            SceneEncodeMs = M(PublishingMetric.SceneEncode), ConnectionsEncodeMs = M(PublishingMetric.ConnectionsEncode), ChecksEncodeMs = M(PublishingMetric.ChecksEncode),
            RawReadMs = M(PublishingMetric.RawRead), MarkdownMs = M(PublishingMetric.Markdown), MasterMs = M(PublishingMetric.Master), TextEncodingMs = M(PublishingMetric.TextEncoding),
            AtomicPrepareMs = M(PublishingMetric.AtomicPrepare), WriteMs = M(PublishingMetric.Write), FlushMs = M(PublishingMetric.Flush), CloseMs = M(PublishingMetric.Close),
            RecheckMs = M(PublishingMetric.Recheck), MoveMs = M(PublishingMetric.Move), LifecycleWaitMs = M(PublishingMetric.LifecycleWait), PublicationWaitMs = M(PublishingMetric.PublicationWait),
            BackupMs = M(PublishingMetric.Backup), CaptureCopyMs = M(PublishingMetric.CaptureCopy), CaptureFlushMs = M(PublishingMetric.CaptureFlush), CaptureValidationMs = M(PublishingMetric.CaptureValidation),
            LoadMs = M(PublishingMetric.Load), ValidationMs = M(PublishingMetric.Validation), HashingMs = M(PublishingMetric.Hashing), InventoryMs = M(PublishingMetric.Inventory), ReviewMs = M(PublishingMetric.Review),
            SaveMs = M(PublishingMetric.Save), CleanupMs = M(PublishingMetric.Cleanup), AuthorizationMs = M(PublishingMetric.Authorization), GateWaitMs = M(PublishingMetric.GateWait),
            GitOpenMs = M(PublishingMetric.GitOpen), BeforeReadMs = M(PublishingMetric.BeforeRead), AfterReadMs = M(PublishingMetric.AfterRead), ResponseBuildMs = M(PublishingMetric.ResponseBuild), ProjectionMs = M(PublishingMetric.Projection),
            RoomLoadMs = M(PublishingMetric.RoomLoad), GroupLoadMs = M(PublishingMetric.GroupLoad),
            AreaMapLoadMs = M(PublishingMetric.AreaMapLoad), JsonSerializationMs = M(PublishingMetric.JsonSerialization),
            Attempts = Attempts, Labels = Labels, Candidates = Candidates, Files = Files, Bytes = Bytes, InputBytes = InputBytes, OutputBytes = OutputBytes,
            BeforeLines = BeforeLines, AfterLines = AfterLines, FailedSpans = FailedSpans, SkipReason = SkipReason
        };
    }
}
