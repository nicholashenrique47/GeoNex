using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using SkiaSharp;

internal sealed record PipelinePercentiles(double P50, double P95, double P99);
internal sealed record PipelineStageResult(string Stage, PipelinePercentiles Milliseconds);
internal sealed record PipelineScenarioResult(
    string Name,
    double Zoom,
    double TolerancePixels,
    double MicroLodPixels,
    double ColdStartTotalMilliseconds,
    IReadOnlyList<PipelineStageResult> Stages,
    PipelinePercentiles PayloadBytes,
    long ManagedAllocatedBytes,
    long WorkingSetBytes,
    long PrivateBytes);
internal sealed record PipelineMetricsReport(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    int Samples,
    int WarmupPasses,
    int FeatureCount,
    int LogicalCpuCount,
    bool UsesNativeV4,
    string Presentation,
    string NativePath,
    IReadOnlyList<PipelineScenarioResult> Scenarios);

internal static class PipelineMetrics
{
    private const int Samples = 25;
    private const int WarmupPasses = 3;

    public static unsafe PipelineMetricsReport Run(
        byte[] shp,
        long[] offsets,
        bool v4,
        int webpQuality,
        string? jsonOutputPath = null)
    {
        if (webpQuality != 0 && webpQuality is not (70 or 95))
            throw new ArgumentOutOfRangeException(nameof(webpQuality), "Use 0 (PNG), 70 ou 95.");
        bool measureWebp = webpQuality != 0;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string presentation = measureWebp ? $"WebP{webpQuality}" : "PNG100";
        string nativePath = Environment.GetEnvironmentVariable("GEONEX_NATIVE_PATH") ?? "output/GeoNexNative.dll";
        Console.WriteLine($"Pipeline samples={Samples} warmup={WarmupPasses} features={offsets.Length} logicalCPU={Environment.ProcessorCount} V4={v4} presentation={presentation}");
        Console.WriteLine($"DLL={nativePath}");
        Console.WriteLine("scenario,stage,p50_ms,p95_ms,p99_ms");
        var scenarioResults = new List<PipelineScenarioResult>();

        foreach (var (name, zoom, tolerance, micro) in new[] {
            ("detail", 6f, .30f, .65f), ("overview", .1f, .85f, 1.25f), ("exact", 6f, 0f, 0f) })
        {
            const int width = 1920, height = 1080;
            float top = -260, bottom = top + height / zoom;
            var output = new float[1_048_576];
            var grid = new byte[(width + 2) * (height + 2)];
            var times = Enumerable.Range(0, 8).Select(_ => new List<double>()).ToArray();
            var payloadSizes = new List<long>();
            double coldStartTotal = 0;
            using var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            using var fill = new SKPaint { Color = new SKColor(40, 140, 220, 90), IsAntialias = true };
            using var border = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.DarkBlue, StrokeWidth = .15f, IsAntialias = true };
            long allocatedBefore = GC.GetTotalAllocatedBytes(true);
            for (int pass = -WarmupPasses; pass < Samples; pass++)
            {
                canvas.ResetMatrix(); canvas.Clear(SKColors.White);
                Array.Clear(grid);
                using var path = new SKPath { FillType = SKPathFillType.Winding };
                double native = 0, pathMs = 0;
                long start = Stopwatch.GetTimestamp();
                fixed (byte* data = shp)
                fixed (long* records = offsets)
                fixed (float* commands = output)
                fixed (byte* mask = grid)
                {
                    for (int first = 0; first < offsets.Length;)
                    {
                        long tick = Stopwatch.GetTimestamp();
                        int processed, required, written;
                        if (v4)
                            written = RenderContracts.ProcessShapeBatchV4(data, shp.LongLength, 0,
                                records + first, Math.Min(4096, offsets.Length - first), commands, output.Length,
                                0, 0, zoom, tolerance, micro, 0, top, width / zoom, bottom,
                                mask, width + 2, height + 2, out processed, out required, out _, out _, out _);
                        else
                            written = Native.ProcessShapeBatchV3(data, records + first, Math.Min(4096, offsets.Length - first),
                                commands, output.Length, 0, 0, zoom, tolerance, micro, 0, top, width / zoom, bottom,
                                mask, width + 2, height + 2, out processed, out required, out _, out _, out _);
                        native += Stopwatch.GetElapsedTime(tick).TotalMilliseconds;
                        if (written < 0 || processed <= 0) throw new Exception($"Batch status={written} required={required}");
                        first += processed;
                        tick = Stopwatch.GetTimestamp();
                        for (int c = 0; c < written;)
                        {
                            int count = (int)output[c++];
                            if (count <= 0 || c + count * 2 > written) throw new Exception("Bad command protocol");
                            path.AddPoly(MemoryMarshal.Cast<float, SKPoint>(output.AsSpan(c, count * 2)), true);
                            c += count * 2;
                        }
                        pathMs += Stopwatch.GetElapsedTime(tick).TotalMilliseconds;
                    }
                }
                long gridStart = Stopwatch.GetTimestamp();
                for (int y = 0; y < height + 2; y++)
                {
                    int row = y * (width + 2);
                    for (int x = 0; x < width + 2;)
                    {
                        if (grid[row + x] == 0) { x++; continue; }
                        int first = x++;
                        while (x < width + 2 && grid[row + x] != 0) x++;
                        path.AddRect(new SKRect(first / zoom, top + y / zoom, x / zoom, top + (y + 1) / zoom));
                    }
                }
                double gridMs = Stopwatch.GetElapsedTime(gridStart).TotalMilliseconds;
                canvas.Scale(zoom); canvas.Translate(0, -top);
                long drawStart = Stopwatch.GetTimestamp();
                canvas.DrawPath(path, fill); canvas.DrawPath(path, border); canvas.Flush();
                double drawMs = Stopwatch.GetElapsedTime(drawStart).TotalMilliseconds;
                using var image = SKImage.FromBitmap(bitmap);
                long encodeStart = Stopwatch.GetTimestamp();
                using var encoded = image.Encode(
                    measureWebp ? SKEncodedImageFormat.Webp : SKEncodedImageFormat.Png,
                    measureWebp ? webpQuality : 100);
                if (encoded == null) throw new InvalidOperationException("Image encoding failed.");
                double encodeMs = Stopwatch.GetElapsedTime(encodeStart).TotalMilliseconds;
                long streamStart = Stopwatch.GetTimestamp();
                using var responseStream = new MemoryStream(checked((int)encoded.Size));
                encoded.SaveTo(responseStream);
                double streamCopyMs = Stopwatch.GetElapsedTime(streamStart).TotalMilliseconds;

                responseStream.Position = 0;
                long decodeStart = Stopwatch.GetTimestamp();
                using SKBitmap? decoded = SKBitmap.Decode(responseStream);
                double decodeProxyMs = Stopwatch.GetElapsedTime(decodeStart).TotalMilliseconds;
                if (decoded == null) throw new InvalidOperationException("Image decode proxy failed.");
                double total = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (pass == -WarmupPasses) coldStartTotal = total;
                if (pass >= 0)
                {
                    double[] sample = [native, pathMs, gridMs, drawMs, encodeMs, streamCopyMs, decodeProxyMs, total];
                    for (int stage = 0; stage < sample.Length; stage++) times[stage].Add(sample[stage]);
                    payloadSizes.Add(encoded.Size);
                }
            }

            string formatStage = measureWebp ? $"webp{webpQuality}" : "png";
            string[] stages = ["native", "path", "mask_path", "draw", formatStage, "stream_copy_proxy", "skia_decode_proxy", "total"];
            var stageResults = new List<PipelineStageResult>(stages.Length);
            for (int stage = 0; stage < stages.Length; stage++)
            {
                PipelinePercentiles percentiles = Percentiles(times[stage]);
                stageResults.Add(new PipelineStageResult(stages[stage], percentiles));
                Console.WriteLine($"{name},{stages[stage]},{percentiles.P50:F3},{percentiles.P95:F3},{percentiles.P99:F3}");
            }
            PipelinePercentiles payload = Percentiles(payloadSizes.Select(value => (double)value));
            Console.WriteLine($"payload,{name},{formatStage}_bytes_p50={payload.P50:F0},p95={payload.P95:F0},p99={payload.P99:F0}");
            long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            long workingSet = Environment.WorkingSet;
            long privateBytes = Process.GetCurrentProcess().PrivateMemorySize64;
            Console.WriteLine($"memory,{name},managed_alloc_bytes={allocated},working_set={workingSet},private_bytes={privateBytes}");
            scenarioResults.Add(new PipelineScenarioResult(
                name, zoom, tolerance, micro, coldStartTotal, stageResults, payload,
                allocated, workingSet, privateBytes));
        }

        var report = new PipelineMetricsReport(
            1,
            DateTimeOffset.UtcNow,
            Samples,
            WarmupPasses,
            offsets.Length,
            Environment.ProcessorCount,
            v4,
            presentation,
            nativePath,
            scenarioResults);
        if (!string.IsNullOrWhiteSpace(jsonOutputPath))
        {
            string fullPath = Path.GetFullPath(jsonOutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, JsonSerializer.Serialize(report, JsonOptions));
            Console.WriteLine($"Pipeline metrics JSON: {fullPath}");
        }
        return report;
    }

    private static PipelinePercentiles Percentiles(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0) return new PipelinePercentiles(0, 0, 0);
        double Pick(double percentile) => sorted[Math.Clamp(
            (int)Math.Ceiling(percentile * sorted.Length) - 1,
            0,
            sorted.Length - 1)];
        return new PipelinePercentiles(Pick(.50), Pick(.95), Pick(.99));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
