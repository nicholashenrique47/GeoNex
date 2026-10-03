using GeoNex.Services;
using SkiaSharp;
using NetTopologySuite.Geometries;
using DigitizingCore;
GdalConfiguration.ConfigureGdal();
GdalConfiguration.ConfigureOgr();

int passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
}

foreach (float rotation in new[] { 0f, 45f, 90f, 180f, 270f })
foreach (float scale in new[] { 0.01f, 1f, 250f })
{
    SKMatrix matrix = SKMatrix.CreateScale(scale, scale).PostConcat(SKMatrix.CreateRotationDegrees(rotation));
    float tolerance = DigitizingCursor.WorldTolerance(matrix, 15);
    Check(Math.Abs(tolerance * scale - 15) < 0.0001, "A tolerância deve permanecer em pixels de tela.");

    SKPoint expected = new(10, 20);
    SKPoint? Find(SKPoint _, float radius, bool vertices, bool edges) =>
        vertices && !edges && Math.Abs(radius * scale - 15) < 0.0001 ? expected : null;
    var preview = DigitizingCursor.Resolve(new(11, 21), matrix, 15, true, false, null, false, 0, false, Find);
    var click = DigitizingCursor.Resolve(new(11, 21), matrix, 15, true, false, null, false, 0, false, Find);
    Check(preview == click && preview.Position == expected && preview.Snap == expected,
        "Prévia e clique devem resolver exatamente o mesmo ponto.");
}

int snapCalls = 0;
SKPoint? CountSnap(SKPoint point, float tolerance, bool vertices, bool edges) { snapCalls++; return new(99, 99); }
var fixedPoint = DigitizingCursor.Resolve(new(3, 4), SKMatrix.Identity, 15, true, true,
    new(0, 0), true, 10, true, CountSnap);
Check(fixedPoint.Position == new SKPoint(6, 8) && fixedPoint.Snap is null && snapCalls == 0,
    "Restrição fixa deve prevalecer sem consultar o índice de snap.");
var maximumPoint = DigitizingCursor.Resolve(new(30, 40), SKMatrix.Identity, 15, true, true,
    new(0, 0), true, 10, false, CountSnap);
Check(maximumPoint.Position == new SKPoint(6, 8) && snapCalls == 0,
    "Restrição máxima deve limitar o segmento.");
Check(DigitizingCursor.WorldTolerance(default, 15) == 0, "Matriz degenerada deve desativar a consulta.");
Check(DigitizingCursor.WorldTolerance(SKMatrix.Identity, float.NaN) == 0, "Tolerância inválida deve ser segura.");

var points = new List<int>();
var history = new SketchVertexHistory<int>(points);
Check(!history.Undo() && !history.Redo(), "Histórico vazio deve ser seguro.");
history.Add(1); history.Add(2); history.Add(3);
Check(history.Undo() && history.Undo() && points.SequenceEqual([1]) && history.CanRedo,
    "Desfazer deve remover somente os últimos vértices.");
Check(history.Redo() && points.SequenceEqual([1, 2]), "Refazer deve restaurar na ordem correta.");
history.Add(9);
Check(!history.CanRedo && points.SequenceEqual([1, 2, 9]), "Novo vértice deve descartar a ramificação antiga.");
history.Clear();
Check(points.Count == 0 && !history.CanUndo && !history.CanRedo, "Limpar deve zerar desenho e histórico.");

void Reject(DigitizingGeometry.Kind kind, Coordinate[] input, string expected)
{
    var snapshot = input.Select(c => c.Copy()).ToArray();
    try
    {
        DigitizingGeometry.Create(kind, input);
        throw new InvalidOperationException("An invalid sketch was accepted.");
    }
    catch (ArgumentException exception)
    {
        Check(exception.Message.Contains(expected, StringComparison.OrdinalIgnoreCase), exception.Message);
        Check(input.Zip(snapshot).All(pair => pair.First.Equals2D(pair.Second)),
            "Rejected geometry must preserve every source vertex.");
    }
}

var triangle = new[] { new Coordinate(0, 0), new Coordinate(10, 0), new Coordinate(0, 10) };
var polygon = DigitizingGeometry.Create(DigitizingGeometry.Kind.Polygon, triangle);
Check(polygon.IsValid && polygon.Area == 50 && polygon.NumPoints == 4,
    "An open triangle must close exactly once and preserve its area.");
var closed = DigitizingGeometry.Create(DigitizingGeometry.Kind.Polygon, triangle.Append(triangle[0]));
Check(closed.EqualsExact(polygon) && triangle.Length == 3,
    "Closing via snap and via Finish must produce the same geometry without mutating the sketch.");
triangle[0].X = 99;
Check(polygon.Coordinates[0].X == 0, "The accepted geometry must own its coordinates.");
Reject(DigitizingGeometry.Kind.Polygon,
    [new(0, 0), new(4, 4), new(0, 4), new(4, 0)], "cruza");
Reject(DigitizingGeometry.Kind.Polygon, [new(0, 0), new(1, 1), new(2, 2)], "contorno");
Reject(DigitizingGeometry.Kind.Polygon, [new(0, 0), new(1, 0)], "pelo menos 3");
Reject(DigitizingGeometry.Kind.Line, [], "pelo menos 2");
Reject(DigitizingGeometry.Kind.Line, [new(1, 1), new(1, 1)], "coincidem");
Reject(DigitizingGeometry.Kind.Point, [new(1, 1), new(2, 2)], "exatamente um");
foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
{
    try
    {
        DigitizingGeometry.Create(DigitizingGeometry.Kind.Point, [new(invalid, 0)]);
        throw new InvalidOperationException("Non-finite point accepted.");
    }
    catch (ArgumentException exception) { Check(exception.Message.Contains("inválida"), "Finite coordinates required."); }
}
var geographic = DigitizingGeometry.Create(DigitizingGeometry.Kind.Line,
    [new(-47, -23), new(-46.999999, -23)]);
Check(geographic.Length > 0 && geographic.Length < 0.001,
    "Valid short geographic segments cannot be rejected by a metre-based epsilon.");
var smallPolygon = DigitizingGeometry.Create(DigitizingGeometry.Kind.Polygon,
    [new(0, 0), new(1e-7, 0), new(0, 1e-7)]);
Check(smallPolygon.Area > 0, "Small valid polygons must not be silently collapsed.");
var crossingLine = DigitizingGeometry.Create(DigitizingGeometry.Kind.Line,
    [new(0, 0), new(4, 4), new(0, 4), new(4, 0)]);
Check(crossingLine.IsValid && !crossingLine.IsSimple,
    "Lines may legitimately cross; polygon-ring restrictions do not apply to them.");
var pointGeometry = DigitizingGeometry.Create(DigitizingGeometry.Kind.Point, [new(500000, 7400000)]);
Check(pointGeometry.Coordinate.Equals2D(new Coordinate(500000, 7400000)), "Project coordinates must be preserved.");

SnapSearch.Candidate? FindSnap(SKPath path, SKPoint cursor, float radius,
    bool vertices = false, bool edges = false, bool midpoints = false)
{
    var search = new SnapSearch(cursor, radius, vertices, edges, midpoints);
    search.AddPath(path);
    return search.Best;
}
using var multipart = new SKPath();
multipart.MoveTo(0, 0); multipart.LineTo(1, 0);
multipart.MoveTo(10, 0); multipart.LineTo(11, 0);
Check(FindSnap(multipart, new(5, 0), 1, edges: true) is null,
    "Parts must not create a fake connecting edge.");
Check(FindSnap(multipart, new(5.5f, 0), 1, midpoints: true) is null,
    "Parts must not create a fake midpoint.");
Check(FindSnap(multipart, new(10.5f, 0.2f), 1, midpoints: true)?.Point == new SKPoint(10.5f, 0),
    "Each part must retain its own midpoint.");
using var ring = new SKPath();
ring.MoveTo(0, 0); ring.LineTo(10, 0); ring.LineTo(10, 10); ring.LineTo(0, 10); ring.Close();
Check(FindSnap(ring, new(0.2f, 5), 1, edges: true)?.Point == new SKPoint(0, 5),
    "Implicit closing edges must participate in snap.");
Check(FindSnap(ring, new(0.2f, 5), 1, midpoints: true)?.Kind == SnapKind.Midpoint,
    "Closing edges also have midpoints.");
ring.MoveTo(4, 4); ring.LineTo(6, 4); ring.LineTo(6, 6); ring.LineTo(4, 6); ring.Close();
Check(FindSnap(ring, new(2, 7), 0.1f, edges: true) is null,
    "Hole contours must not be connected to the shell.");
Check(FindSnap(ring, new(4.1f, 5), 0.2f, edges: true)?.Point == new SKPoint(4, 5),
    "Real hole edges remain snappable.");
using var single = new SKPath();
single.MoveTo(2, 3);
Check(FindSnap(single, new(2.1f, 3), 1, vertices: true)?.Point == new SKPoint(2, 3),
    "A single point must snap without requiring a second point.");
using var segment = new SKPath();
segment.MoveTo(0, 0); segment.LineTo(10, 0);
Check(FindSnap(segment, new(5, 0.5f), 1, vertices: true) is null,
    "Vertex-only mode must not attract midpoints.");
Check(FindSnap(segment, new(5, 0.5f), 1, edges: true, midpoints: true)?.Kind == SnapKind.Midpoint,
    "Equal-distance ties identify the more specific midpoint.");
Check(FindSnap(segment, new(0, 0), 1, vertices: true, edges: true)?.Kind == SnapKind.Vertex,
    "Equal-distance ties identify the endpoint.");
Check(FindSnap(segment, new(5, 1), 1, midpoints: true) != null,
    "The tolerance boundary must be inclusive.");
Check(FindSnap(segment, new(5, 1.001f), 1, midpoints: true) is null,
    "Candidates beyond the tolerance must be rejected.");
using var curve = new SKPath();
curve.MoveTo(0, 0); curve.QuadTo(5, 10, 10, 0);
Check(FindSnap(curve, new(5, 0), 0.1f, edges: true, midpoints: true) is null,
    "A curve's chord is not a real linear edge.");
Check(FindSnap(curve, new(5, 10), 0.1f, vertices: true) is null,
    "Bezier controls are not geometry vertices.");
var closest = new SnapSearch(new(0, 0), 10, true, false);
closest.AddVertex(new(4, 0)); closest.AddVertex(new(1, 0)); closest.AddVertex(new(3, 0));
Check(closest.Best?.Point == new SKPoint(1, 0), "The nearest candidate must win across paths.");
int midpointCalls = 0;
var sketchSnap = new SnapSearch(new(5, 0.5f), 1, false, false, true);
sketchSnap.AddPolyline(new SKPoint[] { new(0, 0), new(10, 0) });
Check(sketchSnap.Best?.Point == new SKPoint(5, 0), "An unfinished sketch supports midpoint snapping.");
var midpointCursor = DigitizingCursor.Resolve(new(5, 0.5f), SKMatrix.Identity, 1,
    false, false, null, false, 0, false, (p, radius, vertices, edges) =>
    {
        midpointCalls++;
        return FindSnap(segment, p, radius, vertices, edges, midpoints: true)?.Point;
    }, midpoints: true);
Check(midpointCalls == 1 && midpointCursor.Snap == new SKPoint(5, 0),
    "Midpoint-only mode must reach the shared preview/click resolver.");

using var measurements = new MapMeasurementService();
var metric = measurements.Measure([new(0, 0), new(100, 0)], "EPSG:31983", 500000, 7400000, false);
Check(Math.Abs(metric.LengthMetres - 100) < 1e-6 && !metric.Geodesic,
    "Projected metre CRS must return grid length in metres.");
var metricArea = measurements.Measure([new(0, 0), new(100, 0), new(0, 100)],
    "EPSG:31983", 500000, 7400000, true);
Check(metricArea.AreaSquareMetres is > 4999.99 and < 5000.01,
    "Projected metre CRS must return area in square metres.");
var feet = measurements.Measure([new(0, 0), new(100, 0)], "EPSG:2227", 0, 0, false);
Check(Math.Abs(feet.LengthMetres - 100 * 1200.0 / 3937.0) < 1e-6,
    "Projected US survey-foot CRS must convert length to metres.");
var geodesic = measurements.Measure([new(0, 0), new(0.001f, 0)], "EPSG:4326", -47, -23, false);
Check(geodesic.Geodesic && geodesic.LengthMetres is > 100 and < 105,
    "Geographic CRS must use ellipsoidal geodesic length.");
var geodesicArea = measurements.Measure([new(0, 0), new(0.001f, 0), new(0, 0.001f)],
    "EPSG:4326", -47, -23, true);
Check(geodesicArea.AreaSquareMetres is > 4500 and < 6000 && geodesicArea.Geodesic,
    "Geographic CRS must use ellipsoidal geodesic area.");
Check(MapMeasurementService.GridAzimuth(new(0, 0), new(1, 0)) == 90,
    "Grid azimuth must be clockwise from north in the local Y-down frame.");

Console.WriteLine($"Digitizing core contracts passed: {passed} assertions.");
