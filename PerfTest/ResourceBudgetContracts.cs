using GeoNex.Services;

internal static class ResourceBudgetContracts
{
    private const long Mebibyte = 1024L * 1024L;

    public static void Run()
    {
        GeoNexResourceBudget constrained = GeoNexResourceBudgetPolicy.Calculate(
            8L * 1024 * Mebibyte,
            1280L * Mebibyte,
            12,
            _ => null);
        Assert(constrained.GdalCacheMb == 320, "8 GB constrained cache");
        Assert(constrained.GdalThreads == 4, "shared CPU budget");
        Assert(constrained.VsiCacheMb == 5 && constrained.GdalSwathMb == 320 && constrained.GdalWarpMb == 40,
            "bounded I/O and Warp buffers");
        Assert(constrained.GdalDatasetPoolSize == 40, "bounded dataset pool");

        GeoNexResourceBudget large = GeoNexResourceBudgetPolicy.Calculate(
            64L * 1024 * Mebibyte,
            32L * 1024 * Mebibyte,
            64,
            _ => null);
        Assert(large.GdalCacheMb == 4096 && large.GdalThreads == 8, "large host caps");

        var overrides = new Dictionary<string, string>
        {
            ["GEONEX_GDAL_CACHE_MB"] = "768",
            ["GEONEX_GDAL_THREADS"] = "3",
            ["GEONEX_VSI_CACHE_MB"] = "24",
            ["GEONEX_GDAL_SWATH_MB"] = "32",
            ["GEONEX_GDAL_WARP_MB"] = "20",
            ["GEONEX_GDAL_DATASET_POOL"] = "48"
        };
        GeoNexResourceBudget configured = GeoNexResourceBudgetPolicy.Calculate(
            8L * 1024 * Mebibyte,
            1280L * Mebibyte,
            12,
            name => overrides.GetValueOrDefault(name));
        Assert(configured.GdalCacheMb == 768 && configured.GdalThreads == 3, "explicit compute overrides");
        Assert(configured.VsiCacheMb == 24 && configured.GdalSwathMb == 768 && configured.GdalWarpMb == 20 &&
            configured.GdalDatasetPoolSize == 48,
            "explicit memory overrides");

        Console.WriteLine("Resource budget contracts: PASS (available RAM, cache, threads, VSI/swath/Warp, pool, overrides)");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
