using System.Diagnostics;
using GeoNex.Services;
using SkiaSharp;

internal static class PolygonPixelFormatMetrics
{
    public static void Measure(SKPath path, int width, int height, float scale)
    {
        using var fill = new SKPaint { Color = SKColors.Cyan.WithAlpha(25), IsAntialias = true };
        using var stroke = new SKPaint { Color = SKColors.Cyan.WithAlpha(200), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 1 / scale, StrokeJoin = SKStrokeJoin.Round };
        var matrix = MapCoordinateFrame.Create(MapViewportMetrics.Create(width, height, 1), SKPoint.Empty, scale).LocalToPhysicalMatrix;
        byte[]? reference = null;
        var timings = new Dictionary<SKColorType, List<double>> { [SKColorType.Rgba8888] = new(), [SKColorType.Bgra8888] = new() };
        for (int round = 0; round < 4; round++)
        foreach (var format in round % 2 == 0 ? timings.Keys.ToArray() : timings.Keys.Reverse().ToArray())
        {
            var timer = Stopwatch.StartNew();
            using var bitmap = new SKBitmap(width, height, format, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent); canvas.SetMatrix(matrix);
                canvas.DrawPath(path, fill); canvas.DrawPath(path, stroke); canvas.Flush();
            }
            using var rgba = bitmap.Copy(SKColorType.Rgba8888);
            timer.Stop();
            byte[] pixels = rgba.Bytes;
            reference ??= pixels;
            if (!reference.SequenceEqual(pixels))
            {
                int max = reference.Zip(pixels, (a, b) => Math.Abs(a - b)).Max();
                Console.WriteLine($"PIXEL_FORMAT {format} REJECTED delta={max}"); return;
            }
            if (round > 0) timings[format].Add(timer.Elapsed.TotalMilliseconds);
        }
        foreach (var pair in timings) Console.WriteLine($"PIXEL_FORMAT {pair.Key} draw_copy_median_ms={pair.Value.Order().ElementAt(1):F2} pixels=exact");
    }
}
