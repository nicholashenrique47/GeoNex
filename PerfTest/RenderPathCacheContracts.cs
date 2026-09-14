using GeoNex.Services;
using SkiaSharp;

internal static class RenderPathCacheContracts
{
    public static void Run()
    {
        using var shape = new SKPath { FillType = SKPathFillType.Winding };
        shape.AddRect(new SKRect(10, 10, 90, 90));
        shape.AddRect(new SKRect(30, 30, 70, 70), SKPathDirection.CounterClockwise);
        // Deliberate duplicate: neither winding multiplicity nor alpha may be simplified by caching.
        shape.AddRect(new SKRect(10, 10, 90, 90));
        var coverage = new SKRect(0, 0, 100, 100);
        long cost = RenderPathCache.EstimateBytes(shape);
        using (var precise = new RenderPathCache(cost * 2))
        {
            var origin = new SKPoint(-5400000, 3000000);
            precise.Store(1, shape, coverage, 2, false, false, true, origin);
            Check(!precise.TryGet(1, coverage, 2, false, false, out _), "world paths cannot borrow precise paths");
            Check(!precise.TryGet(1, coverage, 2, false, false, out _, new SKPoint(origin.X + .5f, origin.Y)), "origin change rebuilds");
            Check(precise.TryGet(1, coverage, 3, false, false, out var hit, origin), "exact precise path reused across zoom");
            using (hit) Check(Pixels(shape).SequenceEqual(Pixels(hit!)), "precise cache preserves every pixel");
            Check(!precise.TryGet(1, coverage, 3, true, false, out _, origin), "precise preview/final isolation");
            Check(!precise.TryGet(1, new SKRect(-1, 0, 100, 100), 3, false, false, out _, origin), "precise coverage miss");
            precise.Invalidate(1);
            Check(precise.RetainedBytes == 0, "precise paths share eviction/invalidation budget");
        }
        using var cache = new RenderPathCache(cost * 2);
        cache.Store(1, shape, coverage, 2, false, false);
        cache.Store(1, shape, coverage, 2, true, true);
        Check(cache.RetainedBytes == cost * 2, "independent quality slots");
        using var final = Read(cache, 1, coverage, false);
        using var preview = Read(cache, 1, coverage, true, true);
        Check(!cache.TryGet(1, coverage, 2, true, false, out _), "compact identity");
        Check(!cache.TryGet(1, coverage, 2.0001f, false, false, out _), "exact LOD scale");
        Check(!cache.TryGet(1, new SKRect(-1, 0, 100, 100), 2, false, false, out _), "coverage miss");
        Check(!cache.TryGet(1, coverage, float.NaN, false, false, out _), "invalid scale");
        Check(!cache.TryGet(1, new SKRect(float.NaN, 0, 100, 100), 2, false, false, out _), "invalid viewport");
        Check(!cache.TryGet(2, coverage, 2, false, false, out _), "owner isolation");
        Check(Pixels(shape).SequenceEqual(Pixels(final)), "pixel-exact fill/border/alpha/winding");
        preview.Reset();
        using (var reread = Read(cache, 1, coverage, true, true))
            Check(reread.PointCount == shape.PointCount, "copy-on-write isolation");

        cache.Store(2, shape, coverage, 2, false, false);
        Check(!cache.TryGet(1, coverage, 2, true, true, out _), "preview evicted first");
        cache.Store(3, shape, coverage, 2, true, false);
        Check(!cache.TryGet(3, coverage, 2, true, false, out _), "preview cannot evict finals");
        using (Read(cache, 1, coverage, false)) { }
        cache.Store(3, shape, coverage, 2, false, false);
        Check(!cache.TryGet(2, coverage, 2, false, false, out _), "least recently used final evicted");
        cache.Invalidate(1);
        Check(cache.RetainedBytes == cost, "invalidation releases owner");
        Check(Pixels(shape).SequenceEqual(Pixels(final)), "invalidation leaves borrowed snapshot valid");
        cache.Dispose();
        cache.Store(1, shape, coverage, 2, false, false);
        Check(cache.RetainedBytes == 0, "disposal prevents late publication");

        using var tiny = new RenderPathCache(cost - 1);
        tiny.Store(1, shape, coverage, 2, false, false);
        Check(tiny.RetainedBytes == 0, "oversized path renders without retention");
        using var disabled = new RenderPathCache(0);
        disabled.Store(1, shape, coverage, 2, false, false);
        Check(disabled.RetainedBytes == 0, "zero budget disables retention");
        using var invalidation = new RenderPathCache(cost * 2);
        invalidation.Store(1, shape, coverage, 2, false, false);
        invalidation.Store(1, shape, coverage, 2, true, false);
        invalidation.Invalidate(1);
        Check(invalidation.RetainedBytes == 0, "both quality slots invalidated");
        invalidation.Store(1, shape, coverage, 2, false, false);
        invalidation.Store(1, shape, coverage, float.PositiveInfinity, false, false);
        Check(invalidation.RetainedBytes == 0, "invalid replacement removes stale entry");
        using var reuse = new RenderPathCache(cost * 2);
        int builds = 0;
        for (int i = 0; i < 100; i++)
        {
            bool interactive = (i & 1) == 0;
            if (reuse.TryGet(1, coverage, 2, interactive, false, out var reused)) reused!.Dispose();
            else { builds++; reuse.Store(1, shape, coverage, 2, interactive, false); }
        }
        Check(builds == 2, "100 alternating quality requests only build geometry twice");
        using var exact = new RenderPathCache(cost * 4);
        exact.Store(1, shape, coverage, 2, false, false, scaleIndependent: true);
        for (int i = 0; i < 30; i++)
        {
            Check(exact.TryGet(1, coverage, 2 + i * .1f, false, false, out var scaled), "exact geometry reused across zooms");
            using (scaled) Check(Pixels(shape).SequenceEqual(Pixels(scaled!)), "scale reuse keeps exact winding/alpha");
        }
        Check(!exact.TryGet(1, new SKRect(-1, 0, 100, 100), 3, false, false, out _), "exact geometry still enforces coverage");
        Check(!exact.TryGet(1, coverage, 9, false, false, out _), "large scale jump rebuilds bounded query");
        exact.Store(2, shape, coverage, 2, true, false, scaleIndependent: true);
        Check(!exact.TryGet(2, coverage, 3, true, false, out _), "preview cannot opt into scale-independent reuse");
        exact.Store(3, shape, coverage, 2, false, true, scaleIndependent: true);
        Check(!exact.TryGet(3, coverage, 3, false, true, out _), "compact geometry cannot opt into scale-independent reuse");
        long dynamicBudget = cost * 2;
        using var adaptive = new RenderPathCache(budgetProvider: () => dynamicBudget);
        adaptive.Store(1, shape, coverage, 2, false, false);
        adaptive.Store(1, shape, coverage, 2, true, false);
        using var borrowed = Read(adaptive, 1, coverage, false);
        dynamicBudget = cost;
        using (Read(adaptive, 1, coverage, false)) { }
        Check(adaptive.RetainedBytes == cost && adaptive.BudgetBytes == cost, "pressure trims preview first");
        dynamicBudget = 0;
        Check(!adaptive.TryGet(1, coverage, 2, false, false, out _), "critical pressure disables retention");
        Check(adaptive.RetainedBytes == 0 && Pixels(borrowed).SequenceEqual(Pixels(shape)), "pressure preserves active snapshot");
        dynamicBudget = cost * 2;
        adaptive.Store(1, shape, coverage, 2, false, false);
        using (Read(adaptive, 1, coverage, false)) { }
        using var concurrent = new RenderPathCache(cost * 4);
        Parallel.For(0, 200, i =>
        {
            using var local = new SKPath();
            local.AddRect(coverage);
            concurrent.Store(i % 8, local, coverage, 2, (i & 1) == 0, false);
            if (concurrent.TryGet(i % 8, coverage, 2, (i & 1) == 0, false, out var copy))
                using (copy) Check(copy!.PointCount == 4, "concurrent snapshot");
            concurrent.Invalidate(i % 8);
        });
        Check(concurrent.RetainedBytes <= cost * 4, "shared budget under contention");
        Console.WriteLine("Render path cache: PASS (quality isolation, pixels, LRU, adaptive budget, COW, invalidation, concurrency, 30 exact zoom reuses)");
    }

    private static SKPath Read(RenderPathCache cache, long owner, SKRect coverage, bool interactive, bool compact = false)
    {
        Check(cache.TryGet(owner, coverage, 2, interactive, compact, out var result), "expected cache hit");
        return result!;
    }

    private static byte[] Pixels(SKPath path)
    {
        using var bitmap = new SKBitmap(120, 120, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var fill = new SKPaint { Color = SKColors.Blue.WithAlpha(89), IsAntialias = true };
        using var stroke = new SKPaint { Color = SKColors.Red.WithAlpha(127), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, stroke);
        return bitmap.Bytes;
    }

    private static void Check(bool valid, string message)
    { if (!valid) throw new InvalidOperationException(message); }
}
