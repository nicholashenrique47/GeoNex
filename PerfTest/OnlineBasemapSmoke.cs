using System.Diagnostics;
using GeoNex.Services;
using OSGeo.GDAL;

internal static unsafe class OnlineBasemapSmoke
{
    public static void Run()
    {
        GdalRuntimeBootstrap.Configure();
        Gdal.SetConfigOption("GDAL_HTTP_CONNECTTIMEOUT", "5");
        Gdal.SetConfigOption("GDAL_HTTP_TIMEOUT", "15");
        Gdal.SetConfigOption("GDAL_HTTP_MAX_RETRY", "2");
        Gdal.SetConfigOption("GDAL_HTTP_RETRY_DELAY", "1");
        Gdal.SetConfigOption("GDAL_HTTP_USE_CAPI_STORE", "YES");
        Gdal.SetConfigOption("GDAL_HTTP_UNSAFESSL", "NO");

        if (!OnlineBasemapPolicy.TryResolve("ESRI", out var basemap))
            throw new InvalidOperationException("ESRI provider missing");

        string cachePath = Path.Combine(Path.GetTempPath(), "GeoNex", "BasemapSmokeCache");
        Directory.CreateDirectory(cachePath);
        var cache = new OnlineBasemapCacheSettings(cachePath, 64, 604800, 900);
        string xml = OnlineBasemapPolicy.BuildGdalTmsXml(basemap, cache);
        const string vsiPath = "/vsimem/geonex_basemap_smoke.xml";
        Gdal.FileFromMemBuffer(vsiPath, System.Text.Encoding.UTF8.GetBytes(xml));

        try
        {
            using Dataset dataset = Gdal.Open(vsiPath, Access.GA_ReadOnly) ??
                throw new InvalidOperationException($"GDAL open failed: {Gdal.GetLastErrorMsg()}");
            Assert(dataset.GetDriver()?.ShortName == "WMS", "WMS driver");
            Assert(dataset.RasterCount == 3, "RGB tile dataset");
            Assert(dataset.RasterXSize == 1_073_741_824, "level-22 virtual pyramid width");

            const int outputSize = 512;
            const int requestedZoom = 12;
            int sourceSize = outputSize << (basemap.MaximumZoom - requestedZoom);
            (double normalizedX, double normalizedY) = NormalizeWebMercator(-48.5747, -25.8828);
            int sourceX = Math.Clamp(
                (int)Math.Round(normalizedX * dataset.RasterXSize) - sourceSize / 2,
                0,
                dataset.RasterXSize - sourceSize);
            int sourceY = Math.Clamp(
                (int)Math.Round(normalizedY * dataset.RasterYSize) - sourceSize / 2,
                0,
                dataset.RasterYSize - sourceSize);

            byte[] pixels = new byte[outputSize * outputSize * 4];
            int[] bands = [1, 2, 3];
            double firstMs = Read(dataset, sourceX, sourceY, sourceSize, pixels, bands);
            long checksum = 0;
            for (int i = 0; i < pixels.Length; i += 4096) checksum += pixels[i];
            Assert(checksum > 0, "non-empty online viewport");
            double cachedMs = Read(dataset, sourceX, sourceY, sourceSize, pixels, bands);

            Console.WriteLine(
                $"Online basemap smoke: PASS (z={requestedZoom}, virtual={dataset.RasterXSize:N0}px, " +
                $"first={firstMs:F0}ms, cached={cachedMs:F0}ms, cache={dataset.GetMetadataItem("CACHE_PATH", "")})");
        }
        finally
        {
            Gdal.Unlink(vsiPath);
        }
    }

    private static double Read(
        Dataset dataset,
        int sourceX,
        int sourceY,
        int sourceSize,
        byte[] pixels,
        int[] bands)
    {
        var stopwatch = Stopwatch.StartNew();
        fixed (byte* pointer = pixels)
        {
            using var extra = new RasterIOExtraArg { eResampleAlg = RIOResampleAlg.GRIORA_Bilinear };
            dataset.ReadRaster(
                sourceX, sourceY, sourceSize, sourceSize,
                (IntPtr)pointer, 512, 512, DataType.GDT_Byte,
                bands.Length, bands, 4, 512 * 4, 1, extra);
        }
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static (double X, double Y) NormalizeWebMercator(double longitude, double latitude)
    {
        double sinLatitude = Math.Sin(latitude * Math.PI / 180.0);
        return (
            (longitude + 180.0) / 360.0,
            0.5 - Math.Log((1.0 + sinLatitude) / (1.0 - sinLatitude)) / (4.0 * Math.PI));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Online smoke failed: {message}");
    }
}
