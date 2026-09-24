using System.Diagnostics;
using GeoNex.Services;
using SkiaSharp;

internal static class RasterSurfaceMetrics
{
    public static void Run(string file, int width, int height, float scale, float strokeWidth)
    {
        using var path = CapturedPolygonMetrics.Read(file, out _);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var matrix = MapCoordinateFrame.Create(MapViewportMetrics.Create(width, height, 1), SKPoint.Empty, scale).LocalToPhysicalMatrix;
        using var fill = new SKPaint { Color = SKColor.Parse("#38bdf8").WithAlpha(89), IsAntialias = true };
        using var stroke = new SKPaint { Color = SKColor.Parse("#0ea5e9").WithAlpha(89), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = strokeWidth, StrokeJoin = SKStrokeJoin.Round };
        var samples = new[] { new List<double>(), new List<double>() };
        byte[]? reference = null;
        for (int round = 0; round < 9; round++)
        foreach (int version in round % 2 == 0 ? new[] { 0, 1 } : new[] { 1, 0 })
        {
            long start = Stopwatch.GetTimestamp();
            using var surface = SKSurface.Create(info);
            if (version == 0) surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.SetMatrix(matrix);
            surface.Canvas.DrawPath(path, fill); surface.Canvas.DrawPath(path, stroke);
            using var image = surface.Snapshot();
            if (round > 0) samples[version].Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            using var pixels = image.PeekPixels();
            reference ??= pixels.GetPixelSpan().ToArray();
            if (!pixels.GetPixelSpan().SequenceEqual(reference)) throw new InvalidOperationException("Fresh raster pixels changed");
        }
        for (int i = 0; i < samples.Length; i++)
        {
            var values = samples[i].Order().ToArray();
            Console.WriteLine(FormattableString.Invariant($"SURFACE extra_clear={i == 0} median_ms={(values[3] + values[4]) / 2:F3} samples={values.Length} pixels=exact"));
        }
    }
}
