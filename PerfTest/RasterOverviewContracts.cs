using System.Diagnostics;
using System.Security.Cryptography;
using GeoNex.Services;
using OSGeo.GDAL;

internal static class RasterOverviewContracts
{
    public static void Run()
    {
        GdalRuntimeBootstrap.Configure();

        string directory = Path.Combine(Path.GetTempPath(), $"geonex-overview-{Guid.NewGuid():N}");
        string rasterPath = Path.Combine(directory, "continuous.tif");
        Directory.CreateDirectory(directory);
        string? previousDelay = Environment.GetEnvironmentVariable("GEONEX_OVERVIEW_DELAY_MS");
        try
        {
            Driver driver = Gdal.GetDriverByName("GTiff")
                ?? throw new InvalidOperationException("GTiff driver is unavailable.");
            using (Dataset dataset = driver.Create(
                rasterPath, 1024, 1024, 1, DataType.GDT_Float32,
                ["TILED=YES", "COMPRESS=DEFLATE"]))
            using (Band band = dataset.GetRasterBand(1))
            {
                var row = new float[1024];
                for (int x = 0; x < row.Length; ++x) row[x] = x / 1023f;
                for (int y = 0; y < 1024; ++y)
                    Assert(band.WriteRaster(0, y, 1024, 1, row, 1024, 1, 0, 0) == CPLErr.CE_None,
                        "synthetic raster write");
                dataset.FlushCache();
            }

            byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(rasterPath));
            Environment.SetEnvironmentVariable("GEONEX_OVERVIEW_DELAY_MS", "0");
            Assert(RasterOverviewBuilder.Schedule(rasterPath, new object()), "overview job scheduled");

            var timeout = Stopwatch.StartNew();
            while (RasterOverviewBuilder.IsActive(rasterPath) && timeout.Elapsed < TimeSpan.FromSeconds(30))
                Thread.Sleep(25);
            Assert(!RasterOverviewBuilder.IsActive(rasterPath), "overview job completed");
            Assert(File.Exists(rasterPath + ".ovr"), "external .ovr created");
            Assert(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(rasterPath))),
                "source raster remains byte-identical");

            using Dataset reopened = Gdal.Open(rasterPath, Access.GA_ReadOnly)
                ?? throw new InvalidOperationException("Synthetic raster could not be reopened.");
            using Band reopenedBand = reopened.GetRasterBand(1);
            Assert(reopenedBand.GetOverviewCount() == 2, "expected overview levels are visible after reopen");

            Console.WriteLine("Raster overview contracts: PASS (read-only sidecar, source hash, visible levels)");
        }
        finally
        {
            RasterOverviewBuilder.Cancel(rasterPath);
            var cleanupWait = Stopwatch.StartNew();
            while (RasterOverviewBuilder.IsActive(rasterPath) && cleanupWait.Elapsed < TimeSpan.FromSeconds(5))
                Thread.Sleep(25);
            Environment.SetEnvironmentVariable("GEONEX_OVERVIEW_DELAY_MS", previousDelay);
            if (!RasterOverviewBuilder.IsActive(rasterPath) && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Raster overview contract failed: {message}");
    }
}
