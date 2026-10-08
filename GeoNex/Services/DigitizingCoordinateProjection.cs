using SkiaSharp;

namespace GeoNex.Services;

/// <summary>Projects a typed coordinate into the map CRS and the map's local drawing space.</summary>
public static class DigitizingCoordinateProjection
{
    public static SKPoint ToMapLocal(
        double x,
        double y,
        string inputSrs,
        string mapSrs,
        double localOriginX,
        double localOriginY)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new ArgumentOutOfRangeException(nameof(x), "X and Y must be finite coordinates.");
        ArgumentException.ThrowIfNullOrWhiteSpace(inputSrs);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapSrs);

        if (!SrsFactory.IsSame(inputSrs, mapSrs))
        {
            double[] coordinate = [x, y, 0];
            using var transform = SrsFactory.CreateTransform(inputSrs, mapSrs);
            transform.TransformPoint(coordinate);
            x = coordinate[0];
            y = coordinate[1];
        }

        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new InvalidOperationException("The coordinate transformation produced a non-finite point.");

        return MapCoordinateSpace.WorldToLocal(x, y, localOriginX, localOriginY);
    }
}
