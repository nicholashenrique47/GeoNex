using System.Diagnostics;
using GeoNex.Services;
using SkiaSharp;

internal static class PolygonBandPainting
{
    public static void Measure(SKPath path, int width, int height, float scale)
    {
        using var fill = new SKPaint { Color = SKColors.Cyan.WithAlpha(25), IsAntialias = true };
        using var stroke = new SKPaint { Color = SKColors.Cyan.WithAlpha(200), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 1 / scale, StrokeJoin = SKStrokeJoin.Round };
        var matrix = MapCoordinateFrame.Create(MapViewportMetrics.Create(width, height, 1), SKPoint.Empty, scale).LocalToPhysicalMatrix;
        using var reference = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(reference))
        {
            canvas.Clear(SKColors.Transparent); canvas.SetMatrix(matrix);
            var timer = Stopwatch.StartNew(); canvas.DrawPath(path, fill);
            double fillMs = timer.Elapsed.TotalMilliseconds; timer.Restart();
            canvas.DrawPath(path, stroke); canvas.Flush();
            Console.WriteLine($"PAINT fill_ms={fillMs:F2} stroke_ms={timer.Elapsed.TotalMilliseconds:F2}");
        }
        byte[] expected = reference.Bytes;
        var timings = new Dictionary<int, List<double>> { [1] = new(), [2] = new(), [4] = new() };
        for (int round = 0; round < 4; round++)
        foreach (int bands in round % 2 == 0 ? new[] { 1, 2, 4 } : new[] { 4, 2, 1 })
        {
            var timer = Stopwatch.StartNew();
            using var image = Paint(path, fill, stroke, matrix, width, height, bands);
            timer.Stop();
            byte[] pixels = image.Bytes;
            if (!expected.SequenceEqual(pixels))
            {
                int max = expected.Zip(pixels, (a, b) => Math.Abs(a - b)).Max();
                var rows = Enumerable.Range(0, height).Where(y => !expected.AsSpan(y * width * 4, width * 4).SequenceEqual(pixels.AsSpan(y * width * 4, width * 4))).ToArray();
                Console.WriteLine($"BANDS={bands} REJECTED pixels max_delta={max} changed_rows={rows.Length} first={rows[0]} last={rows[^1]}"); return;
            }
            if (round > 0) timings[bands].Add(timer.Elapsed.TotalMilliseconds);
        }
        foreach (var pair in timings) Console.WriteLine($"BANDS={pair.Key} paint_median_ms={pair.Value.Order().ElementAt(1):F2} pixels=exact");
    }

    public static unsafe SKBitmap Paint(SKPath path, SKPaint? fill, SKPaint? stroke, SKMatrix matrix,
        int width, int height, int bands, CancellationToken token = default)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        try
        {
            bitmap.Erase(SKColors.Transparent);
            var jobs = new List<(SKBitmap View, SKPath Path, SKPaint? Fill, SKPaint? Stroke, int Top, int Bottom)>();
            try
            {
                for (int band = 0; band < bands; band++)
                {
                    int top = height * band / bands, bottom = height * (band + 1) / bands;
                    var view = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                    view.Erase(SKColors.Transparent);
                    if (!matrix.TryInvert(out var inverse)) throw new InvalidOperationException("Invalid matrix");
                    var extent = inverse.MapRect(new SKRect(0, top - 4, width, bottom + 4));
                    extent.Left = path.Bounds.Left - 1;
                    extent.Right = path.Bounds.Right + 1;
                    // Keep complete contours that can touch these rows. The
                    // original bitmap clip and coordinate matrix remain exact.
                    var selected = PolygonContourCulling.Cull(path, extent) ?? new SKPath(path);
                    jobs.Add((view, selected, fill?.Clone(), stroke?.Clone(), top, bottom));
                }
                Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = bands, CancellationToken = token }, job =>
                {
                    using var canvas = new SKCanvas(job.View);
                    canvas.SetMatrix(matrix);
                    if (job.Fill != null) canvas.DrawPath(job.Path, job.Fill);
                    token.ThrowIfCancellationRequested();
                    if (job.Stroke != null) canvas.DrawPath(job.Path, job.Stroke);
                    canvas.Flush();
                    long offset = (long)job.Top * bitmap.RowBytes;
                    long bytes = (long)(job.Bottom - job.Top) * bitmap.RowBytes;
                    Buffer.MemoryCopy((byte*)job.View.GetPixels() + offset, (byte*)bitmap.GetPixels() + offset, bytes, bytes);
                });
            }
            finally
            {
                foreach (var job in jobs) { job.View.Dispose(); job.Path.Dispose(); job.Fill?.Dispose(); job.Stroke?.Dispose(); }
            }
            token.ThrowIfCancellationRequested();
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
}
