using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace GeoNex.Services;

public readonly record struct RenderTraceSpan(
    string Stage,
    string? Layer,
    long StartTicks,
    long EndTicks,
    long ManagedAllocatedBytes,
    long Bytes,
    long Items);

public sealed record RenderPresentationTrace(
    double NetworkMilliseconds,
    double DecodeMilliseconds,
    double DrawMilliseconds,
    double EndToEndMilliseconds,
    long TransferBytes);

public sealed record RenderFrameTraceSnapshot(
    long CorrelationId,
    long Generation,
    long AcceptedTicks,
    long ResponseCompletedTicks,
    string Status,
    int CssWidth,
    int CssHeight,
    float Dpi,
    bool IsInteracting,
    int LayerCount,
    string Encoding,
    long OutputBytes,
    long ManagedAllocatedBytes,
    IReadOnlyList<RenderTraceSpan> Spans,
    RenderPresentationTrace? Presentation);

public sealed record RenderMetricDistribution(double P50, double P95, double P99);

public sealed record RenderTelemetrySummary(
    int FrameCount,
    RenderMetricDistribution ResponseMilliseconds,
    RenderMetricDistribution EndToEndMilliseconds,
    RenderMetricDistribution OutputBytes,
    RenderMetricDistribution ManagedAllocatedBytes,
    IReadOnlyDictionary<string, RenderMetricDistribution> StageMilliseconds,
    IReadOnlyDictionary<string, RenderMetricDistribution> LayerStageMilliseconds);

/// <summary>
/// Opt-in, bounded frame tracing. Disabled collectors do not allocate traces or scopes.
/// Server spans retain timestamps so overlapping work is measured by interval union.
/// </summary>
public sealed class RenderTelemetryCollector
{
    private readonly object _gate = new();
    private readonly Dictionary<long, RenderFrameTrace> _frames = new();
    private readonly Queue<long> _order = new();
    private readonly int _capacity;

    public RenderTelemetryCollector(bool enabled, int capacity = 256)
    {
        Enabled = enabled;
        _capacity = Math.Clamp(capacity, 16, 4096);
    }

    public bool Enabled { get; }

    public RenderFrameTrace? BeginFrame(long clientFrameId, long generation, long acceptedTicks)
    {
        if (!Enabled) return null;

        long correlationId = clientFrameId > 0 ? clientFrameId : -generation;
        var trace = new RenderFrameTrace(correlationId, generation, acceptedTicks);
        lock (_gate)
        {
            _frames[correlationId] = trace;
            _order.Enqueue(correlationId);
            while (_order.Count > _capacity)
            {
                long expired = _order.Dequeue();
                if (expired != correlationId) _frames.Remove(expired);
            }
        }
        return trace;
    }

    public void CompleteResponse(RenderFrameTrace? trace, string status, long outputBytes)
    {
        if (trace == null || !trace.CompleteResponse(status, outputBytes)) return;
        DebugLogger.Log(RenderTelemetryStatistics.ToLogLine(trace.Snapshot()));
    }

    public void RecordPresentation(
        long correlationId,
        double networkMilliseconds,
        double decodeMilliseconds,
        double drawMilliseconds,
        double endToEndMilliseconds,
        long transferBytes)
    {
        if (!Enabled || correlationId <= 0) return;
        RenderFrameTrace? trace;
        lock (_gate) _frames.TryGetValue(correlationId, out trace);
        if (trace == null) return;

        trace.RecordPresentation(new RenderPresentationTrace(
            SanitizeMilliseconds(networkMilliseconds),
            SanitizeMilliseconds(decodeMilliseconds),
            SanitizeMilliseconds(drawMilliseconds),
            SanitizeMilliseconds(endToEndMilliseconds),
            Math.Max(0, transferBytes)));
        DebugLogger.Log(RenderTelemetryStatistics.ToLogLine(trace.Snapshot()));
    }

    public IReadOnlyList<RenderFrameTraceSnapshot> Snapshot()
    {
        lock (_gate) return _frames.Values.Select(frame => frame.Snapshot()).ToArray();
    }

    public RenderTelemetrySummary Summarize() => RenderTelemetryStatistics.Summarize(Snapshot());

    private static double SanitizeMilliseconds(double value) =>
        double.IsFinite(value) && value >= 0 ? value : 0;
}

public sealed class RenderFrameTrace
{
    private readonly object _gate = new();
    private readonly List<RenderTraceSpan> _spans = new(16);
    private long _renderStartedTicks;
    private long _renderStartedAllocatedBytes;
    private int _renderThreadId;
    private bool _queueCompleted;
    private bool _renderCompleted;
    private bool _responseCompleted;
    private long _responseCompletedTicks;
    private string _status = "active";
    private int _cssWidth;
    private int _cssHeight;
    private float _dpi;
    private bool _isInteracting;
    private int _layerCount;
    private string _encoding = string.Empty;
    private long _outputBytes;
    private long _managedAllocatedBytes;
    private RenderPresentationTrace? _presentation;

    internal RenderFrameTrace(long correlationId, long generation, long acceptedTicks)
    {
        CorrelationId = correlationId;
        Generation = generation;
        AcceptedTicks = acceptedTicks;
    }

    public long CorrelationId { get; }
    public long Generation { get; }
    public long AcceptedTicks { get; }
    public bool IsResponseCompleted { get { lock (_gate) return _responseCompleted; } }

    public void Configure(int cssWidth, int cssHeight, float dpi, bool isInteracting, int layerCount)
    {
        lock (_gate)
        {
            _cssWidth = cssWidth;
            _cssHeight = cssHeight;
            _dpi = dpi;
            _isInteracting = isInteracting;
            _layerCount = layerCount;
        }
    }

    public void SetEncoding(string encoding)
    {
        lock (_gate) _encoding = encoding;
    }

    public void MarkDequeued(long nowTicks = 0)
    {
        if (nowTicks == 0) nowTicks = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_queueCompleted) return;
            _queueCompleted = true;
            AddSpanUnsafe("queue", null, AcceptedTicks, nowTicks, 0, 0, 0);
        }
    }

    public void MarkRenderStarted(long nowTicks = 0)
    {
        if (nowTicks == 0) nowTicks = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _renderStartedTicks = nowTicks;
            _renderStartedAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
            _renderThreadId = Environment.CurrentManagedThreadId;
        }
    }

    public void MarkRenderReady(long nowTicks = 0)
    {
        if (nowTicks == 0) nowTicks = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_renderCompleted || _renderStartedTicks == 0) return;
            _renderCompleted = true;
            AddSpanUnsafe("render", null, _renderStartedTicks, nowTicks, 0, 0, 0);
        }
    }

    public RenderTraceScope Measure(string stage, string? layer = null) => new(this, stage, layer);

    public void AddSpan(
        string stage,
        string? layer,
        long startTicks,
        long endTicks,
        long managedAllocatedBytes = 0,
        long bytes = 0,
        long items = 0)
    {
        if (string.IsNullOrWhiteSpace(stage) || endTicks < startTicks) return;
        lock (_gate)
            AddSpanUnsafe(stage, layer, startTicks, endTicks, managedAllocatedBytes, bytes, items);
    }

    internal bool CompleteResponse(string status, long outputBytes)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_responseCompleted) return false;
            if (!_queueCompleted)
            {
                _queueCompleted = true;
                AddSpanUnsafe("queue", null, AcceptedTicks, now, 0, 0, 0);
            }
            if (!_renderCompleted && _renderStartedTicks != 0)
            {
                _renderCompleted = true;
                AddSpanUnsafe("render", null, _renderStartedTicks, now, 0, 0, 0);
            }
            _responseCompleted = true;
            _responseCompletedTicks = now;
            _status = string.IsNullOrWhiteSpace(status) ? "unknown" : status;
            _outputBytes = Math.Max(0, outputBytes);
            if (_renderStartedTicks != 0 && _renderThreadId == Environment.CurrentManagedThreadId)
            {
                _managedAllocatedBytes = Math.Max(
                    0,
                    GC.GetAllocatedBytesForCurrentThread() - _renderStartedAllocatedBytes);
            }
            return true;
        }
    }

    internal void RecordPresentation(RenderPresentationTrace presentation)
    {
        lock (_gate) _presentation = presentation;
    }

    public RenderFrameTraceSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new RenderFrameTraceSnapshot(
                CorrelationId,
                Generation,
                AcceptedTicks,
                _responseCompletedTicks,
                _status,
                _cssWidth,
                _cssHeight,
                _dpi,
                _isInteracting,
                _layerCount,
                _encoding,
                _outputBytes,
                _managedAllocatedBytes,
                _spans.ToArray(),
                _presentation);
        }
    }

    private void AddSpanUnsafe(
        string stage,
        string? layer,
        long startTicks,
        long endTicks,
        long managedAllocatedBytes,
        long bytes,
        long items) =>
        _spans.Add(new RenderTraceSpan(
            stage,
            layer,
            startTicks,
            endTicks,
            Math.Max(0, managedAllocatedBytes),
            Math.Max(0, bytes),
            Math.Max(0, items)));
}

public sealed class RenderTraceScope : IDisposable
{
    private RenderFrameTrace? _trace;
    private readonly string _stage;
    private readonly string? _layer;
    private readonly long _startTicks;
    private readonly long _allocatedBytes;
    private readonly int _threadId;

    internal RenderTraceScope(RenderFrameTrace trace, string stage, string? layer)
    {
        _trace = trace;
        _stage = stage;
        _layer = layer;
        _startTicks = Stopwatch.GetTimestamp();
        _allocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        _threadId = Environment.CurrentManagedThreadId;
    }

    public void Dispose()
    {
        RenderFrameTrace? trace = Interlocked.Exchange(ref _trace, null);
        if (trace == null) return;
        long allocated = _threadId == Environment.CurrentManagedThreadId
            ? Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - _allocatedBytes)
            : 0;
        trace.AddSpan(_stage, _layer, _startTicks, Stopwatch.GetTimestamp(), allocated);
    }
}

public static class RenderTelemetryStatistics
{
    public static RenderTelemetrySummary Summarize(IEnumerable<RenderFrameTraceSnapshot> source)
    {
        RenderFrameTraceSnapshot[] frames = source.ToArray();
        var response = frames
            .Where(frame => frame.ResponseCompletedTicks >= frame.AcceptedTicks)
            .Select(frame => TicksToMilliseconds(frame.ResponseCompletedTicks - frame.AcceptedTicks));
        var endToEnd = frames
            .Where(frame => frame.Presentation != null)
            .Select(frame => frame.Presentation!.EndToEndMilliseconds);

        string[] stages = frames.SelectMany(frame => frame.Spans).Select(span => span.Stage)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var stageDistributions = stages.ToDictionary(
            stage => stage,
            stage => Distribution(frames
                .Where(frame => frame.Spans.Any(span => string.Equals(span.Stage, stage, StringComparison.Ordinal)))
                .Select(frame => UnionMilliseconds(
                    frame.Spans.Where(span => string.Equals(span.Stage, stage, StringComparison.Ordinal))))),
            StringComparer.Ordinal);

        var layerKeys = frames.SelectMany(frame => frame.Spans)
            .Where(span => !string.IsNullOrEmpty(span.Layer))
            .Select(span => (span.Stage, Layer: span.Layer!))
            .Distinct()
            .OrderBy(value => value.Stage, StringComparer.Ordinal)
            .ThenBy(value => value.Layer, StringComparer.Ordinal)
            .ToArray();
        var layerDistributions = layerKeys.ToDictionary(
            key => $"{key.Stage}@{key.Layer}",
            key => Distribution(frames
                .Where(frame => frame.Spans.Any(span =>
                    string.Equals(span.Stage, key.Stage, StringComparison.Ordinal) &&
                    string.Equals(span.Layer, key.Layer, StringComparison.Ordinal)))
                .Select(frame => UnionMilliseconds(frame.Spans.Where(span =>
                    string.Equals(span.Stage, key.Stage, StringComparison.Ordinal) &&
                    string.Equals(span.Layer, key.Layer, StringComparison.Ordinal))))),
            StringComparer.Ordinal);

        AddPresentationStage(frames, stageDistributions, "network", value => value.NetworkMilliseconds);
        AddPresentationStage(frames, stageDistributions, "decode", value => value.DecodeMilliseconds);
        AddPresentationStage(frames, stageDistributions, "presentation_draw", value => value.DrawMilliseconds);

        return new RenderTelemetrySummary(
            frames.Length,
            Distribution(response),
            Distribution(endToEnd),
            Distribution(frames.Select(frame => (double)frame.OutputBytes)),
            Distribution(frames.Select(frame => (double)frame.ManagedAllocatedBytes)),
            stageDistributions,
            layerDistributions);
    }

    public static double StageMilliseconds(RenderFrameTraceSnapshot frame, string stage) =>
        UnionMilliseconds(frame.Spans.Where(span => string.Equals(span.Stage, stage, StringComparison.Ordinal)));

    public static string ToLogLine(RenderFrameTraceSnapshot frame)
    {
        double responseMs = frame.ResponseCompletedTicks >= frame.AcceptedTicks
            ? TicksToMilliseconds(frame.ResponseCompletedTicks - frame.AcceptedTicks)
            : 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"RenderTrace id={frame.CorrelationId} generation={frame.Generation} status={frame.Status} " +
            $"queue_ms={StageMilliseconds(frame, "queue"):F3} io_ms={StageMilliseconds(frame, "io"):F3} " +
            $"geometry_ms={StageMilliseconds(frame, "geometry"):F3} draw_ms={StageMilliseconds(frame, "draw"):F3} " +
            $"encode_ms={StageMilliseconds(frame, "encode"):F3} write_ms={StageMilliseconds(frame, "write"):F3} " +
            $"response_ms={responseMs:F3} e2e_ms={frame.Presentation?.EndToEndMilliseconds ?? 0:F3} " +
            $"bytes={frame.OutputBytes} transfer_bytes={frame.Presentation?.TransferBytes ?? 0} alloc_bytes={frame.ManagedAllocatedBytes}");
    }

    public static RenderMetricDistribution Distribution(IEnumerable<double> source)
    {
        double[] sorted = source.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0) return new RenderMetricDistribution(0, 0, 0);
        return new RenderMetricDistribution(
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99));
    }

    private static void AddPresentationStage(
        IReadOnlyList<RenderFrameTraceSnapshot> frames,
        IDictionary<string, RenderMetricDistribution> destination,
        string name,
        Func<RenderPresentationTrace, double> selector)
    {
        destination[name] = Distribution(frames
            .Where(frame => frame.Presentation != null)
            .Select(frame => selector(frame.Presentation!)));
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        int rank = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[rank];
    }

    private static double UnionMilliseconds(IEnumerable<RenderTraceSpan> source)
    {
        RenderTraceSpan[] spans = source
            .Where(span => span.EndTicks >= span.StartTicks)
            .OrderBy(span => span.StartTicks)
            .ThenBy(span => span.EndTicks)
            .ToArray();
        if (spans.Length == 0) return 0;

        long totalTicks = 0;
        long currentStart = spans[0].StartTicks;
        long currentEnd = spans[0].EndTicks;
        for (int index = 1; index < spans.Length; index++)
        {
            RenderTraceSpan span = spans[index];
            if (span.StartTicks <= currentEnd)
            {
                currentEnd = Math.Max(currentEnd, span.EndTicks);
                continue;
            }
            totalTicks += currentEnd - currentStart;
            currentStart = span.StartTicks;
            currentEnd = span.EndTicks;
        }
        totalTicks += currentEnd - currentStart;
        return TicksToMilliseconds(totalTicks);
    }

    private static double TicksToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
