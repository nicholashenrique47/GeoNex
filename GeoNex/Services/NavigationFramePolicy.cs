using SkiaSharp;

namespace GeoNex.Services;

public static class NavigationFramePolicy
{
    public static SKMatrix RasterPreviewMatrix(MapCoordinateFrame source, int bitmapWidth, int bitmapHeight,
        MapCoordinateFrame target)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitmapWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitmapHeight);
        // Compose around camera centers in double precision. Multiplying the
        // float world translations/inverses shifts imagery by several pixels
        // at Mercator/UTM magnitudes, even for an identical source/target camera.
        var s = source.LocalToPhysicalMatrix;
        var t = target.LocalToPhysicalMatrix;
        double determinant = (double)s.ScaleX * s.ScaleY - (double)s.SkewX * s.SkewY;
        if (!double.IsFinite(determinant) || determinant == 0)
            throw new ArgumentException("Noninvertible source frame.", nameof(source));
        double a = ((double)t.ScaleX * s.ScaleY - (double)t.SkewX * s.SkewY) / determinant;
        double b = ((double)t.SkewX * s.ScaleX - (double)t.ScaleX * s.SkewX) / determinant;
        double c = ((double)t.SkewY * s.ScaleY - (double)t.ScaleY * s.SkewY) / determinant;
        double d = ((double)t.ScaleY * s.ScaleX - (double)t.SkewY * s.SkewX) / determinant;
        double dx = source.PreciseLocalCenter.X - target.PreciseLocalCenter.X;
        double dy = source.PreciseLocalCenter.Y - target.PreciseLocalCenter.Y;
        double x = target.Viewport.PhysicalWidth / 2.0 - a * source.Viewport.PhysicalWidth / 2.0 - b * source.Viewport.PhysicalHeight / 2.0
            + t.ScaleX * dx + t.SkewX * dy;
        double y = target.Viewport.PhysicalHeight / 2.0 - c * source.Viewport.PhysicalWidth / 2.0 - d * source.Viewport.PhysicalHeight / 2.0
            + t.SkewY * dx + t.ScaleY * dy;
        double sx = source.Viewport.PhysicalWidth / (double)bitmapWidth;
        double sy = source.Viewport.PhysicalHeight / (double)bitmapHeight;
        return new SKMatrix((float)(a * sx), (float)(b * sy), (float)x,
            (float)(c * sx), (float)(d * sy), (float)y, 0, 0, 1);
    }

    public static MapCoordinateFrame Rebase(MapCoordinateFrame source, float sourceScale,
        MapCameraState sourceCamera, MapCameraState targetCamera)
    {
        var viewport = source.Viewport;
        SKPoint center = source.LocalCenter;
        float targetScale = (float)(sourceScale * targetCamera.Zoom / sourceCamera.Zoom);
        var baseCenter = new SKPoint(center.X + (float)(sourceCamera.PanX / sourceScale),
            center.Y + (float)(sourceCamera.PanY / sourceScale));
        var targetCenter = new SKPoint(baseCenter.X - (float)(targetCamera.PanX / targetScale),
            baseCenter.Y - (float)(targetCamera.PanY / targetScale));
        return MapCoordinateFrame.Create(viewport, targetCenter, targetScale);
    }

    // Unrotated navigation only. Activate alongside the precise final camera,
    // never independently: otherwise the old final frame would snap back.
    public static MapCoordinateFrame RebasePrecisely(MapCoordinateFrame source, float sourceScale,
        MapCameraState sourceCamera, MapCameraState targetCamera)
    {
        if (!float.IsFinite(sourceScale) || sourceScale <= 0 ||
            !double.IsFinite(sourceCamera.Zoom) || sourceCamera.Zoom <= 0 ||
            !double.IsFinite(targetCamera.Zoom) || targetCamera.Zoom <= 0 ||
            !double.IsFinite(sourceCamera.PanX) || !double.IsFinite(sourceCamera.PanY) ||
            !double.IsFinite(targetCamera.PanX) || !double.IsFinite(targetCamera.PanY) ||
            source.LocalToCssMatrix.SkewX != 0 || source.LocalToCssMatrix.SkewY != 0 ||
            source.LocalToCssMatrix.ScaleX != sourceScale || source.LocalToCssMatrix.ScaleY != sourceScale)
            throw new ArgumentException("Invalid unrotated navigation camera.");
        float targetScale = (float)(sourceScale * targetCamera.Zoom / sourceCamera.Zoom);
        double ratio = (double)targetScale / sourceScale;
        var center = source.PreciseLocalCenter;
        var target = new MapLocalCoordinate(center.X + (sourceCamera.PanX - targetCamera.PanX / ratio) / sourceScale,
            center.Y + (sourceCamera.PanY - targetCamera.PanY / ratio) / sourceScale);
        return MapCoordinateFrame.CreatePrecise(source.Viewport, target, targetScale);
    }

    public static int Padding(int width, int height, float dpi)
    {
        if (!MapViewportMetrics.TryCreate(width, height, dpi, out _)) return 0;
        int padding = Math.Min(Math.Min(width, height) / 4, (int)(256 / dpi));
        // Limit the persistent CPU/GPU image footprint on large/HiDPI displays.
        while (padding > 0 && (!MapViewportMetrics.TryCreate(width + padding * 2,
                   height + padding * 2, dpi, out var padded) ||
               (long)padded.PhysicalWidth * padded.PhysicalHeight > 16_777_216)) padding /= 2;
        return padding;
    }

    public static bool TryReuse(MapCoordinateFrame source, MapCoordinateFrame target,
        int visibleWidth, int visibleHeight, out SKMatrix sourcePixelsToTarget)
    {
        sourcePixelsToTarget = RasterPreviewMatrix(source, source.Viewport.PhysicalWidth, source.Viewport.PhysicalHeight, target);
        SKMatrix toSource = RasterPreviewMatrix(target, target.Viewport.CssWidth, target.Viewport.CssHeight, source);
        float x = (target.Viewport.CssWidth - visibleWidth) / 2f;
        float y = (target.Viewport.CssHeight - visibleHeight) / 2f;
        SKPoint[] corners = [new(x, y), new(x + visibleWidth, y),
            new(x + visibleWidth, y + visibleHeight), new(x, y + visibleHeight)];
        foreach (SKPoint corner in corners)
        {
            SKPoint p = toSource.MapPoint(corner);
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || p.X < -0.01f || p.Y < -0.01f ||
                p.X > source.Viewport.PhysicalWidth + 0.01f || p.Y > source.Viewport.PhysicalHeight + 0.01f)
                return false;
        }
        return true;
    }
}
