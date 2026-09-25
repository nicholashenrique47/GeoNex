using GeoNex.Services;
using SkiaSharp;

internal static class RasterRenderTargetContracts
{
    public static void Run()
    {
        using var pool = new RasterPixelBuffer(128L * 1024 * 1024);
        pool.Maintain(128L * 1024 * 1024);
        foreach (var format in new[] { SKColorType.Rgba8888, SKColorType.Bgra8888 })
        foreach (var alpha in new[] { SKAlphaType.Premul, SKAlphaType.Opaque })
        foreach (bool managedColor in new[] { false, true })
        {
            using var colorSpace = managedColor ? SKColorSpace.CreateSrgb() : null;
            var info = new SKImageInfo(641, 479, format, alpha, colorSpace);
            for (int frame = 0; frame < 4; frame++)
            {
                using var original = SKSurface.Create(info);
                using var target = RasterRenderTarget.Create(info, pool);
                Draw(original.Canvas, frame); Draw(target.Surface.Canvas, frame);
                using var expected = original.Snapshot();
                using var result = target.Finish();
                using var a = expected.PeekPixels(); using var b = result.PeekPixels();
                Require(a.GetPixelSpan().SequenceEqual(b.GetPixelSpan()), "Final surface pixels changed");
                try { _ = target.Surface; throw new InvalidOperationException("Finished frame remains writable"); }
                catch (ObjectDisposedException) { }
                try { using var duplicate = target.Finish(); throw new InvalidOperationException("Frame finished twice"); }
                catch (ObjectDisposedException) { }
                target.Dispose(); // Published image remains valid.
                Require(a.GetPixelSpan().SequenceEqual(b.GetPixelSpan()), "Disposing target changed frozen pixels");
            }
        }
        pool.Maintain(0);
        Require(pool.AllocatedBytes == 0, "Rendered frames leaked allocations");
        pool.Maintain(128L * 1024 * 1024);
        var rgba = new SKImageInfo(641, 479, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var first = RasterRenderTarget.Create(rgba, pool);
        first.Surface.Canvas.Clear(SKColors.Cyan);
        using var published = new RasterFrameSnapshot(first.Finish());
        var cached = published.CreateCacheBitmap();
        IntPtr originalAddress = cached.GetPixels();
        using var shader = published.Image.ToShader();
        published.Dispose();
        using (var next = RasterRenderTarget.Create(rgba, pool))
        {
            next.Surface.Canvas.Clear(SKColors.Magenta);
            using var nextImage = next.Finish();
            using var pixels = nextImage.PeekPixels();
            Require(pixels.GetPixels() != originalAddress && cached.GetPixel(0, 0) == SKColors.Cyan,
                "Next frame overwrote the scene cache");
        }
        cached.Dispose();
        pool.Maintain(0);
        Require(pool.AllocatedBytes == (long)rgba.RowBytes * rgba.Height, "Native reader lost original allocation");
        shader.Dispose();
        Require(pool.AllocatedBytes == 0, "Final native reader did not release allocation");
        pool.Maintain(128L * 1024 * 1024);
        using (var abandoned = RasterRenderTarget.Create(rgba, pool))
            abandoned.Surface.Canvas.Clear(SKColors.Red); // Cancellation before Finish.
        using (var reused = RasterRenderTarget.Create(rgba, pool))
        {
            reused.Surface.Canvas.Clear(SKColors.Transparent);
            using var image = reused.Finish();
            using var pixels = image.PeekPixels();
            Require(pixels.GetPixelSpan().IndexOfAnyExcept((byte)0) < 0, "Abandoned frame leaked into next frame");
        }
        pool.Dispose();
        Require(pool.AllocatedBytes == 0, "Shutdown leaked idle raster target");
        Console.WriteLine("Raster render target: PASS (32 exact frames, RGBA/BGRA/alpha/color space, terminal freeze, cached pixels and native readers, canceled frame, pressure/shutdown)");
    }

    private static void Draw(SKCanvas canvas, int frame)
    {
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(320.5f, 239.5f); canvas.RotateDegrees(frame * 13);
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        path.AddRect(new SKRect(-311.37f, -225.63f, 300.13f, 210.7f));
        path.AddCircle(30.31f, -13.17f, 91.23f);
        using var fill = new SKPaint { Color = new SKColor(56, 189, 248, (byte)(31 + frame * 61)), IsAntialias = true };
        using var stroke = new SKPaint { Color = new SKColor(14, 165, 233, 89), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 2.37f, StrokeJoin = SKStrokeJoin.Round };
        canvas.DrawPath(path, fill); canvas.DrawPath(path, stroke);
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
