using System.Globalization;
using System.Xml.Linq;
using OSGeo.GDAL;
using SkiaSharp;

namespace GeoNex.Services;

/// <summary>Reads an owned or leased GDAL dataset. Failed tiles never replace a good cached image.</summary>
public static class OnlineRasterFrameReader
{
    public static SKBitmap Read(string xml, string targetSrs, SKRect localBounds, double offsetX, double offsetY,
        int width, int height, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var source = OpenSource(xml);
        return ReadDataset(source, targetSrs, localBounds, offsetX, offsetY, width, height, token);
    }

    internal static Dataset OpenSource(string xml)
    {
        var config = XDocument.Parse(xml);
        // A delimiter-only list permits no HTTP failures to become black tiles.
        config.Root?.SetElementValue("ZeroBlockHttpCodes", ",");
        config.Root?.Element("ZeroBlockOnServerException")?.Remove();
        string path = $"/vsimem/geonex-online-{Guid.NewGuid():N}.xml";
        Gdal.FileFromMemBuffer(path, System.Text.Encoding.UTF8.GetBytes(config.ToString(SaveOptions.DisableFormatting)));
        try { return Gdal.Open(path, Access.GA_ReadOnly) ?? throw new IOException(Gdal.GetLastErrorMsg()); }
        finally { Gdal.Unlink(path); } // WMS has parsed the XML; tiles have independent URLs.
    }

    internal static SKBitmap ReadDataset(Dataset source, string targetSrs, SKRect localBounds, double offsetX, double offsetY,
        int width, int height, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (localBounds.IsEmpty || !double.IsFinite(offsetX) || !double.IsFinite(offsetY) ||
            !float.IsFinite(localBounds.Left) || !float.IsFinite(localBounds.Right) ||
            !float.IsFinite(localBounds.Top) || !float.IsFinite(localBounds.Bottom))
            throw new ArgumentException("Invalid online viewport bounds.", nameof(localBounds));
        token.ThrowIfCancellationRequested();
        double left = localBounds.Left + offsetX, right = localBounds.Right + offsetX;
        double top = offsetY - localBounds.Top, bottom = offsetY - localBounds.Bottom;
        double[] geo = new double[6];
        source.GetGeoTransform(geo);
        if (SrsFactory.IsSame(source.GetProjection(), targetSrs) && RasterGeometry.IsAxisAligned(geo) && geo[1] > 0 && geo[5] < 0)
        {
            int available = GdalRuntimeConfiguration.Apply().AvailablePhysicalMb;
            bool inside = left >= geo[0] && right <= geo[0] + source.RasterXSize * geo[1] &&
                top <= geo[3] && bottom >= geo[3] + source.RasterYSize * geo[5];
            var direct = inside ? OnlineBasemapPolicy.CalculateDirectRenderDimensions(width, height, available)
                : OnlineBasemapPolicy.CalculateRenderDimensions(width, height, available, false);
            width = direct.Width; height = direct.Height;
            return ReadDirect(source, geo, left, top, right, bottom, width, height, token);
        }

        var dimensions = OnlineBasemapPolicy.CalculateRenderDimensions(width, height,
            GdalRuntimeConfiguration.Apply().AvailablePhysicalMb, false);
        width = dimensions.Width; height = dimensions.Height;

        string F(double n) => n.ToString("R", CultureInfo.InvariantCulture);
        using var options = new GDALWarpAppOptions(new[] { "-of", "MEM", "-t_srs", targetSrs,
            "-te", F(left), F(bottom), F(right), F(top), "-ts", width.ToString(CultureInfo.InvariantCulture),
            height.ToString(CultureInfo.InvariantCulture), "-r", "bilinear", "-et", "0", "-dstalpha", "-wm", "32" });
        using var warped = Gdal.Warp("", new[] { source }, options,
            (complete, message, data) => token.IsCancellationRequested ? 0 : 1, null)
            ?? throw new IOException(Gdal.GetLastErrorMsg());
        token.ThrowIfCancellationRequested();
        return ReadPixels(warped, 0, 0, width, height, width, height, token);
    }

    private static SKBitmap ReadDirect(Dataset source, double[] geo, double left, double top, double right, double bottom,
        int width, int height, CancellationToken token, int stripHeight = 128, bool writeRgbDirect = true)
    {
        double px0 = (left - geo[0]) / geo[1], px1 = (right - geo[0]) / geo[1];
        double py0 = (top - geo[3]) / geo[5], py1 = (bottom - geo[3]) / geo[5];
        double x0 = Math.Max(0, px0), y0 = Math.Max(0, py0);
        double x1 = Math.Min(source.RasterXSize, px1), y1 = Math.Min(source.RasterYSize, py1);
        var output = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        try
        {
            using var canvas = new SKCanvas(output);
            canvas.Clear(SKColors.Transparent);
            if (px0 >= 0 && py0 >= 0 && px1 <= source.RasterXSize && py1 <= source.RasterYSize)
            {
                // One resampling, exact physical-pixel grid. The float window
                // retains fractional native pixels even in deep overzoom.
                int ix = (int)Math.Floor(px0), iw = (int)Math.Ceiling(px1) - ix;
                bool directRgb = writeRgbDirect && source.RasterCount == 3;
                // RGB has no alpha to premultiply. GDAL can write the final RGBA
                // rows directly, preserving opaque alpha and avoiding one bitmap
                // allocation + Skia copy per strip. RGBA retains its conversion.
                if (directRgb) output.Erase(SKColors.Black);
                int[] rgbBands = { 1, 2, 3 };
                using var extra = new RasterIOExtraArg
                {
                    eResampleAlg = RIOResampleAlg.GRIORA_Bilinear,
                    bFloatingPointWindowValidity = 1,
                    dfXOff = px0, dfXSize = px1 - px0
                };
                for (int row = 0; row < height; row += stripHeight)
                {
                    int rows = Math.Min(stripHeight, height - row);
                    double a = py0 + row * (py1 - py0) / height;
                    double b = py0 + (row + rows) * (py1 - py0) / height;
                    int iy = (int)Math.Floor(a), ih = (int)Math.Ceiling(b) - iy;
                    if (directRgb)
                    {
                        token.ThrowIfCancellationRequested();
                        extra.dfYOff = a; extra.dfYSize = b - a;
                        var destination = IntPtr.Add(output.GetPixels(), checked(row * output.RowBytes));
                        CPLErr result = source.ReadRaster(ix, iy, iw, ih, destination, width, rows,
                            DataType.GDT_Byte, 3, rgbBands, 4, output.RowBytes, 1, extra);
                        if (result != CPLErr.CE_None) throw new IOException($"Online RasterIO failed: {Gdal.GetLastErrorMsg()}");
                        token.ThrowIfCancellationRequested();
                        continue;
                    }
                    using var strip = ReadPixels(source, ix, iy, iw, ih, width, rows, token,
                        a, b - a, px0, px1 - px0);
                    canvas.DrawBitmap(strip, 0, row);
                }
                token.ThrowIfCancellationRequested();
                return output;
            }
            if (x1 > x0 && y1 > y0)
            {
                int x = (int)Math.Floor(x0), y = (int)Math.Floor(y0);
                int w = Math.Min((int)Math.Ceiling(x1) - x, source.RasterXSize - x);
                int h = Math.Min((int)Math.Ceiling(y1) - y, source.RasterYSize - y);
                double sx = width / (px1 - px0), sy = height / (py1 - py0);
                // Native-pixel overzoom is magnified by Skia, not by a huge GDAL allocation.
                int dw = Math.Min(width + 2, sx > 1 ? w : Math.Max(1, (int)Math.Round(w * sx)));
                int dh = Math.Min(height + 2, sy > 1 ? h : Math.Max(1, (int)Math.Round(h * sy)));
                using var tile = ReadPixels(source, x, y, w, h, dw, dh, token);
                using var image = SKImage.FromBitmap(tile);
                canvas.DrawImage(image, new SKRect((float)((x - px0) * sx), (float)((y - py0) * sy),
                    (float)((x + w - px0) * sx), (float)((y + h - py0) * sy)),
                    new SKSamplingOptions(SKFilterMode.Linear));
            }
            token.ThrowIfCancellationRequested();
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    private static SKBitmap ReadPixels(Dataset source, int x, int y, int w, int h, int width, int height, CancellationToken token,
        double? sourceY = null, double sourceHeight = 0, double sourceX = 0, double sourceWidth = 0)
    {
        // GDAL supplies straight RGBA, including Warp's destination alpha.
        // Skia performs premultiplication when compositing this bitmap.
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        try
        {
            bitmap.Erase(SKColors.Black); // RGB tiles retain opaque alpha; RGBA overwrites it.
            int bands = Math.Min(source.RasterCount, 4);
            if (bands < 3) throw new InvalidDataException("Online imagery must provide RGB/RGBA bands.");
            using var extra = new RasterIOExtraArg { eResampleAlg = RIOResampleAlg.GRIORA_Bilinear };
            if (sourceY.HasValue)
            {
                extra.bFloatingPointWindowValidity = 1;
                extra.dfXOff = sourceX; extra.dfXSize = sourceWidth;
                extra.dfYOff = sourceY.Value; extra.dfYSize = sourceHeight;
            }
            token.ThrowIfCancellationRequested();
            CPLErr result = source.ReadRaster(x, y, w, h, bitmap.GetPixels(), width, height, DataType.GDT_Byte,
                bands, Enumerable.Range(1, bands).ToArray(), 4, bitmap.RowBytes, 1, extra);
            if (result != CPLErr.CE_None) throw new IOException($"Online RasterIO failed: {Gdal.GetLastErrorMsg()}");
            token.ThrowIfCancellationRequested();
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
}
