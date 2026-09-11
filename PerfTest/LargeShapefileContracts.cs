using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using GeoNex.Services;
using SkiaSharp;

internal static unsafe class LargeShapefileContracts
{
    public static void Run()
    {
        byte[] shape = Shape();
        byte[] file = new byte[100 + shape.Length * 63];
        long[] offsets = new long[63];
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = 100 + i * shape.Length;
            shape.CopyTo(file, (int)offsets[i]);
        }
        fixed (byte* data = file)
        {
            var representatives = ShapefileRenderCatalog.Build(data, file.Length, offsets, minimumFeatures: 1);
            Require(representatives is [0], "exact records share geometry without changing offsets");
            Require(ShapefileRenderCatalog.Build(data, file.Length, offsets, minimumFeatures: 1,
                maximumUniqueFeatures: 0) == null, "bounded catalog falls back");
            long[] invalid = [file.Length - 3];
            Require(ShapefileRenderCatalog.Build(data, file.Length, invalid, minimumFeatures: 1) == null,
                "truncated offset falls back");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            bool canceled = false;
            try { ShapefileRenderCatalog.Build(data, file.Length, offsets, cancellation.Token, 1); }
            catch (OperationCanceledException) { canceled = true; }
            Require(canceled, "catalog cancellation");
            // Reverse one exterior: removing winding multiplicity would change overlap.
            int xy = (int)offsets[^1] + 56;
            file.AsSpan(xy + 16, 16).CopyTo(file.AsSpan(xy + 80, 16));
            file.AsSpan(xy + 48, 16).CopyTo(file.AsSpan(xy + 16, 16));
            file.AsSpan(xy + 80, 16).CopyTo(file.AsSpan(xy + 48, 16));
            Require(ShapefileRenderCatalog.Build(data, file.Length, offsets, minimumFeatures: 1) is { Length: 2 },
                "different ring winding is never treated as identical geometry");
        }
        foreach (float dpi in new[] { 1f, 1.25f, 1.5f, 2f, 4f })
        {
            int padding = NavigationFramePolicy.Padding(800, 600, dpi);
            var viewport = MapViewportMetrics.Create(800 + 2 * padding, 600 + 2 * padding, dpi);
            var source = MapCoordinateFrame.Create(viewport, new SKPoint(123, -456), 2);
            var rebased = NavigationFramePolicy.Rebase(source, 2,
                new MapCameraState(20, -30, 4), new MapCameraState(60, -40, 8));
            var expectedRebase = MapCoordinateFrame.Create(viewport, new SKPoint(118, -461), 4);
            SKPoint sample = new(127, -452);
            Require(Math.Abs(rebased.LocalToPhysical(sample).X - expectedRebase.LocalToPhysical(sample).X) < .01f &&
                Math.Abs(rebased.LocalToPhysical(sample).Y - expectedRebase.LocalToPhysical(sample).Y) < .01f,
                "preview camera is independent of a canceled in-flight render");
            foreach (float rotation in new[] { 0f, 17f, -33f })
            {
                var target = MapCoordinateFrame.Create(viewport, new SKPoint(126, -458), 2.1f, rotation);
                if (NavigationFramePolicy.TryReuse(source, target, 800, 600, out var matrix))
                {
                    var local = new SKPoint(128, -466);
                    var actual = matrix.MapPoint(source.LocalToPhysical(local));
                    var expected = target.LocalToPhysical(local);
                    Require(Math.Abs(actual.X - expected.X) < .01 && Math.Abs(actual.Y - expected.Y) < .01,
                        "cache composition handles pan, zoom, rotation and DPI once");
                }
            }
            var outside = MapCoordinateFrame.Create(viewport, new SKPoint(10000, 10000), 2);
            Require(!NavigationFramePolicy.TryReuse(source, outside, 800, 600, out _), "uncovered cache rejected");
        }
        using var bitmap = new SKBitmap(192, 108, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.Orange.WithAlpha(89), IsAntialias = true };
            canvas.DrawCircle(74.3f, 42.7f, 31.2f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var fast = MapFrameEncoding.EncodePng(image);
        using var normal = image.Encode(SKEncodedImageFormat.Png, 100);
        using var fastDecoded = SKBitmap.Decode(fast);
        using var normalDecoded = SKBitmap.Decode(normal);
        Require(fastDecoded.Bytes.SequenceEqual(normalDecoded.Bytes), "fast PNG is lossless including alpha");
        Console.WriteLine("Large SHP contracts: PASS (identity, bounds, budget, cancellation, winding, camera/DPI, lossless PNG)");
    }

    private static byte[] Shape()
    {
        // Extra Z/M-sized tail is included in identity, as in the user's PolygonZ.
        var bytes = new byte[176];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), (bytes.Length - 8) / 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 5);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(44), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48), 5);
        double[] xy = [0, 0, 0, 10, 10, 10, 10, 0, 0, 0];
        for (int i = 0; i < xy.Length; i++) BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(56 + i * 8), xy[i]);
        return bytes;
    }

    public static void Measure(string path)
    {
        using var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* data = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref data);
        try
        {
            byte[] shx = File.ReadAllBytes(Path.ChangeExtension(path, ".shx"));
            int count = (shx.Length - 100) / 8;
            var offsets = new long[count];
            for (int i = 0; i < count; i++) offsets[i] = 2L * BinaryPrimitives.ReadInt32BigEndian(shx.AsSpan(100 + i * 8, 4));
            var timer = Stopwatch.StartNew();
            var representatives = ShapefileRenderCatalog.Build(data, view.Capacity, offsets);
            Console.WriteLine($"catalog_ms={timer.Elapsed.TotalMilliseconds:F1} original={count} unique={representatives?.Length.ToString() ?? "fallback"}");
            Console.WriteLine($"preview_unique={representatives?.Length.ToString() ?? "fallback"}");
            if (representatives == null) return;
            long[] compact = representatives.Select(i => offsets[i]).ToArray();
            double* bounds = (double*)(data + 36);
            double cx = (bounds[0] + bounds[2]) / 2, cy = (bounds[1] + bounds[3]) / 2;
            double* focus = (double*)(data + offsets[count / 2] + 12);
            double fx = (focus[0] + focus[2]) / 2, fy = (focus[1] + focus[3]) / 2;
            foreach (var (name, span) in new[] { ("overview", 46000f), ("district", 4000f), ("street", 400f) })
            {
                double x = name == "overview" ? cx : fx, y = name == "overview" ? cy : fy;
                using var baseline = Render(data, view.Capacity, offsets, x, y, span, false, name);
                using var candidate = Render(data, view.Capacity, compact, x, y, span, true, name);
                byte[] a = baseline.Bytes, b = candidate.Bytes;
                long different = 0;
                for (int i = 0; i < a.Length; i += 4)
                    if (!a.AsSpan(i, 4).SequenceEqual(b.AsSpan(i, 4))) different++;
                Console.WriteLine($"{name} changed_pixels={different}/{a.Length / 4}");
            }
        }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
    }

    private static SKBitmap Render(byte* data, long length, long[] offsets, double cx, double cy,
        float span, bool compact, string name)
    {
        const int width = 1920, height = 1080;
        float zoom = width / span, halfHeight = height / zoom / 2;
        var timer = Stopwatch.StartNew();
        nint index;
        fixed (long* p = offsets) index = NativeMethods.CreateShapeSpatialIndex(data, length, p, null, null, offsets.Length, 8);
        if (index == 0) throw new InvalidOperationException("Index creation failed");
        Console.WriteLine($"{name} compact={compact} index_ms={timer.Elapsed.TotalMilliseconds:F1}");
        try
        {
            var ids = new int[offsets.Length];
            int count;
            timer.Restart();
            fixed (int* p = ids) count = NativeMethods.QueryShapeSpatialIndex(index,
                cx - span / 2, cy - halfHeight, cx + span / 2, cy + halfHeight, p, ids.Length, out _);
            Console.WriteLine($"{name} compact={compact} query_ms={timer.Elapsed.TotalMilliseconds:F1} visible={count}");
            var selected = new long[count];
            for (int i = 0; i < count; i++) selected[i] = offsets[ids[i]];
            timer.Restart();
            using var path = BuildPath(data, length, selected, cx, cy, zoom,
                new SKRect(-span / 2, -halfHeight, span / 2, halfHeight), compact);
            double geometryMs = timer.Elapsed.TotalMilliseconds;
            var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(width / 2f, height / 2f); canvas.Scale(zoom);
            using var fill = new SKPaint { Color = SKColors.DeepSkyBlue.WithAlpha(89), IsAntialias = !compact };
            using var border = new SKPaint { Color = SKColors.DarkBlue.WithAlpha(89), IsAntialias = true,
                Style = SKPaintStyle.Stroke, StrokeWidth = 1f / zoom, StrokeJoin = SKStrokeJoin.Round };
            timer.Restart(); canvas.DrawPath(path, fill); if (!compact) canvas.DrawPath(path, border);
            double drawMs = timer.Elapsed.TotalMilliseconds;
            using var image = SKImage.FromBitmap(bitmap);
            timer.Restart();
            using var png = compact ? MapFrameEncoding.EncodePng(image) : image.Encode(SKEncodedImageFormat.Png, 100);
            Console.WriteLine($"{name} compact={compact} geometry_ms={geometryMs:F1} draw_ms={drawMs:F1} png_ms={timer.Elapsed.TotalMilliseconds:F1} png_bytes={png.Size}");
            if (!compact)
            {
                timer.Restart();
                using var fastFinal = MapFrameEncoding.EncodePng(image);
                Console.WriteLine($"{name} final_fast_png_ms={timer.Elapsed.TotalMilliseconds:F1} bytes={fastFinal.Size}");
            }
            return bitmap;
        }
        finally { NativeMethods.DestroyShapeSpatialIndex(index); }
    }

    private static SKPath BuildPath(byte* data, long length, long[] offsets,
        double cx, double cy, float zoom, SKRect viewport, bool interactive)
    {
        int width = (int)Math.Ceiling(viewport.Width * zoom), height = (int)Math.Ceiling(viewport.Height * zoom);
            var output = System.Buffers.ArrayPool<float>.Shared.Rent(1_048_576);
            var mask = System.Buffers.ArrayPool<byte>.Shared.Rent((width + 2) * (height + 2));
            Array.Clear(mask, 0, (width + 2) * (height + 2));
            var path = new SKPath { FillType = SKPathFillType.Winding };
        try
        {
            fixed (long* p = offsets)
            fixed (float* commands = output)
            fixed (byte* grid = mask)
            {
                for (int first = 0; first < offsets.Length;)
                {
                    int written = NativeMethods.ProcessShapeBatchV4(data, length, 0, p + first, Math.Min(4096, offsets.Length - first),
                        commands, output.Length, cx, cy, zoom, interactive ? .85f : .30f, interactive ? 1.25f : .65f, viewport.Left, viewport.Top, viewport.Right, viewport.Bottom,
                        grid, width + 2, height + 2, out int processed, out _, out _, out _, out _);
                    Require(written >= 0 && processed > 0, "real batch progress");
                    for (int c = 0; c < written;)
                    {
                        int vertices = (int)output[c++];
                        if (vertices == -4) { path.AddRect(new SKRect(output[c], output[c + 1], output[c + 2], output[c + 3])); c += 4; }
                        else { path.AddPoly(MemoryMarshal.Cast<float, SKPoint>(output.AsSpan(c, vertices * 2)), true); c += vertices * 2; }
                    }
                    first += processed;
                }
            }
            for (int y = 0; y < height + 2; y++)
                for (int x = 0; x < width + 2;)
                {
                    if (mask[y * (width + 2) + x] == 0) { x++; continue; }
                    int start = x++;
                    while (x < width + 2 && mask[y * (width + 2) + x] != 0) x++;
                    path.AddRect(new SKRect(viewport.Left + start / zoom, viewport.Top + y / zoom,
                        viewport.Left + x / zoom, viewport.Top + (y + 1) / zoom));
                }
            return path;
        }
        catch { path.Dispose(); throw; }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(output);
            System.Buffers.ArrayPool<byte>.Shared.Return(mask);
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
