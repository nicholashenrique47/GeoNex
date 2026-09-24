using System.Diagnostics;
using System.Runtime.InteropServices;
using GeoNex.Services;
using SkiaSharp;

internal static class CapturedPolygonMetrics
{
    // A diagnostic capture of closed, linear SHP contours from ProductionMapMetrics.
    // These files are local benchmark artifacts, not an interchange GIS format.
    public static void Run(string file, int width, int height, float physicalScale, float strokeWidth)
    {
        using var path = Read(file, out int rings);
        int tolerance = Environment.GetEnvironmentVariable("GEONEX_BENCH_PIXEL_TOLERANCE") == "1" ? 1 : 0;
        int pointCount = path.PointCount;
        var viewport = MapViewportMetrics.Create(width, height, 1);
        var matrix = MapCoordinateFrame.Create(viewport, SKPoint.Empty, physicalScale).LocalToPhysicalMatrix;
        using var fill = new SKPaint { Color = SKColors.Cyan.WithAlpha(25), IsAntialias = true };
        using var stroke = new SKPaint { Color = SKColors.Cyan.WithAlpha(200), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = strokeWidth, StrokeJoin = SKStrokeJoin.Round };
        if (Environment.GetEnvironmentVariable("GEONEX_BENCH_STYLE") == "default")
        { fill.Color = SKColor.Parse("#38bdf8").WithAlpha(89); stroke.Color = SKColor.Parse("#0ea5e9").WithAlpha(89); }
        if (Environment.GetEnvironmentVariable("GEONEX_BENCH_COMPONENT") == "fill") stroke.Color = SKColors.Transparent;
        if (Environment.GetEnvironmentVariable("GEONEX_BENCH_COMPONENT") == "stroke") fill.Color = SKColors.Transparent;
        byte[] reference;
        using (var expected = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
        {
            using var canvas = new SKCanvas(expected); canvas.Clear(SKColors.Transparent); canvas.SetMatrix(matrix);
            canvas.DrawPath(path, fill); canvas.DrawPath(path, stroke); canvas.Flush();
            reference = expected.Bytes;
        }
        Console.WriteLine($"CAPTURE points={pointCount} rings={rings} size={width}x{height}");
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("GEONEX_BENCH_ROUNDS"), out int requestedRounds)
            ? Math.Clamp(requestedRounds, 3, 21) : 3;
        var samples = new Dictionary<(int Budget, bool Parallel), List<double>>();
        for (int round = 0; round < rounds; round++)
        foreach (int budgetMb in round % 2 == 0 ? new[] { 64, 256 } : new[] { 256, 64 })
        {
            var timer = Stopwatch.StartNew();
            using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            bool parallel = ParallelPolygonPainter.TryPaint(bitmap, path, fill, stroke, matrix,
                VectorRuntimeResources.Current.Workers, budgetMb * 1024L * 1024 - (long)bitmap.RowBytes * height, default,
                Environment.GetEnvironmentVariable("GEONEX_BENCH_BANDS") == "1"
                    ? (band, count, milliseconds) => Console.WriteLine(FormattableString.Invariant($"BAND index={band} points={count} ms={milliseconds:F2}")) : null,
                Environment.GetEnvironmentVariable("GEONEX_BENCH_VERTICAL") switch { "1" => true, "0" => false, _ => null });
            if (!parallel)
            {
                using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.Transparent); canvas.SetMatrix(matrix);
                canvas.DrawPath(path, fill); canvas.DrawPath(path, stroke); canvas.Flush();
            }
            timer.Stop();
            var pixels = bitmap.Bytes;
            int maximumDelta = 0;
            if (!reference.SequenceEqual(pixels))
            {
                var changed = Enumerable.Range(0, pixels.Length).Where(i => reference[i] != pixels[i]).ToArray();
                Console.WriteLine($"DIFF rgba={string.Join(',', Enumerable.Range(0,4).Select(c => changed.Count(i => i%4 == c)))}");
                Console.WriteLine($"DIFF channels={changed.Length} x={changed.Min(i => i/4%width)}..{changed.Max(i => i/4%width)} y={changed.Min(i => i/4/width)}..{changed.Max(i => i/4/width)} first={string.Join(';', changed.Take(12).Select(i => $"{i/4%width},{i/4/width}:{reference[i]}>{pixels[i]}"))}");
                maximumDelta = changed.Max(i => Math.Abs(reference[i]-pixels[i]));
                if (maximumDelta > tolerance)
                    throw new InvalidOperationException($"Captured polygon pixels changed: max={maximumDelta}, tolerance={tolerance}");
            }
            Console.WriteLine(FormattableString.Invariant($"CAPTURE round={round} budget_mb={budgetMb} parallel={parallel} paint_ms={timer.Elapsed.TotalMilliseconds:F2} max_delta={maximumDelta}"));
            if (round > 0)
            {
                var key = (budgetMb, parallel);
                if (!samples.TryGetValue(key, out var values)) samples[key] = values = new();
                values.Add(timer.Elapsed.TotalMilliseconds);
            }
        }
        foreach (var (key, values) in samples)
        {
            values.Sort();
            double median = (values[(values.Count - 1) / 2] + values[values.Count / 2]) / 2;
            Console.WriteLine(FormattableString.Invariant($"SUMMARY budget_mb={key.Budget} parallel={key.Parallel} samples={values.Count} median_ms={median:F2} min_ms={values[0]:F2} max_ms={values[^1]:F2}"));
        }
    }

    public static void Save(SKPath path, string file)
    {
        using var writer = new BinaryWriter(File.Create(file));
        writer.Write(0x50584E47); writer.Write(1); writer.Write((int)path.FillType); writer.Write(path.PointCount);
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        while (true)
        {
            var verb = iterator.Next(points); writer.Write((byte)verb);
            if (verb == SKPathVerb.Done) break;
            if (verb == SKPathVerb.Close) continue;
            if (verb is not (SKPathVerb.Move or SKPathVerb.Line)) throw new InvalidDataException("Capture requires linear geometry");
            var point = points[verb == SKPathVerb.Move ? 0 : 1];
            writer.Write(point.X); writer.Write(point.Y);
        }
    }

    internal static SKPath Read(string file, out int rings)
    {
        rings = 0;
        var length = new FileInfo(file).Length;
        if (length <= 0 || length > 16 * 1024 * 1024) throw new InvalidDataException("Invalid capture size");
        if (file.EndsWith(".gpath", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new BinaryReader(File.OpenRead(file));
            if (reader.ReadInt32() != 0x50584E47 || reader.ReadInt32() != 1) throw new InvalidDataException("Invalid path header");
            var type = (SKPathFillType)reader.ReadInt32(); int expectedPoints = reader.ReadInt32();
            var exact = new SKPath { FillType = type };
            try
            {
                while (true)
                {
                    var verb = (SKPathVerb)reader.ReadByte();
                    if (verb == SKPathVerb.Done) break;
                    if (verb == SKPathVerb.Close) { exact.Close(); rings++; continue; }
                    if (verb is not (SKPathVerb.Move or SKPathVerb.Line)) throw new InvalidDataException("Invalid path verb");
                    float x = reader.ReadSingle(), y = reader.ReadSingle();
                    if (!float.IsFinite(x) || !float.IsFinite(y)) throw new InvalidDataException("Invalid vertex");
                    if (verb == SKPathVerb.Move) exact.MoveTo(x, y); else exact.LineTo(x, y);
                }
                if (exact.PointCount != expectedPoints || reader.BaseStream.Position != length) throw new InvalidDataException("Capture mismatch");
                return exact;
            }
            catch { exact.Dispose(); throw; }
        }
        if (length % 8 != 0) throw new InvalidDataException("Invalid XY size");
        var points = MemoryMarshal.Cast<byte, SKPoint>(File.ReadAllBytes(file)).ToArray();
        var path = new SKPath { FillType = SKPathFillType.Winding };
        int start = 0;
        try
        {
            for (int i = 0; i < points.Length; i++)
            {
                if (!float.IsFinite(points[i].X) || !float.IsFinite(points[i].Y)) throw new InvalidDataException("Invalid XY vertex");
                if (i - start >= 2 && points[i] == points[start])
                { path.AddPoly(points.AsSpan(start, i - start + 1), true); start = i + 1; rings++; }
            }
            if (start != points.Length) throw new InvalidDataException("Unclosed contour in XY capture");
            return path;
        }
        catch { path.Dispose(); throw; }
    }
}
