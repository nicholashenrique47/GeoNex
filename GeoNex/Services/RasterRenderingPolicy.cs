using System.Globalization;

namespace GeoNex.Services;

public readonly record struct RasterDimensions(int Width, int Height);

public static class RasterRenderingPolicy
{
    private const float InteractiveVectorPreviewMaximumDpi = 1.5f;
    // Mixed ECW/SHP measurements halved first-preview latency at 1 CSS pixel
    // per output pixel; the settled frame still renders at the requested DPI.
    private const float InteractiveRasterPreviewMaximumDpi = 1.0f;
    private const float StartupRasterPreviewMaximumDpi = 0.75f;

    // Gesture frames retain their CSS dimensions while limiting HiDPI work
    // without excessive loss of detail during zoom gestures.
    public static float SelectInteractivePreviewDpi(float requestedDpi, bool isInteracting,
        bool rasterContent = false, bool startupPreview = false)
    {
        if (!isInteracting || !float.IsFinite(requestedDpi)) return requestedDpi;
        float maximumDpi = !rasterContent
            ? InteractiveVectorPreviewMaximumDpi
            : startupPreview
                ? SelectStartupRasterPreviewMaximumDpi()
                : SelectRasterPreviewMaximumDpi();
        return Math.Min(requestedDpi, maximumDpi);
    }

    // A settled frame may be reused during a zoom-in only while its physical
    // source pixels still resolve the requested preview. Once the old frame
    // would be enlarged beyond this margin, render a fresh viewport preview.
    public static bool HasSufficientCachedRasterResolution(double sourceCameraZoom,
        MapViewportMetrics sourceViewport, double targetCameraZoom,
        MapViewportMetrics targetViewport, double maximumUpscale = 1.10)
    {
        if (!double.IsFinite(sourceCameraZoom) || sourceCameraZoom <= 0 ||
            !double.IsFinite(targetCameraZoom) || targetCameraZoom <= 0 ||
            !double.IsFinite(maximumUpscale) || maximumUpscale < 1 ||
            sourceViewport.PhysicalScaleX <= 0 || sourceViewport.PhysicalScaleY <= 0 ||
            targetViewport.PhysicalScaleX <= 0 || targetViewport.PhysicalScaleY <= 0)
            return false;

        double zoomRatio = targetCameraZoom / sourceCameraZoom;
        double upscaleX = zoomRatio * targetViewport.PhysicalScaleX / sourceViewport.PhysicalScaleX;
        double upscaleY = zoomRatio * targetViewport.PhysicalScaleY / sourceViewport.PhysicalScaleY;
        return double.IsFinite(upscaleX) && double.IsFinite(upscaleY) &&
               upscaleX <= maximumUpscale && upscaleY <= maximumUpscale;
    }

    private static float SelectRasterPreviewMaximumDpi()
    {
        string? configured = Environment.GetEnvironmentVariable("GEONEX_INTERACTIVE_RASTER_PREVIEW_DPI");
        return float.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out float dpi) &&
               float.IsFinite(dpi) && dpi is >= 0.5f and <= 2f
            ? dpi
            : InteractiveRasterPreviewMaximumDpi;
    }

    private static float SelectStartupRasterPreviewMaximumDpi()
    {
        string? configured = Environment.GetEnvironmentVariable("GEONEX_STARTUP_RASTER_PREVIEW_DPI");
        return float.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out float dpi) &&
               float.IsFinite(dpi) && dpi is >= 0.5f and <= 2f
            ? dpi
            : StartupRasterPreviewMaximumDpi;
    }

    // A low-resolution interaction frame must never satisfy a final-quality request.
    public static string CacheKeyForQuality(string cameraKey, bool isInteracting) =>
        cameraKey + (isInteracting ? "|preview" : "|final");

    private const long Mebibyte = 1024L * 1024L;
    private const int DefaultMinimumOverviewSize = 256;
    private const int AbsoluteMaximumDimension = 8192;
    private const long EstimatedTransientBytesPerPixel = 20;

    public static string SelectRenderResampling(
        bool hasPalette,
        bool isFloatingPoint,
        bool isRgbLike,
        bool isInteracting,
        Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        string? configured = NormalizeRenderAlgorithm(readEnvironment("GEONEX_RASTER_RESAMPLING"));
        if (configured != null) return configured;

        bool categorical = hasPalette || (!isFloatingPoint && !isRgbLike);
        // Rasters are ultimately presented at screen resolution. Bilinear keeps
        // continuous imagery smooth while substantially reducing the per-pixel
        // work of cubic-spline resampling on large ECW/TIFF sources.
        return categorical ? "near" : "bilinear";
    }

    public static string SelectOverviewResampling(
        bool hasPalette,
        bool isFloatingPoint,
        bool isRgbLike,
        Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        string? configured = NormalizeOverviewAlgorithm(readEnvironment("GEONEX_OVERVIEW_RESAMPLING"));
        if (configured != null) return configured;

        bool categorical = hasPalette || (!isFloatingPoint && !isRgbLike);
        return categorical ? "NEAREST" : "AVERAGE";
    }

    public static int[] BuildOverviewFactors(
        int rasterWidth,
        int rasterHeight,
        int minimumOverviewSize = DefaultMinimumOverviewSize)
    {
        if (rasterWidth <= 0) throw new ArgumentOutOfRangeException(nameof(rasterWidth));
        if (rasterHeight <= 0) throw new ArgumentOutOfRangeException(nameof(rasterHeight));
        if (minimumOverviewSize <= 0) throw new ArgumentOutOfRangeException(nameof(minimumOverviewSize));

        int largestDimension = Math.Max(rasterWidth, rasterHeight);
        var factors = new List<int>();
        for (int factor = 2; largestDimension / factor >= minimumOverviewSize; factor *= 2)
        {
            factors.Add(factor);
            if (factor > int.MaxValue / 2) break;
        }
        return factors.ToArray();
    }

    public static long CalculateMaxFramePixels(int availablePhysicalMb)
    {
        long availableBytes = Math.Max(128L, availablePhysicalMb) * Mebibyte;
        long transientBudget = availableBytes / 8;
        long byMemory = transientBudget / EstimatedTransientBytesPerPixel;
        return Math.Clamp(byMemory, 1920L * 1080L, 32L * 1024L * 1024L);
    }

    public static RasterDimensions FitDimensions(
        int requestedWidth,
        int requestedHeight,
        long maximumPixels,
        int maximumDimension = AbsoluteMaximumDimension)
    {
        if (requestedWidth <= 0) throw new ArgumentOutOfRangeException(nameof(requestedWidth));
        if (requestedHeight <= 0) throw new ArgumentOutOfRangeException(nameof(requestedHeight));
        if (maximumPixels <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPixels));
        if (maximumDimension <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDimension));

        double scale = Math.Min(1.0, Math.Min(
            maximumDimension / (double)requestedWidth,
            maximumDimension / (double)requestedHeight));
        double scaledPixels = requestedWidth * (double)requestedHeight * scale * scale;
        if (scaledPixels > maximumPixels)
            scale *= Math.Sqrt(maximumPixels / scaledPixels);

        int width = Math.Max(1, (int)Math.Floor(requestedWidth * scale));
        int height = Math.Max(1, (int)Math.Floor(requestedHeight * scale));
        while ((long)width * height > maximumPixels)
        {
            if (width >= height && width > 1) --width;
            else if (height > 1) --height;
            else break;
        }
        return new RasterDimensions(width, height);
    }

    private static string? NormalizeRenderAlgorithm(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "near" or "nearest" or "nearestneighbour" => "near",
        "bilinear" => "bilinear",
        "cubic" => "cubic",
        "cubicspline" => "cubicspline",
        "lanczos" => "lanczos",
        "average" => "average",
        "mode" => "mode",
        _ => null
    };

    private static string? NormalizeOverviewAlgorithm(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "NEAR" or "NEAREST" or "NEARESTNEIGHBOUR" => "NEAREST",
        "AVERAGE" => "AVERAGE",
        "MODE" => "MODE",
        "GAUSS" => "GAUSS",
        "CUBIC" => "CUBIC",
        "CUBICSPLINE" => "CUBICSPLINE",
        "LANCZOS" => "LANCZOS",
        _ => null
    };
}
