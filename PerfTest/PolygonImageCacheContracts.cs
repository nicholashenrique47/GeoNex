using GeoNex.Services;
using SkiaSharp;

internal static class PolygonImageCacheContracts
{
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    public static void Run()
    {
        using var cache = new PolygonImageCache();
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        // Subpixel diagonals, a hole, and densely overlapping contours.
        path.AddRect(new SKRect(11.13f, 9.8f, 151.7f, 104.2f));
        path.AddCircle(61.3f, 49.9f, 12.7f);
        for (int i = 0; i < 30; i++)
        {
            path.MoveTo(9.1f + i * 4.1f, 3.4f);
            path.LineTo(19.3f + i * 4.1f, 109.7f);
            path.LineTo(13.6f + i * 4.1f, 113.3f);
            path.Close();
        }
        using var fill = new SKPaint { Color = new SKColor(0, 187, 231, 91), IsAntialias = true };
        using var stroke = new SKPaint { Color = new SKColor(13, 94, 163, 207),
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.31f };
        var context = new PolygonImageContext(new object(), 1, new SKRect(0, 0, 160, 120),
            default, "EPSG:3857", 0, 0);
        const long budget = 8 * 1024 * 1024;
        int maximumDifference = 0;
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        foreach (var background in new[] { SKColors.Transparent, SKColors.White,
                     new SKColor(53, 107, 36), new SKColor(170, 33, 91, 127) })
        {
            int width = (int)(160 * scale), height = (int)(120 * scale);
            using var direct = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var actual = new SKBitmap(direct.Info);
            using var reference = new SKCanvas(direct);
            using var target = new SKCanvas(actual);
            reference.Clear(background); target.Clear(background);
            reference.Scale(scale); target.Scale(scale);
            reference.DrawPath(path, fill); reference.DrawPath(path, stroke);
            Require(cache.TryDraw("lots", context, target, path, fill, stroke, width, height, budget, default), "Cache rejected eligible pass");
            var expected = direct.Bytes; var observed = actual.Bytes;
            if (background == SKColors.Transparent) Require(expected.SequenceEqual(observed), "Transparent layer pixels changed");
            for (int i = 0; i < expected.Length; i++) maximumDifference = Math.Max(maximumDifference, Math.Abs(expected[i] - observed[i]));
            // A repeated cache hit must be bit-identical, not introduce progressive loss.
            target.Clear(background);
            long hits = cache.Hits;
            Require(cache.TryDraw("lots", context, target, path, fill, stroke, width, height, budget, default), "Hit failed");
            Require(cache.Hits == hits + 1 && observed.SequenceEqual(actual.Bytes), "Cache hit changed pixels");
        }
        Console.WriteLine($"POLYGON IMAGE premultiplied_channel_max_delta={maximumDifference}");
        Require(maximumDifference <= 2, "Offscreen compositing exceeds 2/255 channel rounding tolerance");
        using var bitmap = new SKBitmap(160, 120);
        using var canvas = new SKCanvas(bitmap);
        long builds = cache.Builds;
        Require(cache.TryDraw("lots", context with { Revision = 2 }, canvas, path, fill, stroke, 160, 120, budget, default), "Revision draw failed");
        Require(cache.Builds == builds + 1, "Revision reused stale image");
        builds = cache.Builds; fill.Color = SKColors.Red;
        cache.TryDraw("lots", context with { Revision = 2 }, canvas, path, fill, stroke, 160, 120, budget, default);
        Require(cache.Builds == builds + 1, "Style reused stale image");
        builds = cache.Builds;
        cache.TryDraw("lots", context with { Source = new object(), Revision = 2 }, canvas, path, fill, stroke, 160, 120, budget, default);
        Require(cache.Builds == builds + 1, "Replaced source reused stale image");
        builds = cache.Builds;
        canvas.Translate(.25f, -.5f);
        var matrix = canvas.TotalMatrix;
        cache.TryDraw("lots", context, canvas, path, fill, stroke, 160, 120, budget, default);
        Require(cache.Builds == builds + 1 && canvas.TotalMatrix == matrix, "Camera invalidation or matrix restoration failed");
        foreach (var changed in new[] { context with { Crs = "EPSG:4326" },
                     context with { OffsetX = 123.5 }, context with { Origin = new SKPoint(1, 2) },
                     context with { Viewport = new SKRect(-10, -10, 160, 120) } })
        {
            builds = cache.Builds;
            cache.TryDraw("lots", changed, canvas, path, fill, stroke, 160, 120, budget, default);
            Require(cache.Builds == builds + 1, "Spatial context reused stale pixels");
        }
        cache.Maintain(budget, 500);
        Require(cache.Bytes == 0, "Edited/removed layer was retained");
        const long singleImage = 160 * 120 * 4;
        cache.TryDraw("first", context, canvas, path, fill, stroke, 160, 120, singleImage, default);
        cache.TryDraw("second", context, canvas, path, fill, null, 160, 120, singleImage, default);
        Require(cache.Bytes == singleImage, "LRU exceeded byte budget");
        builds = cache.Builds;
        cache.TryDraw("first", context, canvas, path, null, stroke, 160, 120, singleImage, default);
        Require(cache.Builds == builds + 1, "LRU retained evicted image");
        Require(!cache.TryDraw("oversized", context, canvas, path, fill, stroke, int.MaxValue, int.MaxValue, budget, default), "Dimension overflow accepted");
        Require(!cache.TryDraw("lots", context, canvas, path, fill, stroke, 160, 120, 0, default) && cache.Bytes == 0, "Memory pressure failed to release cache");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { cache.TryDraw("lots", context, canvas, path, fill, stroke, 160, 120, budget, cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        Require(cache.Bytes == 0, "Cancelled image published");
        stroke.PathEffect = SKPathEffect.CreateDash(new[] { 2f, 2f }, 0);
        Require(!cache.TryDraw("lots", context, canvas, path, fill, stroke, 160, 120, budget, default), "Unsupported effect cached");
        stroke.PathEffect.Dispose(); stroke.PathEffect = null;
        Parallel.For(0, 24, i =>
        {
            using var concurrentBitmap = new SKBitmap(160, 120);
            using var concurrentCanvas = new SKCanvas(concurrentBitmap);
            if (i == 12) cache.Dispose();
            cache.TryDraw("lots", context, concurrentCanvas, path, fill, stroke, 160, 120, budget, default);
        });
        cache.Dispose();
        Require(!cache.TryDraw("lots", context, canvas, path, fill, stroke, 160, 120, budget, default), "Disposed cache resurrected");
        Console.WriteLine("PASS polygon-image-cache: pixels, DPI, repeat, camera/CRS/origin, revision, style, source, LRU/RAM, cancellation, effects, concurrent disposal");
    }
}
