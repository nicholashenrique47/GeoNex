using GeoNex.Services;

internal static class VectorResourceContracts
{
    public static void Run()
    {
        const long mb = VectorResourcePolicy.MiB;
        foreach (int cpus in new[] { 1, 2, 4, 8, 20, 64, 256 })
        foreach (int totalMb in new[] { 2048, 4096, 8192, 32768, 131072 })
        foreach (int freeMb in new[] { 0, 64, 512, 2048, 8192, 32768 })
        {
            long free = Math.Min(freeMb, totalMb) * mb;
            var budget = VectorResourcePolicy.Calculate(totalMb * mb, free, cpus, _ => null);
            Check(budget.CacheBytes <= Math.Min(totalMb * mb / 32, free / 8), "memory ceiling");
            Check(budget.CacheBytes <= 512 * mb, "default upper bound");
            Check(budget.PolygonWorkingBytes <= budget.CacheBytes / 2 &&
                budget.PolygonWorkingBytes <= 256 * mb, "transient painting ceiling");
            Check(budget.Workers >= 1 && budget.Workers <= Math.Min(16, Math.Max(1, cpus - 1)), "CPU reserve");
            Check(budget.WorkersFor(8) == 1, "small source has no parallel fan-out");
            Check(budget.WorkersFor(int.MaxValue) == budget.Workers, "large source uses available workers");
        }
        var pressure = VectorResourcePolicy.Calculate(8192 * mb, 64 * mb, 64, _ => "999999");
        Check(pressure.CacheBytes == 0 && pressure.Workers == 1, "pressure overrides unsafe configuration");
        Check(pressure.PolygonWorkingBytes == 0, "pressure disables transient expansion");
        Check(new VectorResourceBudget(2_048 * mb, 16).PolygonWorkingBytes == 256 * mb, "large override cannot enlarge transient cap");
        Check(VectorResourcePolicy.Calculate(8192 * mb, 4096 * mb, 8,
            name => name == "GEONEX_VECTOR_CACHE_MB" ? "0" : null).CacheBytes == 0, "cache opt-out");
        Check(VectorResourcePolicy.Calculate(-1, -1, 0, _ => "bad") == new VectorResourceBudget(0, 1), "invalid readings are conservative");
        var live = VectorRuntimeResources.Current;
        Console.WriteLine($"Vector resources: PASS (210 hardware profiles, limits, overrides, pressure); live cache_mb={live.CacheBytes / mb}, workers={live.Workers}");
    }

    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }
}
