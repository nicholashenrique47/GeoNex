using NetTopologySuite.Geometries;
using OSGeo.OSR;
using SkiaSharp;
using OgrGeometry = OSGeo.OGR.Geometry;
using OgrKind = OSGeo.OGR.wkbGeometryType;

namespace GeoNex.Services;

/// <summary>Measures local screen-Y-down coordinates using the project's actual CRS.</summary>
public sealed class MapMeasurementService : IDisposable
{
    public sealed record Result(double LengthMetres, double? AreaSquareMetres,
        bool Geodesic, string Method, string? AreaError);
    private readonly object _gate = new();
    private SpatialReference? _reference;
    private string? _crs;
    private bool _geographic;
    private double _metresPerUnit;
    private bool _disposed;

    private void Configure(string crs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_crs == crs && _reference != null) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(crs);
        var reference = new SpatialReference("");
        try
        {
            if (reference.SetFromUserInput(crs) != 0 || reference.Validate() != 0)
                throw new ArgumentException("O SRC do projeto não é válido para medição.");
            reference.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            bool geographic = reference.IsGeographic() == 1;
            double units = reference.IsProjected() == 1 ? reference.GetLinearUnits() : 0;
            if (!geographic && (!double.IsFinite(units) || units <= 0))
                throw new ArgumentException("Defina um SRC geográfico ou projetado com unidades conhecidas.");
            _reference?.Dispose();
            _reference = reference;
            _crs = crs; _geographic = geographic; _metresPerUnit = units;
        }
        catch { reference.Dispose(); throw; }
    }

    /// <summary>
    /// Returns metres/m². Projected results are grid distances/areas, not ground
    /// distances. Geographic results follow the CRS ellipsoid via GDAL geodesics.
    /// All native objects stay under one lock, including the cached CRS.
    /// </summary>
    public Result Measure(IReadOnlyList<SKPoint> points, string crs, double offsetX, double offsetY, bool area)
    {
        ArgumentNullException.ThrowIfNull(points);
        lock (_gate)
        {
            Configure(crs);
            if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY) ||
                points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y)))
                throw new ArgumentException("A medição contém coordenadas inválidas.");
            double length = 0;
            if (_geographic && points.Count >= 2)
            {
                using var line = new OgrGeometry(OgrKind.wkbLineString);
                foreach (var p in points) line.AddPoint_2D(p.X + offsetX, offsetY - p.Y);
                line.AssignSpatialReference(_reference);
                length = line.GeodesicLength();
            }
            else if (!_geographic)
            {
                for (int i = 1; i < points.Count; i++)
                {
                    double dx = (double)points[i].X - points[i - 1].X;
                    double dy = (double)points[i].Y - points[i - 1].Y;
                    length += Math.Sqrt(dx * dx + dy * dy) * _metresPerUnit;
                }
            }
            if (!double.IsFinite(length) || length < 0)
                throw new InvalidOperationException("Não foi possível medir a distância neste SRC.");

            double? measuredArea = null;
            string? areaError = null;
            if (area && points.Count >= 3)
            {
                try
                {
                    // Validate in local coordinates first (avoids large-origin
                    // cancellation); geographic area itself uses absolute coordinates.
                    var polygon = DigitizingGeometry.Create(DigitizingGeometry.Kind.Polygon,
                        points.Select(p => new Coordinate(p.X, -p.Y)));
                    if (_geographic)
                    {
                        using var ring = new OgrGeometry(OgrKind.wkbLinearRing);
                        foreach (var c in polygon.Coordinates) ring.AddPoint_2D(c.X + offsetX, c.Y + offsetY);
                        using var surface = new OgrGeometry(OgrKind.wkbPolygon);
                        if (surface.AddGeometry(ring) != 0) throw new InvalidOperationException("Falha ao formar o anel da medição.");
                        surface.AssignSpatialReference(_reference);
                        measuredArea = surface.GeodesicArea();
                    }
                    else measuredArea = polygon.Area * _metresPerUnit * _metresPerUnit;
                    if (!double.IsFinite(measuredArea.Value) || measuredArea < 0)
                        throw new InvalidOperationException("Não foi possível medir a área neste SRC.");
                }
                catch (ArgumentException) { areaError = "Área indisponível: revise vértices repetidos ou cruzamentos do contorno."; }
                catch (InvalidOperationException ex) { measuredArea = null; areaError = ex.Message; }
            }
            return new(length, measuredArea, _geographic,
                _geographic ? "Geodésica · elipsoide do SRC" : "Plana · grade do SRC, convertida para metros", areaError);
        }
    }

    /// <summary>Grid bearing, clockwise from north; local Y points south.</summary>
    public static double? GridAzimuth(SKPoint start, SKPoint end)
    {
        double dx = (double)end.X - start.X, north = (double)start.Y - end.Y;
        if (!double.IsFinite(dx) || !double.IsFinite(north) || (dx == 0 && north == 0)) return null;
        return (Math.Atan2(dx, north) * 180 / Math.PI + 360) % 360;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _reference?.Dispose();
            _reference = null;
        }
    }
}
