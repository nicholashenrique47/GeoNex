using System.Diagnostics;
using GeoNex.Services;
using SkiaSharp;

internal static class PolygonBlitterMetrics
{
    public static void Run(string file, int width, int height, float scale, float strokeWidth)
    {
        using var path = CapturedPolygonMetrics.Read(file, out int rings);
        var matrix = MapCoordinateFrame.Create(MapViewportMetrics.Create(width, height, 1), SKPoint.Empty, scale).LocalToPhysicalMatrix;
        using var fill = new SKPaint { Color = SKColor.Parse("#38bdf8").WithAlpha(89), IsAntialias = true };
        using var stroke = new SKPaint { Color = SKColor.Parse("#0ea5e9").WithAlpha(89), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = strokeWidth, StrokeJoin = SKStrokeJoin.Round };
        Console.WriteLine($"BLITTER points={path.PointCount} rings={rings} size={width}x{height}");
        byte[]? reference = null;
        string[] modes = { "rgba", "bgra", "rgba-src", "bgra-src", "rows", "columns" };
        var samples = modes.ToDictionary(m => m, _ => new List<(double Total, double Fill, double Stroke)>());
        var deltas = modes.ToDictionary(m => m, _ => 0);
        for (int round = 0; round < 7; round++)
        foreach (string mode in round % 2 == 0 ? modes : modes.Reverse())
        {
            long started = Stopwatch.GetTimestamp();
            using var bitmap = new SKBitmap(width, height, mode.StartsWith("bgra") ? SKColorType.Bgra8888 : SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent); canvas.SetMatrix(matrix);
            fill.BlendMode = mode.EndsWith("src") ? SKBlendMode.Src : SKBlendMode.SrcOver;
            long fillStart = Stopwatch.GetTimestamp();
            bool bands = mode is "rows" or "columns";
            void Paint(SKPaint paint)
            {
                if (!bands) { canvas.DrawPath(path, paint); return; }
                Parallel.For(0, 4, band =>
                {
                    using var localPaint = paint.Clone();
                    using var localPath = new SKPath(path);
                    using var local = SKSurface.Create(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes);
                    var clip = mode == "rows" ? new SKRect(0, height * band / 4, width, height * (band + 1) / 4)
                        : new SKRect(width * band / 4, 0, width * (band + 1) / 4, height);
                    local.Canvas.ClipRect(clip, SKClipOperation.Intersect, false);
                    local.Canvas.SetMatrix(matrix);
                    local.Canvas.DrawPath(localPath, localPaint);
                });
            }
            Paint(fill);
            double fillMs = Stopwatch.GetElapsedTime(fillStart).TotalMilliseconds;
            long strokeStart = Stopwatch.GetTimestamp();
            Paint(stroke);
            double strokeMs = Stopwatch.GetElapsedTime(strokeStart).TotalMilliseconds;
            double totalMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            // Conversion is outside the measured region; compare visual RGBA pixels.
            using var rgba = bitmap.Copy(SKColorType.Rgba8888);
            var pixels = rgba.GetPixelSpan();
            reference ??= pixels.ToArray();
            int maximum = 0;
            for (int i = 0; i < pixels.Length; i++) maximum = Math.Max(maximum, Math.Abs(reference[i] - pixels[i]));
            deltas[mode] = Math.Max(deltas[mode], maximum);
            if (round > 0) samples[mode].Add((totalMs, fillMs, strokeMs));
        }
        foreach (string mode in modes)
        {
            double Median(Func<(double Total, double Fill, double Stroke), double> select)
            { var values = samples[mode].Select(select).Order().ToArray(); return (values[2] + values[3]) / 2; }
            Console.WriteLine(FormattableString.Invariant($"BLITTER mode={mode} total_ms={Median(x => x.Total):F3} fill_ms={Median(x => x.Fill):F3} stroke_ms={Median(x => x.Stroke):F3} max_delta={deltas[mode]}"));
        }
    }
}
