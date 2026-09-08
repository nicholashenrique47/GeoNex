using OSGeo.GDAL;

namespace GeoNex.Services;

public readonly record struct RasterDatasetSemantics(
    bool HasPalette,
    bool IsFloatingPoint,
    bool IsRgbLike);

public static class RasterDatasetPolicy
{
    public static bool SupportsExternalOverviews(Dataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        string driver = dataset.GetDriver()?.ShortName ?? string.Empty;
        return driver is not ("WMS" or "ECW" or "JP2ECW");
    }

    public static RasterDatasetSemantics Inspect(Dataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (dataset.RasterCount <= 0) return default;

        using Band firstBand = dataset.GetRasterBand(1);
        ColorInterp interpretation = firstBand.GetRasterColorInterpretation();
        bool hasPalette = interpretation == ColorInterp.GCI_PaletteIndex;
        if (!hasPalette)
        {
            using ColorTable? colorTable = firstBand.GetRasterColorTable();
            hasPalette = colorTable != null;
        }

        bool isFloatingPoint = firstBand.DataType is
            DataType.GDT_Float16 or DataType.GDT_Float32 or DataType.GDT_Float64 or
            DataType.GDT_CFloat16 or DataType.GDT_CFloat32 or DataType.GDT_CFloat64;
        bool isRgbLike = dataset.RasterCount >= 3;
        return new RasterDatasetSemantics(hasPalette, isFloatingPoint, isRgbLike);
    }

    public static string SelectRenderResampling(Dataset dataset, bool isInteracting)
    {
        RasterDatasetSemantics semantics = Inspect(dataset);
        return RasterRenderingPolicy.SelectRenderResampling(
            semantics.HasPalette,
            semantics.IsFloatingPoint,
            semantics.IsRgbLike,
            isInteracting);
    }

    public static string SelectOverviewResampling(Dataset dataset)
    {
        RasterDatasetSemantics semantics = Inspect(dataset);
        return RasterRenderingPolicy.SelectOverviewResampling(
            semantics.HasPalette,
            semantics.IsFloatingPoint,
            semantics.IsRgbLike);
    }
}
