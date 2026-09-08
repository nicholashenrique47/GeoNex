using GeoNex.Services;
using OSGeo.GDAL;

internal static class EcwContracts
{
    public static void Run()
    {
        GdalRuntimeBootstrap.Configure();
        Driver driver = Gdal.GetDriverByName("ECW")
            ?? throw new InvalidOperationException("ECW driver is unavailable.");
        Assert(driver.GetMetadataItem("DCAP_RASTER", null) == "YES", "raster capability");
        Assert(driver.GetMetadataItem("DCAP_OPEN", null) == "YES", "read capability");

        string? sample = Environment.GetEnvironmentVariable("GEONEX_ECW_SAMPLE");
        if (!string.IsNullOrWhiteSpace(sample)) ValidateDecode(Path.GetFullPath(sample));

        Console.WriteLine(
            $"ECW contracts: PASS (driver={driver.LongName}, decode={(string.IsNullOrWhiteSpace(sample) ? "not-requested" : "verified")})");
    }

    private static void ValidateDecode(string sample)
    {
        using Dataset dataset = Gdal.Open(sample, Access.GA_ReadOnly)
            ?? throw new InvalidOperationException($"ECW sample could not be opened: {Gdal.GetLastErrorMsg()}");
        Assert(dataset.GetDriver()?.ShortName == "ECW", "sample selected ECW driver");
        Assert(dataset.RasterXSize > 0 && dataset.RasterYSize > 0 && dataset.RasterCount > 0,
            "valid raster dimensions");

        using Band band = dataset.GetRasterBand(1);
        byte[] pixels = new byte[64 * 64];
        Assert(band.ReadRaster(
            0, 0, dataset.RasterXSize, dataset.RasterYSize,
            pixels, 64, 64, 0, 0) == CPLErr.CE_None, "pixel decode");
        Assert(pixels.Any(value => value != 0), "decoded pixels are non-empty");
        Assert(!RasterDatasetPolicy.SupportsExternalOverviews(dataset),
            "ECW internal wavelet pyramid bypasses external overview builder");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"ECW contract failed: {message}");
    }
}
