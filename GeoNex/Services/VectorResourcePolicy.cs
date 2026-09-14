using System.Runtime.InteropServices;

namespace GeoNex.Services;

public readonly record struct VectorResourceBudget(long CacheBytes, int Workers)
{
    public int WorkersFor(int featureCount) => Math.Min(Workers, Math.Max(1, featureCount / 16_384));
}

/// <summary>Vector-only budgets. No change to raster/GDAL settings or source geometry.</summary>
public static class VectorResourcePolicy
{
    public const long MiB = 1024L * 1024;

    public static VectorResourceBudget Calculate(long totalBytes, long availableBytes, int processors,
        Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        long total = Math.Max(0, totalBytes);
        long available = Math.Clamp(availableBytes, 0, total);
        // Use spare RAM, not a reservation. Never impose a minimum under memory pressure.
        long ceiling = Math.Min(total / 32, available / 8);
        long configured = int.TryParse(environment("GEONEX_VECTOR_CACHE_MB"), out int mb) && mb >= 0
            ? Math.Min(mb, 2048) * MiB : 512 * MiB;
        long cache = Math.Min(ceiling, configured);
        if (available < 256 * MiB) cache = 0;

        int cpus = Math.Max(1, processors);
        int cpuLimit = Math.Min(16, Math.Max(1, cpus - 1));
        int workers = Math.Min(cpuLimit, Math.Max(1, cpus / 2));
        if (int.TryParse(environment("GEONEX_INDEX_WORKERS"), out int requested) && requested > 0)
            workers = Math.Min(cpuLimit, requested);
        workers = (int)Math.Min(workers, Math.Max(1, available / (128 * MiB)));
        return new VectorResourceBudget(cache, workers);
    }
}

internal static class VectorRuntimeResources
{
    private static readonly object Gate = new();
    private static long _nextSample;
    private static VectorResourceBudget _current;

    public static VectorResourceBudget Current
    {
        get
        {
            lock (Gate)
            {
                long now = Environment.TickCount64;
                if (now < _nextSample) return _current;
                var memory = ReadMemory();
                _current = VectorResourcePolicy.Calculate(memory.Total, memory.Available, Environment.ProcessorCount);
                _nextSample = now + 2000;
                return _current;
            }
        }
    }

    private static (long Total, long Available) ReadMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref status) && status.TotalPhysical > 0)
                return ((long)Math.Min(status.TotalPhysical, (ulong)long.MaxValue),
                    (long)Math.Min(status.AvailablePhysical, (ulong)long.MaxValue));
        }
        // Conservative fallback; the GC limit is not a measurement of free physical RAM.
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (total <= 0) total = 2L * 1024 * VectorResourcePolicy.MiB;
        return (total, Math.Max(0, total / 4 - GC.GetTotalMemory(false)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
