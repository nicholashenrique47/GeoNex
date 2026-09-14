using GeoNex.Services;
using SkiaSharp;

internal static class LayerCameraContracts
{
    public static void Run()
    {
        var world = new SKRect(-20037508, -20037508, 20037508, 20037508);
        // Projected local bounds after loading UTM vectors into a Web Mercator project.
        var layer = new SKRect(-5410000, 2980000, -5400000, 2995000);
        foreach (var scene in new[] { world, layer, new SKRect(-6000000, 2000000, 0, 6000000) })
        foreach (var size in new[] { (800, 600), (1366, 768), (1920, 1080) })
        foreach (float dpi in new[] { 1f, 1.25f, 2f })
        {
            Check(LayerCameraPolicy.TryFit(scene, layer, size.Item1, size.Item2, out var camera), "fit succeeds");
            float scale = Math.Min(size.Item1 / scene.Width, size.Item2 / scene.Height) * .8f * (float)camera.Zoom;
            var center = MapCoordinateSpace.ApplyCssPanToLocalCenter(new SKPoint(scene.MidX, scene.MidY),
                scale, (float)camera.PanX, (float)camera.PanY);
            int pad = NavigationFramePolicy.Padding(size.Item1, size.Item2, dpi);
            var viewport = MapViewportMetrics.Create(size.Item1 + 2 * pad, size.Item2 + 2 * pad, dpi);
            var frame = MapCoordinateFrame.Create(viewport, center, scale);
            var middle = frame.LocalToPhysical(new SKPoint(layer.MidX, layer.MidY));
            Check(Math.Abs(middle.X - viewport.PhysicalWidth / 2f) < .5f &&
                Math.Abs(middle.Y - viewport.PhysicalHeight / 2f) < .5f, "layer centered with basemap, DPI and padding");
            Check(layer.Width * scale <= size.Item1 * .801f &&
                layer.Height * scale <= size.Item2 * .801f, "layer fits visible viewport");
        }
        Check(LayerCameraPolicy.TryFit(layer, layer, 800, 600, out var alone) &&
            alone.Zoom == 1 && alone.PanX == 0 && alone.PanY == 0, "vector-only behavior unchanged");
        Check(!LayerCameraPolicy.TryFit(world, SKRect.Empty, 800, 600, out _), "empty layer rejected");
        Check(!LayerCameraPolicy.TryFit(world, layer, 0, 600, out _), "invalid viewport rejected");
        Check(!LayerCameraPolicy.TryFit(new SKRect(float.NaN, 0, 1, 1), layer, 800, 600, out _), "invalid scene rejected");
        Console.WriteLine("Layer camera: PASS (basemap + vector, vector-only, current scene, CSS/DPI/overscan, invalid inputs)");
    }

    private static void Check(bool valid, string message)
    { if (!valid) throw new InvalidOperationException(message); }
}
