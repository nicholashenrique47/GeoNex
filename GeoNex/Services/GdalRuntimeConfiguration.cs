using System.Runtime.InteropServices;
using OSGeo.GDAL;

namespace GeoNex.Services;

public static class GdalRuntimeConfiguration
{
    private const long Mebibyte = 1024L * 1024L;
    private static readonly object Gate = new();
    private static bool _configured;

    public static GeoNexResourceBudget Current { get; private set; }
    public static OnlineBasemapCacheSettings BasemapCache { get; private set; }

    public static GeoNexResourceBudget Apply()
    {
        lock (Gate)
        {
            if (_configured) return Current;

            (long totalBytes, long availableBytes) = ReadPhysicalMemory();
            Current = GeoNexResourceBudgetPolicy.Calculate(
                totalBytes,
                availableBytes,
                Environment.ProcessorCount);
            BasemapCache = OnlineBasemapPolicy.CreateCacheSettings(Current.AvailablePhysicalMb);
            try
            {
                Directory.CreateDirectory(BasemapCache.Path);
            }
            catch (Exception cacheError)
            {
                string fallbackPath = Path.Combine(Path.GetTempPath(), "GeoNex", "Cache", "Basemaps");
                BasemapCache = BasemapCache with { Path = fallbackPath };
                Directory.CreateDirectory(BasemapCache.Path);
                DebugLogger.Log($"Basemap cache fallback: {cacheError.Message}");
            }

            Gdal.SetConfigOption("OSR_DEFAULT_AXIS_MAPPING_STRATEGY", "TRADITIONAL_GIS_ORDER");
            Gdal.SetConfigOption("GDAL_CACHEMAX", $"{Current.GdalCacheMb}M");
            // TRUE avoids enumerating huge directories but still lets drivers probe
            // known siblings such as .ovr/.msk/.aux.xml. EMPTY_DIR hides them.
            Gdal.SetConfigOption("GDAL_DISABLE_READDIR_ON_OPEN", "TRUE");
            Gdal.SetConfigOption("GDAL_NUM_THREADS", Current.GdalThreads.ToString());
            Gdal.SetConfigOption("VSI_CACHE", "TRUE");
            Gdal.SetConfigOption("VSI_CACHE_SIZE", checked(Current.VsiCacheMb * Mebibyte).ToString());
            Gdal.SetConfigOption("GDAL_SWATH_SIZE", checked(Current.GdalSwathMb * Mebibyte).ToString());
            Gdal.SetConfigOption("GDAL_MAX_DATASET_POOL_SIZE", Current.GdalDatasetPoolSize.ToString());
            Gdal.SetConfigOption("GDAL_HTTP_MULTIPLEX", "YES");
            Gdal.SetConfigOption("GDAL_HTTP_MERGE_CONSECUTIVE_RANGES", "YES");
            Gdal.SetConfigOption("GDAL_ENABLE_WMS_CACHE", "YES");
            Gdal.SetConfigOption("GDAL_DEFAULT_WMS_CACHE_PATH", BasemapCache.Path);
            Gdal.SetConfigOption("GDAL_HTTP_CONNECTTIMEOUT", OnlineBasemapPolicy.ConnectTimeoutSeconds.ToString());
            Gdal.SetConfigOption("GDAL_HTTP_TIMEOUT", OnlineBasemapPolicy.RequestTimeoutSeconds.ToString());
            Gdal.SetConfigOption("GDAL_HTTP_MAX_RETRY", "2");
            Gdal.SetConfigOption("GDAL_HTTP_RETRY_DELAY", "1");
            Gdal.SetConfigOption("GDAL_HTTP_TCP_KEEPALIVE", "YES");
            Gdal.SetConfigOption("GDAL_HTTP_USE_CAPI_STORE", "YES");
            Gdal.SetConfigOption("CPL_CURL_GZIP", "YES");
            Gdal.SetConfigOption("GDAL_HTTP_UNSAFESSL", "NO");

            _configured = true;
            DebugLogger.Log(
                $"GDAL budget total_mb={Current.TotalPhysicalMb} available_mb={Current.AvailablePhysicalMb} " +
                $"cache_mb={Current.GdalCacheMb} vsi_mb={Current.VsiCacheMb} swath_mb={Current.GdalSwathMb} " +
                $"warp_mb={Current.GdalWarpMb} " +
                $"threads={Current.GdalThreads} dataset_pool={Current.GdalDatasetPoolSize} " +
                $"basemap_cache_mb={BasemapCache.MaximumSizeMb} basemap_cache={BasemapCache.Path}");
            return Current;
        }
    }

    private static (long TotalBytes, long AvailableBytes) ReadPhysicalMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status) && status.TotalPhysical > 0)
                return (checked((long)status.TotalPhysical), checked((long)status.AvailablePhysical));
        }

        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (total <= 0) total = 4L * 1024 * Mebibyte;
        return (total, total / 2);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}
