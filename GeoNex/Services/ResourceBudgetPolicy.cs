namespace GeoNex.Services;

public readonly record struct GeoNexResourceBudget(
    int TotalPhysicalMb,
    int AvailablePhysicalMb,
    int GdalCacheMb,
    int VsiCacheMb,
    int GdalSwathMb,
    int GdalWarpMb,
    int GdalThreads,
    int GdalDatasetPoolSize);

public static class GeoNexResourceBudgetPolicy
{
    private const long Mebibyte = 1024L * 1024L;

    public static GeoNexResourceBudget Calculate(
        long totalPhysicalBytes,
        long availablePhysicalBytes,
        int logicalProcessorCount,
        Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        long totalMb = Math.Max(512, totalPhysicalBytes / Mebibyte);
        long availableMb = Math.Clamp(availablePhysicalBytes / Mebibyte, 128, totalMb);

        // The cache gets at most 1/16 of physical RAM and 1/4 of RAM currently
        // available. This leaves room for mapped SHPs, Skia surfaces, the UI,
        // GDAL working buffers and the operating system.
        int defaultCacheMb = (int)Math.Clamp(
            Math.Min(totalMb / 16, availableMb / 4),
            128,
            4096);
        int cacheMb = ReadBounded(readEnvironment, "GEONEX_GDAL_CACHE_MB", defaultCacheMb, 64, 16_384);

        int defaultThreads = Math.Clamp(Math.Max(1, logicalProcessorCount / 3), 1, 8);
        int threads = ReadBounded(readEnvironment, "GEONEX_GDAL_THREADS", defaultThreads, 1, 64);
        // VSI_CACHE_SIZE is per file, so keep it small when VRTs retain many
        // source datasets. The explicit override remains available for remote I/O.
        int defaultVsiMb = Math.Clamp(cacheMb / 64, 2, 8);
        int vsiMb = ReadBounded(readEnvironment, "GEONEX_VSI_CACHE_MB", defaultVsiMb, 1, 128);
        int configuredSwathMb = ReadBounded(
            readEnvironment, "GEONEX_GDAL_SWATH_MB", cacheMb, 1, 16_384);
        int swathMb = Math.Max(cacheMb, configuredSwathMb);
        int defaultWarpMb = Math.Clamp(cacheMb / 8, 16, 64);
        int warpMb = ReadBounded(readEnvironment, "GEONEX_GDAL_WARP_MB", defaultWarpMb, 8, 1024);
        int defaultPool = Math.Clamp(cacheMb / 8, 16, 128);
        int pool = ReadBounded(readEnvironment, "GEONEX_GDAL_DATASET_POOL", defaultPool, 2, 1000);

        return new GeoNexResourceBudget(
            checked((int)Math.Min(totalMb, int.MaxValue)),
            checked((int)Math.Min(availableMb, int.MaxValue)),
            cacheMb,
            vsiMb,
            swathMb,
            warpMb,
            threads,
            pool);
    }

    private static int ReadBounded(
        Func<string, string?> readEnvironment,
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        string? text = readEnvironment(name);
        return int.TryParse(text, out int value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
    }
}
