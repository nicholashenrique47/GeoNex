using GeoNex.Services;
using SkiaSharp;

internal static class ParallelPolygonContracts
{
    public static void Run()
    {
        using var path = new SKPath();
        var random = new Random(418);
        for (int i = 0; i < 2400; i++)
        {
            float x = random.NextSingle() * 660 - 10, y = random.NextSingle() * 500 - 10;
            path.AddPoly(new[] { new SKPoint(x, y), new SKPoint(x + 18.31f, y + 11.93f),
                new SKPoint(x + 7.77f, y + 21.61f), new SKPoint(x - 3.81f, y + 8.97f) }, true);
            if (i % 19 == 0) path.AddRect(new SKRect(x, y, x + 4.13f, y + 4.83f), SKPathDirection.CounterClockwise);
        }
        using var fill = new SKPaint { Color = new SKColor(13, 169, 230, 25), IsAntialias = true };
        using var stroke = new SKPaint { Color = new SKColor(19, 161, 228, 200), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round };
        foreach (var type in new[] { SKPathFillType.Winding, SKPathFillType.EvenOdd })
        foreach (float dpi in new[] { 1f, 1.25f, 2f })
        foreach (float lineWidth in new[] { 0f, 1f, 3.5f })
        foreach (int components in new[] { 0, 1, 2 })
        {
            path.FillType = type; stroke.StrokeWidth = lineWidth;
            stroke.StrokeJoin = lineWidth > 1 ? SKStrokeJoin.Miter : SKStrokeJoin.Round;
            stroke.StrokeMiter = 10;
            using var expected = new SKBitmap((int)(640 * dpi), (int)(480 * dpi), SKColorType.Rgba8888, SKAlphaType.Premul);
            var matrix = SKMatrix.CreateScale(dpi, dpi);
            matrix.TransX = .375f; matrix.TransY = -.1875f;
            SKPaint? activeFill = components == 1 ? null : fill;
            SKPaint? activeStroke = components == 2 ? null : stroke;
            using (var canvas = new SKCanvas(expected))
            {
                canvas.Clear(SKColors.Transparent); canvas.SetMatrix(matrix);
                if (activeFill != null) canvas.DrawPath(path, activeFill);
                if (activeStroke != null) canvas.DrawPath(path, activeStroke);
            }
            foreach (int workers in new[] { 2, 4 })
            foreach (bool vertical in new[] { false, true })
            {
                using var actual = new SKBitmap(expected.Info);
                Check(ParallelPolygonPainter.TryPaint(actual, path, activeFill, activeStroke, matrix, workers, 64L << 20, default,
                    vertical: vertical), "parallel fixture declined");
                byte[] a = expected.Bytes, b = actual.Bytes;
                if (!a.SequenceEqual(b))
                    throw new InvalidOperationException($"Parallel pixels changed: fill={type} dpi={dpi} width={lineWidth} components={components} workers={workers} vertical={vertical} max={a.Zip(b, (x,y) => Math.Abs(x-y)).Max()}");
            }
        }
        using var image = new SKBitmap(640, 480, SKColorType.Rgba8888, SKAlphaType.Premul);
        foreach (var size in new[] { (Width: 641, Height: 479), (Width: 479, Height: 641) })
        {
            using var expected = new SKBitmap(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var actual = new SKBitmap(expected.Info);
            using (var canvas = new SKCanvas(expected))
            { canvas.Clear(SKColors.Transparent); canvas.DrawPath(path, fill); canvas.DrawPath(path, stroke); }
            Check(ParallelPolygonPainter.TryPaint(actual, path, fill, stroke, SKMatrix.Identity, 4, 64L << 20, default), "odd-size automatic partition declined");
            Check(expected.Bytes.SequenceEqual(actual.Bytes), "odd portrait/landscape region copy changed pixels");
        }
        image.Erase(SKColors.Magenta);
        byte[] unchanged = image.Bytes;
        Check(!ParallelPolygonPainter.TryPaint(image, path, fill, stroke, SKMatrix.Identity, 1, 64L << 20, default), "single worker fallback");
        Check(!ParallelPolygonPainter.TryPaint(image, path, fill, stroke, SKMatrix.Identity, 4, 0, default), "RAM fallback");
        Check(!ParallelPolygonPainter.TryPaint(image, path, fill, stroke, SKMatrix.CreateRotationDegrees(17), 4, 64L << 20, default), "rotation fallback");
        using var unsupported = new SKPath(path); unsupported.AddCircle(1, 1, 3);
        Check(!ParallelPolygonPainter.TryPaint(image, unsupported, fill, stroke, SKMatrix.Identity, 4, 64L << 20, default), "curve fallback");
        using var open = new SKPath(path); open.MoveTo(1, 1); open.LineTo(50, 40);
        Check(!ParallelPolygonPainter.TryPaint(image, open, fill, stroke, SKMatrix.Identity, 4, 64L << 20, default), "open contour fallback");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { ParallelPolygonPainter.TryPaint(image, path, fill, stroke, SKMatrix.Identity, 4, 64L << 20, cancellation.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        using var spanning = new SKPath();
        for (int i = 0; i < 2400; i++) spanning.AddRect(new SKRect(-10, -10, 650, 490));
        Check(!ParallelPolygonPainter.TryPaint(image, spanning, fill, stroke, SKMatrix.Identity, 4, 64L << 20, default), "spanning contours avoid duplicated work");
        Check(unchanged.SequenceEqual(image.Bytes), "declined or cancelled painting modified destination");
        using var midPaintCancellation = new CancellationTokenSource();
        try
        {
            ParallelPolygonPainter.TryPaint(image, path, fill, stroke, SKMatrix.Identity, 4, 64L << 20,
                midPaintCancellation.Token, (_, _, _) => midPaintCancellation.Cancel());
            throw new InvalidOperationException("Mid-paint cancellation ignored");
        }
        catch (OperationCanceledException) { }
        Check(unchanged.SequenceEqual(image.Bytes), "mid-paint cancellation published partial pixels");
        int completedBands = 0;
        long twoWorkerBudget = 3L * image.RowBytes * image.Height + 24L * path.PointCount + 4096;
        Check(ParallelPolygonPainter.TryPaint(image, path, fill, stroke, SKMatrix.Identity, 4, twoWorkerBudget,
            default, (_, _, _) => Interlocked.Increment(ref completedBands)), "two-worker RAM downgrade declined");
        Check(completedBands == 2, "RAM downgrade did not use exactly two workers");
        CheckBalancedParcels();
        CheckClippedParcels();
        Console.WriteLine("Parallel polygons: PASS (exact pixels, rows/columns, 2/4 workers, 3 DPIs, odd portrait/landscape, winding/holes, hairline/miter, fill/border, resource fallbacks, cancellation)");
    }

    private static void CheckBalancedParcels()
    {
        using var path = new SKPath();
        var random = new Random(923);
        for (int i = 0; i < 3200; i++)
        {
            float x = i < 2400 ? random.NextSingle() * 150 : 150 + random.NextSingle() * 480;
            float y = random.NextSingle() * 470;
            path.AddRect(new SKRect(x, y, x + 2.31f, y + 3.73f));
        }
        using var fill = new SKPaint { Color = new SKColor(56, 189, 248, 89), IsAntialias = true };
        using var stroke = new SKPaint { Color = new SKColor(14, 165, 233, 89), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round, StrokeWidth = 2 };
        using var expected = new SKBitmap(641, 479, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var actual = new SKBitmap(expected.Info);
        using (var canvas = new SKCanvas(expected))
        { canvas.Clear(SKColors.Transparent); canvas.DrawPath(path, fill); canvas.DrawPath(path, stroke); }
        var counts = new int[4];
        Check(ParallelPolygonPainter.TryPaint(actual, path, fill, stroke, SKMatrix.Identity, 4, 64L << 20,
            default, (band, count, _) => counts[band] = count), "clustered parcel fixture declined");
        Check(counts.Max() < path.PointCount * .4, "dense cluster was not distributed among workers");
        Check(expected.Bytes.SequenceEqual(actual.Bytes), "balanced clustered parcels changed pixels");
        long tightBudget = 4L * actual.RowBytes * actual.Height + 24L * path.PointCount + 4096 +
            (24L * path.PointCount + 3200 * 16) * 108 / 100;
        int completed = 0;
        Check(ParallelPolygonPainter.TryPaint(actual, path, fill, stroke, SKMatrix.Identity, 4,
            tightBudget, default, (band, count, _) => { counts[band] = count; Interlocked.Increment(ref completed); }), "equal-width RAM fallback declined");
        Check(completed == 4, "quantile path budget unnecessarily downgraded workers");
        Check(counts.Max() > path.PointCount * .6, "fixture did not exercise equal-width fallback");
        Check(expected.Bytes.SequenceEqual(actual.Bytes), "equal-width RAM fallback changed pixels");

        using var concentrated = new SKPath();
        for (int i = 0; i < 3200; i++) concentrated.AddRect(new SKRect(10, i % 470, 10.01f, i % 470 + 2));
        actual.Erase(SKColors.Magenta);
        byte[] unchanged = actual.Bytes;
        Check(!ParallelPolygonPainter.TryPaint(actual, concentrated, fill, stroke, SKMatrix.Identity, 4,
            64L << 20, default), "degenerate quantiles should fall back");
        Check(unchanged.SequenceEqual(actual.Bytes), "degenerate quantiles mutated destination");

        using var outside = new SKPath(path);
        outside.Offset(1000, 0);
        Check(ParallelPolygonPainter.TryPaint(actual, outside, fill, stroke, SKMatrix.Identity, 4,
            64L << 20, default), "empty visible histogram declined");
        Check(actual.Bytes.All(value => value == 0), "empty bands were not completely initialized");
        Console.WriteLine("Balanced parcels: PASS (cluster distribution, exact pixels, degenerate/empty histograms)");
    }

    private static void CheckClippedParcels()
    {
        using var path = new SKPath();
        var random = new Random(230923);
        for (int i = 0; i < 3200; i++)
        {
            float x = random.NextSingle() * 1800 - 900, y = random.NextSingle() * 1500 - 750;
            float angle = random.NextSingle() * MathF.Tau;
            var along = new SKPoint(MathF.Cos(angle) * 115.71f, MathF.Sin(angle) * 115.71f);
            var across = new SKPoint(-MathF.Sin(angle) * 7.13f, MathF.Cos(angle) * 7.13f);
            var a = new SKPoint(x, y); var b = a + along; var c = b + across; var d = a + across;
            path.AddPoly(new[] { a, b, c, d }, true);
        }
        using var fill = new SKPaint { Color = SKColor.Parse("#38bdf8").WithAlpha(89), IsAntialias = true };
        using var stroke = new SKPaint { Color = SKColor.Parse("#0ea5e9").WithAlpha(89), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round };
        foreach (var size in new[] { (Width: 643, Height: 481), (Width: 481, Height: 643) })
        foreach (float width in new[] { 0f, 1f, 1.005f, 2f, 3.5f })
        foreach (int workers in new[] { 2, 4 })
        {
            var matrix = SKMatrix.CreateScale(.5f, .5f);
            matrix.TransX = size.Width / 2f + .375f; matrix.TransY = size.Height / 2f - .1875f;
            stroke.StrokeWidth = width * 2;
            using var expected = new SKBitmap(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var actual = new SKBitmap(expected.Info);
            using (var canvas = new SKCanvas(expected))
            { canvas.Clear(SKColors.Transparent); canvas.SetMatrix(matrix); canvas.DrawPath(path, fill); canvas.DrawPath(path, stroke); }
            Check(ParallelPolygonPainter.TryPaint(actual, path, fill, stroke, matrix, workers, 64L << 20, default), "clipped fixture declined");
            byte[] a = expected.Bytes, b = actual.Bytes;
            int maximum = 0;
            for (int i = 0; i < a.Length; i++) maximum = Math.Max(maximum, Math.Abs(a[i] - b[i]));
            Check(maximum <= 1, $"Clipped parcels changed: {size} width={width} workers={workers} max={maximum}");
        }
        Console.WriteLine("Clipped parcels: PASS (synthetic, portrait/landscape, 2/4 workers, physical widths 0/1/1.005/2/3.5, RGBA tolerance <= 1/255)");
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
