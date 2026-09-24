using GeoNex.Services;
using SkiaSharp;

internal static class OnlineAlignmentContracts
{
    public static void Run()
    {
        var viewport = MapViewportMetrics.Create(1920, 1080, 2);
        foreach (float scale in new[] { 3.7f, 20.1f, 83.7f })
        {
            var center = new SKPoint(-5400000, 3000000);
            var source = MapCoordinateFrame.Create(viewport, center, scale);
            var same = NavigationFramePolicy.RasterPreviewMatrix(source, viewport.PhysicalWidth, viewport.PhysicalHeight, source);
            var sample = new SKPoint(873, 621);
            var actual = same.MapPoint(sample);
            double error = Math.Max(Math.Abs(actual.X - sample.X), Math.Abs(actual.Y - sample.Y));
            Console.WriteLine($"Raster preview same-camera scale={scale} error_pixels={error:F4}");
            if (error > .001) throw new InvalidOperationException("Same camera displaces raster preview");
            var target = MapCoordinateFrame.Create(viewport, new SKPoint(center.X + .5f, center.Y - .5f), scale);
            var moved = NavigationFramePolicy.RasterPreviewMatrix(source, viewport.PhysicalWidth, viewport.PhysicalHeight, target).MapPoint(sample);
            if (Math.Abs(moved.X - (sample.X - scale)) > .001 || Math.Abs(moved.Y - (sample.Y + scale)) > .001)
                throw new InvalidOperationException("Pan relative to world origin is imprecise");
        }
        Console.WriteLine("Online alignment: PASS (large Mercator/UTM coordinates, identity, half-meter pan, HiDPI)");
    }
}
