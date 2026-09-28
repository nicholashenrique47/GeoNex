using GeoNex.Services;
using SkiaSharp;
using NetTopologySuite.Geometries;

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

Console.WriteLine($"Digitizing core contracts passed: {passed} assertions.");
