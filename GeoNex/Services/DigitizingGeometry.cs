using System.Globalization;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Valid;

namespace GeoNex.Services;

/// <summary>Builds an owned, finite, valid 2D geometry without modifying the sketch.</summary>
public static class DigitizingGeometry
{
    public enum Kind { Point, Line, Polygon }

    /// <summary>
    /// Validates a complete sketch in project coordinates. No distance epsilon is
    /// imposed: a project unit may be metres, feet or degrees. Line crossings are
    /// allowed; self-intersections of polygon rings are rejected.
    /// </summary>
    /// <exception cref="ArgumentException">The sketch cannot form the requested geometry.</exception>
    public static Geometry Create(Kind kind, IEnumerable<Coordinate> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var points = new List<Coordinate>();
        foreach (var point in source)
        {
            if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y))
                throw new ArgumentException($"O vértice {points.Count + 1} tem uma coordenada inválida.", nameof(source));
            points.Add(new Coordinate(point.X, point.Y));
        }

        // A snapped closing vertex is accepted once and normalized before validation.
        if (kind == Kind.Polygon && points.Count > 1 && points[0].Equals2D(points[^1]))
            points.RemoveAt(points.Count - 1);

        int minimum = kind switch { Kind.Point => 1, Kind.Line => 2, _ => 3 };
        if (points.Count < minimum)
            throw new ArgumentException($"Adicione pelo menos {minimum} vértice(s) para concluir.", nameof(source));
        if (kind == Kind.Point && points.Count != 1)
            throw new ArgumentException("Uma feição de ponto deve ter exatamente um vértice.", nameof(source));
        for (int i = 1; i < points.Count; i++)
            if (points[i - 1].Equals2D(points[i]))
                throw new ArgumentException($"Os vértices {i} e {i + 1} coincidem. Remova o vértice repetido.", nameof(source));

        var factory = new GeometryFactory();
        Geometry geometry = kind switch
        {
            Kind.Point => factory.CreatePoint(points[0]),
            Kind.Line => factory.CreateLineString(points.ToArray()),
            _ => factory.CreatePolygon(points.Append(points[0].Copy()).ToArray())
        };
        var issue = new IsValidOp(geometry).ValidationError;
        if (issue != null)
        {
            string reason = issue.ErrorType switch
            {
                TopologyValidationErrors.SelfIntersection or TopologyValidationErrors.RingSelfIntersection =>
                    "O contorno cruza ou toca a si mesmo. Ajuste os vértices antes de concluir.",
                TopologyValidationErrors.TooFewPoints => "Faltam vértices distintos para formar a geometria.",
                _ => "A geometria tem um erro de topologia. Revise os vértices antes de concluir."
            };
            string location = issue.Coordinate is { } coordinate
                ? string.Create(CultureInfo.InvariantCulture, $" Local: X {coordinate.X:G12}, Y {coordinate.Y:G12}.")
                : "";
            throw new ArgumentException(reason + location, nameof(source));
        }
        if (kind == Kind.Polygon && (!(geometry.Area > 0) || !double.IsFinite(geometry.Area)))
            throw new ArgumentException("O polígono precisa delimitar uma área finita maior que zero.", nameof(source));
        if (kind == Kind.Line && (!(geometry.Length > 0) || !double.IsFinite(geometry.Length)))
            throw new ArgumentException("A linha precisa ter comprimento finito maior que zero.", nameof(source));
        return geometry;
    }
}
