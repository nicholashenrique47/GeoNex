using SkiaSharp;

namespace GeoNex.Services;

// A complete frame is painted once, then frozen. Dispose the writable surface
// before transferring its pixels; external raster surfaces otherwise copy on Snapshot.
internal sealed class RasterRenderTarget : IDisposable
{
    private SKSurface? _surface;
    private RasterPixelBuffer.Rental? _rental;
    internal SKSurface Surface => _surface ?? throw new ObjectDisposedException(nameof(RasterRenderTarget));

    internal RasterRenderTarget(SKSurface? surface)
        => _surface = surface ?? throw new OutOfMemoryException();

    internal static RasterRenderTarget Create(SKImageInfo info, RasterPixelBuffer? buffer)
    {
        var rental = buffer?.TryRent(info);
        SKSurface? surface = null;
        try
        {
            surface = rental == null ? SKSurface.Create(info)
                : SKSurface.Create(info, rental.Bitmap.GetPixels(), rental.Bitmap.RowBytes);
            return new RasterRenderTarget(surface!) { _rental = rental };
        }
        catch { surface?.Dispose(); rental?.Dispose(); throw; }
    }

    // Terminal operation: the caller owns the returned immutable image.
    internal SKImage Finish()
    {
        var surface = Surface;
        if (_rental == null)
        {
            var image = surface.Snapshot() ?? throw new OutOfMemoryException();
            surface.Dispose(); _surface = null;
            return image;
        }
        surface.Dispose(); _surface = null;
        try
        {
            var image = _rental.CreateImage();
            _rental = null; // Last native pixel ref returns the buffer, not this target.
            return image;
        }
        catch { _rental?.Dispose(); _rental = null; throw; }
    }

    public void Dispose()
    {
        try { _surface?.Dispose(); _surface = null; }
        finally { _rental?.Dispose(); _rental = null; }
    }
}
