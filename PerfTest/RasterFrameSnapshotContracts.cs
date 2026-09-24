using System.Diagnostics;
using GeoNex.Services;
using SkiaSharp;

internal static class RasterFrameSnapshotContracts
{
    public static void Run()
    {
        foreach (var color in new[] { SKColorType.Rgba8888, SKColorType.Bgra8888 })
        foreach (var alpha in new[] { SKAlphaType.Premul, SKAlphaType.Opaque })
        {
            using var surface = SKSurface.Create(new SKImageInfo(641, 479, color, alpha));
            surface.Canvas.Clear(new SKColor(11, 57, 101, 127));
            using var paint = new SKPaint { Color = new SKColor(199, 87, 43, 153), IsAntialias = true };
            surface.Canvas.DrawCircle(233.13f, 147.79f, 101.37f, paint);
            using var frame = new RasterFrameSnapshot(surface);
            SKImage borrowedImage = frame.Image;
            using var reference = SKBitmap.FromImage(borrowedImage);
            using var originalPixels = borrowedImage.PeekPixels();
            byte[] expected = originalPixels!.GetPixelSpan().ToArray();
            var first = frame.CreateCacheBitmap();
            var second = frame.CreateCacheBitmap();
            using (var pixels = borrowedImage.PeekPixels())
                Require(first.GetPixels() == pixels!.GetPixels(), "Snapshot pixels were copied");
            Require(first.IsImmutable && second.IsImmutable, "Cache pixels must be immutable");
            Require(first.Info == originalPixels.Info && first.Bytes.SequenceEqual(expected), "Snapshot changed raw format or pixels");
            using (var target = new SKBitmap(reference.Info))
            using (var canvas = new SKCanvas(target))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(first, 0, 0);
                Require(target.Bytes.SequenceEqual(reference.Bytes), "Drawing shared pixels changed colors");
            }

            // A snapshot must survive later drawing (Skia copy-on-write), as
            // well as disposal of the surface and of the producing request.
            surface.Canvas.Clear(SKColors.Magenta);
            surface.Dispose();
            Require(first.Bytes.SequenceEqual(expected), "Surface mutation changed retained pixels");
            first.Dispose(); // Early cache eviction while the request still encodes.
            using (var encoded = MapFrameEncoding.EncodePng(frame.Image))
            using (var decoded = SKBitmap.Decode(encoded))
                Require(decoded.Bytes.SequenceEqual(DecodeReference(reference)), "Early eviction changed encoding");

            // Skia consumers can retain the native pixel ref after the cache's
            // own SKBitmap has been retired. Release only after the last one.
            using var child = new SKBitmap();
            Require(second.ExtractSubset(child, new SKRectI(0, 0, second.Width, second.Height)), "Subset failed");
            frame.Dispose();
            frame.Dispose();
            second.Dispose();
            Require(borrowedImage.Handle != IntPtr.Zero, "Image released while native reader is alive");
            Require(child.Bytes.SequenceEqual(expected), "Retirement invalidated retained pixels");
            child.Dispose();
            Require(borrowedImage.Handle == IntPtr.Zero, "Last reader did not release snapshot");
        }
        CheckConcurrentRetirement();
        MeasurePublication();
        Console.WriteLine("Raster snapshot: PASS (shared immutable pixels, RGBA/BGRA/alpha, surface COW, early eviction, native readers, concurrent retirement)");
    }

    private static byte[] DecodeReference(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = MapFrameEncoding.EncodePng(image);
        using var decoded = SKBitmap.Decode(data);
        return decoded.Bytes;
    }

    private static void CheckConcurrentRetirement()
    {
        using var surface = SKSurface.Create(new SKImageInfo(641, 479, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(new SKColor(31, 72, 109, 157));
        using var frame = new RasterFrameSnapshot(surface);
        var image = frame.Image;
        using var registry = new LeasedResourceRegistry<int, SKBitmap>();
        registry.Publish(0, frame.CreateCacheBitmap());
        var readers = Enumerable.Range(0, 8).Select(_ => registry.Acquire(0)!).ToArray();
        byte[] expected = readers[0].Resource.Bytes;
        registry.Dispose();
        frame.Dispose();
        surface.Dispose();
        Parallel.ForEach(readers, reader =>
        {
            using (reader)
                Require(reader.Resource.Bytes.SequenceEqual(expected), "Concurrent reader lost retired pixels");
        });
        Require(image.Handle == IntPtr.Zero, "Concurrent readers leaked snapshot");
    }

    private static void MeasurePublication()
    {
        using var surface = SKSurface.Create(new SKImageInfo(3712, 2312, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(new SKColor(31, 72, 109, 157));
        using var frame = new RasterFrameSnapshot(surface);
        var copy = new List<double>(); var share = new List<double>();
        for (int round = 0; round < 7; round++)
        foreach (bool shared in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
        {
            long start = Stopwatch.GetTimestamp();
            using var bitmap = shared ? frame.CreateCacheBitmap() : SKBitmap.FromImage(frame.Image);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (round > 0) (shared ? share : copy).Add(ms);
        }
        copy.Sort(); share.Sort();
        Console.WriteLine(FormattableString.Invariant($"SNAPSHOT copy_median_ms={(copy[2] + copy[3]) / 2:F3} shared_median_ms={(share[2] + share[3]) / 2:F3} avoided_copy_bytes={3712L * 2312 * 4}"));
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
