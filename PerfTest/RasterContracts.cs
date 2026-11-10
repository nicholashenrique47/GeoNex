using GeoNex.Services;

internal static class RasterContracts
{
    public static void Run()
    {
        double[] rotated = [100, 2, 1, 200, -0.5, -3];
        RasterPoint[] boundary = RasterGeometry.GetBoundaryPoints(rotated, 10, 20, 4);
        Assert(boundary.Length == 16, "densified boundary count");
        RasterBounds bounds = RasterGeometry.GetBounds(boundary);
        AssertClose(bounds.MinX, 100, "rotated min X");
        AssertClose(bounds.MaxX, 140, "rotated max X");
        AssertClose(bounds.MinY, 135, "rotated min Y");
        AssertClose(bounds.MaxY, 200, "rotated max Y");
        Assert(!RasterGeometry.IsAxisAligned(rotated), "rotation detection");
        Assert(RasterGeometry.IsAxisAligned([10, 2, 0, 20, 0, -2]), "north-up detection");

        Assert(RasterRenderingPolicy.SelectRenderResampling(true, false, false, false, _ => null) == "near",
            "palette render uses nearest");
        Assert(RasterRenderingPolicy.SelectRenderResampling(false, true, false, true, _ => null) == "bilinear",
            "continuous interaction uses bilinear");
        Assert(RasterRenderingPolicy.SelectRenderResampling(false, false, true, false, _ => null) == "bilinear",
            "RGB final render uses bilinear");
        Assert(RasterRenderingPolicy.SelectInteractivePreviewDpi(2f, true) == 1.5f &&
               RasterRenderingPolicy.SelectInteractivePreviewDpi(2f, true, rasterContent: true) == 1f &&
               RasterRenderingPolicy.SelectInteractivePreviewDpi(2f, true, rasterContent: true,
                   startupPreview: true) == 0.75f &&
               RasterRenderingPolicy.SelectInteractivePreviewDpi(1.2f, true, rasterContent: true) == 1f &&
               RasterRenderingPolicy.SelectInteractivePreviewDpi(1f, true) == 1f &&
               RasterRenderingPolicy.SelectInteractivePreviewDpi(2f, false, rasterContent: true) == 2f,
            "interactive preview adapts physical DPI to raster/vector workload without reducing final quality");
        var settledRasterViewport = MapViewportMetrics.Create(1600, 900, 2f);
        var gestureRasterViewport = MapViewportMetrics.Create(1600, 900, 1.25f);
        Assert(RasterRenderingPolicy.HasSufficientCachedRasterResolution(
                   1, settledRasterViewport, 1.5, gestureRasterViewport) &&
               !RasterRenderingPolicy.HasSufficientCachedRasterResolution(
                   1, settledRasterViewport, 2, gestureRasterViewport) &&
               RasterRenderingPolicy.HasSufficientCachedRasterResolution(
                   2, settledRasterViewport, 1, gestureRasterViewport),
            "raster cache yields to a fresh preview after detail falls below the target zoom resolution");
        string? previousPreviewDpi = Environment.GetEnvironmentVariable("GEONEX_INTERACTIVE_RASTER_PREVIEW_DPI");
        string? previousStartupPreviewDpi = Environment.GetEnvironmentVariable("GEONEX_STARTUP_RASTER_PREVIEW_DPI");
        try
        {
            Environment.SetEnvironmentVariable("GEONEX_STARTUP_RASTER_PREVIEW_DPI", null);
            Assert(RasterRenderingPolicy.SelectInteractivePreviewDpi(2f, true, rasterContent: true,
                       startupPreview: true) == 0.75f,
                "startup raster preview defaults to the measured lower first-frame resolution");
            Environment.SetEnvironmentVariable("GEONEX_INTERACTIVE_RASTER_PREVIEW_DPI", "1.5");
            Assert(RasterRenderingPolicy.SelectInteractivePreviewDpi(2f, true, rasterContent: true) == 1.5f,
                "raster gesture preview allows a bounded field tuning override");
            Environment.SetEnvironmentVariable("GEONEX_INTERACTIVE_RASTER_PREVIEW_DPI", "0.75");
            Assert(RasterRenderingPolicy.SelectInteractivePreviewDpi(2f, true, rasterContent: true) == 0.75f,
                "raster preview accepts a lower temporary resolution for slow remote sources");
            Environment.SetEnvironmentVariable("GEONEX_INTERACTIVE_RASTER_PREVIEW_DPI", "0.25");
            Assert(RasterRenderingPolicy.SelectInteractivePreviewDpi(2f, true, rasterContent: true) == 1f,
                "out-of-range raster preview tuning falls back to the measured default");
            Environment.SetEnvironmentVariable("GEONEX_INTERACTIVE_RASTER_PREVIEW_DPI", "2.5");
            Assert(RasterRenderingPolicy.SelectInteractivePreviewDpi(2f, true, rasterContent: true) == 1f,
                "out-of-range raster preview tuning falls back to the measured default");
            Environment.SetEnvironmentVariable("GEONEX_STARTUP_RASTER_PREVIEW_DPI", "1.0");
            Assert(RasterRenderingPolicy.SelectInteractivePreviewDpi(2f, true, rasterContent: true,
                       startupPreview: true) == 1f,
                "startup raster preview has a separate bounded tuning override");
            Environment.SetEnvironmentVariable("GEONEX_STARTUP_RASTER_PREVIEW_DPI", "0.25");
            Assert(RasterRenderingPolicy.SelectInteractivePreviewDpi(2f, true, rasterContent: true,
                       startupPreview: true) == 0.75f,
                "out-of-range startup preview tuning falls back to its default");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEONEX_INTERACTIVE_RASTER_PREVIEW_DPI", previousPreviewDpi);
            Environment.SetEnvironmentVariable("GEONEX_STARTUP_RASTER_PREVIEW_DPI", previousStartupPreviewDpi);
        }
        Assert(RasterRenderingPolicy.SelectOverviewResampling(false, false, false, _ => null) == "NEAREST",
            "unclassified integer values are not averaged");
        Assert(RasterRenderingPolicy.SelectOverviewResampling(false, true, false, _ => null) == "AVERAGE",
            "continuous overviews use average");
        Assert(RasterRenderingPolicy.SelectRenderResampling(false, true, false, false,
            name => name == "GEONEX_RASTER_RESAMPLING" ? "lanczos" : null) == "lanczos",
            "valid render override");

        Assert(RasterRenderingPolicy.BuildOverviewFactors(10_000, 6_000).SequenceEqual([2, 4, 8, 16, 32]),
            "overview levels stop near useful resolution");
        Assert(RasterRenderingPolicy.BuildOverviewFactors(300, 200).Length == 0,
            "small raster needs no overview");

        RasterDimensions fitted = RasterRenderingPolicy.FitDimensions(20_000, 10_000, 8_000_000);
        Assert((long)fitted.Width * fitted.Height <= 8_000_000, "frame pixel budget");
        Assert(fitted.Width <= 8192 && fitted.Height <= 8192, "frame dimension cap");
        AssertClose(fitted.Width / (double)fitted.Height, 2.0, "frame aspect ratio", 0.001);

        Console.WriteLine("Raster contracts: PASS (affine rotation, densified bounds, semantics, overviews, pixel budget)");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Raster contract failed: {message}");
    }

    private static void AssertClose(double actual, double expected, string message, double tolerance = 1.0e-9) =>
        Assert(Math.Abs(actual - expected) <= tolerance, $"{message}; expected={expected}, actual={actual}");
}
