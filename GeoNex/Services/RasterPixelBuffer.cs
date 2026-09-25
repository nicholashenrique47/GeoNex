using SkiaSharp;

namespace GeoNex.Services;

// One reusable allocation. A native image's final pixel-reference release returns
// its rental, so a pending encoder can never observe a later frame's writes.
internal sealed class RasterPixelBuffer : IDisposable
{
    private readonly long _maximumBytes;
    private readonly object _gate = new();
    private SKBitmap? _idle;
    private long _allocated, _budget;
    private bool _disposed;
    internal long AllocatedBytes { get { lock (_gate) return _allocated; } }

    internal RasterPixelBuffer(long maximumBytes = 64L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        _maximumBytes = Math.Min(maximumBytes, 128L * 1024 * 1024);
    }

    internal void Maintain(long budget)
    {
        lock (_gate)
        {
            _budget = Math.Clamp(budget, 0, _maximumBytes);
            if (_allocated > _budget) DropIdle();
        }
    }

    internal Rental? TryRent(SKImageInfo info)
    {
        lock (_gate)
        {
            if (_disposed || info.ColorType != SKColorType.Rgba8888 ||
                info.AlphaType != SKAlphaType.Premul || info.ColorSpace != null ||
                info.Width <= 0 || info.Height <= 0) return null;
            if (_idle != null && _idle.Info != info) DropIdle();
            long pixels = (long)info.Width * info.Height;
            if (pixels > _budget / 4) return null;
            long cost = pixels * 4;
            if (_idle != null && _idle.Info == info)
            {
                var rental = new Rental(this, _idle); _idle = null;
                return rental;
            }
            if (cost > _budget - _allocated) return null;
            var allocated = new SKBitmap();
            try
            {
                if (!allocated.TryAllocPixels(info)) { allocated.Dispose(); return null; }
                var rental = new Rental(this, allocated);
                _allocated += (long)allocated.RowBytes * allocated.Height;
                return rental;
            }
            catch { allocated.Dispose(); throw; }
        }
    }

    private void Return(SKBitmap bitmap)
    {
        lock (_gate)
        {
            if (!_disposed && _idle == null && _allocated <= _budget) _idle = bitmap;
            else
            {
                _allocated -= (long)bitmap.RowBytes * bitmap.Height;
                bitmap.Dispose();
            }
        }
    }

    private void DropIdle()
    {
        if (_idle == null) return;
        _allocated -= (long)_idle.RowBytes * _idle.Height;
        _idle.Dispose(); _idle = null;
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _budget = 0; DropIdle(); }
    }

    internal sealed class Rental(RasterPixelBuffer owner, SKBitmap bitmap) : IDisposable
    {
        private int _returned;
        internal SKBitmap Bitmap => bitmap;

        // Transfers this rental to the image, including its derived native views.
        // The caller must not dispose a successfully transferred rental.
        internal SKImage CreateImage()
        {
            using var wrapper = new SKBitmap();
            if (!wrapper.InstallPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes,
                    static (_, context) => ((Rental)context).Dispose(), this))
                throw new OutOfMemoryException();
            wrapper.SetImmutable();
            return SKImage.FromBitmap(wrapper) ?? throw new OutOfMemoryException();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0) owner.Return(bitmap);
        }
    }
}
