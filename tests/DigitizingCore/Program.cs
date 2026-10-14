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
var orthogonalHorizontal = DigitizingCursor.Resolve(new(8, 3), SKMatrix.Identity, 15,
    false, false, new(0, 0), false, 0, false, CountSnap, false, false, true);
Check(orthogonalHorizontal.Position == new SKPoint(8, 0),
    "Modo ortogonal deve alinhar ao eixo dominante horizontal.");
var orthogonalVertical = DigitizingCursor.Resolve(new(3, 8), SKMatrix.Identity, 15,
    false, false, new(0, 0), false, 0, false, CountSnap, false, false, true);
Check(orthogonalVertical.Position == new SKPoint(0, 8),
    "Modo ortogonal deve alinhar ao eixo dominante vertical.");
var orthogonalDistance = DigitizingCursor.Resolve(new(8, 3), SKMatrix.Identity, 15,
    false, false, new(0, 0), true, 10, true, CountSnap, false, false, true);
Check(orthogonalDistance.Position == new SKPoint(10, 0),
    "Distância fixa deve continuar compatível com o modo ortogonal.");
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
using var crossingA = new SKPath();
crossingA.MoveTo(0, 0); crossingA.LineTo(10, 10);
using var crossingB = new SKPath();
crossingB.MoveTo(0, 10); crossingB.LineTo(10, 0);
var intersectionSearch = new SnapSearch(new(5, 5.4f), 1, false, false, false, true);
intersectionSearch.AddPath(crossingA);
intersectionSearch.AddPath(crossingB);
intersectionSearch.CompleteIntersections();
Check(intersectionSearch.Best?.Kind == SnapKind.Intersection && intersectionSearch.Best?.Point == new SKPoint(5, 5),
    "Intersection-only mode must snap the crossing of independent contours.");
var endpointIntersection = new SnapSearch(new(0, 0), 1, true, false, false, true);
endpointIntersection.AddPath(crossingA);
endpointIntersection.AddPath(crossingB);
endpointIntersection.CompleteIntersections();
Check(endpointIntersection.Best?.Kind == SnapKind.Vertex,
    "A real vertex must outrank an intersection at the same coordinate.");
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

foreach (var corner in new SKPoint[] { new(12, 7), new(-12, 7), new(12, -7), new(-12, -7) })
{
    var rectangle = SketchConstruction.Build(ConstructionMode.Rectangle, [new(0, 0), corner]);
    var shape = DigitizingGeometry.Create(DigitizingGeometry.Kind.Polygon, rectangle.Select(p => new Coordinate(p.X, p.Y)));
    Check(shape.IsValid && shape.Area == 84, "Rectangles must work in all four drag directions.");
    var preview = SketchConstruction.Preview(ConstructionMode.Rectangle, [new(0, 0)], corner);
    Check(preview.Closed && preview.Points.SequenceEqual(rectangle), "Preview and committed rectangle must match exactly.");
}
foreach (float side in new[] { -1f, 1f })
{
    SKPoint[] controls = [new(10, 20), new(13, 24), new(10 - 8 * side, 20 + 6 * side)];
    var oriented = SketchConstruction.Build(ConstructionMode.OrientedRectangle, controls);
    var shape = DigitizingGeometry.Create(DigitizingGeometry.Kind.Polygon, oriented.Select(p => new Coordinate(p.X, p.Y)));
    Check(Math.Abs(shape.Area - 50) < 1e-5, "Oriented rectangle must preserve base times perpendicular width on either side.");
    double dot = (oriented[1].X - oriented[0].X) * (oriented[2].X - oriented[1].X) +
        (oriented[1].Y - oriented[0].Y) * (oriented[2].Y - oriented[1].Y);
    Check(Math.Abs(dot) < 1e-6, "Oriented rectangle adjacent sides must be perpendicular.");
    var preview = SketchConstruction.Preview(ConstructionMode.OrientedRectangle, controls[..2], controls[2]);
    Check(preview.Closed && preview.Points.SequenceEqual(oriented), "Oriented preview must use the committed ring generator.");
}
var circle = SketchConstruction.Build(ConstructionMode.Circle, [new(20, 30), new(23, 34)]);
var circleShape = DigitizingGeometry.Create(DigitizingGeometry.Kind.Polygon, circle.Select(p => new Coordinate(p.X, p.Y)));
Check(circle.Length == 128 && circle[0] == new SKPoint(23, 34), "Circle must include the exact radius endpoint.");
Check(circleShape.Area < Math.PI * 25 && circleShape.Area > Math.PI * 25 * 0.999,
    "Inscribed circle approximation must have less than 0.1% area deficit.");
Check(circle.All(p => Math.Abs(Math.Sqrt(Math.Pow(p.X - 20, 2) + Math.Pow(p.Y - 30, 2)) - 5) < 1e-5),
    "All generated circle vertices must lie on the radius.");
var ellipse = SketchConstruction.Build(ConstructionMode.Ellipse, [new(20, 30), new(25, 30), new(20, 34)]);
var ellipseShape = DigitizingGeometry.Create(DigitizingGeometry.Kind.Polygon, ellipse.Select(p => new Coordinate(p.X, p.Y)));
Check(ellipse.Length == 128 && ellipse[0] == new SKPoint(25, 30), "Ellipse must include the first axis endpoint.");
Check(ellipseShape.Area < Math.PI * 5 * 4 && ellipseShape.Area > Math.PI * 5 * 4 * 0.999,
    "Inscribed ellipse approximation must preserve the two-axis area.");
Check(ellipse.All(p => Math.Abs(Math.Pow((p.X - 20) / 5, 2) + Math.Pow((p.Y - 30) / 4, 2) - 1) < 1e-5),
    "Ellipse vertices must lie on the generated two-axis curve.");
var pentagon = SketchConstruction.Build(ConstructionMode.RegularPolygon, [new(10, 20), new(20, 20)], 5);
var pentagonShape = DigitizingGeometry.Create(DigitizingGeometry.Kind.Polygon, pentagon.Select(p => new Coordinate(p.X, p.Y)));
Check(pentagon.Length == 5 && pentagon[0] == new SKPoint(20, 20), "Regular polygon must preserve the radius endpoint.");
Check(Math.Abs(pentagonShape.Area - 237.76412907378838) < 1e-4, "Regular polygon must preserve the configured side count and radius.");
var pentagonPreview = SketchConstruction.Preview(ConstructionMode.RegularPolygon, [new(10, 20)], new(20, 20), 5);
Check(pentagonPreview.Closed && pentagonPreview.Points.SequenceEqual(pentagon), "Regular polygon preview must match the committed ring.");
foreach (var mode in new[] { ConstructionMode.Rectangle, ConstructionMode.OrientedRectangle, ConstructionMode.Circle, ConstructionMode.Ellipse, ConstructionMode.RegularPolygon })
{
    Check(!SketchConstruction.Preview(mode, [], null).Closed, "Empty construction must stay incomplete.");
    Check(!SketchConstruction.Preview(mode, [new(1, 1)], new(1, 1)).Closed, "Coincident controls must remain a guide, not a polygon.");
    try { SketchConstruction.Build(mode, [new(0, 0)]); throw new Exception("Incomplete controls accepted"); }
    catch (ArgumentException) { Check(true, "Incomplete construction rejected."); }
}
try { SketchConstruction.Build(ConstructionMode.Rectangle, [new(0, 0), new(0, 10)]); throw new Exception("Zero width accepted"); }
catch (ArgumentException) { Check(true, "Zero width rejected."); }
try { SketchConstruction.Build(ConstructionMode.OrientedRectangle, [new(0, 0), new(3, 4), new(6, 8)]); throw new Exception("Collinear controls accepted"); }
catch (ArgumentException) { Check(true, "Collinear controls rejected."); }
try { SketchConstruction.Build(ConstructionMode.Circle, [new(0, 0), new(float.NaN, 1)]); throw new Exception("NaN accepted"); }
catch (ArgumentException) { Check(true, "Nonfinite controls rejected."); }
try { SketchConstruction.Build(ConstructionMode.Ellipse, [new(0, 0), new(3, 0), new(6, 0)]); throw new Exception("Collinear ellipse accepted"); }
catch (ArgumentException) { Check(true, "Collinear ellipse controls rejected."); }
try { SketchConstruction.Build(ConstructionMode.RegularPolygon, [new(0, 0), new(3, 0)], 2); throw new Exception("Invalid side count accepted"); }
catch (ArgumentOutOfRangeException) { Check(true, "Regular polygon side count rejected."); }
var controlList = new List<SKPoint>();
var controlHistory = new SketchVertexHistory<SKPoint>(controlList);
controlHistory.Add(new(0, 0)); controlHistory.Add(new(10, 10));
var ready = SketchConstruction.Build(ConstructionMode.Rectangle, controlList);
controlHistory.Undo();
Check(controlList.Count == 1 && !SketchConstruction.Preview(ConstructionMode.Rectangle, controlList, null).Closed,
    "Undo must remove a control, not a generated corner.");
controlHistory.Redo();
Check(ready.SequenceEqual(SketchConstruction.Build(ConstructionMode.Rectangle, controlList)), "Redo must reconstruct the exact same polygon.");

var polarMetric = measurements.ProjectGridVector(new(10, 20), 100, 90, "EPSG:31983");
Check(Math.Abs(polarMetric.X - 110) < 1e-5 && Math.Abs(polarMetric.Y - 20) < 1e-5, "Polar input uses metre grid units and clockwise azimuth.");
var polarFeet = measurements.ProjectGridVector(new(0, 0), 100, 0, "EPSG:2227");
Check(Math.Abs(polarFeet.Y + 100 * 3937.0 / 1200.0) < 1e-4, "Polar metre input converts to US survey feet.");
var polarWrapped = measurements.ProjectGridVector(new(10, 20), 100, 450, "EPSG:31983");
Check(polarWrapped == polarMetric, "Azimuths normalize full rotations.");
Check(Math.Abs(measurements.MetresToGridUnits(50, "EPSG:2227") - 50 * 3937d / 1200d) < 1e-8, "Distance constraint uses CRS units.");
foreach (double invalidDistance in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
{
    try { measurements.ProjectGridVector(new(0, 0), invalidDistance, 90, "EPSG:31983"); throw new Exception("Invalid distance accepted"); }
    catch (ArgumentException) { Check(true, "Invalid polar distance rejected."); }
}
try { measurements.ProjectGridVector(new(0, 0), 100, double.NaN, "EPSG:31983"); throw new Exception("Invalid azimuth accepted"); }
catch (ArgumentException) { Check(true, "Invalid azimuth rejected."); }
try { measurements.ProjectGridVector(new(0, 0), 100, 90, "EPSG:4326"); throw new Exception("Geographic grid metres accepted"); }
catch (ArgumentException) { Check(true, "Geographic CRS rejects planar metre construction."); }
try { measurements.ProjectGridVector(new(float.MaxValue, 0), 1, 90, "EPSG:31983"); throw new Exception("Collapsed vector accepted"); }
catch (ArgumentException) { Check(true, "Float precision collapse rejected."); }

var editingFactory = new GeometryFactory();
Polygon CreateEditingPolygon(params Coordinate[] coordinates) =>
    editingFactory.CreatePolygon(editingFactory.CreateLinearRing(coordinates));
var cutTarget = CreateEditingPolygon(
    new(0, 0), new(0, 10), new(10, 10), new(10, 0), new(0, 0));
var invalidBowtie = CreateEditingPolygon(
    new(0, 0), new(2, 2), new(0, 2), new(2, 0), new(0, 0));
string? invalidBowtieMessage = VectorGeometryEditingService.GetGeometryValidationMessage(invalidBowtie);
Check(invalidBowtieMessage is not null && invalidBowtieMessage.Contains("auto-interseção", StringComparison.OrdinalIgnoreCase),
    "Geometry validation must report the bowtie self-intersection in user-facing language.");
var repairedBowtie = VectorGeometryEditingService.FixGeometry(invalidBowtie);
Check(repairedBowtie is MultiPolygon && repairedBowtie.IsValid && Math.Abs(repairedBowtie.Area - 2) < 1e-9,
    "Repairing a self-intersecting polygon must preserve both valid lobes as a multipart result.");
Check(VectorGeometryEditingService.GetGeometryValidationMessage(repairedBowtie) is null,
    "A repaired multipart geometry must pass the same validity diagnostic.");
try
{
    VectorGeometryEditingService.FixGeometry(cutTarget);
    throw new InvalidOperationException("A valid polygon was modified by the repair operation.");
}
catch (InvalidOperationException exception)
{
    Check(exception.Message.Contains("já é válida", StringComparison.OrdinalIgnoreCase),
        "Repair must refuse valid geometries instead of making silent changes.");
}
var sharedEdgeLeft = CreateEditingPolygon(
    new(0, 0), new(0, 5), new(5, 5), new(5, 0), new(0, 0));
var sharedEdgeRight = CreateEditingPolygon(
    new(5, 0), new(5, 5), new(10, 5), new(10, 0), new(5, 0));
var separatePolygon = CreateEditingPolygon(
    new(20, 0), new(20, 5), new(25, 5), new(25, 0), new(20, 0));
var mergedAdjacentPolygons = VectorGeometryEditingService.MergePolygons([sharedEdgeLeft, sharedEdgeRight], 0);
Check(mergedAdjacentPolygons is Polygon && mergedAdjacentPolygons.IsValid &&
    Math.Abs(mergedAdjacentPolygons.Area - 50) < 1e-9,
    "Merging adjacent selected polygons must dissolve their shared edge and preserve their combined area.");
try
{
    VectorGeometryEditingService.MergePolygons([sharedEdgeLeft, separatePolygon], 0);
    throw new InvalidOperationException("Disconnected polygons were stored in a single-polygon primary feature.");
}
catch (InvalidOperationException exception)
{
    Check(exception.Message.Contains("multipartes", StringComparison.OrdinalIgnoreCase),
        "A single-polygon primary must reject a disconnected merge instead of losing geometry.");
}
var multiPolygonPrimary = editingFactory.CreateMultiPolygon([sharedEdgeLeft]);
var mergedDisconnectedPolygons = VectorGeometryEditingService.MergePolygons([multiPolygonPrimary, separatePolygon], 0);
Check(mergedDisconnectedPolygons is MultiPolygon mergedMultiPolygon && mergedMultiPolygon.IsValid &&
    mergedMultiPolygon.NumGeometries == 2 && Math.Abs(mergedMultiPolygon.Area - 50) < 1e-9,
    "A multipolygon primary must preserve disconnected selected polygons in the merge result.");
var sharedVertexAddress = VectorGeometryEditingService.GetVertices(sharedEdgeLeft)
    .Single(vertex => vertex.X == 5 && vertex.Y == 5).Address;
var movedSharedGeometries = VectorGeometryEditingService.MoveCoincidentVertices(
    [sharedEdgeLeft, sharedEdgeRight, separatePolygon], 0, sharedVertexAddress, 6, 5);
Check(movedSharedGeometries[0].IsValid && movedSharedGeometries[1].IsValid &&
    movedSharedGeometries[0].Coordinates.Any(coordinate => coordinate.Equals2D(new Coordinate(6, 5))) &&
    movedSharedGeometries[1].Coordinates.Any(coordinate => coordinate.Equals2D(new Coordinate(6, 5))) &&
    movedSharedGeometries[2].EqualsExact(separatePolygon),
    "Moving a selected shared vertex must update coincident features and leave unrelated geometry unchanged.");
var sharedEdgeAddresses = VectorGeometryEditingService.GetVertices(sharedEdgeLeft)
    .Where(vertex => vertex.X == 5).Select(vertex => vertex.Address).ToArray();
var movedSharedEdge = VectorGeometryEditingService.MoveCoincidentVerticesByDelta(
    [sharedEdgeLeft, sharedEdgeRight, separatePolygon], 0, sharedEdgeAddresses, 1, 0);
Check(movedSharedEdge[0].IsValid && movedSharedEdge[1].IsValid &&
    movedSharedEdge[0].Coordinates.Any(coordinate => coordinate.Equals2D(new Coordinate(6, 0))) &&
    movedSharedEdge[0].Coordinates.Any(coordinate => coordinate.Equals2D(new Coordinate(6, 5))) &&
    movedSharedEdge[1].Coordinates.Any(coordinate => coordinate.Equals2D(new Coordinate(6, 0))) &&
    movedSharedEdge[1].Coordinates.Any(coordinate => coordinate.Equals2D(new Coordinate(6, 5))) &&
    movedSharedEdge[2].EqualsExact(separatePolygon),
    "Moving multiple shared nodes must preserve a shared edge across selected features.");
var cutLine = editingFactory.CreateLineString([new Coordinate(-1, 5), new Coordinate(11, 5)]);
var cutParts = VectorGeometryEditingService.CutPolygonByLine(cutTarget, cutLine);
Check(cutParts.Count == 2 && cutParts.All(part => part.IsValid) &&
    Math.Abs(cutParts.Sum(part => part.Area) - cutTarget.Area) < 1e-9,
    "A border-to-border cut must produce valid parts and preserve total area.");
var bulgeReshape = VectorGeometryEditingService.ReshapePolygon(cutTarget,
    editingFactory.CreateLineString([new Coordinate(0, 10), new Coordinate(5, 12), new Coordinate(10, 10)]));
Check(bulgeReshape is Polygon bulgedPolygon && bulgedPolygon.IsValid &&
    bulgedPolygon.Area > cutTarget.Area && Math.Abs(bulgedPolygon.EnvelopeInternal.MaxY - 12) < 1e-9,
    "A reshape trace outside a polygon must add the traced bulge without splitting the feature.");
var inwardReshape = VectorGeometryEditingService.ReshapePolygon(cutTarget,
    editingFactory.CreateLineString([new Coordinate(0, 10), new Coordinate(5, 8), new Coordinate(10, 10)]));
Check(inwardReshape is Polygon notchedPolygon && notchedPolygon.IsValid &&
    notchedPolygon.Area < cutTarget.Area && notchedPolygon.Area > 0 &&
    Math.Abs(notchedPolygon.EnvelopeInternal.MaxY - 10) < 1e-9,
    "A reshape trace inside a polygon must cut a notch while retaining one valid feature.");
var multiPolygonForReshape = editingFactory.CreateMultiPolygon([cutTarget, separatePolygon]);
var reshapedMultiPolygon = VectorGeometryEditingService.ReshapePolygon(multiPolygonForReshape,
    editingFactory.CreateLineString([new Coordinate(0, 10), new Coordinate(5, 12), new Coordinate(10, 10)]));
Check(reshapedMultiPolygon is MultiPolygon reshapedParts && reshapedParts.IsValid &&
    reshapedParts.NumGeometries == 2 && reshapedParts.Area > multiPolygonForReshape.Area,
    "Reshaping one part of a multipolygon must preserve the multipart type and untouched components.");
try
{
    VectorGeometryEditingService.ReshapePolygon(cutTarget,
        editingFactory.CreateLineString([new Coordinate(2, 2), new Coordinate(8, 8)]));
    throw new InvalidOperationException("A reshape trace without boundary crossings was accepted.");
}
catch (InvalidOperationException exception)
{
    Check(exception.Message.Contains("exatamente duas vezes", StringComparison.OrdinalIgnoreCase),
        "Reshape must require exactly two boundary crossings.");
}
try
{
    VectorGeometryEditingService.CutPolygonByLine(cutTarget,
        editingFactory.CreateLineString([new Coordinate(5, 2), new Coordinate(5, 8)]));
    throw new InvalidOperationException("An interior-only cut was accepted.");
}
catch (InvalidOperationException exception)
{
    Check(exception.Message.Contains("atravessar", StringComparison.OrdinalIgnoreCase),
        "A cut must reach the polygon boundary at both ends.");
}
var lineTarget = editingFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(10, 0)]);
var lineCutter = editingFactory.CreateLineString([new Coordinate(5, -2), new Coordinate(5, 2)]);
var splitLines = VectorGeometryEditingService.SplitGeometryByLine(lineTarget, lineCutter);
Check(splitLines.Count == 2 && splitLines.All(part => part is LineString && part.IsValid) &&
    Math.Abs(splitLines.Sum(part => part.Length) - lineTarget.Length) < 1e-9,
    "A crossing line must split a line feature into valid pieces without changing total length.");
var multiLineTarget = editingFactory.CreateMultiLineString(
    [editingFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(10, 0)]),
     editingFactory.CreateLineString([new Coordinate(0, 10), new Coordinate(10, 10)])]);
var splitMultiLines = VectorGeometryEditingService.SplitGeometryByLine(
    multiLineTarget, editingFactory.CreateLineString([new Coordinate(5, -2), new Coordinate(5, 2)]));
Check(splitMultiLines.Count == 3 && splitMultiLines.All(part => part is MultiLineString && part.IsValid) &&
    Math.Abs(splitMultiLines.Sum(part => part.Length) - multiLineTarget.Length) < 1e-9,
    "Splitting a multipart line must preserve its type and all disconnected parts.");
var mergedConnectedLines = VectorGeometryEditingService.MergeLines(
    [editingFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(5, 0)]),
     editingFactory.CreateLineString([new Coordinate(10, 0), new Coordinate(5, 0)])], 0);
Check(mergedConnectedLines is LineString mergedLine && mergedLine.IsValid && mergedLine.NumPoints == 3 &&
    Math.Abs(mergedLine.Length - 10) < 1e-9,
    "Connected lines with reversed endpoint order must merge into one continuous line.");
try
{
    VectorGeometryEditingService.MergeLines(
        [editingFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(5, 0)]),
         editingFactory.CreateLineString([new Coordinate(0, 5), new Coordinate(5, 5)])], 0);
    throw new InvalidOperationException("Disconnected lines were merged into a single-line primary feature.");
}
catch (InvalidOperationException exception)
{
    Check(exception.Message.Contains("conectada", StringComparison.OrdinalIgnoreCase),
        "A single-line primary cannot silently absorb disconnected features.");
}
var mergedMultipartLines = VectorGeometryEditingService.MergeLines(
    [editingFactory.CreateMultiLineString(
        [editingFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(5, 0)]),
         editingFactory.CreateLineString([new Coordinate(0, 5), new Coordinate(5, 5)])]),
     editingFactory.CreateLineString([new Coordinate(5, 0), new Coordinate(10, 0)])], 0);
Check(mergedMultipartLines is MultiLineString mergedMulti && mergedMulti.IsValid && mergedMulti.NumGeometries == 2 &&
    Math.Abs(mergedMulti.Length - 15) < 1e-9,
    "A multiline primary must preserve disconnected parts while merging connected endpoints.");

var redundantSquare = CreateEditingPolygon(
    new(0, 0), new(0, 1), new(0, 2), new(0, 3), new(0, 4),
    new(1, 4), new(2, 4), new(3, 4), new(4, 4),
    new(4, 3), new(4, 2), new(4, 1), new(4, 0),
    new(3, 0), new(2, 0), new(1, 0), new(0, 0));
var simplifiedSquare = VectorGeometryEditingService.Simplify(redundantSquare, 0.6);
Check(simplifiedSquare.IsValid && simplifiedSquare.GeometryType == redundantSquare.GeometryType &&
    simplifiedSquare.NumPoints < redundantSquare.NumPoints && Math.Abs(simplifiedSquare.Area - 16) < 1e-9,
    "Topology-preserving simplification must remove redundant vertices without changing area or type.");
var movableVerticesPolygon = CreateEditingPolygon(
    new(0, 0), new(0, 5), new(5, 5), new(5, 0), new(0, 0));
var topVertexAddresses = VectorGeometryEditingService.GetVertices(movableVerticesPolygon)
    .Where(vertex => vertex.Y == 5).Select(vertex => vertex.Address).ToArray();
var movedVertexGroup = VectorGeometryEditingService.MoveVerticesByDelta(
    movableVerticesPolygon, topVertexAddresses, 0, 1);
Check(movedVertexGroup.IsValid &&
    movedVertexGroup.Coordinates.Any(coordinate => coordinate.Equals2D(new Coordinate(0, 6))) &&
    movedVertexGroup.Coordinates.Any(coordinate => coordinate.Equals2D(new Coordinate(5, 6))) &&
    movedVertexGroup.Coordinates.Any(coordinate => coordinate.Equals2D(new Coordinate(0, 0))),
    "A multi-vertex move must apply one common displacement and preserve ring closure and validity.");
var editableFiveSidedPolygon = CreateEditingPolygon(
    new(0, 0), new(0, 5), new(0, 10), new(10, 10), new(10, 0), new(0, 0));
var polygonVerticesToRemove = VectorGeometryEditingService.GetVertices(editableFiveSidedPolygon)
    .Where(vertex => (vertex.X == 0 && vertex.Y == 5) || (vertex.X == 10 && vertex.Y == 10))
    .Select(vertex => vertex.Address)
    .ToArray();
var editedFiveSidedPolygon = VectorGeometryEditingService.RemoveVertices(
    editableFiveSidedPolygon, polygonVerticesToRemove);
Check(editedFiveSidedPolygon is Polygon editedPolygon && editedPolygon.IsValid &&
    editedPolygon.NumPoints == 4 && Math.Abs(editedPolygon.Area - 50) < 1e-9 &&
    editedPolygon.ExteriorRing.Coordinates[0].Equals2D(editedPolygon.ExteriorRing.Coordinates[^1]),
    "Removing multiple polygon nodes must preserve the closed ring and produce one valid, smaller boundary.");
try
{
    VectorGeometryEditingService.RemoveVertices(movableVerticesPolygon,
        VectorGeometryEditingService.GetVertices(movableVerticesPolygon)
            .Where(vertex => vertex.X == 0 && vertex.Y != 5 || vertex.X == 5 && vertex.Y == 5)
            .Select(vertex => vertex.Address)
            .ToArray());
    throw new InvalidOperationException("A polygon ring with fewer than three nodes was accepted.");
}
catch (InvalidOperationException exception)
{
    Check(exception.Message.Contains("três vértices", StringComparison.OrdinalIgnoreCase),
        "Bulk vertex deletion must reject a ring that would have fewer than three unique nodes.");
}
var multiLineForVertexRemoval = editingFactory.CreateMultiLineString(
    [editingFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(5, 0), new Coordinate(10, 0)]),
     editingFactory.CreateLineString([new Coordinate(0, 10), new Coordinate(5, 10), new Coordinate(10, 10)])]);
var multiLineVerticesToRemove = VectorGeometryEditingService.GetVertices(multiLineForVertexRemoval)
    .Where(vertex => vertex.X == 5)
    .Select(vertex => vertex.Address)
    .ToArray();
var editedMultiLine = VectorGeometryEditingService.RemoveVertices(multiLineForVertexRemoval, multiLineVerticesToRemove);
Check(editedMultiLine is MultiLineString editedLines && editedLines.IsValid &&
    editedLines.NumGeometries == 2 && editedLines.Geometries.Cast<LineString>().All(line => line.NumPoints == 2),
    "Bulk deletion across multipart lines must retain each part and its minimum two endpoints.");
var offsetSource = editingFactory.CreateLineString([new(0, 0), new(10, 0)]);
var offsetLeft = VectorGeometryEditingService.OffsetLine(offsetSource, 2);
var offsetRight = VectorGeometryEditingService.OffsetLine(offsetSource, -2);
Check(offsetLeft is LineString && offsetRight is LineString && offsetLeft.IsValid && offsetRight.IsValid &&
    offsetLeft.Coordinates.All(coordinate => Math.Abs(coordinate.Y - 2) < 1e-9) &&
    offsetRight.Coordinates.All(coordinate => Math.Abs(coordinate.Y + 2) < 1e-9),
    "Parallel line offsets must honor left/right side, preserve type, and remain valid.");
var offsetMultiLine = VectorGeometryEditingService.OffsetLine(multiLineTarget, 2);
Check(offsetMultiLine is MultiLineString && offsetMultiLine.IsValid &&
    Math.Abs(offsetMultiLine.Length - multiLineTarget.Length) < 1e-9,
    "Offsetting a multipart line must preserve its geometry type and segment lengths.");
try
{
    VectorGeometryEditingService.OffsetLine(offsetSource, 0);
    throw new InvalidOperationException("A zero-distance offset was accepted.");
}
catch (ArgumentOutOfRangeException) { Check(true, "Parallel offset rejects zero distance."); }
try
{
    VectorGeometryEditingService.Simplify(redundantSquare, 0);
    throw new InvalidOperationException("A zero simplification tolerance was accepted.");
}
catch (ArgumentOutOfRangeException) { Check(true, "Simplification rejects non-positive tolerance."); }

var polygonWithHole = editingFactory.CreatePolygon(
    editingFactory.CreateLinearRing([new(0, 0), new(0, 20), new(20, 20), new(20, 0), new(0, 0)]),
    [editingFactory.CreateLinearRing([new(2, 2), new(8, 2), new(8, 8), new(2, 8), new(2, 2)])]);
var addedHole = CreateEditingPolygon(
    new(11, 11), new(11, 16), new(16, 16), new(16, 11), new(11, 11));
var polygonWithTwoHoles = VectorGeometryEditingService.AddInteriorRing(polygonWithHole, addedHole);
Check(polygonWithTwoHoles.IsValid && ((Polygon)polygonWithTwoHoles).NumInteriorRings == 2 &&
    Math.Abs(polygonWithTwoHoles.Area - 339) < 1e-9,
    "Adding an interior ring must create a valid hole and subtract its area.");
var addedHoleVertex = VectorGeometryEditingService.GetVertices(polygonWithTwoHoles)
    .Single(vertex => vertex.X == 11 && vertex.Y == 11);
var polygonAfterHoleRemoval = VectorGeometryEditingService.RemoveInteriorRing(polygonWithTwoHoles, addedHoleVertex.Address);
Check(polygonAfterHoleRemoval.IsValid && ((Polygon)polygonAfterHoleRemoval).NumInteriorRings == 1 &&
    Math.Abs(polygonAfterHoleRemoval.Area - 364) < 1e-9,
    "Removing an interior ring by its vertex address must restore the polygon area.");
try
{
    VectorGeometryEditingService.AddInteriorRing(polygonWithHole,
        CreateEditingPolygon(new(18, 18), new(18, 22), new(22, 22), new(22, 18), new(18, 18)));
    throw new InvalidOperationException("A hole crossing the exterior was accepted.");
}
catch (InvalidOperationException exception)
{
    Check(exception.Message.Contains("dentro", StringComparison.OrdinalIgnoreCase),
        "An interior ring crossing the exterior boundary must be rejected.");
}
try
{
    VectorGeometryEditingService.RemoveInteriorRing(polygonWithTwoHoles,
        VectorGeometryEditingService.GetVertices(polygonWithTwoHoles).First().Address);
    throw new InvalidOperationException("The exterior ring was removed as a hole.");
}
catch (InvalidOperationException exception)
{
    Check(exception.Message.Contains("anel interno", StringComparison.OrdinalIgnoreCase),
        "The exterior boundary cannot be removed through the hole-removal operation.");
}

Console.WriteLine($"Digitizing core contracts passed: {passed} assertions.");
