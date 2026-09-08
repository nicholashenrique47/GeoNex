namespace GeoNex.Services;

public readonly record struct RasterDimensions(int Width, int Height);

public static class RasterRenderingPolicy
{
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
        return categorical ? "near" : isInteracting ? "bilinear" : "cubicspline";
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
