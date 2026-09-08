using GeoNex.Services;
using SkiaSharp;

internal static class CoordinateContracts
{
    private static readonly float[] Dpis = [1f, 1.25f, 1.5f, 2f, 4f];
    private static readonly float[] Rotations = [0f, 17f, 90f, -33f];

    public static void Run()
    {
        VerifyViewportMetrics();
        VerifyWorldLocalRoundTrip();
        VerifyMatrixRoundTripsAndDpiInvariantViewport();
        VerifyScreenAlignedPan();
        VerifyCursorAnchor();
        Console.WriteLine("Coordinate contracts: PASS (world/local/CSS/physical, DPI, rotation, cursor anchor)");
    }

    private static void VerifyViewportMetrics()
    {
        foreach (float dpi in Dpis)
        {
            MapViewportMetrics viewport = MapViewportMetrics.Create(1365, 767, dpi);
            SKPoint physical = viewport.CssToPhysical(new SKPoint(1365, 767));
            AssertPoint(physical, new SKPoint(viewport.PhysicalWidth, viewport.PhysicalHeight), 0.001f,
                $"CSS edge reaches physical edge at DPI {dpi}");
            AssertPoint(viewport.PhysicalToCss(physical), new SKPoint(1365, 767), 0.001f,
                $"physical/CSS round-trip at DPI {dpi}");
        }

        Assert(!MapViewportMetrics.TryCreate(0, 720, 1, out _), "zero width rejected");
        Assert(!MapViewportMetrics.TryCreate(1280, 720, float.NaN, out _), "NaN DPI rejected");
        Assert(!MapViewportMetrics.TryCreate(1280, 720, 4.01f, out _), "unsupported DPI rejected");
        Assert(!MapViewportMetrics.TryCreate(16_385, 720, 1, out _), "oversized CSS viewport rejected");
    }

    private static void VerifyWorldLocalRoundTrip()
    {
        const double originX = 712_345.125;
        const double originY = 7_183_456.75;
        var world = new MapWorldCoordinate(712_478.625, 7_183_401.5);
        SKPoint local = MapCoordinateSpace.WorldToLocal(world.X, world.Y, originX, originY);
        MapWorldCoordinate restored = MapCoordinateSpace.LocalToWorld(local, originX, originY);
        AssertNear(restored.X, world.X, 0.0001, "world/local X round-trip");
        AssertNear(restored.Y, world.Y, 0.0001, "world/local Y round-trip");
        Assert(local.Y > 0, "local Y axis is screen-oriented");
    }

    private static void VerifyMatrixRoundTripsAndDpiInvariantViewport()
    {
        var center = new SKPoint(12_345.5f, -934.25f);
        SKPoint[] samples =
        [
            center,
            new SKPoint(center.X - 120.75f, center.Y + 88.5f),
            new SKPoint(center.X + 219.25f, center.Y - 141.75f)
        ];

        foreach (float rotation in Rotations)
        {
            MapCoordinateFrame baseline = MapCoordinateFrame.Create(
                MapViewportMetrics.Create(1365, 767, 1), center, 2.75f, rotation);

            foreach (float dpi in Dpis)
            {
                MapCoordinateFrame frame = MapCoordinateFrame.Create(
                    MapViewportMetrics.Create(1365, 767, dpi), center, 2.75f, rotation);

                foreach (SKPoint sample in samples)
                {
                    AssertPoint(frame.CssToLocal(frame.LocalToCss(sample)), sample, 0.01f,
                        $"local/CSS round-trip at DPI {dpi}, rotation {rotation}");
                    AssertPoint(frame.PhysicalToLocal(frame.LocalToPhysical(sample)), sample, 0.01f,
                        $"local/physical round-trip at DPI {dpi}, rotation {rotation}");
                }

                AssertPoint(frame.TopLeftLocal, baseline.TopLeftLocal, 0.01f,
                    $"top-left viewport invariant at DPI {dpi}, rotation {rotation}");
                AssertPoint(frame.TopRightLocal, baseline.TopRightLocal, 0.01f,
                    $"top-right viewport invariant at DPI {dpi}, rotation {rotation}");
                AssertPoint(frame.BottomRightLocal, baseline.BottomRightLocal, 0.01f,
                    $"bottom-right viewport invariant at DPI {dpi}, rotation {rotation}");
                AssertPoint(frame.BottomLeftLocal, baseline.BottomLeftLocal, 0.01f,
                    $"bottom-left viewport invariant at DPI {dpi}, rotation {rotation}");

                AssertPoint(
                    frame.CssToLocal(new SKPoint(frame.Viewport.CssWidth, frame.Viewport.CssHeight)),
                    frame.BottomRightLocal,
                    0.01f,
                    $"CSS and physical viewport edges agree at DPI {dpi}, rotation {rotation}");
            }
        }
    }

    private static void VerifyScreenAlignedPan()
    {
        foreach (float rotation in Rotations)
        {
            MapViewportMetrics viewport = MapViewportMetrics.Create(1440, 900, 2f);
            var baseCenter = new SKPoint(800, -300);
            const float scale = 4.25f;
            var referencePoint = new SKPoint(835, -255);
            MapCoordinateFrame before = MapCoordinateFrame.Create(viewport, baseCenter, scale, rotation);
            SKPoint shiftedCenter = MapCoordinateSpace.ApplyCssPanToLocalCenter(
                baseCenter, scale, 73.5, -28.25, rotation);
            MapCoordinateFrame after = MapCoordinateFrame.Create(viewport, shiftedCenter, scale, rotation);
            SKPoint expected = before.LocalToCss(referencePoint);
            expected.Offset(73.5f, -28.25f);
            AssertPoint(after.LocalToCss(referencePoint), expected, 0.01f,
                $"screen-aligned pan at rotation {rotation}");
        }
    }

    private static void VerifyCursorAnchor()
    {
        var baseCenter = new SKPoint(50, 40);
        const float autoFitScale = 3.2f;
        var cursor = new SKPoint(927.25f, 403.75f);

        foreach (float dpi in Dpis)
        foreach (float rotation in Rotations)
        {
            MapViewportMetrics viewport = MapViewportMetrics.Create(1365, 767, dpi);
            var camera = new MapCameraState(87, -41, 1.7);
            float oldScale = autoFitScale * (float)camera.Zoom;
            SKPoint oldCenter = MapCoordinateSpace.ApplyCssPanToLocalCenter(
                baseCenter, oldScale, camera.PanX, camera.PanY, rotation);
            MapCoordinateFrame oldFrame = MapCoordinateFrame.Create(viewport, oldCenter, oldScale, rotation);
            SKPoint localUnderCursor = oldFrame.CssToLocal(cursor);

            const double multiplier = 1.33;
            double cursorFromCenterX = cursor.X - viewport.CssWidth / 2.0;
            double cursorFromCenterY = cursor.Y - viewport.CssHeight / 2.0;
            double deltaX = cursorFromCenterX * (1 - multiplier);
            double deltaY = cursorFromCenterY * (1 - multiplier);
            Assert(MapCoordinateSpace.TryApplyCssCameraDelta(
                camera, deltaX, deltaY, multiplier, out MapCameraState updated),
                "valid camera delta accepted");

            float newScale = autoFitScale * (float)updated.Zoom;
            SKPoint newCenter = MapCoordinateSpace.ApplyCssPanToLocalCenter(
                baseCenter, newScale, updated.PanX, updated.PanY, rotation);
            MapCoordinateFrame newFrame = MapCoordinateFrame.Create(viewport, newCenter, newScale, rotation);
            AssertPoint(newFrame.LocalToCss(localUnderCursor), cursor, 0.02f,
                $"cursor anchor at DPI {dpi}, rotation {rotation}");
        }

        Assert(!MapCoordinateSpace.TryApplyCssCameraDelta(
            new MapCameraState(0, 0, 1), 0, 0, double.NaN, out _),
            "invalid camera delta rejected");
    }

    private static void AssertPoint(SKPoint actual, SKPoint expected, float tolerance, string message)
    {
        if (MathF.Abs(actual.X - expected.X) > tolerance || MathF.Abs(actual.Y - expected.Y) > tolerance)
            throw new InvalidOperationException(
                $"Coordinate contract failed: {message}; expected {expected}, actual {actual}");
    }

    private static void AssertNear(double actual, double expected, double tolerance, string message)
    {
        if (Math.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException(
                $"Coordinate contract failed: {message}; expected {expected}, actual {actual}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Coordinate contract failed: {message}");
    }
}
