using OSGeo.GDAL;
using System.Runtime.CompilerServices;

namespace GeoNex.Services;

public readonly record struct RasterDatasetSemantics(
    bool HasPalette,
    bool IsFloatingPoint,
    bool IsRgbLike);

internal readonly record struct RasterPaletteColor(byte Red, byte Green, byte Blue, byte Alpha);

public static class RasterDatasetPolicy
{
    private sealed record RenderMetadata(
        int[] RgbaBandMap,
        RasterPaletteColor[]? Palette,
        bool ExpandGray,
        bool ExpandGrayAlpha,
        RasterDatasetSemantics Semantics,
        double[] GeoTransform,
        string DriverShortName,
        bool HasPyramids);

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

    internal static RasterPaletteColor[]? GetCachedPalette(Dataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return GetRenderMetadata(dataset).Palette;
    }

    internal static bool RequiresGrayExpansion(Dataset dataset) => GetRenderMetadata(dataset).ExpandGray;

    internal static bool RequiresGrayAlphaExpansion(Dataset dataset) => GetRenderMetadata(dataset).ExpandGrayAlpha;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void ExpandGrayInPlace(IntPtr pixels, int width, int height, int rowBytes)
    {
        if (pixels == IntPtr.Zero || width <= 0 || height <= 0 || rowBytes != (long)width * 4)
            throw new ArgumentException("Invalid gray destination.");
        int count = checked(width * height);
        byte* data = (byte*)pixels;
        uint* dst32 = (uint*)pixels;
        int index = count - 1;
        for (; index >= 3; index -= 4)
        {
            uint g3 = data[index];
            uint g2 = data[index - 1];
            uint g1 = data[index - 2];
            uint g0 = data[index - 3];
            dst32[index] = 0xFF000000u | (g3 * 0x00010101u);
            dst32[index - 1] = 0xFF000000u | (g2 * 0x00010101u);
            dst32[index - 2] = 0xFF000000u | (g1 * 0x00010101u);
            dst32[index - 3] = 0xFF000000u | (g0 * 0x00010101u);
        }
        for (; index >= 0; index--)
        {
            uint gray = data[index];
            dst32[index] = 0xFF000000u | (gray * 0x00010101u);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void ExpandGrayAlphaInPlace(IntPtr pixels, int width, int height, int rowBytes)
    {
        if (pixels == IntPtr.Zero || width <= 0 || height <= 0 || rowBytes != (long)width * 4)
            throw new ArgumentException("Invalid gray-alpha destination.");
        int count = checked(width * height);
        uint* dst32 = (uint*)pixels;
        for (int index = count - 1; index >= 0; index--)
        {
            uint raw = dst32[index];
            uint gray = raw & 0xFFu;
            uint alpha = (raw >> 8) & 0xFFu;
            dst32[index] = (alpha << 24) | (gray * 0x00010101u);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void ExpandPaletteInPlace(IntPtr pixels, int width, int height, int rowBytes,
        RasterPaletteColor[] palette, bool hasSeparateAlpha)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (pixels == IntPtr.Zero || width <= 0 || height <= 0 || rowBytes != (long)width * 4)
            throw new ArgumentException("Invalid palette destination.");
        int count = checked(width * height);
        byte* data = (byte*)pixels;
        uint* dst32 = (uint*)pixels;
        if (hasSeparateAlpha)
        {
            for (int index = count - 1; index >= 0; index--)
            {
                uint raw = dst32[index];
                byte paletteIndex = (byte)raw;
                byte sourceAlpha = (byte)(raw >> 8);
                RasterPaletteColor color = paletteIndex < palette.Length
                    ? palette[paletteIndex] : default;
                uint alpha = (uint)((color.Alpha * sourceAlpha + 127) / 255);
                dst32[index] = color.Red | ((uint)color.Green << 8) | ((uint)color.Blue << 16) | (alpha << 24);
            }
            return;
        }

        Span<uint> packed = stackalloc uint[256];
        packed.Clear();
        int limit = Math.Min(palette.Length, 256);
        for (int i = 0; i < limit; i++)
        {
            RasterPaletteColor c = palette[i];
            packed[i] = c.Red | ((uint)c.Green << 8) | ((uint)c.Blue << 16) | ((uint)c.Alpha << 24);
        }

        int idx = count - 1;
        for (; idx >= 3; idx -= 4)
        {
            byte p3 = data[idx];
            byte p2 = data[idx - 1];
            byte p1 = data[idx - 2];
            byte p0 = data[idx - 3];
            dst32[idx] = packed[p3];
            dst32[idx - 1] = packed[p2];
            dst32[idx - 2] = packed[p1];
            dst32[idx - 3] = packed[p0];
        }
        for (; idx >= 0; idx--)
        {
            dst32[idx] = packed[data[idx]];
        }
    }

    internal static double[] GetCachedGeoTransform(Dataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return GetRenderMetadata(dataset).GeoTransform;
    }

    internal static bool IsDirectWaveletDataset(Dataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return GetRenderMetadata(dataset).DriverShortName is "ECW" or "JP2ECW";
    }

    internal static bool SupportsDirectSkiaFilteredRead(Dataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        RenderMetadata metadata = GetRenderMetadata(dataset);
        return metadata.DriverShortName is "ECW" or "JP2ECW"
            || (metadata.DriverShortName is "GTiff" or "COG" && metadata.HasPyramids && !metadata.Semantics.IsFloatingPoint);
    }

    private static RenderMetadata GetRenderMetadata(Dataset dataset) =>
        RenderMetadataByDataset.GetValue(dataset, static key =>
            new Lazy<RenderMetadata>(() => InspectForRendering(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static RenderMetadata InspectForRendering(Dataset dataset)
    {
        RasterDatasetSemantics semantics = InspectSemantics(dataset);
        RasterPaletteColor[]? palette = InspectPalette(dataset, semantics);
        bool expandGray = palette == null && dataset.RasterCount == 1;
        bool expandGrayAlpha = palette == null && dataset.RasterCount == 2 && IsGrayAlpha(dataset);
        int bandCount = Math.Min(dataset.RasterCount, 3);
        int[] bandMap;
        int alphaBand = FindAlphaBand(dataset);
        if (palette != null)
        {
            bandMap = alphaBand > 0 ? [1, alphaBand] : [1];
        }
        else if (expandGray)
        {
            // Read one channel and expand into RGBA after RasterIO.
            bandMap = [1];
        }
        else if (expandGrayAlpha)
        {
            bandMap = [1, 2];
        }
        else if (dataset.RasterCount >= 3 && alphaBand > 0 && alphaBand <= 3)
        {
            // Keep all interpreted color channels and alpha in their semantic
            // positions when a source does not use the conventional RGBA order.
            int[] colorBands = FindRgbBands(dataset);
            bandMap = colorBands.Length == 3 ? [colorBands[0], colorBands[1], colorBands[2], alphaBand]
                : [1, 2, 3, alphaBand];
        }
        else if (bandCount < 3)
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

        double[] geoTransform = new double[6];
        dataset.GetGeoTransform(geoTransform);
        using Driver? driver = dataset.GetDriver();
        string driverShortName = driver?.ShortName ?? string.Empty;
        bool hasPyramids = false;
        if (dataset.RasterCount > 0)
        {
            using Band firstBand = dataset.GetRasterBand(1);
            hasPyramids = firstBand.GetOverviewCount() > 0;
        }
        return new RenderMetadata(bandMap, palette, expandGray, expandGrayAlpha, semantics,
            geoTransform, driverShortName, hasPyramids);
    }

    private static RasterPaletteColor[]? InspectPalette(Dataset dataset, RasterDatasetSemantics semantics)
    {
        if (!semantics.HasPalette || dataset.RasterCount <= 0) return null;
        using Band band = dataset.GetRasterBand(1);
        using ColorTable? table = band.GetRasterColorTable();
        if (table == null || table.GetCount() <= 0) return null;
        PaletteInterp interpretation = table.GetPaletteInterpretation();
        if (interpretation is not (PaletteInterp.GPI_Gray or PaletteInterp.GPI_RGB or
            PaletteInterp.GPI_CMYK or PaletteInterp.GPI_HLS)) return null;

        var colors = new RasterPaletteColor[table.GetCount()];
        for (int index = 0; index < colors.Length; index++)
        {
            ColorEntry entry = table.GetColorEntry(index);
            byte c1 = ToByte(entry.c1), c2 = ToByte(entry.c2), c3 = ToByte(entry.c3), c4 = ToByte(entry.c4);
            colors[index] = interpretation switch
            {
                PaletteInterp.GPI_Gray => new RasterPaletteColor(c1, c1, c1, c4),
                PaletteInterp.GPI_RGB => new RasterPaletteColor(c1, c2, c3, c4),
                PaletteInterp.GPI_CMYK => new RasterPaletteColor(
                    (byte)((255 - c1) * (255 - c4) / 255),
                    (byte)((255 - c2) * (255 - c4) / 255),
                    (byte)((255 - c3) * (255 - c4) / 255), 255),
                PaletteInterp.GPI_HLS => HlsToRgb(entry.c1, c2, c3, c4),
                _ => default
            };
        }
        return colors;
    }

    private static RasterPaletteColor HlsToRgb(short hue, byte luminance, byte saturation, byte alpha)
    {
        double h = ((hue % 360) + 360) % 360 / 360.0, l = luminance / 255.0, s = saturation / 255.0;
        if (s <= 0) return new RasterPaletteColor(luminance, luminance, luminance, alpha);
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        static double Channel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        return new RasterPaletteColor(
            (byte)Math.Clamp((int)Math.Round(Channel(p, q, h + 1.0 / 3) * 255), 0, 255),
            (byte)Math.Clamp((int)Math.Round(Channel(p, q, h) * 255), 0, 255),
            (byte)Math.Clamp((int)Math.Round(Channel(p, q, h - 1.0 / 3) * 255), 0, 255),
            alpha);
    }

    private static byte ToByte(short value) => (byte)Math.Clamp((int)value, 0, 255);

    private static int FindAlphaBand(Dataset dataset)
    {
        for (int index = 1; index <= dataset.RasterCount; index++)
        {
            using Band band = dataset.GetRasterBand(index);
            if (band.GetRasterColorInterpretation() == ColorInterp.GCI_AlphaBand) return index;
        }
        return 0;
    }

    private static int[] FindRgbBands(Dataset dataset)
    {
        int red = 0, green = 0, blue = 0;
        for (int index = 1; index <= dataset.RasterCount; index++)
        {
            using Band band = dataset.GetRasterBand(index);
            switch (band.GetRasterColorInterpretation())
            {
                case ColorInterp.GCI_RedBand: red = index; break;
                case ColorInterp.GCI_GreenBand: green = index; break;
                case ColorInterp.GCI_BlueBand: blue = index; break;
            }
        }
        return red > 0 && green > 0 && blue > 0 ? [red, green, blue] : [];
    }

    private static bool IsGrayAlpha(Dataset dataset)
    {
        using Band gray = dataset.GetRasterBand(1);
        using Band alpha = dataset.GetRasterBand(2);
        return gray.GetRasterColorInterpretation() is ColorInterp.GCI_GrayIndex or ColorInterp.GCI_Undefined &&
               alpha.GetRasterColorInterpretation() == ColorInterp.GCI_AlphaBand;
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
        bool isRgbLike = dataset.RasterCount >= 3 ||
                         (interpretation == ColorInterp.GCI_GrayIndex && !hasPalette);
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
