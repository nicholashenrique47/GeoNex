using SkiaSharp;

namespace GeoNex.Services;

internal static class CachedPreviewImage
{
    // The caller keeps its cache lease until encoding finishes. Integer pans
    // need a pixel transfer, not a resampling/compositing pass over a clear frame.
    public static SKImage Create(SKBitmap source, SKMatrix matrix, CancellationToken token,
        RasterPixelBuffer? buffer = null)
    {
        token.ThrowIfCancellationRequested();
        if (source.ColorType == SKColorType.Rgba8888 && source.AlphaType == SKAlphaType.Premul &&
            source.Info.ColorSpace == null && matrix.ScaleX == 1 && matrix.ScaleY == 1 &&
            matrix.SkewX == 0 && matrix.SkewY == 0 && matrix.Persp0 == 0 && matrix.Persp1 == 0 && matrix.Persp2 == 1 &&
            float.IsFinite(matrix.TransX) && float.IsFinite(matrix.TransY) &&
            MathF.Abs(matrix.TransX) < source.Width && MathF.Abs(matrix.TransY) < source.Height &&
            matrix.TransX == MathF.Truncate(matrix.TransX) && matrix.TransY == MathF.Truncate(matrix.TransY))
        {
            if (matrix.TransX == 0 && matrix.TransY == 0)
                return SKImage.FromBitmap(source) ?? throw new OutOfMemoryException();
            int dx = (int)matrix.TransX, dy = (int)matrix.TransY;
            RasterPixelBuffer.Rental? rental = buffer?.TryRent(source.Info);
            using var allocated = rental == null ? new SKBitmap(source.Info) : null;
            try
            {
                var target = rental?.Bitmap ?? allocated!;
                var destination = target.GetPixelSpan();
                var original = source.GetPixelSpan();
                int x = Math.Max(0, dx), y = Math.Max(0, dy);
                int rows = source.Height - Math.Abs(dy), bytes = (source.Width - Math.Abs(dx)) * 4;
                destination[..(y * target.RowBytes)].Clear();
                destination[((y + rows) * target.RowBytes)..].Clear();
                if (dx == 0 && target.RowBytes == source.RowBytes && target.RowBytes == bytes)
                {
                    token.ThrowIfCancellationRequested();
                    original.Slice((y - dy) * source.RowBytes, rows * bytes)
                        .CopyTo(destination.Slice(y * target.RowBytes, rows * bytes));
                }
                else
                {
                    int leftClearBytes = x * 4;
                    int rightClearOffset = leftClearBytes + bytes;
                    int srcOffsetX = (x - dx) * 4;
                    for (int row = 0; row < rows; row++)
                    {
                        if ((row & 63) == 0) token.ThrowIfCancellationRequested();
                        var targetRow = destination.Slice((y + row) * target.RowBytes, target.RowBytes);
                        if (leftClearBytes > 0) targetRow[..leftClearBytes].Clear();
                        original.Slice((y + row - dy) * source.RowBytes + srcOffsetX, bytes)
                            .CopyTo(targetRow.Slice(leftClearBytes, bytes));
                        if (rightClearOffset < target.RowBytes) targetRow[rightClearOffset..].Clear();
                    }
                }
                if (rental != null)
                {
                    var image = rental.CreateImage();
                    rental = null; // Native pixel-ref owns the reusable allocation.
                    return image;
                }
                target.SetImmutable();
                return SKImage.FromBitmap(target) ?? throw new OutOfMemoryException();
            }
            finally { rental?.Dispose(); }
        }
        // Fractional pans, zoom, rotation and other pixel formats retain Skia's
        // existing sampling, blending and color conversion semantics while reusing
        // the pooled RasterPixelBuffer to eliminate 8-16 MB per-frame native allocations.
        var targetInfo = new SKImageInfo(source.Width, source.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul);
        using var renderTarget = RasterRenderTarget.Create(targetInfo, buffer);
        var canvas = renderTarget.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);
        return renderTarget.Finish();
    }
}
