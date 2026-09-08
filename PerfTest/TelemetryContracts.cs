using System.Diagnostics;
using GeoNex.Services;

internal static class TelemetryContracts
{
    public static void Run()
    {
        VerifyDisabledFastPath();
        VerifyOverlappingIntervalsAndPercentiles();
        VerifyBoundedRetentionAndPresentationMerge();
        Console.WriteLine("Telemetry contracts: PASS (disabled fast-path, interval union, p50/p95/p99, bounded retention, browser merge)");
    }

    private static void VerifyDisabledFastPath()
    {
        var collector = new RenderTelemetryCollector(enabled: false);
        Require(collector.BeginFrame(1, 1, Stopwatch.GetTimestamp()) == null, "Disabled collector allocated a trace.");
        Require(collector.Snapshot().Count == 0, "Disabled collector retained data.");
    }

    private static void VerifyOverlappingIntervalsAndPercentiles()
    {
        long Milliseconds(double value) => (long)Math.Round(value * Stopwatch.Frequency / 1000.0);
        var frames = new List<RenderFrameTraceSnapshot>();
        for (int value = 1; value <= 100; value++)
        {
            long accepted = Milliseconds(1000 + value * 200);
            long response = accepted + Milliseconds(value);
            IReadOnlyList<RenderTraceSpan> spans = value == 1
                ? new[]
                {
                    new RenderTraceSpan("io", "ortofoto", accepted, accepted + Milliseconds(10), 10, 0, 0),
                    new RenderTraceSpan("io", "ortofoto", accepted + Milliseconds(5), accepted + Milliseconds(15), 20, 0, 0)
                }
                : Array.Empty<RenderTraceSpan>();
            frames.Add(new RenderFrameTraceSnapshot(
                value, value, accepted, response, "ok", 1920, 1080, 1, false, 1,
                "png", value * 1000, value * 100, spans,
                new RenderPresentationTrace(1, 2, 3, value + 10, value * 900)));
        }

        RenderTelemetrySummary summary = RenderTelemetryStatistics.Summarize(frames);
        RequireNear(summary.ResponseMilliseconds.P50, 50, 0.02, "response p50");
        RequireNear(summary.ResponseMilliseconds.P95, 95, 0.02, "response p95");
        RequireNear(summary.ResponseMilliseconds.P99, 99, 0.02, "response p99");
        RequireNear(summary.EndToEndMilliseconds.P50, 60, 0.001, "end-to-end p50");
        RequireNear(summary.StageMilliseconds["io"].P99, 15, 0.02, "overlapping I/O union");
        RequireNear(summary.LayerStageMilliseconds["io@ortofoto"].P99, 15, 0.02, "per-layer I/O union");
    }

    private static void VerifyBoundedRetentionAndPresentationMerge()
    {
        var collector = new RenderTelemetryCollector(enabled: true, capacity: 16);
        for (int frameId = 1; frameId <= 20; frameId++)
        {
            RenderFrameTrace trace = collector.BeginFrame(frameId, frameId, Stopwatch.GetTimestamp())!;
            trace.MarkDequeued();
            trace.MarkRenderStarted();
            trace.MarkRenderReady();
            collector.CompleteResponse(trace, "ok", frameId * 10);
        }
        Require(collector.Snapshot().Count == 16, "Trace retention is not bounded.");

        collector.RecordPresentation(20, 4, 2, 1, 12, 180);
        RenderFrameTraceSnapshot latest = collector.Snapshot().Single(frame => frame.CorrelationId == 20);
        Require(latest.Presentation?.EndToEndMilliseconds == 12, "Browser presentation was not correlated.");
        Require(latest.Presentation?.TransferBytes == 180, "Browser transfer bytes were not retained.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireNear(double actual, double expected, double tolerance, string label)
    {
        if (Math.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException($"{label}: expected={expected}, actual={actual}");
    }
}
