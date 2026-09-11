using SkiaSharp;

namespace GeoNex.Services;

/// <summary>
/// Describes one browser viewport without confusing CSS pixels with bitmap pixels.
/// The effective X/Y scales include the unavoidable integer rounding of the backing bitmap.
/// </summary>
public readonly record struct MapViewportMetrics
{
    public const float MinimumDpi = 0.5f;
    public const float MaximumDpi = 4.0f;
    public const int MaximumCssDimension = 16_384;
    public const int MaximumPhysicalDimension = 32_768;
    public const long MaximumPhysicalPixels = 128L * 1024L * 1024L;

    private MapViewportMetrics(
        int cssWidth,
        int cssHeight,
        float requestedDpi,
        int physicalWidth,
        int physicalHeight)
    {
        CssWidth = cssWidth;
        CssHeight = cssHeight;
        RequestedDpi = requestedDpi;
        PhysicalWidth = physicalWidth;
        PhysicalHeight = physicalHeight;
        PhysicalScaleX = physicalWidth / (float)cssWidth;
        PhysicalScaleY = physicalHeight / (float)cssHeight;
    }

    public int CssWidth { get; }
    public int CssHeight { get; }
    public float RequestedDpi { get; }
    public int PhysicalWidth { get; }
    public int PhysicalHeight { get; }
    public float PhysicalScaleX { get; }
    public float PhysicalScaleY { get; }
    public float PixelScale => MathF.Sqrt(PhysicalScaleX * PhysicalScaleY);

    public static bool TryCreate(
        int cssWidth,
        int cssHeight,
        float dpi,
        out MapViewportMetrics viewport)
    {
        viewport = default;
        if (cssWidth <= 0 || cssHeight <= 0 ||
            cssWidth > MaximumCssDimension || cssHeight > MaximumCssDimension ||
            !float.IsFinite(dpi) || dpi < MinimumDpi || dpi > MaximumDpi)
            return false;

        double requestedPhysicalWidth = cssWidth * (double)dpi;
        double requestedPhysicalHeight = cssHeight * (double)dpi;
        if (!double.IsFinite(requestedPhysicalWidth) || !double.IsFinite(requestedPhysicalHeight))
            return false;

        int physicalWidth = Math.Max(1, (int)Math.Round(requestedPhysicalWidth, MidpointRounding.AwayFromZero));
        int physicalHeight = Math.Max(1, (int)Math.Round(requestedPhysicalHeight, MidpointRounding.AwayFromZero));
        if (physicalWidth > MaximumPhysicalDimension || physicalHeight > MaximumPhysicalDimension ||
            (long)physicalWidth * physicalHeight > MaximumPhysicalPixels)
            return false;

        viewport = new MapViewportMetrics(cssWidth, cssHeight, dpi, physicalWidth, physicalHeight);
        return true;
    }

    public static MapViewportMetrics Create(int cssWidth, int cssHeight, float dpi)
    {
        if (!TryCreate(cssWidth, cssHeight, dpi, out MapViewportMetrics viewport))
            throw new ArgumentOutOfRangeException(nameof(dpi), "Invalid CSS viewport or DPI.");
        return viewport;
    }

    public SKPoint CssToPhysical(SKPoint point) =>
        new(point.X * PhysicalScaleX, point.Y * PhysicalScaleY);

    public SKPoint PhysicalToCss(SKPoint point) =>
        new(point.X / PhysicalScaleX, point.Y / PhysicalScaleY);

    public bool MatchesBitmap(SKBitmap bitmap) =>
        bitmap.Width == PhysicalWidth && bitmap.Height == PhysicalHeight;
}

public readonly record struct MapWorldCoordinate(double X, double Y);

/// <summary>
/// Immutable transformation frame shared by the live map and print renderer.
/// Local coordinates are the float, origin-shifted and Y-inverted coordinates used by Skia.
/// </summary>
public readonly struct MapCoordinateFrame
{
    private MapCoordinateFrame(
        MapViewportMetrics viewport,
        SKMatrix localToCss,
        SKMatrix cssToLocal,
        SKMatrix localToPhysical,
        SKMatrix physicalToLocal,
        SKPoint topLeft,
        SKPoint topRight,
        SKPoint bottomRight,
        SKPoint bottomLeft)
    {
        Viewport = viewport;
        LocalToCssMatrix = localToCss;
        CssToLocalMatrix = cssToLocal;
        LocalToPhysicalMatrix = localToPhysical;
        PhysicalToLocalMatrix = physicalToLocal;
        TopLeftLocal = topLeft;
        TopRightLocal = topRight;
        BottomRightLocal = bottomRight;
        BottomLeftLocal = bottomLeft;
        LocalViewportBounds = new SKRect(
            MathF.Min(MathF.Min(topLeft.X, topRight.X), MathF.Min(bottomRight.X, bottomLeft.X)),
            MathF.Min(MathF.Min(topLeft.Y, topRight.Y), MathF.Min(bottomRight.Y, bottomLeft.Y)),
            MathF.Max(MathF.Max(topLeft.X, topRight.X), MathF.Max(bottomRight.X, bottomLeft.X)),
            MathF.Max(MathF.Max(topLeft.Y, topRight.Y), MathF.Max(bottomRight.Y, bottomLeft.Y)));
    }

    public MapViewportMetrics Viewport { get; }
    public SKMatrix LocalToCssMatrix { get; }
    public SKMatrix CssToLocalMatrix { get; }
    public SKMatrix LocalToPhysicalMatrix { get; }
    public SKMatrix PhysicalToLocalMatrix { get; }
    public SKPoint TopLeftLocal { get; }
    public SKPoint TopRightLocal { get; }
    public SKPoint BottomRightLocal { get; }
    public SKPoint BottomLeftLocal { get; }
    public SKRect LocalViewportBounds { get; }

    public static bool TryCreate(
        MapViewportMetrics viewport,
        SKPoint localCenter,
        float localToCssScale,
        float clockwiseRotationDegrees,
        out MapCoordinateFrame frame,
        double layoutWidth = 0,
        double layoutHeight = 0)
    {
        frame = default;
        if (viewport.CssWidth <= 0 || viewport.CssHeight <= 0 ||
            !float.IsFinite(localCenter.X) || !float.IsFinite(localCenter.Y) ||
            !float.IsFinite(localToCssScale) || localToCssScale <= 0 ||
            !float.IsFinite(clockwiseRotationDegrees))
            return false;

        SKMatrix localToCss = SKMatrix.CreateTranslation(-localCenter.X, -localCenter.Y);
        localToCss = localToCss.PostConcat(SKMatrix.CreateScale(localToCssScale, localToCssScale));
        localToCss = localToCss.PostConcat(SKMatrix.CreateRotationDegrees(-clockwiseRotationDegrees));
        // Print frames may have fractional CSS dimensions (millimeters). Cancel integer request
        // rounding before fitting the resulting image back into that exact physical frame.
        if (layoutWidth != 0 || layoutHeight != 0)
        {
            if (!double.IsFinite(layoutWidth) || !double.IsFinite(layoutHeight) || layoutWidth <= 0 || layoutHeight <= 0)
                return false;
            localToCss = localToCss.PostConcat(SKMatrix.CreateScale(
                (float)(viewport.CssWidth / layoutWidth), (float)(viewport.CssHeight / layoutHeight)));
        }
        localToCss = localToCss.PostConcat(SKMatrix.CreateTranslation(
            viewport.CssWidth / 2f,
            viewport.CssHeight / 2f));

        if (!localToCss.TryInvert(out SKMatrix cssToLocal)) return false;

        SKMatrix localToPhysical = localToCss.PostConcat(SKMatrix.CreateScale(
            viewport.PhysicalScaleX,
            viewport.PhysicalScaleY));
        if (!localToPhysical.TryInvert(out SKMatrix physicalToLocal)) return false;

        SKPoint topLeft = physicalToLocal.MapPoint(SKPoint.Empty);
        SKPoint topRight = physicalToLocal.MapPoint(new SKPoint(viewport.PhysicalWidth, 0));
        SKPoint bottomRight = physicalToLocal.MapPoint(new SKPoint(viewport.PhysicalWidth, viewport.PhysicalHeight));
        SKPoint bottomLeft = physicalToLocal.MapPoint(new SKPoint(0, viewport.PhysicalHeight));
        if (!IsFinite(topLeft) || !IsFinite(topRight) || !IsFinite(bottomRight) || !IsFinite(bottomLeft))
            return false;

        frame = new MapCoordinateFrame(
            viewport,
            localToCss,
            cssToLocal,
            localToPhysical,
            physicalToLocal,
            topLeft,
            topRight,
            bottomRight,
            bottomLeft);
        return true;
    }

    public static MapCoordinateFrame Create(
        MapViewportMetrics viewport,
        SKPoint localCenter,
        float localToCssScale,
        float clockwiseRotationDegrees = 0)
    {
        if (!TryCreate(viewport, localCenter, localToCssScale, clockwiseRotationDegrees, out MapCoordinateFrame frame))
            throw new ArgumentException("The coordinate frame is not invertible.");
        return frame;
    }

    public SKPoint LocalToCss(SKPoint point) => LocalToCssMatrix.MapPoint(point);
    public SKPoint CssToLocal(SKPoint point) => CssToLocalMatrix.MapPoint(point);
    public SKPoint LocalToPhysical(SKPoint point) => LocalToPhysicalMatrix.MapPoint(point);
    public SKPoint PhysicalToLocal(SKPoint point) => PhysicalToLocalMatrix.MapPoint(point);

    private static bool IsFinite(SKPoint point) => float.IsFinite(point.X) && float.IsFinite(point.Y);
}

public readonly record struct MapCameraState(double PanX, double PanY, double Zoom);

public static class MapCoordinateSpace
{
    public static SKPoint WorldToLocal(
        double worldX,
        double worldY,
        double localOriginX,
        double localOriginY)
    {
        double localX = worldX - localOriginX;
        double localY = localOriginY - worldY;
        if (!double.IsFinite(localX) || !double.IsFinite(localY) ||
            Math.Abs(localX) > float.MaxValue || Math.Abs(localY) > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(worldX), "World coordinate cannot be represented locally.");
        return new SKPoint((float)localX, (float)localY);
    }

    public static MapWorldCoordinate LocalToWorld(
        SKPoint local,
        double localOriginX,
        double localOriginY) =>
        new(local.X + localOriginX, localOriginY - local.Y);

    public static SKPoint ApplyCssPanToLocalCenter(
        SKPoint baseLocalCenter,
        float localToCssScale,
        double panX,
        double panY,
        float clockwiseRotationDegrees = 0)
    {
        if (!float.IsFinite(localToCssScale) || localToCssScale <= 0 ||
            !double.IsFinite(panX) || !double.IsFinite(panY) ||
            !float.IsFinite(clockwiseRotationDegrees))
            throw new ArgumentOutOfRangeException(nameof(localToCssScale));

        double radians = clockwiseRotationDegrees * Math.PI / 180.0;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        double localPanX = (panX * cosine - panY * sine) / localToCssScale;
        double localPanY = (panX * sine + panY * cosine) / localToCssScale;
        return new SKPoint(
            baseLocalCenter.X - (float)localPanX,
            baseLocalCenter.Y - (float)localPanY);
    }

    public static bool TryApplyCssCameraDelta(
        MapCameraState current,
        double deltaX,
        double deltaY,
        double scaleMultiplier,
        out MapCameraState updated,
        double minimumZoom = 0.05,
        double maximumZoom = 1_000_000.0)
    {
        updated = current;
        if (!double.IsFinite(current.PanX) || !double.IsFinite(current.PanY) ||
            !double.IsFinite(current.Zoom) || current.Zoom <= 0 ||
            !double.IsFinite(deltaX) || !double.IsFinite(deltaY) ||
            !double.IsFinite(scaleMultiplier) || scaleMultiplier <= 0 ||
            !double.IsFinite(minimumZoom) || !double.IsFinite(maximumZoom) ||
            minimumZoom <= 0 || maximumZoom < minimumZoom)
            return false;

        double oldZoom = Math.Clamp(current.Zoom, minimumZoom, maximumZoom);
        double newZoom = Math.Clamp(oldZoom * scaleMultiplier, minimumZoom, maximumZoom);
        double effectiveScale = newZoom / oldZoom;
        double newPanX = current.PanX * effectiveScale + deltaX;
        double newPanY = current.PanY * effectiveScale + deltaY;
        if (!double.IsFinite(newPanX) || !double.IsFinite(newPanY) ||
            Math.Abs(newPanX) > float.MaxValue || Math.Abs(newPanY) > float.MaxValue)
            return false;

        updated = new MapCameraState(newPanX, newPanY, newZoom);
        return true;
    }
}
