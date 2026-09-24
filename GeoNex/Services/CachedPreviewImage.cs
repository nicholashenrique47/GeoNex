using SkiaSharp;

namespace GeoNex.Services;

internal static class CachedPreviewImage
{
    // The caller keeps its cache lease until encoding finishes. Integer pans
    // need a pixel transfer, not a resampling/compositing pass over a clear frame.
    public static SKImage Create(SKBitmap source, SKMatrix matrix, CancellationToken token)
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
            using var target = new SKBitmap(source.Info);
            var destination = target.GetPixelSpan();
            var original = source.GetPixelSpan();
            int x = Math.Max(0, dx), y = Math.Max(0, dy);
            int rows = source.Height - Math.Abs(dy), bytes = (source.Width - Math.Abs(dx)) * 4;
            destination[..(y * target.RowBytes)].Clear();
            destination[((y + rows) * target.RowBytes)..].Clear();
            for (int row = 0; row < rows; row++)
            {
                if ((row & 63) == 0) token.ThrowIfCancellationRequested();
                var targetRow = destination.Slice((y + row) * target.RowBytes, target.RowBytes);
                targetRow[..(x * 4)].Clear();
                original.Slice((y + row - dy) * source.RowBytes + (x - dx) * 4, bytes)
                    .CopyTo(targetRow.Slice(x * 4, bytes));
                targetRow[(x * 4 + bytes)..].Clear();
            }
            target.SetImmutable();
            return SKImage.FromBitmap(target) ?? throw new OutOfMemoryException();
        }
        // Fractional pans, zoom, rotation and other pixel formats retain Skia's
        // existing sampling, blending and color conversion semantics.
        using var surface = SKSurface.Create(new SKImageInfo(source.Width, source.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul)) ?? throw new OutOfMemoryException();
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.SetMatrix(matrix);
        surface.Canvas.DrawBitmap(source, 0, 0);
        return surface.Snapshot();
    }
}
