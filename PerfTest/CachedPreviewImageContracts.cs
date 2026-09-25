using System.Diagnostics;
using GeoNex.Services;
using SkiaSharp;

internal static class CachedPreviewImageContracts
{
    public static unsafe void Run()
    {
        using var buffer = new RasterPixelBuffer();
        buffer.Maintain(64L * 1024 * 1024);
        foreach (var format in new[] { SKColorType.Rgba8888, SKColorType.Bgra8888 })
        foreach (var alpha in new[] { SKAlphaType.Premul, SKAlphaType.Opaque })
        foreach (bool immutable in new[] { false, true })
        foreach (bool padded in new[] { false, true })
        {
            using var source = new SKBitmap();
            if (!source.TryAllocPixels(new SKImageInfo(129, 97, format, alpha), 129 * 4 + (padded ? 16 : 0)))
                throw new OutOfMemoryException();
            var pixels = source.GetPixelSpan();
            var random = new Random(43);
            for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
            {
                int offset = y * source.RowBytes + x * 4;
                int a = alpha == SKAlphaType.Opaque ? 255 : random.Next(256);
                for (int c = 0; c < 3; c++) pixels[offset + c] = (byte)random.Next(a + 1);
                pixels[offset + 3] = (byte)a;
            }
            if (immutable) source.SetImmutable();
            foreach (var matrix in new[] { SKMatrix.Identity, SKMatrix.CreateTranslation(17, -5),
                SKMatrix.CreateTranslation(-13, 19), SKMatrix.CreateTranslation(128, 96),
                SKMatrix.CreateTranslation(-128, -96), SKMatrix.CreateTranslation(129, 97),
                SKMatrix.CreateTranslation(.25f, -.5f), SKMatrix.CreateScale(1.125f, .875f),
                SKMatrix.CreateRotationDegrees(12) })
            foreach (bool reuse in new[] { false, true })
            {
                using var expected = Original(source, matrix);
                using var actual = CachedPreviewImage.Create(source, matrix, default, reuse ? buffer : null);
                using var a = expected.PeekPixels(); using var b = actual.PeekPixels();
                for (int y = 0; y < source.Height; y++)
                    if (!new ReadOnlySpan<byte>((byte*)a.GetPixels() + y * a.RowBytes, source.Width * 4)
                        .SequenceEqual(new ReadOnlySpan<byte>((byte*)b.GetPixels() + y * b.RowBytes, source.Width * 4)))
                        throw new InvalidOperationException($"Preview pixels differ: {format}/{alpha}, immutable={immutable}, padded={padded}, matrix={matrix}");
            }
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { using var ignored = CachedPreviewImage.Create(source, SKMatrix.CreateTranslation(1, -1), cancelled.Token, buffer); throw new InvalidOperationException("Cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        // Native image owns pixels after the cache lease and bitmap are retired.
        using (var source = new SKBitmap(17, 13, SKColorType.Rgba8888, SKAlphaType.Premul))
        {
            source.Erase(SKColors.Cyan); source.SetImmutable();
            using var identity = CachedPreviewImage.Create(source, SKMatrix.Identity, default);
            using var shifted = CachedPreviewImage.Create(source, SKMatrix.CreateTranslation(1, -1), default);
            source.Dispose();
            using var a = SKBitmap.FromImage(identity); using var b = SKBitmap.FromImage(shifted);
            if (a.GetPixel(0, 0) != SKColors.Cyan || b.GetPixel(1, 0) != SKColors.Cyan || b.GetPixel(0, 0).Alpha != 0)
                throw new InvalidOperationException("Native image lost its pixel owner");
        }
        Console.WriteLine("Cached preview image: PASS (exact premultiplied pixels, alpha 0..255, RGBA/BGRA, mutable/immutable, padded rows, signed pans, fractions/scale/rotation fallback, cancellation, lifetime)");
        using var large = new SKBitmap(3712, 2312, SKColorType.Rgba8888, SKAlphaType.Premul);
        large.Erase(new SKColor(14, 165, 233, 89)); large.SetImmutable();
        foreach (int shift in new[] { 0, 8, -8 })
        {
            var matrix = SKMatrix.CreateTranslation(shift, shift);
            var times = new[] { new List<double>(), new List<double>(), new List<double>() };
            for (int round = 0; round < 7; round++)
            foreach (int version in round % 2 == 0 ? new[] { 0, 1, 2 } : new[] { 2, 1, 0 })
            {
                long start = Stopwatch.GetTimestamp();
                using var image = version == 0 ? Original(large, matrix) : CachedPreviewImage.Create(large, matrix, default, version == 2 ? buffer : null);
                if (round > 0) times[version].Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            double Median(int i) { var v = times[i].Order().ToArray(); return (v[2] + v[3]) / 2; }
            Console.WriteLine(FormattableString.Invariant($"PREVIEW shift={shift} original_ms={Median(0):F3} transfer_ms={Median(1):F3} reused_ms={Median(2):F3}"));
        }
    }

    private static SKImage Original(SKBitmap source, SKMatrix matrix)
    {
        using var surface = SKSurface.Create(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.SetMatrix(matrix);
        surface.Canvas.DrawBitmap(source, 0, 0);
        return surface.Snapshot();
    }
}
