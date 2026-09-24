using GeoNex.Services;
using SkiaSharp;

internal static class ProjectedPathGeometryContracts
{
    public static void Run()
    {
        CheckBlockBoundaries();
        const double baseX = 100.125, baseY = -200.0625;
        var origin = new SKPoint(-5400000, 3000000);
        var coverage = new SKRect(-100, -100, 100, 100);
        double[] x = { -5400000.012345, -5399999.871721, -5399999.871721, -5400000.012345, -5400000.012345 };
        double[] y = { -3000000.125125, -3000000.125125, -3000000.003333, -3000000.003333, -3000000.125125 };
        for (int i = 0; i < x.Length; i++) { x[i] += baseX; y[i] += baseY; }
        double lx = x[0] + .03, rx = x[1] - .03, by = y[0] + .03, ty = y[2] - .03;
        var rings = new[] { (X: x, Y: y),
            (X: new[] { lx, lx, rx, rx, lx }, Y: new[] { by, ty, ty, by, by }), (X: x, Y: y) };
        var builder = new ProjectedPathGeometry.Builder(1 << 20, baseX, baseY);
        using var original = new SKPath();
        // Opposite winding, holes and repeated contours must survive the cache.
        foreach (var ring in rings)
        {
            builder.Append(ring.X, ring.Y, 0, ring.X.Length, true, default);
            TransformedRingWriter.Append(original, ring.X, ring.Y, 0, ring.X.Length, baseX + origin.X, baseY - origin.Y, true, 1, false);
        }
        var geometry = builder.Build()!;
        long cost = RenderPathCache.EstimateBytes(original) + geometry.RetainedBytes;
        long budget = cost * 2;
        using var cache = new RenderPathCache(budgetProvider: () => budget);
        cache.Store(1, original, coverage, 1000, false, false, true, origin, geometry);
        Check(cache.RetainedBytes == cost, "doubles count toward shared budget");
        foreach (float delta in new[] { -.5f, 0, .5f, 1f })
        {
            var camera = new SKPoint(origin.X + delta, origin.Y - .25f);
            Check(cache.TryGetProjected(1, new SKRect(-1, -1, 1, 1), camera, 1000, baseX, baseY, out var hit), "projected pan hit");
            using (hit)
            using (var expected = new SKPath())
            {
                foreach (var ring in rings)
                    TransformedRingWriter.Append(expected, ring.X, ring.Y, 0, ring.X.Length, baseX + camera.X, baseY - camera.Y, true, 1, false);
                Check(hit!.Points.SequenceEqual(expected.Points), "pan vertices exactly match fresh double conversion");
                foreach (float dpi in new[] { 1f, 1.25f, 2f })
                foreach (float rotation in new[] { 0f, 17f, 90f })
                    Check(Pixels(expected, dpi, rotation).SequenceEqual(Pixels(hit, dpi, rotation)), "pan pixels exactly match fresh render");
            }
        }
        // Mutating the pooled projection input after publication must be harmless.
        x[0] = double.NaN;
        Check(cache.TryGetProjected(1, new SKRect(-1, -1, 1, 1), origin, 1000, baseX, baseY, out var copy), "owned projected arrays");
        using (copy) Check(copy!.Points.SequenceEqual(original.Points), "source buffers are detached");
        Check(!cache.TryGetProjected(1, coverage, new SKPoint(origin.X + .5f, origin.Y), 1000, baseX, baseY, out _), "coverage miss");
        Check(!cache.TryGetProjected(1, coverage, origin, 5000, baseX, baseY, out _), "zoom jump miss");
        Check(!cache.TryGetProjected(1, coverage, origin, 1000, baseX + 1, baseY, out _), "world offset miss");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { cache.TryGetProjected(1, coverage, origin, 1000, baseX, baseY, out _, cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        cache.Invalidate(1);
        Check(!cache.TryGetProjected(1, coverage, origin, 1000, baseX, baseY, out _), "edit invalidates doubles");
        cache.Store(1, original, coverage, 1000, false, false, true, origin, geometry);
        budget = 0;
        Check(!cache.TryGetProjected(1, coverage, origin, 1000, baseX, baseY, out _) && cache.RetainedBytes == 0, "pressure releases both representations");
        var disabled = new ProjectedPathGeometry.Builder(0, baseX, baseY);
        disabled.Append(y, y, 0, y.Length, true, default);
        Check(disabled.Build() == null, "zero budget falls back");
        var invalid = new ProjectedPathGeometry.Builder(1 << 20, baseX, baseY);
        invalid.Append(x, y, 0, y.Length, true, default);
        Check(invalid.Build() == null, "invalid coordinate declines caching");
        using var small = new RenderPathCache(RenderPathCache.EstimateBytes(original));
        small.Store(1, original, coverage, 1000, false, false, true, origin, geometry);
        Check(small.TryGet(1, coverage, 1000, false, false, out var retained, origin), "small budget retains original cache");
        retained!.Dispose();
        Check(!small.TryGetProjected(1, coverage, origin, 1000, baseX, baseY, out _), "small budget declines extra doubles");
        foreach (var quality in new[] { (Interactive: true, Compact: false), (Interactive: false, Compact: true) })
        {
            using var isolated = new RenderPathCache(cost * 2);
            isolated.Store(1, original, coverage, 1000, quality.Interactive, quality.Compact, true, origin, geometry);
            Check(!isolated.TryGetProjected(1, coverage, origin, 1000, baseX, baseY, out _), "preview/LOD cannot publish projected finals");
        }
        Console.WriteLine("Projected geometry: PASS (exact vertices/pixels, pan/DPI/rotation, detached buffers, coverage, budget, cancellation)");
    }
    private static void CheckBlockBoundaries()
    {
        var builder = new ProjectedPathGeometry.Builder(8 << 20, 0, 0);
        using var expected = new SKPath();
        // Fill a block exactly, cross into another, then include a ring larger
        // than the default block. Closed/open ordering must survive all cases.
        foreach (int count in new[] { 8_192, 8_192, 5, 20_001, 7 })
        {
            double[] x = Enumerable.Range(0, count).Select(i => 5000000 + i * .123456789).ToArray();
            double[] y = Enumerable.Range(0, count).Select(i => -3000000 + Math.Sin(i) * 4).ToArray();
            bool closed = count != 7;
            builder.Append(x, y, 0, count, closed, default);
            TransformedRingWriter.Append(expected, x, y, 0, count, 5000000, -3000000, closed, 1, false);
        }
        var geometry = builder.Build()!;
        using var actual = geometry.CreatePath(new SKPoint(5000000, 3000000), expected.FillType, default);
        Check(actual.Points.SequenceEqual(expected.Points) && actual.VerbCount == expected.VerbCount,
            "packed block boundaries retain points and ring closure");
        Check(geometry.RetainedBytes <= 8 << 20, "full allocated blocks respect budget");
        builder.Append(new[] { 0d }, new[] { 0d }, 0, 1, false, default);
        Check(builder.Build() == null, "published builder cannot mutate immutable blocks");
    }
    private static byte[] Pixels(SKPath path, float dpi, float rotation)
    {
        var frame = MapCoordinateFrame.Create(MapViewportMetrics.Create(600, 600, dpi), SKPoint.Empty, 1000, rotation);
        using var bitmap = new SKBitmap(frame.Viewport.PhysicalWidth, frame.Viewport.PhysicalHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent); canvas.SetMatrix(frame.LocalToPhysicalMatrix);
        using var fill = new SKPaint { Color = SKColors.Cyan.WithAlpha(25), IsAntialias = true };
        using var stroke = new SKPaint { Color = SKColors.Cyan.WithAlpha(200), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = .001f };
        canvas.DrawPath(path, fill); canvas.DrawPath(path, stroke);
        return bitmap.Bytes;
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
