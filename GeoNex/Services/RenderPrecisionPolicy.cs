using SkiaSharp;

namespace GeoNex.Services;

/// <summary>Keep subpixel detail before doubles are converted to Skia floats.</summary>
public static class RenderPrecisionPolicy
{
    public static bool NeedsLocalOrigin(MapLocalCoordinate center, float physicalScale)
    {
        double magnitude = Math.Max(Math.Abs(center.X), Math.Abs(center.Y));
        double next = Math.BitIncrement(magnitude);
        return (next - magnitude) * physicalScale > 0.125;
    }

    public static bool NeedsLocalOrigin(SKPoint center, float physicalScale)
    {
        float magnitude = Math.Max(Math.Abs(center.X), Math.Abs(center.Y));
        return (MathF.BitIncrement(magnitude) - magnitude) * physicalScale > 0.125f;
    }
}
