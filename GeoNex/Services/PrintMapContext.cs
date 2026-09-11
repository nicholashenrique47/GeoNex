using System.Collections.Specialized;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace GeoNex.Services;

/// <summary>Print-only camera contract. Never changes the project camera, raster or cache policy.</summary>
public sealed record PrintMapContext(string CrsName, string Token, double MetersPerUnit,
    double CenterX, double CenterY, double CssZoom, double Scale)
{
    public double? OriginX { get; init; }
    public double? OriginY { get; init; }
    public static double ScaleToZoom(double scale, double metersPerUnit)
    {
        if (!double.IsFinite(scale) || scale < 1 || scale > 1_000_000_000 ||
            !double.IsFinite(metersPerUnit) || metersPerUnit <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale), "Escala ou unidade do SRC inválida.");
        return 96 / .0254 * metersPerUnit / scale;
    }

    public static PrintMapContext Capture(MapRenderingService service)
    {
        using var sr = SrsFactory.FromUserInput(service.ProjetoSRS);
        double units = sr.IsProjected() == 1 ? sr.GetLinearUnits() : 0;
        if (!double.IsFinite(units) || units <= 0) units = 0;
        double zoom = service.ViewportEscalaAutoFit * (double)service.CameraZoom;
        if (!double.IsFinite(zoom) || zoom <= 0) zoom = 1;
        var center = MapCoordinateSpace.ApplyCssPanToLocalCenter(
            new SKPoint(service.ViewportMidX, service.ViewportMidY), (float)zoom,
            service.CameraPanX, service.CameraPanY);
        string name = sr.GetName() ?? service.ProjetoSRS;
        string basis = FormattableString.Invariant($"{service.ProjetoSRS}|{service.OffsetMundoX:R}|{service.OffsetMundoY:R}");
        string token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basis)));
        return new(name, token, units, center.X, center.Y, zoom, units > 0 ? 96 / .0254 * units / zoom : 0)
        { OriginX = service.OffsetMundoX, OriginY = service.OffsetMundoY };
    }

    /// <summary>Rejects stale CRS/origin and malformed parameters rather than printing a wrong scale.</summary>
    public static bool TryRead(NameValueCollection query, PrintMapContext basis,
        out SKPoint center, out float zoom)
    {
        center = default; zoom = 0;
        bool Read(string key, out double value) => double.TryParse(query[key], NumberStyles.Float,
            CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        if (query["ct"] != basis.Token || !Read("cs", out double scale) || scale < 1 || scale > 1e9 ||
            !Read("cx", out double x) || !Read("cy", out double y) || Math.Abs(x) > 1e12 || Math.Abs(y) > 1e12 || basis.MetersPerUnit <= 0)
            return false;
        zoom = (float)ScaleToZoom(scale, basis.MetersPerUnit);
        center = new((float)x, (float)y);
        return float.IsFinite(zoom) && zoom > 0;
    }

    public static bool TryReadLayoutSize(NameValueCollection query, out double width, out double height)
    {
        width = height = 0;
        return double.TryParse(query["cw"], NumberStyles.Float, CultureInfo.InvariantCulture, out width) &&
            double.TryParse(query["ch"], NumberStyles.Float, CultureInfo.InvariantCulture, out height) &&
            double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0 &&
            width <= MapViewportMetrics.MaximumCssDimension && height <= MapViewportMetrics.MaximumCssDimension;
    }
}
