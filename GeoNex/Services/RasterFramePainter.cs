using SkiaSharp;

namespace GeoNex.Services;

internal static class RasterFramePainter
{
    public static void DrawRegion(SKSurface surface, SKBitmap bitmap, SKRect destination,
        CancellationToken token, bool interacting, int? requestedWorkers = null)
    {
        token.ThrowIfCancellationRequested();
        var canvas = surface.Canvas;
        canvas.ResetMatrix();
        int workers = requestedWorkers ?? ((long)canvas.DeviceClipBounds.Width * canvas.DeviceClipBounds.Height >= 1024 * 1024
            ? Math.Min(4, VectorRuntimeResources.Current.Workers) : 1);
        workers = Math.Clamp(workers, 1, 4);
        if (Environment.GetEnvironmentVariable("GEONEX_RASTER_PARALLEL_COMPOSITE") == "0") workers = 1;
        using var pixels = workers > 1 && canvas.IsClipRect ? surface.PeekPixels() : null;
        bitmap.SetImmutable();
        var clip = canvas.DeviceClipBounds;
        if (pixels != null && pixels.GetPixels() != IntPtr.Zero)
        {
            canvas.Flush();
            Parallel.For(0, workers, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = workers }, band =>
            {
                int top = Math.Max(clip.Top, pixels.Height * band / workers);
                int bottom = Math.Min(clip.Bottom, pixels.Height * (band + 1) / workers);
                if (bottom <= top || clip.Right <= clip.Left) return;
                using var target = SKSurface.Create(pixels.Info, pixels.GetPixels(), pixels.RowBytes)
                    ?? throw new OutOfMemoryException();
                target.Canvas.ClipRect(new SKRect(clip.Left, top, clip.Right, bottom), SKClipOperation.Intersect, false);
                using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
                target.Canvas.DrawBitmap(bitmap, destination, paint);
            });
            token.ThrowIfCancellationRequested();
            return;
        }

        using var serialPaint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
        canvas.DrawBitmap(bitmap, destination, serialPaint);
    }

    // Only for a request-owned writable frame BEFORE any snapshots are taken.
    // LocalMapServer never applies an AA/nonrectangular clip to these surfaces.
    // Disjoint device-space rows retain the original sampling matrix and filter;
    // no partial result is published when a request is canceled.
    public static void Draw(SKSurface surface, SKBitmap bitmap, MapViewportMetrics viewport, bool interacting,
        CancellationToken token, int? requestedWorkers = null)
    {
        token.ThrowIfCancellationRequested();
        var canvas = surface.Canvas;
        canvas.ResetMatrix();
        int workers = requestedWorkers ?? ((long)viewport.PhysicalWidth * viewport.PhysicalHeight >= 1024 * 1024
            ? Math.Min(4, VectorRuntimeResources.Current.Workers) : 1);
        workers = Math.Clamp(workers, 1, 4);
        if (Environment.GetEnvironmentVariable("GEONEX_RASTER_PARALLEL_COMPOSITE") == "0") workers = 1;
        using var pixels = workers > 1 && canvas.IsClipRect ? surface.PeekPixels() : null;
        bitmap.SetImmutable();
        var clip = canvas.DeviceClipBounds;
        if (pixels != null && pixels.GetPixels() != IntPtr.Zero)
        {
            canvas.Flush();
            Parallel.For(0, workers, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = workers }, band =>
            {
                int top = Math.Max(clip.Top, pixels.Height * band / workers);
                int bottom = Math.Min(clip.Bottom, pixels.Height * (band + 1) / workers);
                if (bottom <= top || clip.Right <= clip.Left) return;
                using var target = SKSurface.Create(pixels.Info, pixels.GetPixels(), pixels.RowBytes)
                    ?? throw new OutOfMemoryException();
                target.Canvas.ClipRect(new SKRect(clip.Left, top, clip.Right, bottom), SKClipOperation.Intersect, false);
                Paint(target.Canvas, bitmap, viewport, interacting);
            });
            token.ThrowIfCancellationRequested();
            canvas.Scale(viewport.PhysicalScaleX, viewport.PhysicalScaleY);
            return;
        }
        Paint(canvas, bitmap, viewport, interacting);
    }

    private static void Paint(SKCanvas canvas, SKBitmap bitmap, MapViewportMetrics viewport, bool interacting)
    {
        canvas.Scale(viewport.PhysicalScaleX, viewport.PhysicalScaleY);
        using var paint = new SKPaint { FilterQuality = interacting ? SKFilterQuality.Medium : SKFilterQuality.High, IsAntialias = true };
        canvas.DrawBitmap(bitmap, new SKRect(0, 0, viewport.CssWidth, viewport.CssHeight), paint);
    }
}
