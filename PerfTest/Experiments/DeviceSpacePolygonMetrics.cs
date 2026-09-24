using System.Diagnostics;
using GeoNex.Services;
using SkiaSharp;

// Diagnostic only. Includes preparation in timing and compares the entire
// premultiplied frame with the unmodified production DrawPath semantics.
internal static class DeviceSpacePolygonMetrics
{
    public static void Run(string file, int width, int height, float scale, float strokeWidth)
    {
        using var path = CapturedPolygonMetrics.Read(file, out int rings);
        var matrix = MapCoordinateFrame.Create(MapViewportMetrics.Create(width, height, 1),
            SKPoint.Empty, scale).LocalToPhysicalMatrix;
        using var fill = new SKPaint { Color = SKColor.Parse("#38bdf8").WithAlpha(89), IsAntialias = true };
        using var stroke = new SKPaint { Color = SKColor.Parse("#0ea5e9").WithAlpha(89), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = strokeWidth, StrokeJoin = SKStrokeJoin.Round };
        Console.WriteLine($"DEVICE_CAPTURE points={path.PointCount} rings={rings} size={width}x{height} scale={scale}");
        foreach (string component in new[] { "fill", "stroke", "both" })
        {
            var samples = new Dictionary<string, List<double>>();
            var deltas = new Dictionary<string, int>();
            using var expected = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(expected))
            {
                canvas.Clear(SKColors.Transparent); canvas.SetMatrix(matrix);
                if (component != "stroke") canvas.DrawPath(path, fill);
                if (component != "fill") canvas.DrawPath(path, stroke);
            }
            byte[] reference = expected.Bytes;
            string[] modes = { "direct", "device", "parallel", "device-parallel" };
            for (int round = 0; round < 5; round++)
            foreach (string mode in round % 2 == 0 ? modes : modes.Reverse())
            {
                long started = Stopwatch.GetTimestamp();
                using var target = new SKBitmap(expected.Info);
                bool device = mode.StartsWith("device");
                using var transformed = device ? new SKPath(path) : null;
                using var scaledStroke = device ? stroke.Clone() : null;
                if (device)
                {
                    transformed!.Transform(matrix);
                    // Stroke width is in local coordinates: an identity canvas
                    // needs the physical width, including the original hairline 0.
                    scaledStroke!.StrokeWidth *= scale;
                }
                var geometry = transformed ?? path;
                var paint = scaledStroke ?? stroke;
                var transform = device ? SKMatrix.Identity : matrix;
                bool parallel = mode.EndsWith("parallel") && ParallelPolygonPainter.TryPaint(target,
                    geometry, component == "stroke" ? null : fill, component == "fill" ? null : paint,
                    transform, VectorRuntimeResources.Current.Workers, 220L << 20, default);
                if (!parallel)
                {
                    using var canvas = new SKCanvas(target);
                    canvas.Clear(SKColors.Transparent); canvas.SetMatrix(transform);
                    if (component != "stroke") canvas.DrawPath(geometry, fill);
                    if (component != "fill") canvas.DrawPath(geometry, paint);
                }
                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var actual = target.GetPixelSpan();
                int maximum = 0;
                for (int i = 0; i < actual.Length; i++) maximum = Math.Max(maximum, Math.Abs(reference[i] - actual[i]));
                if (!samples.TryGetValue(mode, out var values)) samples[mode] = values = new();
                if (round > 0) values.Add(elapsed);
                deltas[mode] = Math.Max(deltas.GetValueOrDefault(mode), maximum);
            }
            foreach (var (mode, values) in samples)
            {
                values.Sort();
                Console.WriteLine(FormattableString.Invariant($"DEVICE component={component} mode={mode} median_ms={(values[1] + values[2]) / 2:F3} max_delta={deltas[mode]} eligible_quality={deltas[mode] <= 1}"));
            }
        }
    }
}
