using SkiaSharp;

namespace GeoNex.Services;

public readonly partial struct MapCoordinateFrame
{
    // LocalCenter/global SKMatrix properties remain float compatibility views.
    // Precise drawing must use a nearby path origin and the matrix below; tools
    // must use the precise inverse, not invert a rounded world translation.
    private MapCoordinateFrame(MapCoordinateFrame compatible, MapLocalCoordinate center)
    {
        this = compatible;
        PreciseLocalCenter = center;
    }

    public static bool TryCreatePrecise(MapViewportMetrics viewport, MapLocalCoordinate center,
        float scale, float rotation, out MapCoordinateFrame frame, double layoutWidth = 0, double layoutHeight = 0)
    {
        frame = default;
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) ||
            !TryCreate(viewport, new SKPoint((float)center.X, (float)center.Y), scale, rotation,
                out var compatible, layoutWidth, layoutHeight)) return false;
        frame = new(compatible, center);
        return true;
    }

    public static MapCoordinateFrame CreatePrecise(MapViewportMetrics viewport, MapLocalCoordinate center,
        float scale, float rotation = 0)
        => TryCreatePrecise(viewport, center, scale, rotation, out var frame)
            ? frame : throw new ArgumentException("The precise coordinate frame is not invertible.");

    public SKMatrix LocalToPhysicalForOrigin(MapLocalCoordinate pathOrigin)
    {
        var linear = LocalToPhysicalMatrix;
        double dx = pathOrigin.X - PreciseLocalCenter.X, dy = pathOrigin.Y - PreciseLocalCenter.Y;
        var translation = FinitePoint(Viewport.PhysicalWidth / 2d + linear.ScaleX * dx + linear.SkewX * dy,
            Viewport.PhysicalHeight / 2d + linear.SkewY * dx + linear.ScaleY * dy);
        return new SKMatrix(linear.ScaleX, linear.SkewX, translation.X,
            linear.SkewY, linear.ScaleY, translation.Y, 0, 0, 1);
    }

    public SKPoint PreciseLocalToCss(MapLocalCoordinate point)
        => Forward(point, LocalToCssMatrix, Viewport.CssWidth, Viewport.CssHeight);

    public SKPoint PreciseLocalToPhysical(MapLocalCoordinate point)
        => Forward(point, LocalToPhysicalMatrix, Viewport.PhysicalWidth, Viewport.PhysicalHeight);

    public MapLocalCoordinate CssToPreciseLocal(SKPoint point)
        => Inverse(point, LocalToCssMatrix, Viewport.CssWidth, Viewport.CssHeight);

    public MapLocalCoordinate PhysicalToPreciseLocal(SKPoint point)
        => Inverse(point, LocalToPhysicalMatrix, Viewport.PhysicalWidth, Viewport.PhysicalHeight);

    // Outward float rounding prevents dropping a thin query sliver after the
    // double camera has been expressed relative to the geometry-cache origin.
    public SKRect LocalViewportBoundsForOrigin(MapLocalCoordinate origin)
    {
        var linear = LocalToPhysicalMatrix;
        int width = Viewport.PhysicalWidth, height = Viewport.PhysicalHeight;
        MapLocalCoordinate Offset(SKPoint point) => InverseOffset(point, linear, width, height);
        var a = Offset(SKPoint.Empty);
        var b = Offset(new(Viewport.PhysicalWidth, 0));
        var c = Offset(new(Viewport.PhysicalWidth, Viewport.PhysicalHeight));
        var d = Offset(new(0, Viewport.PhysicalHeight));
        double dx = PreciseLocalCenter.X - origin.X, dy = PreciseLocalCenter.Y - origin.Y;
        return new(Lower(Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) + dx),
            Lower(Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)) + dy),
            Upper(Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)) + dx),
            Upper(Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)) + dy));
    }

    private SKPoint Forward(MapLocalCoordinate point, SKMatrix linear, int width, int height)
    {
        double x = point.X - PreciseLocalCenter.X, y = point.Y - PreciseLocalCenter.Y;
        return FinitePoint(width / 2d + linear.ScaleX * x + linear.SkewX * y,
            height / 2d + linear.SkewY * x + linear.ScaleY * y);
    }

    private MapLocalCoordinate Inverse(SKPoint point, SKMatrix linear, int width, int height)
    {
        var offset = InverseOffset(point, linear, width, height);
        return new(PreciseLocalCenter.X + offset.X, PreciseLocalCenter.Y + offset.Y);
    }

    private static MapLocalCoordinate InverseOffset(SKPoint point, SKMatrix linear, int width, int height)
    {
        double determinant = (double)linear.ScaleX * linear.ScaleY - (double)linear.SkewX * linear.SkewY;
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !double.IsFinite(determinant) || determinant == 0)
            throw new ArgumentException("Noninvertible frame or nonfinite screen point.");
        double x = point.X - width / 2d, y = point.Y - height / 2d;
        return new((linear.ScaleY * x - linear.SkewX * y) / determinant,
            (linear.ScaleX * y - linear.SkewY * x) / determinant);
    }

    private static SKPoint FinitePoint(double x, double y)
        => float.IsFinite((float)x) && float.IsFinite((float)y)
            ? new((float)x, (float)y) : throw new ArgumentOutOfRangeException(nameof(x));
    private static float Lower(double value)
    {
        if (!float.IsFinite((float)value)) throw new ArgumentOutOfRangeException(nameof(value));
        float rounded = (float)value;
        return rounded > value ? MathF.BitDecrement(rounded) : rounded;
    }
    private static float Upper(double value)
    {
        if (!float.IsFinite((float)value)) throw new ArgumentOutOfRangeException(nameof(value));
        float rounded = (float)value;
        return rounded < value ? MathF.BitIncrement(rounded) : rounded;
    }
}
