using GeoNex.Services;
using SkiaSharp;

internal static class RasterFramePainterContracts
{
    public static void Run()
    {
        int cases = 0;
        foreach (float dpi in new[] { 1f, 1.25f, 1.5f, 2f })
        foreach (bool interactive in new[] { false, true })
        foreach (bool reduced in new[] { false, true })
        foreach (int clip in new[] { 0, 1, 2, 3 })
        foreach (int workers in new[] { 1, 2, 4 })
        {
            var viewport = MapViewportMetrics.Create(321, 179, dpi);
            using var source = new SKBitmap(reduced ? viewport.PhysicalWidth / 2 : viewport.PhysicalWidth,
                reduced ? viewport.PhysicalHeight / 2 : viewport.PhysicalHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            var random = new Random(31);
            source.Pixels = Enumerable.Range(0, source.Width * source.Height)
                .Select(_ => new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256))).ToArray();
            byte[] Render(bool original)
            {
                using var result = SKSurface.Create(new SKImageInfo(viewport.PhysicalWidth, viewport.PhysicalHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
                var canvas = result.Canvas;
                canvas.Clear(new SKColor(20, 90, 180, 200));
                if (clip == 1) canvas.ClipRect(new SKRect(17, 11, viewport.PhysicalWidth - 23, viewport.PhysicalHeight - 19));
                if (clip == 2) canvas.ClipRect(new SKRect(17, 11, 60, 12)); // Entire worker bands lie outside this clip.
                if (clip == 3)
                {
                    using var path = new SKPath(); path.AddCircle(50, 50, 37);
                    canvas.ClipPath(path, SKClipOperation.Intersect, true); // Must use the original canvas.
                }
                if (original)
                {
                    canvas.Scale(viewport.PhysicalScaleX, viewport.PhysicalScaleY);
                    using var paint = new SKPaint { FilterQuality = interactive ? SKFilterQuality.Medium : SKFilterQuality.High, IsAntialias = true };
                    canvas.DrawBitmap(source, new SKRect(0, 0, viewport.CssWidth, viewport.CssHeight), paint);
                }
                else RasterFramePainter.Draw(result, source, viewport, interactive, default, workers);
                using var snapshot = result.Snapshot();
                using var bitmap = SKBitmap.FromImage(snapshot);
                return bitmap.Bytes;
            }
            var expected = Render(true); var actual = Render(false);
            int delta = expected.Zip(actual, (a, b) => Math.Abs(a - b)).Max();
            if (delta > 1) throw new InvalidOperationException($"Raster composition differs: dpi={dpi} interactive={interactive} reduced={reduced} clip={clip} delta={delta}");
            cases++;
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var untouched = SKSurface.Create(new SKImageInfo(16, 16));
        untouched.Canvas.Clear(SKColors.Red);
        using var tile = new SKBitmap(16, 16); tile.Erase(SKColors.Blue);
        try { RasterFramePainter.Draw(untouched, tile, MapViewportMetrics.Create(16, 16, 1), false, canceled.Token, 4); throw new InvalidOperationException("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        using var snapshot = untouched.Snapshot(); using var check = SKBitmap.FromImage(snapshot);
        if (check.Pixels.Any(p => p != SKColors.Red)) throw new InvalidOperationException("Canceled request modified the target");
        Console.WriteLine($"Raster composition: PASS ({cases} cases, 1/2/4 workers, alpha, empty-band/nonrectangular clipping, 4 DPIs, reduced frames, preview/final, cancellation, delta <= 1)");
    }
}
