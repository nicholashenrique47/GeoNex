using SkiaSharp;

namespace GeoNex.Services;

public static class NavigationFramePolicy
{
    public static MapCoordinateFrame Rebase(MapCoordinateFrame source, float sourceScale,
        MapCameraState sourceCamera, MapCameraState targetCamera)
    {
        var viewport = source.Viewport;
        SKPoint center = source.PhysicalToLocal(new SKPoint(viewport.PhysicalWidth / 2f, viewport.PhysicalHeight / 2f));
        float targetScale = (float)(sourceScale * targetCamera.Zoom / sourceCamera.Zoom);
        var baseCenter = new SKPoint(center.X + (float)(sourceCamera.PanX / sourceScale),
            center.Y + (float)(sourceCamera.PanY / sourceScale));
        var targetCenter = new SKPoint(baseCenter.X - (float)(targetCamera.PanX / targetScale),
            baseCenter.Y - (float)(targetCamera.PanY / targetScale));
        return MapCoordinateFrame.Create(viewport, targetCenter, targetScale);
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
        sourcePixelsToTarget = source.PhysicalToLocalMatrix.PostConcat(target.LocalToPhysicalMatrix);
        SKMatrix toSource = target.CssToLocalMatrix.PostConcat(source.LocalToPhysicalMatrix);
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
