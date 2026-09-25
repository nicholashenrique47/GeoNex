using GeoNex.Services;
using SkiaSharp;

internal static class PreciseCameraContracts
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    public static void Run()
    {
        var focus = new MapLocalCoordinate(-5408820.5, 2984029);
        var legacy = MapCoordinateSpace.ApplyCssPanToLocalCenter(new((float)focus.X, (float)focus.Y), 64, 4, 0);
        Require(legacy.X == focus.X, "Fixture no longer reproduces the lost high-zoom pan");
        int cases = 0, previews = 0;
        foreach (var center in new[] { focus, new MapLocalCoordinate(20037508.25, -20037508.5),
            new MapLocalCoordinate(700000.25, -7200000.125), new MapLocalCoordinate(.125, -.5) })
        foreach (float dpi in new[] { .5f, 1f, 1.25f, 2f, 4f })
        foreach (float scale in new[] { 4f, 16f, 64f, 1000f })
        foreach (float rotation in new[] { 0f, 17f, 90f })
        {
            var viewport = MapViewportMetrics.Create(601, 399, dpi);
            var frame = MapCoordinateFrame.CreatePrecise(viewport, center, scale, rotation);
            var targetCenter = MapCoordinateSpace.ApplyCssPanToPreciseLocalCenter(center, scale, .25, -1, rotation);
            var moved = MapCoordinateFrame.CreatePrecise(viewport, targetCenter, scale, rotation);
            var actual = moved.PreciseLocalToCss(center);
            Require(Math.Abs(actual.X - (viewport.CssWidth / 2d + .25)) < .002 &&
                Math.Abs(actual.Y - (viewport.CssHeight / 2d - 1)) < .002, "Fractional pan was quantized");
            foreach (var screen in new[] { new SKPoint(3.25f, 9.75f), new SKPoint(300.5f, 199.5f), new SKPoint(596, 397) })
            {
                var local = moved.CssToPreciseLocal(screen);
                var roundTrip = moved.PreciseLocalToCss(local);
                Require(SKPoint.Distance(screen, roundTrip) < .002, "Precise picking round trip drifted");
                var physical = moved.PreciseLocalToPhysical(local);
                var fromPhysical = moved.PhysicalToPreciseLocal(physical);
                Require(Math.Abs(fromPhysical.X - local.X) * scale < .002 &&
                    Math.Abs(fromPhysical.Y - local.Y) * scale < .002, "CSS/physical inverse drifted");
            }
            var origin = new MapLocalCoordinate((float)center.X, (float)center.Y);
            var matrix = moved.LocalToPhysicalForOrigin(origin);
            var bounds = moved.LocalViewportBoundsForOrigin(origin);
            var corners = new[] { SKPoint.Empty, new SKPoint(viewport.PhysicalWidth, 0),
                new SKPoint(viewport.PhysicalWidth, viewport.PhysicalHeight), new SKPoint(0, viewport.PhysicalHeight) };
            foreach (var corner in corners)
            {
                var local = moved.PhysicalToPreciseLocal(corner);
                var relative = new SKPoint((float)(local.X - origin.X), (float)(local.Y - origin.Y));
                var mapped = matrix.MapPoint(relative);
                Require(SKPoint.Distance(mapped, corner) < .01, "Near-origin paint differs from picking");
                // Permit only the known double center-add rounding when checking
                // this independent world-coordinate conversion of each corner.
                double epsilon = 1e-8;
                Require(local.X - origin.X >= bounds.Left - epsilon && local.X - origin.X <= bounds.Right + epsilon &&
                    local.Y - origin.Y >= bounds.Top - epsilon && local.Y - origin.Y <= bounds.Bottom + epsilon,
                    "Precise query bounds excluded a viewport corner");
            }
            if (rotation == 0)
            foreach (double ratio in new[] { .5, 1, 2 })
            {
                var camera = new MapCameraState(1000.25, -333.125, 2);
                var targetCamera = new MapCameraState(camera.PanX * ratio + .25, camera.PanY * ratio - 1, camera.Zoom * ratio);
                var rebased = NavigationFramePolicy.RebasePrecisely(frame, scale, camera, targetCamera);
                var reuse = NavigationFramePolicy.RasterPreviewMatrix(frame, viewport.PhysicalWidth, viewport.PhysicalHeight, rebased);
                var sample = new SKPoint(123, 71);
                var reused = reuse.MapPoint(sample);
                Require(Math.Abs(reused.X - ((sample.X - viewport.PhysicalWidth / 2d) * ratio + viewport.PhysicalWidth / 2d + .25 * viewport.PhysicalScaleX)) < .01 &&
                    Math.Abs(reused.Y - ((sample.Y - viewport.PhysicalHeight / 2d) * ratio + viewport.PhysicalHeight / 2d - viewport.PhysicalScaleY)) < .01,
                    "Precise preview disagrees with the camera gesture");
                previews++;
            }
            cases++;
        }
        foreach (float dpi in new[] { 1f, 2f, 4f })
        foreach (float rotation in new[] { 0f, 17f, 90f })
        foreach (bool printLayout in new[] { false, true })
        {
            var viewport = MapViewportMetrics.Create(301, 201, dpi);
            // This double camera shift is lost by the old world SKPoint. Use
            // the established near-zero renderer as an independent pixel oracle.
            var center = new MapLocalCoordinate(focus.X - .0625, focus.Y + .03125);
            double layoutWidth = printLayout ? 300.5 : 0, layoutHeight = printLayout ? 200.75 : 0;
            Require(MapCoordinateFrame.TryCreatePrecise(viewport, center, 64, rotation, out var precise, layoutWidth, layoutHeight), "Precise print frame rejected");
            Require(MapCoordinateFrame.TryCreate(viewport, new SKPoint(-.0625f, .03125f), 64, rotation, out var oracle, layoutWidth, layoutHeight), "Oracle print frame rejected");
            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            path.AddRect(new(-1.25f, -.875f, -.015625f, .875f));
            path.AddRect(new(.015625f, -.875f, 1.25f, .875f));
            byte[] expected = Paint(path, viewport, oracle.LocalToPhysicalMatrix);
            byte[] actual = Paint(path, viewport, precise.LocalToPhysicalForOrigin(focus));
            int maximum = 0;
            for (int i = 0; i < actual.Length; i++) maximum = Math.Max(maximum, Math.Abs(actual[i] - expected[i]));
            Require(maximum <= 1, $"Precise paint disagrees with the near-origin oracle: {maximum}");
        }
        foreach (var bad in new[] { new MapLocalCoordinate(double.NaN, 0), new MapLocalCoordinate(0, double.PositiveInfinity), new MapLocalCoordinate(double.MaxValue, 0) })
            Require(!MapCoordinateFrame.TryCreatePrecise(MapViewportMetrics.Create(600, 400, 1), bad, 64, 0, out _), "Nonfinite center accepted");
        Console.WriteLine($"Precise camera math: PASS ({cases} camera/picking/coverage cases; {previews} preview transforms; small pan retained; 18 fill/stroke/print frames <=1/255 against near-origin oracle). Production camera activation is separate.");
    }

    public static void RunCapture(string file, int width, int height, float scale, float strokeWidth)
    {
        using var path = CapturedPolygonMetrics.Read(file, out int rings);
        var viewport = MapViewportMetrics.Create(width, height, 1);
        var origin = new MapLocalCoordinate(-5408820.5, 2984029);
        int largest = 0;
        foreach (float pan in new[] { .25f, 1f, 4f, 8f })
        {
            var offset = new SKPoint(-pan / scale, pan / (2 * scale));
            var frame = MapCoordinateFrame.CreatePrecise(viewport,
                new(origin.X + offset.X, origin.Y + offset.Y), scale);
            var oracle = MapCoordinateFrame.Create(viewport, offset, scale);
            byte[] expected = Paint(path, viewport, oracle.LocalToPhysicalMatrix, strokeWidth);
            byte[] actual = Paint(path, viewport, frame.LocalToPhysicalForOrigin(origin), strokeWidth);
            int maximum = 0;
            for (int i = 0; i < actual.Length; i++) maximum = Math.Max(maximum, Math.Abs(actual[i] - expected[i]));
            Require(maximum <= 1, $"Captured camera paint exceeds near-origin oracle: pan={pan}, max={maximum}");
            largest = Math.Max(largest, maximum);
        }
        Console.WriteLine($"Precise captured camera: PASS (points={path.PointCount}, rings={rings}, 4 physical pan offsets, full frame max_delta={largest})");
    }

    private static byte[] Paint(SKPath path, MapViewportMetrics viewport, SKMatrix matrix, float strokeWidth = 1f / 64)
    {
        using var bitmap = new SKBitmap(viewport.PhysicalWidth, viewport.PhysicalHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        using var fill = new SKPaint { Color = new SKColor(56, 189, 248, 89), IsAntialias = true };
        using var stroke = new SKPaint { Color = new SKColor(14, 165, 233, 89), Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
        canvas.Clear(SKColors.Transparent); canvas.SetMatrix(matrix);
        canvas.DrawPath(path, fill); canvas.DrawPath(path, stroke);
        return bitmap.Bytes;
    }
}
