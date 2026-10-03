using SkiaSharp;

namespace GeoNex.Services;

/// <summary>Resolves one authoritative cursor position for preview and committed clicks.</summary>
public static class DigitizingCursor
{
    public readonly record struct Result(SKPoint Position, SKPoint? Snap);

    /// <summary>Converts a screen-space tolerance to map-local units, including rotated maps.</summary>
    public static float WorldTolerance(SKMatrix localToScreen, float pixels)
    {
        if (!float.IsFinite(pixels) || pixels <= 0) return 0;
        double scale = Math.Sqrt((double)localToScreen.ScaleX * localToScreen.ScaleX +
                                 (double)localToScreen.SkewY * localToScreen.SkewY);
        double tolerance = pixels / scale;
        return double.IsFinite(tolerance) && tolerance > 0 && tolerance <= float.MaxValue
            ? (float)tolerance : 0;
    }

    /// <summary>Distance constraints take precedence over snap and use project units.</summary>
    public static Result Resolve(
        SKPoint cursor,
        SKMatrix localToScreen,
        float tolerancePixels,
        bool vertices,
        bool edges,
        SKPoint? anchor,
        bool constrainDistance,
        double distance,
        bool fixedDistance,
        Func<SKPoint, float, bool, bool, SKPoint?> findSnap,
        bool midpoints = false,
        bool intersections = false)
    {
        if (!float.IsFinite(cursor.X) || !float.IsFinite(cursor.Y))
            throw new ArgumentException("A posição do cursor deve ser finita.", nameof(cursor));

        if (constrainDistance && anchor is SKPoint start && double.IsFinite(distance) && distance > 0)
        {
            double dx = (double)cursor.X - start.X;
            double dy = (double)cursor.Y - start.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length > 0 && (fixedDistance || length > distance))
            {
                cursor = new SKPoint(
                    (float)(start.X + dx / length * distance),
                    (float)(start.Y + dy / length * distance));
            }
            return new(cursor, null);
        }

        float tolerance = WorldTolerance(localToScreen, tolerancePixels);
        SKPoint? snap = tolerance > 0 && (vertices || edges || midpoints || intersections)
            ? findSnap(cursor, tolerance, vertices, edges)
            : null;
        return new(snap ?? cursor, snap);
    }
}
