using OSGeo.GDAL;
using System.Runtime.CompilerServices;

namespace GeoNex.Services;

public readonly record struct RasterDatasetSemantics(
    bool HasPalette,
    bool IsFloatingPoint,
    bool IsRgbLike);

public static class RasterDatasetPolicy
{
    private sealed record RenderMetadata(
        int[] RgbaBandMap,
        RasterDatasetSemantics Semantics,
        double[] GeoTransform);

    private static readonly ConditionalWeakTable<Dataset, Lazy<RenderMetadata>> RenderMetadataByDataset = new();

    public static int[] GetRgbaBandMap(Dataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return (int[])GetRenderMetadata(dataset).RgbaBandMap.Clone();
    }

    // Render paths consume but never modify these cached arrays. Keeping them
    // alongside the dataset avoids repeating GDAL metadata calls and allocating
    // a new band map for every frame.
    internal static int[] GetCachedRgbaBandMap(Dataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return GetRenderMetadata(dataset).RgbaBandMap;
    }

    internal static double[] GetCachedGeoTransform(Dataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return GetRenderMetadata(dataset).GeoTransform;
    }

    private static RenderMetadata GetRenderMetadata(Dataset dataset) =>
        RenderMetadataByDataset.GetValue(dataset, static key =>
            new Lazy<RenderMetadata>(() => InspectForRendering(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static RenderMetadata InspectForRendering(Dataset dataset)
    {
        int bandCount = Math.Min(dataset.RasterCount, 3);
        int[] bandMap;
        if (bandCount < 3)
        {
            bandMap = Enumerable.Range(1, bandCount).ToArray();
        }
        else
        {
            bandMap = [1, 2, 3];
            for (int index = 4; index <= dataset.RasterCount; index++)
            {
                using Band band = dataset.GetRasterBand(index);
                if (band.GetRasterColorInterpretation() != ColorInterp.GCI_AlphaBand) continue;
                bandMap = [1, 2, 3, index];
                break;
            }
        }

        RasterDatasetSemantics semantics = InspectSemantics(dataset);
        double[] geoTransform = new double[6];
        dataset.GetGeoTransform(geoTransform);
        return new RenderMetadata(bandMap, semantics, geoTransform);
    }

    public static bool SupportsExternalOverviews(Dataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        string driver = dataset.GetDriver()?.ShortName ?? string.Empty;
        return driver is not ("WMS" or "ECW" or "JP2ECW");
    }

    public static RasterDatasetSemantics Inspect(Dataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return InspectSemantics(dataset);
    }

    private static RasterDatasetSemantics InspectSemantics(Dataset dataset)
    {
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
        ArgumentNullException.ThrowIfNull(dataset);
        RasterDatasetSemantics semantics = GetRenderMetadata(dataset).Semantics;
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
