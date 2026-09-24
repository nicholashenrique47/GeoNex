using SkiaSharp;

namespace GeoNex.Services;

// A surface snapshot owns immutable pixels. Cache bitmaps borrow those pixels
// through leases, independently of the request and the surface lifetime.
internal sealed class RasterFrameSnapshot : IDisposable
{
    private readonly LeasedResourceRegistry<byte, SKImage> _images = new();
    private readonly SKImage _image;
    private bool _disposed;

    public RasterFrameSnapshot(SKSurface surface)
    {
        _image = surface.Snapshot() ?? throw new InvalidOperationException("Cannot snapshot the map surface.");
        try { _images.Publish(0, _image); }
        catch { _image.Dispose(); throw; }
    }

    public SKImage Image
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return _image; }
    }

    // The returned bitmap transfers to the existing scene-cache registry.
    // Retiring it cannot invalidate the request's image, and ending the request
    // cannot invalidate a bitmap still leased by an interactive renderer.
    public SKBitmap CreateCacheBitmap()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var pixels = _image.PeekPixels();
        if (pixels == null || pixels.GetPixels() == IntPtr.Zero)
        {
            var copy = SKBitmap.FromImage(_image) ?? throw new OutOfMemoryException();
            copy.SetImmutable();
            return copy;
        }

        var bitmap = new SKBitmap();
        ResourceLease<SKImage>? lease = null;
        try
        {
            lease = _images.Acquire(0)!;
            if (!bitmap.InstallPixels(pixels.Info, pixels.GetPixels(), pixels.RowBytes,
                    static (_, context) => ((ResourceLease<SKImage>)context).Dispose(), lease))
                throw new OutOfMemoryException();
            lease = null; // Native pixel-ref callback now owns this lease.
            bitmap.SetImmutable();
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
        finally { lease?.Dispose(); } // Also safe if native installation already released it.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _images.Dispose();
    }
}
