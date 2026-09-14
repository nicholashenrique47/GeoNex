using SkiaSharp;

namespace GeoNex.Services;

// Exact physical camera only; never resample this final-quality layer image.
internal readonly record struct PolygonImageContext(object Source, long Revision,
    SKRect Viewport, SKPoint Origin, string Crs, double OffsetX, double OffsetY);

internal sealed class PolygonImageCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _bytes, _clock;
    private bool _disposed;
    private long _hits, _builds;
    public long Hits { get { lock (_gate) return _hits; } }
    public long Builds { get { lock (_gate) return _builds; } }
    public long Bytes { get { lock (_gate) return _bytes; } }

    private readonly record struct PaintKey(SKColor Color, SKPaintStyle Style, float Width,
        SKStrokeCap Cap, SKStrokeJoin Join, float Miter, bool Antialias);
    private readonly record struct Key(PolygonImageContext Context, SKMatrix Matrix,
        int Width, int Height, PaintKey? Fill, PaintKey? Stroke);
    private sealed record Entry(Key Key, SKBitmap Image, long Bytes)
    {
        public long Used;
        public void Dispose() => Image.Dispose();
    }

    public void Maintain(long budget, long revision)
    {
        lock (_gate)
        {
            foreach (string layer in _entries.Where(pair => pair.Value.Key.Context.Revision != revision)
                         .Select(pair => pair.Key).ToArray()) Remove(layer);
            Trim(Math.Clamp(budget, 0, 64L * 1024 * 1024));
        }
    }

    private static PaintKey? Describe(SKPaint? paint) => paint == null ? null :
        new(paint.Color, paint.Style, paint.StrokeWidth, paint.StrokeCap,
            paint.StrokeJoin, paint.StrokeMiter, paint.IsAntialias);

    private static bool Supported(SKPaint? paint) => paint == null ||
        (paint.BlendMode == SKBlendMode.SrcOver && paint.Shader == null &&
         paint.ColorFilter == null && paint.ImageFilter == null && paint.MaskFilter == null &&
         paint.PathEffect == null && !paint.IsDither);

    // Caller supplies a periodically sampled spare-memory budget. The lock protects
    // native bitmap lifetime against Stop; there are no background publishers.
    public bool TryDraw(string layer, PolygonImageContext context, SKCanvas canvas,
        SKPath path, SKPaint? fill, SKPaint? stroke, int width, int height,
        long budget, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            budget = Math.Clamp(budget, 0, 64L * 1024 * 1024);
            Trim(budget);
            if (width <= 0 || height <= 0 || !Supported(fill) || !Supported(stroke)) return false;
            long pixels = (long)width * height;
            if ((fill == null && stroke == null) || pixels > budget / 4) return false;
            long cost = pixels * 4;
            var key = new Key(context, canvas.TotalMatrix, width, height, Describe(fill), Describe(stroke));
            cancellationToken.ThrowIfCancellationRequested();
            if (_entries.TryGetValue(layer, out var entry) && entry.Key == key)
            {
                entry.Used = ++_clock;
                Composite(canvas, entry);
                _hits++;
                return true;
            }

            Remove(layer);
            Trim(budget - cost); // Include working bitmaps, not only published entries.
            if (_entries.Count >= 64) Remove(_entries.MinBy(pair => pair.Value.Used).Key);
            SKBitmap? image = null;
            try
            {
                image = Paint(path, fill, stroke, key, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                entry = new Entry(key, image, cost) { Used = ++_clock };
                _entries.Add(layer, entry);
                _bytes += cost;
                image = null;
                _builds++;
            }
            catch (OutOfMemoryException) { return false; } // Original direct path is the fallback.
            finally { image?.Dispose(); }
            // Do not fall back after composition starts: a partial destination draw
            // must never be painted a second time if Skia reports a failure.
            Composite(canvas, entry);
            return true;
        }
    }

    private static SKBitmap Paint(SKPath path, SKPaint? fill, SKPaint? stroke, Key key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bitmap = new SKBitmap();
        try
        {
            if (!bitmap.TryAllocPixels(new SKImageInfo(key.Width, key.Height,
                    SKColorType.Rgba8888, SKAlphaType.Premul))) throw new OutOfMemoryException();
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(key.Matrix);
            if (fill != null) canvas.DrawPath(path, fill);
            token.ThrowIfCancellationRequested();
            if (stroke != null) canvas.DrawPath(path, stroke);
            canvas.Flush();
            bitmap.SetImmutable();
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static void Composite(SKCanvas canvas, Entry entry)
    {
        canvas.Save();
        try
        {
            canvas.ResetMatrix();
            canvas.DrawBitmap(entry.Image, 0, 0);
        }
        finally { canvas.Restore(); }
    }

    private void Remove(string layer)
    {
        if (!_entries.Remove(layer, out var entry)) return;
        _bytes -= entry.Bytes;
        entry.Dispose();
    }
    private void Trim(long budget)
    {
        while (_bytes > budget && _entries.Count > 0)
            Remove(_entries.MinBy(pair => pair.Value.Used).Key);
    }
    public void Dispose()
    {
        lock (_gate) { _disposed = true; Trim(0); }
    }
}
