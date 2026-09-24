using System.Buffers;
using SkiaSharp;

namespace GeoNex.Services;

// Each worker paints complete contours against the original full-image clip.
// Only its owned region is copied. Clipping the canvas to a band changes Skia's
// antialiasing at intersecting edges, even when a small overlap is added.
internal static class ParallelPolygonPainter
{
    public static bool TryPaint(SKBitmap destination, SKPath source, SKPaint? fill, SKPaint? stroke,
        SKMatrix matrix, int workers, long spareBytes, CancellationToken token,
        Action<int, int, double>? measureBand = null, bool? vertical = null)
    {
        if (workers < 2 || source.PointCount < 8192 || (fill == null && stroke == null) ||
            source.FillType is not (SKPathFillType.Winding or SKPathFillType.EvenOdd) ||
            !Simple(fill, SKPaintStyle.Fill) || !Simple(stroke, SKPaintStyle.Stroke) ||
            matrix.SkewX != 0 || matrix.SkewY != 0 || matrix.Persp0 != 0 || matrix.Persp1 != 0 || matrix.Persp2 != 1 ||
            !float.IsFinite(matrix.ScaleX) || !float.IsFinite(matrix.ScaleY) || matrix.ScaleX <= 0 || matrix.ScaleY <= 0 ||
            !float.IsFinite(matrix.TransX) || !float.IsFinite(matrix.TransY) ||
            destination.ColorType != SKColorType.Rgba8888 || destination.AlphaType != SKAlphaType.Premul ||
            destination.RowBytes != (long)destination.Width * 4) return false;
        double join = stroke?.StrokeJoin == SKStrokeJoin.Miter ? Math.Max(1, stroke.StrokeMiter) : 1;
        double strokePixels = (stroke?.StrokeWidth ?? 0) * Math.Max(matrix.ScaleX, matrix.ScaleY);
        double guard = 2 + Math.Max(1, strokePixels * join);
        // Wide strokes use columns: removing preceding rows changed coverage
        // on clipped parcels. Keep the faster row partition for pixel-wide lines.
        // Analytic AA can still differ by one 8-bit level after contour filtering.
        bool columns = vertical ?? (strokePixels > 1);
        int dimension = columns ? destination.Width : destination.Height;
        if (!double.IsFinite(guard) || guard >= dimension / 4d) return false;
        token.ThrowIfCancellationRequested();
        long imageBytes = (long)destination.RowBytes * destination.Height;
        // Cover the peak of either the pooled vertex histogram input (<=16N)
        // or the growing contour buffer (<=24N); they never coexist.
        long scratch = 24L * source.PointCount + 4096;
        for (int bands = workers >= 4 ? 4 : 2; bands >= 2; bands /= 2)
        {
            long pathBudget = spareBytes - imageBytes * bands - scratch;
            if (pathBudget <= 0) continue;
            SKPath[]? paths = null;
            var images = new SKBitmap?[bands];
            var fills = new SKPaint?[bands]; var strokes = new SKPaint?[bands];
            try
            {
                var cuts = Boundaries(source, matrix, dimension, bands, columns, guard, token);
                paths = Partition(source, matrix, bands, guard, pathBudget, token, columns, cuts);
                if (paths == null && columns)
                {
                    // Quantiles can duplicate more spanning contours. Preserve
                    // the prior equal-width path before downgrading CPU/RAM.
                    var uniform = EqualBands(dimension, bands);
                    if (!cuts.AsSpan().SequenceEqual(uniform))
                    {
                        cuts = uniform;
                        paths = Partition(source, matrix, bands, guard, pathBudget, token, columns, cuts);
                    }
                }
                if (paths == null) continue;
                for (int i = 0; i < bands; i++)
                {
                    token.ThrowIfCancellationRequested();
                    images[i] = new SKBitmap();
                    if (!images[i]!.TryAllocPixels(destination.Info)) throw new OutOfMemoryException();
                    fills[i] = fill?.Clone(); strokes[i] = stroke?.Clone();
                }
                Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = bands, CancellationToken = token }, i =>
                {
                    long started = measureBand == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
                    using var canvas = new SKCanvas(images[i]!);
                    // SrcOver and the supported paints have no neighbor reads.
                    // Clear only the owned pixels with memset; this avoids a
                    // Skia clip/save/restore while preserving the original
                    // full-image clip for both DrawPath calls.
                    ClearRegion(images[i]!, cuts[i], cuts[i + 1], columns);
                    canvas.SetMatrix(matrix);
                    if (fills[i] != null) canvas.DrawPath(paths[i], fills[i]);
                    token.ThrowIfCancellationRequested();
                    if (strokes[i] != null) canvas.DrawPath(paths[i], strokes[i]);
                    // Raster SKCanvas writes directly to its bitmap; defer all
                    // observation until the workers have completed.
                    measureBand?.Invoke(i, paths[i].PointCount, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                });
                token.ThrowIfCancellationRequested();
                // No destination mutation before every worker has succeeded.
                for (int i = 0; i < bands; i++)
                    CopyRegion(images[i]!, destination, cuts[i], cuts[i + 1], columns);
                return true;
            }
            catch (OutOfMemoryException) { return false; }
            catch (AggregateException e) when (e.Flatten().InnerExceptions.All(x => x is OutOfMemoryException)) { return false; }
            finally
            {
                if (paths != null) foreach (var path in paths) path.Dispose();
                foreach (var image in images) image?.Dispose();
                foreach (var paint in fills) paint?.Dispose();
                foreach (var paint in strokes) paint?.Dispose();
            }
        }
        return false;
    }

    private static bool Simple(SKPaint? paint, SKPaintStyle style) => paint == null ||
        (paint.Style == style && paint.BlendMode == SKBlendMode.SrcOver && !paint.IsDither &&
         paint.Shader == null && paint.ColorFilter == null && paint.ImageFilter == null &&
         paint.MaskFilter == null && paint.PathEffect == null && float.IsFinite(paint.StrokeWidth) && paint.StrokeWidth >= 0);

    private static int[] Boundaries(SKPath source, SKMatrix matrix, int dimension, int bands,
        bool vertical, double guard, CancellationToken token)
    {
        var cuts = EqualBands(dimension, bands);
        if (!vertical) return cuts;
        // O(points + buckets). Quantiles balance dense parcel clusters without
        // sorting vertices or changing the number of workers/full-size bitmaps.
        // Keep the existing row partition for thin strokes.
        const int buckets = 256;
        Span<int> histogram = stackalloc int[buckets];
        histogram.Clear();
        int total = 0;
        int count = source.PointCount;
        var points = ArrayPool<SKPoint>.Shared.Rent(count);
        try
        {
            source.GetPoints(points, count);
            for (int i = 0; i < count; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!float.IsFinite(points[i].X) || !float.IsFinite(points[i].Y)) return cuts;
                double x = points[i].X * (double)matrix.ScaleX + matrix.TransX;
                if (x < 0 || x >= dimension) continue;
                histogram[Math.Min(buckets - 1, (int)(x * buckets / dimension))]++;
                total++;
            }
        }
        finally { ArrayPool<SKPoint>.Shared.Return(points); }
        if (total == 0) return cuts;
        int cumulative = 0, cut = 1;
        var balanced = (int[])cuts.Clone();
        for (int i = 0; i < buckets && cut < bands; i++)
        {
            cumulative += histogram[i];
            while (cut < bands && cumulative >= (long)total * cut / bands)
                balanced[cut++] = (int)((long)dimension * (i + 1) / buckets);
        }
        for (int i = 0; i < bands; i++)
            if (balanced[i + 1] - balanced[i] <= 2 * guard) return cuts;
        return balanced;
    }

    private static int[] EqualBands(int dimension, int bands)
    {
        var cuts = new int[bands + 1];
        for (int i = 0; i <= bands; i++) cuts[i] = (int)((long)dimension * i / bands);
        return cuts;
    }

    private static SKPath[]? Partition(SKPath source, SKMatrix matrix, int bands,
        double guard, long budget, CancellationToken token, bool vertical, int[] cuts)
    {
        var paths = new SKPath[bands];
        SKPoint[]? buffer = null;
        var verbPoints = new SKPoint[4];
        int count = 0, verbs = 0;
        var pointCounts = new int[bands];
        double maximumBandPoints = source.PointCount * .85;
        float scale = vertical ? matrix.ScaleX : matrix.ScaleY;
        float translation = vertical ? matrix.TransX : matrix.TransY;
        float low = 0, high = 0;
        long bytes = 512L * bands;
        bool success = false;
        try
        {
            for (int i = 0; i < bands; i++) paths[i] = new SKPath { FillType = source.FillType };
            buffer = ArrayPool<SKPoint>.Shared.Rent(256);
            using var iterator = source.CreateRawIterator();
            while (true)
            {
                if ((verbs++ & 4095) == 0) token.ThrowIfCancellationRequested();
                var verb = iterator.Next(verbPoints);
                if (verb == SKPathVerb.Done) break;
                if (verb == SKPathVerb.Close)
                {
                    if (count == 0) continue;
                    double top = low * (double)scale + translation - guard;
                    double bottom = high * (double)scale + translation + guard;
                    for (int i = 0; i < bands; i++)
                    {
                        if (bottom < cuts[i] || top > cuts[i + 1]) continue;
                        bytes += count * 24L + 16;
                        if (bytes > budget) return null;
                        paths[i].AddPoly(buffer.AsSpan(0, count), true);
                        // A band retaining almost every vertex cannot amortize
                        // partitioning or repeated painting on another CPU core.
                        pointCounts[i] += count;
                        if (pointCounts[i] > maximumBandPoints) return null;
                    }
                    count = 0;
                    continue;
                }
                if (verb is not (SKPathVerb.Move or SKPathVerb.Line) || (verb == SKPathVerb.Move && count != 0)) return null;
                var point = verbPoints[verb == SKPathVerb.Move ? 0 : 1];
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return null;
                if (count == buffer.Length)
                {
                    var grown = ArrayPool<SKPoint>.Shared.Rent(checked(count * 2));
                    buffer.AsSpan(0, count).CopyTo(grown);
                    ArrayPool<SKPoint>.Shared.Return(buffer); buffer = grown;
                }
                buffer[count++] = point;
                float coordinate = vertical ? point.X : point.Y;
                if (count == 1) { low = high = coordinate; }
                else { low = Math.Min(low, coordinate); high = Math.Max(high, coordinate); }
            }
            if (count != 0) return null;
            token.ThrowIfCancellationRequested();
            success = true;
            return paths;
        }
        finally
        {
            if (buffer != null) ArrayPool<SKPoint>.Shared.Return(buffer);
            if (!success) foreach (var path in paths) path?.Dispose();
        }
    }

    private static unsafe void CopyRegion(SKBitmap source, SKBitmap target, int start, int end, bool vertical)
    {
        byte* sourcePixels = (byte*)source.GetPixels(), targetPixels = (byte*)target.GetPixels();
        int top = vertical ? 0 : start, bottom = vertical ? target.Height : end;
        int offset = vertical ? start * 4 : 0, bytes = vertical ? (end - start) * 4 : target.RowBytes;
        for (int y = top; y < bottom; y++)
                Buffer.MemoryCopy(sourcePixels + (long)y * source.RowBytes + offset,
                targetPixels + (long)y * target.RowBytes + offset, bytes, bytes);
    }

    private static unsafe void ClearRegion(SKBitmap bitmap, int start, int end, bool vertical)
    {
        byte* pixels = (byte*)bitmap.GetPixels();
        int top = vertical ? 0 : start;
        int bottom = vertical ? bitmap.Height : end;
        int offset = vertical ? start * 4 : 0;
        int bytes = vertical ? (end - start) * 4 : bitmap.RowBytes;
        for (int y = top; y < bottom; y++)
            new Span<byte>(pixels + (long)y * bitmap.RowBytes + offset, bytes).Clear();
    }
}
