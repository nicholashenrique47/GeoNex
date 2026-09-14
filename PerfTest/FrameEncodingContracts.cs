using GeoNex.Services;
using SkiaSharp;

internal static class FrameEncodingContracts
{
    public static void Run()
    {
        string? previousProbe = Environment.GetEnvironmentVariable("GEONEX_PNG_COMPRESSION_PROBE");
        Environment.SetEnvironmentVariable("GEONEX_PNG_COMPRESSION_PROBE", "1");
        try
        {
        foreach (float dpi in new[] { 1f, 1.25f, 2f })
        foreach (float rotation in new[] { 0f, 17f, 90f })
        {
            var viewport = MapViewportMetrics.Create(801, 603, dpi);
            var source = MapCoordinateFrame.Create(viewport, new SKPoint(100, -80), 2);
            var target = MapCoordinateFrame.Create(viewport, new SKPoint(125, -96), 3, rotation);
            foreach (int size in new[] { 512, viewport.PhysicalWidth })
            {
                var matrix = NavigationFramePolicy.RasterPreviewMatrix(source, size, 347, target);
                var pixel = new SKPoint(size * .37f, 347 * .61f);
                var local = source.PhysicalToLocal(new SKPoint(pixel.X * viewport.PhysicalWidth / size,
                    pixel.Y * viewport.PhysicalHeight / 347));
                var expected = target.LocalToPhysical(local);
                var actual = matrix.MapPoint(pixel);
                Check(Math.Abs(actual.X - expected.X) < .001 && Math.Abs(actual.Y - expected.Y) < .001,
                    "cached satellite follows pan, zoom, rotation and physical DPI");
            }
        }
        foreach (var colorType in new[] { SKColorType.Rgba8888, SKColorType.Bgra8888 })
        {
            using var bitmap = new SKBitmap(768, 512, colorType, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            using (var empty = SKImage.FromBitmap(bitmap))
                Check(MapFrameEncoding.NavigationCompressionLevel(empty, 1024) == 1, "sparse frames stay compressed");
            using var paint = new SKPaint();
            // Mixed transparency and sharply varying adjacent colors, deterministically.
            for (int x = 0; x < bitmap.Width; x++)
            {
                paint.Color = new SKColor((byte)(x * 31), (byte)(x * 47), (byte)(x * 73), (byte)x);
                canvas.DrawRect(x, 0, 1, bitmap.Height, paint);
            }
            using var image = SKImage.FromBitmap(bitmap);
            Check(MapFrameEncoding.NavigationCompressionLevel(image, 1024) == 1, "dense repeated rows stay compressed");
            Environment.SetEnvironmentVariable("GEONEX_PNG_COMPRESSION_PROBE", "0");
            Check(MapFrameEncoding.NavigationCompressionLevel(image, 1024) == 0, "default navigation preserves low-latency path");
            Environment.SetEnvironmentVariable("GEONEX_PNG_COMPRESSION_PROBE", "1");
            foreach (int memory in new[] { -1, 0, 16, 32 })
                Check(MapFrameEncoding.NavigationCompressionLevel(image, memory) == 1, "memory pressure bounds larger payloads");
            using var standard = MapFrameEncoding.EncodePng(image);
            using var fast = MapFrameEncoding.EncodePng(image, 0);
            using var expected = SKBitmap.Decode(standard);
            using var actual = SKBitmap.Decode(fast);
            Check(expected.Bytes.SequenceEqual(actual.Bytes), "identical decoded pixels, RGBA/BGRA and alpha");
            Check(standard.Size * 4 < fast.Size, "Compressible fixture must demonstrate material payload savings");
            Console.WriteLine($"PNG repeated rows {colorType}: level0_bytes={fast.Size}, level1_bytes={standard.Size}");

            var random = new Random(417);
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            using var noise = SKImage.FromBitmap(bitmap);
            Check(MapFrameEncoding.NavigationCompressionLevel(noise, 1024) == 0, "incompressible noise keeps fast path");
            using var noiseFast = MapFrameEncoding.EncodePng(noise, 0);
            using var noiseStandard = MapFrameEncoding.EncodePng(noise, 1);
            using var noiseA = SKBitmap.Decode(noiseFast);
            using var noiseB = SKBitmap.Decode(noiseStandard);
            Check(noiseA.Bytes.SequenceEqual(noiseB.Bytes), "noise pixels changed");
        }
        using var small = new SKBitmap(32, 32);
        using var smallImage = SKImage.FromBitmap(small);
        Check(MapFrameEncoding.NavigationCompressionLevel(smallImage, int.MaxValue) == 1, "small images stay compressed");
        using var narrow = new SKBitmap(64, 8192);
        using (var canvas = new SKCanvas(narrow))
        using (var paint = new SKPaint())
            for (int x = 0; x < narrow.Width; x++)
            { paint.Color = new SKColor((byte)(x * 31), (byte)(x * 47), (byte)(x * 73)); canvas.DrawRect(x, 0, 1, narrow.Height, paint); }
        using var narrowImage = SKImage.FromBitmap(narrow);
        Check(MapFrameEncoding.NavigationCompressionLevel(narrowImage, int.MaxValue) == 1, "narrow/tall images never read beyond row bounds");
        Console.WriteLine("Frame encoding: PASS (pixel-exact, alpha, channel order, compressible detail/noise, memory budget, small frames)");
        }
        finally { Environment.SetEnvironmentVariable("GEONEX_PNG_COMPRESSION_PROBE", previousProbe); }
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
