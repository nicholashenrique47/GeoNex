using GeoNex.Services;
using SkiaSharp;
using System.Diagnostics;
using System.Text.Json;

internal static class TransformedRingContracts
{
    public static void Run()
    {
        double[] x = [10, 90, 90, 10, 10], y = [-10, -10, -90, -90, -10];
        using var legacy = new SKPath { FillType = SKPathFillType.Winding };
        using var bulk = new SKPath { FillType = SKPathFillType.Winding };
        Legacy(legacy, x, y);
        TransformedRingWriter.Append(bulk, x, y, 0, x.Length, 0, 0, true, 1, false);
        double[] hx = [30, 30, 70, 70, 30], hy = [-30, -70, -70, -30, -30];
        Legacy(legacy, hx, hy);
        TransformedRingWriter.Append(bulk, hx, hy, 0, hx.Length, 0, 0, true, 1, false);
        Check(Draw(legacy, 1, 0, 0).SequenceEqual(Draw(bulk, 1, 0, 0)), "exact final pixels and hole");
        int n = 10_001;
        x = new double[n]; y = new double[n];
        for (int i = 0; i < n; i++) { x[i] = 50 + 40 * Math.Cos(i * Math.Tau / (n - 1)); y[i] = -50 + 40 * Math.Sin(i * Math.Tau / (n - 1)); }
        using var preview = new SKPath();
        using var detailed = new SKPath();
        TransformedRingWriter.Append(preview, x, y, 0, n, 0, 0, true, 1, true);
        TransformedRingWriter.Append(detailed, x, y, 0, n, 0, 0, true, 1, false);
        Check(preview.PointCount < n / 10 && detailed.PointCount == n, "preview reduces dense vertices, final retains every vertex");
        Check(preview.Contains(50, 50), "preview ring remains closed");
        Check(TransformedRingWriter.SkipPreviewBorder(true, 8, 1_078_031), "complexity is not feature count");
        Check(!TransformedRingWriter.SkipPreviewBorder(false, 8, 1_078_031), "final retains border");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool caught = false;
        try { TransformedRingWriter.Append(preview, x, y, 0, n, 0, 0, true, 1, true, canceled.Token); }
        catch (OperationCanceledException) { caught = true; }
        Check(caught, "cancellation before work");
        Console.WriteLine("Transformed rings: PASS (bulk exact pixels, holes, dense preview, final vertices/border, cancellation)");
    }

    public static void Measure(string source)
    {
        var clock = Stopwatch.StartNew();
        using var stream = File.OpenRead(source);
        using var json = JsonDocument.Parse(stream);
        var rings = new List<(double[] X, double[] Y, bool LegacyScalar)>();
        int features = 0;
        // Isolated fixture preparation: RFC 7946 lon/lat to Web Mercator. Not an import/GDAL benchmark.
        if (json.RootElement.TryGetProperty("crs", out _)) throw new InvalidDataException("Fixture must be RFC 7946 without alternate CRS.");
        foreach (var feature in json.RootElement.GetProperty("features").EnumerateArray())
        {
            features++;
            var geometry = feature.GetProperty("geometry");
            var coordinates = geometry.GetProperty("coordinates");
            IEnumerable<JsonElement> polygons = geometry.GetProperty("type").GetString() switch
            {
                "Polygon" => new[] { coordinates },
                "MultiPolygon" => coordinates.EnumerateArray().ToArray(),
                _ => throw new InvalidDataException("Polygon fixture required")
            };
            var featureRings = polygons.SelectMany(p => p.EnumerateArray()).ToArray();
            bool scalar = featureRings.Sum(r => r.GetArrayLength()) > 262_144;
            foreach (var ring in featureRings)
            {
                var x = new double[ring.GetArrayLength()]; var y = new double[x.Length];
                int i = 0;
                foreach (var point in ring.EnumerateArray())
                {
                    double lon = point[0].GetDouble(), lat = point[1].GetDouble();
                    if (Math.Abs(lon) > 180 || Math.Abs(lat) >= 85.051129) throw new InvalidDataException("Unsupported fixture coordinates");
                    x[i] = 6378137 * lon * Math.PI / 180;
                    y[i++] = 6378137 * Math.Log(Math.Tan(Math.PI / 4 + lat * Math.PI / 360));
                }
                rings.Add((x, y, scalar));
            }
        }
        Console.WriteLine($"fixture={Path.GetFileName(source)} features={features} rings={rings.Count} vertices={rings.Sum(r => r.X.Length)} prepare_ms={clock.Elapsed.TotalMilliseconds:F1}");
        float scale = 1;
        using var baseline = Build(false, false);
        scale = Math.Min(1920f / baseline.Bounds.Width, 1080f / baseline.Bounds.Height) * .8f;
        foreach (bool preview in new[] { false, true })
        {
            var times = new List<double>();
            for (int pass = 0; pass < 4; pass++)
            {
                clock.Restart(); using var path = Build(true, preview); clock.Stop();
                if (pass > 0) times.Add(clock.Elapsed.TotalMilliseconds);
                if (pass == 3)
                {
                    clock.Restart(); var bytes = Draw(path, scale, baseline.Bounds.MidX, baseline.Bounds.MidY, !preview); clock.Stop();
                    if (!preview) Check(bytes.SequenceEqual(Draw(baseline, scale, baseline.Bounds.MidX, baseline.Bounds.MidY)), "real fixture final pixels exact");
                    Console.WriteLine($"candidate preview={preview} build_median_ms={times.Order().ElementAt(1):F2} draw_ms={clock.Elapsed.TotalMilliseconds:F2} points={path.PointCount}");
                }
            }
        }
        var oldTimes = new List<double>();
        for (int pass = 0; pass < 4; pass++) { clock.Restart(); using var old = Build(false, false); clock.Stop(); if (pass > 0) oldTimes.Add(clock.Elapsed.TotalMilliseconds); }
        Console.WriteLine($"baseline build_median_ms={oldTimes.Order().ElementAt(1):F2}; final pixels PASS; isolated CPU 1920x1080, no WebView/basemap/GDAL/import timing");

        using var cache = new RenderPathCache(RenderPathCache.EstimateBytes(baseline) * 2);
        var coverage = new SKRect(baseline.Bounds.MidX - 1100 / scale, baseline.Bounds.MidY - 650 / scale,
            baseline.Bounds.MidX + 1100 / scale, baseline.Bounds.MidY + 650 / scale);
        cache.Store(1, baseline, coverage, scale, false, false, scaleIndependent: true);
        var lookupTimes = new List<double>();
        for (int frame = 0; frame < 30; frame++)
        {
            float frameScale = scale * (1 + frame * .04f);
            var viewport = new SKRect(baseline.Bounds.MidX - 960 / frameScale, baseline.Bounds.MidY - 540 / frameScale,
                baseline.Bounds.MidX + 960 / frameScale, baseline.Bounds.MidY + 540 / frameScale);
            clock.Restart();
            bool hit = cache.TryGet(1, viewport, frameScale, false, false, out var snapshot);
            clock.Stop();
            lookupTimes.Add(clock.Elapsed.TotalMilliseconds);
            using (snapshot)
            {
                Check(hit, "real fixture exact path reused during zoom");
                if (frame is 0 or 15 or 29)
                    Check(Draw(snapshot!, frameScale, baseline.Bounds.MidX, baseline.Bounds.MidY)
                        .SequenceEqual(Draw(baseline, frameScale, baseline.Bounds.MidX, baseline.Bounds.MidY)),
                        "real fixture cache pixels at multiple scales");
            }
        }
        lookupTimes.Sort();
        Console.WriteLine($"exact cache: 30/30 zoom hits; lookup p50_ms={lookupTimes[14]:F4} p95_ms={lookupTimes[28]:F4}; three scales pixel-exact; retained_mb={cache.RetainedBytes / 1048576d:F2}; lookup only, NOT frame latency");

        SKPath Build(bool candidate, bool preview)
        {
            var path = new SKPath { FillType = SKPathFillType.Winding };
            foreach (var ring in rings)
            {
                if (candidate && (preview || ring.LegacyScalar)) TransformedRingWriter.Append(path, ring.X, ring.Y, 0, ring.X.Length, 0, 0, true, scale, preview);
                else if (ring.LegacyScalar) Legacy(path, ring.X, ring.Y);
                else
                {
                    var points = System.Buffers.ArrayPool<SKPoint>.Shared.Rent(ring.X.Length);
                    try
                    {
                        for (int i = 0; i < ring.X.Length; i++) points[i] = new SKPoint((float)ring.X[i], -(float)ring.Y[i]);
                        path.AddPoly(points.AsSpan(0, ring.X.Length), true);
                    }
                    finally { System.Buffers.ArrayPool<SKPoint>.Shared.Return(points); }
                }
            }
            return path;
        }
    }

    private static void Legacy(SKPath path, double[] x, double[] y)
    {
        for (int i = 0; i < x.Length; i++)
            if (i == 0) path.MoveTo((float)x[i], -(float)y[i]); else path.LineTo((float)x[i], -(float)y[i]);
        path.Close();
    }

    private static byte[] Draw(SKPath path, float scale, float centerX, float centerY, bool border = true)
    {
        using var bitmap = new SKBitmap(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(960, 540); canvas.Scale(scale); canvas.Translate(-centerX, -centerY);
        using var paint = new SKPaint { Color = SKColors.Blue.WithAlpha(89), IsAntialias = true };
        canvas.DrawPath(path, paint);
        if (border) { paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 1 / scale; canvas.DrawPath(path, paint); }
        return bitmap.Bytes;
    }

    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
