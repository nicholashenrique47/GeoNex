using System.Buffers;
using SkiaSharp;

namespace GeoNex.Services;

/// <summary>Bulk Skia submission after reprojection; bounded display-only reduction for previews.</summary>
public static class TransformedRingWriter
{
    public static void Append(SKPath path, double[] x, double[] y, int start, int count,
        double offsetX, double offsetY, bool closed, float zoom, bool preview,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (start < 0 || count < 0 || start > x.Length - count || start > y.Length - count)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return;
        SKPoint[] buffer = ArrayPool<SKPoint>.Shared.Rent(count);
        try
        {
            Fill();
            int written = count;
            if (preview && count > 4 && float.IsFinite(zoom) && zoom > 0)
            {
                double tolerance = .85 / zoom;
                double squared = tolerance * tolerance;
                double originalArea = closed ? Area(buffer.AsSpan(0, count)) : 0;
                written = 1;
                for (int i = 1; i < count - 1; i++)
                {
                    if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    double dx = (double)buffer[i].X - buffer[written - 1].X;
                    double dy = (double)buffer[i].Y - buffer[written - 1].Y;
                    if (dx * dx + dy * dy >= squared) buffer[written++] = buffer[i];
                }
                buffer[written++] = buffer[count - 1];
                // Never collapse a ring or reverse its winding (outer ring versus hole).
                double reducedArea = closed ? Area(buffer.AsSpan(0, written)) : 0;
                if (closed && (written < 4 || !double.IsFinite(originalArea) ||
                    !double.IsFinite(reducedArea) || originalArea == 0 || reducedArea == 0 ||
                    Math.Sign(originalArea) != Math.Sign(reducedArea)))
                {
                    Fill();
                    written = count;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            path.AddPoly(buffer.AsSpan(0, written), closed);
        }
        finally { ArrayPool<SKPoint>.Shared.Return(buffer); }

        void Fill()
        {
            for (int i = 0; i < count; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                buffer[i] = new SKPoint((float)(x[start + i] - offsetX), -(float)(y[start + i] - offsetY));
            }
        }
    }

    private static double Area(ReadOnlySpan<SKPoint> points)
    {
        // Translate before summing to avoid cancellation at large projected coordinates.
        double area = 0, ox = points[0].X, oy = points[0].Y;
        var previous = points[^1];
        foreach (var point in points)
        {
            area += ((double)previous.X - ox) * ((double)point.Y - oy) -
                ((double)point.X - ox) * ((double)previous.Y - oy);
            previous = point;
        }
        return area;
    }

    public static bool SkipPreviewBorder(bool interactive, int features, int points) =>
        interactive && (features > 500 || points > 50_000);
}
