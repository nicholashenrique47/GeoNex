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

    /// <summary>Distance constraints limit the cursor to a radius locus while allowing magnetic snapping to intersecting features.</summary>
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
        bool intersections = false,
        bool orthogonal = false)
    {
        if (!float.IsFinite(cursor.X) || !float.IsFinite(cursor.Y))
            throw new ArgumentException("A posição do cursor deve ser finita.", nameof(cursor));

        if (orthogonal && anchor is SKPoint orthogonalStart)
        {
            double dx = (double)cursor.X - orthogonalStart.X;
            double dy = (double)cursor.Y - orthogonalStart.Y;
            if (double.IsFinite(dx) && double.IsFinite(dy) && (dx != 0 || dy != 0))
                cursor = Math.Abs(dx) >= Math.Abs(dy)
                    ? new SKPoint(cursor.X, orthogonalStart.Y)
                    : new SKPoint(orthogonalStart.X, cursor.Y);
        }

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

            float tol = WorldTolerance(localToScreen, tolerancePixels);
            if (tol > 0 && (vertices || edges || midpoints || intersections))
            {
                SKPoint? snapCandidate = findSnap(cursor, tol, vertices, edges);
                if (snapCandidate is SKPoint snapPt)
                {
                    double snapDx = (double)snapPt.X - start.X;
                    double snapDy = (double)snapPt.Y - start.Y;
                    double snapDist = Math.Sqrt(snapDx * snapDx + snapDy * snapDy);
                    if (Math.Abs(snapDist - distance) <= tol * 1.5 || (!fixedDistance && snapDist <= distance + tol))
                    {
                        return new(snapPt, snapPt);
                    }
                }
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
