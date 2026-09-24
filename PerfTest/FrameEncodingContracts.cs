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
        CheckLargeFrames();
        Console.WriteLine("Frame encoding: PASS (pixel-exact, alpha, channel order, compressible detail/noise, memory budget, small frames)");
        }
        finally { Environment.SetEnvironmentVariable("GEONEX_PNG_COMPRESSION_PROBE", previousProbe); }
    }

    private static void CheckLargeFrames()
    {
        foreach (var color in new[] { SKColorType.Rgba8888, SKColorType.Bgra8888 })
        foreach (var alpha in new[] { SKAlphaType.Premul, SKAlphaType.Unpremul, SKAlphaType.Opaque })
        {
            using var bitmap = new SKBitmap();
            Check(bitmap.TryAllocPixels(new SKImageInfo(1537, 1025, color, alpha), 1537 * 4 + 64), "padded fixture allocation");
            using var canvas = new SKCanvas(bitmap);
            using var paint = new SKPaint { IsAntialias = true };
            foreach (string scene in new[] { "parcels", "gradient" })
            {
                canvas.Clear(SKColors.Transparent);
                if (scene == "parcels")
                {
                    var random = new Random(917);
                    for (int i = 0; i < 8000; i++)
                    {
                        paint.Color = new SKColor(56, 189, 248, 89);
                        canvas.DrawRect(random.Next(1537), random.Next(1025), 3.7f, 7.1f, paint);
                    }
                }
                else
                {
                    using var shader = SKShader.CreateLinearGradient(SKPoint.Empty, new SKPoint(1537, 1025),
                        new[] { SKColors.DarkGreen, SKColors.LightGoldenrodYellow, SKColors.DarkBlue }, SKShaderTileMode.Clamp);
                    paint.Shader = shader;
                    canvas.DrawPaint(paint);
                    paint.Shader = null;
                }
                canvas.Flush();
                using var paddedPixels = bitmap.PeekPixels();
                using var image = SKImage.FromBitmap(bitmap);
                using var pixels = image.PeekPixels();
                var filter = MapFrameEncoding.SelectFilter(paddedPixels, 1);
                Check(filter == MapFrameEncoding.SelectFilter(pixels!, 1), "row padding changed filter selection");
                if (scene == "gradient") Check(filter == SKPngEncoderFilterFlags.Sub, "smooth raster lost Sub filtering");
                Check(MapFrameEncoding.SelectFilter(pixels!, 0) == SKPngEncoderFilterFlags.Sub, "stored PNG path changed");
                using var expectedPng = pixels!.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub, 1))
                    ?? throw new InvalidOperationException("Reference PNG failed");
                using var actualPng = MapFrameEncoding.EncodePng(image, 1);
                using var expected = SKBitmap.Decode(expectedPng);
                using var actual = SKBitmap.Decode(actualPng);
                Check(expected.Bytes.SequenceEqual(actual.Bytes), $"adaptive filter changed {scene}/{color}/{alpha} pixels");
                Console.WriteLine($"PNG filter {scene}/{color}/{alpha}: selected={filter} before={expectedPng.Size} after={actualPng.Size}");
            }
        }
        using var narrow = new SKBitmap(64, 16385);
        using var narrowPixels = narrow.PeekPixels();
        Check(MapFrameEncoding.SelectFilter(narrowPixels, 1) == SKPngEncoderFilterFlags.Sub, "narrow filter fallback");
        using var space = SKColorSpace.CreateSrgb();
        using var managed = new SKBitmap(new SKImageInfo(1024, 1024, SKColorType.Rgba8888, SKAlphaType.Premul, space));
        using var managedPixels = managed.PeekPixels();
        Check(MapFrameEncoding.SelectFilter(managedPixels, 1) == SKPngEncoderFilterFlags.Sub, "color-managed filter fallback");
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
