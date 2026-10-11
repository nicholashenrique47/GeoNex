using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.IO;
using OSGeo.GDAL;
using OSGeo.OGR;
using OSGeo.OSR;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using NtsPoint = NetTopologySuite.Geometries.Point;
using OgrGeometry = OSGeo.OGR.Geometry;

namespace GeoNex.Services;

/// <summary>Loads and edits one OGR feature in project coordinates without changing its source until save.</summary>
public static class VectorGeometryEditingService
{
    public readonly record struct VertexAddress(int Sequence, int Coordinate);
    public readonly record struct Vertex(VertexAddress Address, double X, double Y);
    public readonly record struct VertexInsertion(NtsGeometry Geometry, VertexAddress Address, double X, double Y);

    public static NtsGeometry ReadGeometry(string path, long featureId, string projectSrs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSrs);
        Ogr.RegisterAll();

        using DataSource dataSource = Ogr.Open(path, 0)
            ?? throw new IOException($"O GDAL não conseguiu abrir '{Path.GetFileName(path)}' para edição. {Gdal.GetLastErrorMsg()}");
        using Layer layer = dataSource.GetLayerByIndex(0)
            ?? throw new InvalidDataException("A fonte não contém uma camada vetorial.");
        using OSGeo.OGR.Feature feature = layer.GetFeature(featureId)
            ?? throw new InvalidDataException($"A feição {featureId} não existe mais na fonte.");
        using OgrGeometry sourceGeometry = feature.GetGeometryRef()
            ?? throw new InvalidDataException("A feição selecionada não possui geometria.");
        using OgrGeometry geometry = sourceGeometry.Clone();
        using SpatialReference sourceSrs = layer.GetSpatialRef()
            ?? throw new InvalidDataException("A camada não possui SRC definido; a geometria não pode ser editada com segurança.");
        using SpatialReference targetSrs = SrsFactory.FromUserInput(projectSrs);
        sourceSrs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        if (sourceSrs.IsSame(targetSrs, null) != 1)
        {
            using var transform = new CoordinateTransformation(sourceSrs, targetSrs);
            EnsureSuccess(geometry.Transform(transform), "reprojetar a feição para o SRC do projeto");
        }

        EnsureSuccess(geometry.ExportToWkt(out string wkt), "ler a geometria da feição");
        NtsGeometry result = new WKTReader().Read(wkt);
        if (result.IsEmpty) throw new InvalidDataException("A geometria selecionada está vazia.");
        return result;
    }

    public static IReadOnlyList<Vertex> GetVertices(NtsGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var result = new List<Vertex>();
        int sequence = 0;
        AppendVertices(geometry, result, ref sequence);
        return result;
    }

    public static NtsGeometry MoveVertex(NtsGeometry geometry, VertexAddress address, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new ArgumentOutOfRangeException(nameof(x), "A nova coordenada precisa ser finita.");
        if (address.Sequence < 0 || address.Coordinate < 0)
            throw new ArgumentOutOfRangeException(nameof(address));

        int sequence = 0;
        NtsGeometry result = Rebuild(geometry, address, x, y, ref sequence);
        if (sequence <= address.Sequence)
            throw new ArgumentOutOfRangeException(nameof(address), "O vértice selecionado não pertence à geometria.");
        if (!result.IsValid)
        {
            throw new InvalidOperationException("A edição criaria uma geometria inválida. O vértice voltou à posição anterior.");
        }
        return result;
    }

    public static NtsGeometry Translate(NtsGeometry geometry, double deltaX, double deltaY)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            throw new ArgumentOutOfRangeException(nameof(deltaX), "O deslocamento precisa ser finito.");
        NtsGeometry result = AffineTransformation.TranslationInstance(deltaX, deltaY).Transform(geometry);
        if (result.IsEmpty || !result.IsValid)
            throw new InvalidOperationException("O deslocamento produziu uma geometria inválida.");
        return result;
    }

    public static NtsGeometry RotateAroundCentroid(NtsGeometry geometry, double degrees)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (!double.IsFinite(degrees))
            throw new ArgumentOutOfRangeException(nameof(degrees), "O ângulo precisa ser finito.");
        if (geometry.IsEmpty) throw new InvalidOperationException("Uma geometria vazia não pode ser rotacionada.");
        var center = geometry.Centroid;
        double radians = degrees * Math.PI / 180d;
        NtsGeometry result = AffineTransformation.RotationInstance(radians, center.X, center.Y).Transform(geometry);
        if (!result.IsValid)
            throw new InvalidOperationException("A rotação produziu uma geometria inválida.");
        return result;
    }

    public static NtsGeometry ScaleAroundCentroid(NtsGeometry geometry, double factor)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (!double.IsFinite(factor) || factor <= 0)
            throw new ArgumentOutOfRangeException(nameof(factor), "O fator de escala precisa ser finito e maior que zero.");
        if (geometry.IsEmpty) throw new InvalidOperationException("Uma geometria vazia não pode ser escalada.");
        var center = geometry.Centroid;
        NtsGeometry result = AffineTransformation.ScaleInstance(factor, factor, center.X, center.Y).Transform(geometry);
        if (result.IsEmpty || !result.IsValid)
            throw new InvalidOperationException("A escala produziu uma geometria inválida.");
        return result;
    }

    public static VertexInsertion InsertVertexAtPoint(NtsGeometry geometry, double x, double y, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(tolerance) || tolerance <= 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance), "O ponto e a tolerância precisam ser finitos e positivos.");

        var nearest = new SegmentCandidate(double.PositiveInfinity, -1, -1, 0, 0, 0);
        int sequence = 0;
        FindNearestSegment(geometry, x, y, ref sequence, ref nearest);
        if (nearest.Sequence < 0 || nearest.DistanceSquared > tolerance * tolerance)
            throw new InvalidOperationException("Clique próximo a uma aresta da feição selecionada para inserir o vértice.");
        if (nearest.Fraction <= 1e-7 || nearest.Fraction >= 1 - 1e-7)
            throw new InvalidOperationException("O clique está sobre um vértice existente. Clique no meio da aresta para inserir um novo vértice.");

        int rebuildSequence = 0;
        NtsGeometry result = InsertOnSequence(
            geometry, nearest.Sequence, nearest.InsertAt, nearest.X, nearest.Y, ref rebuildSequence);
        if (!result.IsValid)
            throw new InvalidOperationException("A inserção criaria uma geometria inválida. Nenhuma alteração foi aplicada.");
        return new VertexInsertion(result, new VertexAddress(nearest.Sequence, nearest.InsertAt), nearest.X, nearest.Y);
    }

    public static NtsGeometry RemoveVertex(NtsGeometry geometry, VertexAddress address)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (address.Sequence < 0 || address.Coordinate < 0)
            throw new ArgumentOutOfRangeException(nameof(address));

        int sequence = 0;
        NtsGeometry result = RemoveFromSequence(geometry, address, ref sequence);
        if (sequence <= address.Sequence)
            throw new ArgumentOutOfRangeException(nameof(address), "O vértice selecionado não pertence à geometria.");
        if (result.IsEmpty || !result.IsValid)
            throw new InvalidOperationException("A remoção deixaria uma geometria vazia ou inválida. Nenhuma alteração foi aplicada.");
        return result;
    }

    private readonly record struct SegmentCandidate(
        double DistanceSquared, int Sequence, int InsertAt, double X, double Y, double Fraction);

    private static void FindNearestSegment(
        NtsGeometry geometry, double x, double y, ref int sequence, ref SegmentCandidate nearest)
    {
        switch (geometry)
        {
            case Polygon polygon:
                FindNearestSegment(polygon.ExteriorRing, x, y, ref sequence, ref nearest);
                for (int i = 0; i < polygon.NumInteriorRings; i++)
                    FindNearestSegment(polygon.GetInteriorRingN(i), x, y, ref sequence, ref nearest);
                return;
            case LineString line:
            {
                Coordinate[] coordinates = line.Coordinates;
                int currentSequence = sequence++;
                bool closed = geometry is LinearRing && coordinates.Length > 2 && coordinates[0].Equals2D(coordinates[^1]);
                int uniqueCount = closed ? coordinates.Length - 1 : coordinates.Length;
                int segmentCount = closed ? uniqueCount : Math.Max(0, uniqueCount - 1);
                for (int i = 0; i < segmentCount; i++)
                {
                    Coordinate a = coordinates[i];
                    Coordinate b = coordinates[(i + 1) % uniqueCount];
                    double dx = b.X - a.X, dy = b.Y - a.Y;
                    double lengthSquared = dx * dx + dy * dy;
                    if (lengthSquared <= double.Epsilon) continue;
                    double fraction = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / lengthSquared, 0, 1);
                    double projectedX = a.X + fraction * dx;
                    double projectedY = a.Y + fraction * dy;
                    double distanceX = x - projectedX, distanceY = y - projectedY;
                    double distanceSquared = distanceX * distanceX + distanceY * distanceY;
                    if (distanceSquared < nearest.DistanceSquared)
                        nearest = new SegmentCandidate(distanceSquared, currentSequence, i + 1, projectedX, projectedY, fraction);
                }
                return;
            }
            case NtsPoint:
                sequence++;
                return;
            case GeometryCollection collection:
                for (int i = 0; i < collection.NumGeometries; i++)
                    FindNearestSegment(collection.GetGeometryN(i), x, y, ref sequence, ref nearest);
                return;
            default:
                throw new NotSupportedException($"Edição de vértices não dá suporte a {geometry.GeometryType}.");
        }
    }

    private static NtsGeometry InsertOnSequence(
        NtsGeometry geometry, int targetSequence, int insertAt, double x, double y, ref int sequence)
    {
        GeometryFactory factory = geometry.Factory;
        switch (geometry)
        {
            case Polygon polygon:
            {
                LinearRing shell = (LinearRing)InsertOnSequence(polygon.ExteriorRing, targetSequence, insertAt, x, y, ref sequence);
                var holes = new LinearRing[polygon.NumInteriorRings];
                for (int i = 0; i < holes.Length; i++)
                    holes[i] = (LinearRing)InsertOnSequence(polygon.GetInteriorRingN(i), targetSequence, insertAt, x, y, ref sequence);
                return factory.CreatePolygon(shell, holes);
            }
            case LinearRing ring:
                return factory.CreateLinearRing(InsertCoordinates(ring.Coordinates, targetSequence, insertAt, x, y, ref sequence, true));
            case LineString line:
                return factory.CreateLineString(InsertCoordinates(line.Coordinates, targetSequence, insertAt, x, y, ref sequence, false));
            case NtsPoint point:
                sequence++;
                return (NtsGeometry)point.Copy();
            case MultiPoint multiPoint:
            {
                var points = new NtsPoint[multiPoint.NumGeometries];
                for (int i = 0; i < points.Length; i++)
                    points[i] = (NtsPoint)InsertOnSequence(multiPoint.GetGeometryN(i), targetSequence, insertAt, x, y, ref sequence);
                return factory.CreateMultiPoint(points);
            }
            case MultiLineString multiLine:
            {
                var lines = new LineString[multiLine.NumGeometries];
                for (int i = 0; i < lines.Length; i++)
                    lines[i] = (LineString)InsertOnSequence(multiLine.GetGeometryN(i), targetSequence, insertAt, x, y, ref sequence);
                return factory.CreateMultiLineString(lines);
            }
            case MultiPolygon multiPolygon:
            {
                var polygons = new Polygon[multiPolygon.NumGeometries];
                for (int i = 0; i < polygons.Length; i++)
                    polygons[i] = (Polygon)InsertOnSequence(multiPolygon.GetGeometryN(i), targetSequence, insertAt, x, y, ref sequence);
                return factory.CreateMultiPolygon(polygons);
            }
            case GeometryCollection collection:
            {
                var parts = new NtsGeometry[collection.NumGeometries];
                for (int i = 0; i < parts.Length; i++)
                    parts[i] = InsertOnSequence(collection.GetGeometryN(i), targetSequence, insertAt, x, y, ref sequence);
                return factory.CreateGeometryCollection(parts);
            }
            default:
                throw new NotSupportedException($"Edição de vértices não dá suporte a {geometry.GeometryType}.");
        }
    }

    private static Coordinate[] InsertCoordinates(
        Coordinate[] source, int targetSequence, int insertAt, double x, double y,
        ref int sequence, bool closed)
    {
        int currentSequence = sequence++;
        if (currentSequence != targetSequence) return source.Select(coordinate => coordinate.Copy()).ToArray();

        int uniqueCount = closed && source.Length > 2 && source[0].Equals2D(source[^1]) ? source.Length - 1 : source.Length;
        if (insertAt < 1 || insertAt > uniqueCount)
            throw new ArgumentOutOfRangeException(nameof(insertAt), "A posição de inserção está fora do segmento.");

        Coordinate anchor = source[Math.Max(0, insertAt - 1)];
        var inserted = anchor.Copy();
        inserted.X = x;
        inserted.Y = y;
        var result = new List<Coordinate>(source.Length + 1);
        result.AddRange(source.Take(insertAt).Select(coordinate => coordinate.Copy()));
        result.Add(inserted);
        if (closed)
        {
            result.AddRange(source.Skip(insertAt).Take(Math.Max(0, uniqueCount - insertAt)).Select(coordinate => coordinate.Copy()));
            result.Add(result[0].Copy());
        }
        else
        {
            result.AddRange(source.Skip(insertAt).Select(coordinate => coordinate.Copy()));
        }
        return result.ToArray();
    }

    private static NtsGeometry RemoveFromSequence(NtsGeometry geometry, VertexAddress address, ref int sequence)
    {
        GeometryFactory factory = geometry.Factory;
        switch (geometry)
        {
            case Polygon polygon:
            {
                LinearRing shell = (LinearRing)RemoveFromSequence(polygon.ExteriorRing, address, ref sequence);
                var holes = new LinearRing[polygon.NumInteriorRings];
                for (int i = 0; i < holes.Length; i++)
                    holes[i] = (LinearRing)RemoveFromSequence(polygon.GetInteriorRingN(i), address, ref sequence);
                return factory.CreatePolygon(shell, holes);
            }
            case LinearRing ring:
                return factory.CreateLinearRing(RemoveCoordinates(ring.Coordinates, address, ref sequence, true));
            case LineString line:
                return factory.CreateLineString(RemoveCoordinates(line.Coordinates, address, ref sequence, false));
            case NtsPoint point:
                sequence++;
                if (sequence - 1 == address.Sequence)
                    throw new InvalidOperationException("Não é possível remover o único vértice de um ponto.");
                return (NtsGeometry)point.Copy();
            case MultiPoint multiPoint:
            {
                var points = new NtsPoint[multiPoint.NumGeometries];
                for (int i = 0; i < points.Length; i++)
                    points[i] = (NtsPoint)RemoveFromSequence(multiPoint.GetGeometryN(i), address, ref sequence);
                return factory.CreateMultiPoint(points);
            }
            case MultiLineString multiLine:
            {
                var lines = new LineString[multiLine.NumGeometries];
                for (int i = 0; i < lines.Length; i++)
                    lines[i] = (LineString)RemoveFromSequence(multiLine.GetGeometryN(i), address, ref sequence);
                return factory.CreateMultiLineString(lines);
            }
            case MultiPolygon multiPolygon:
            {
                var polygons = new Polygon[multiPolygon.NumGeometries];
                for (int i = 0; i < polygons.Length; i++)
                    polygons[i] = (Polygon)RemoveFromSequence(multiPolygon.GetGeometryN(i), address, ref sequence);
                return factory.CreateMultiPolygon(polygons);
            }
            case GeometryCollection collection:
            {
                var parts = new NtsGeometry[collection.NumGeometries];
                for (int i = 0; i < parts.Length; i++)
                    parts[i] = RemoveFromSequence(collection.GetGeometryN(i), address, ref sequence);
                return factory.CreateGeometryCollection(parts);
            }
            default:
                throw new NotSupportedException($"Edição de vértices não dá suporte a {geometry.GeometryType}.");
        }
    }

    private static Coordinate[] RemoveCoordinates(Coordinate[] source, VertexAddress address, ref int sequence, bool closed)
    {
        int currentSequence = sequence++;
        if (currentSequence != address.Sequence) return source.Select(coordinate => coordinate.Copy()).ToArray();

        bool hasClosure = closed && source.Length > 2 && source[0].Equals2D(source[^1]);
        int uniqueCount = hasClosure ? source.Length - 1 : source.Length;
        if (address.Coordinate >= uniqueCount)
            throw new ArgumentOutOfRangeException(nameof(address), "O índice do vértice está fora da geometria.");
        int minimum = closed ? 3 : 2;
        if (uniqueCount <= minimum)
            throw new InvalidOperationException(closed
                ? "Um anel precisa manter pelo menos três vértices."
                : "Uma linha precisa manter pelo menos dois vértices.");

        var remaining = source.Take(uniqueCount)
            .Where((_, index) => index != address.Coordinate)
            .Select(coordinate => coordinate.Copy())
            .ToList();
        if (hasClosure) remaining.Add(remaining[0].Copy());
        return remaining.ToArray();
    }

    private static void AppendVertices(NtsGeometry geometry, List<Vertex> result, ref int sequence)
    {
        switch (geometry)
        {
            case Polygon polygon:
                AppendVertices(polygon.ExteriorRing, result, ref sequence);
                for (int i = 0; i < polygon.NumInteriorRings; i++)
                    AppendVertices(polygon.GetInteriorRingN(i), result, ref sequence);
                return;
            case LineString line:
                AppendSequence(line.Coordinates, result, sequence++);
                return;
            case NtsPoint point:
                AppendSequence(point.Coordinates, result, sequence++);
                return;
            case GeometryCollection collection:
                for (int i = 0; i < collection.NumGeometries; i++)
                    AppendVertices(collection.GetGeometryN(i), result, ref sequence);
                return;
            default:
                throw new NotSupportedException($"Edição de vértices não dá suporte a {geometry.GeometryType}.");
        }
    }

    private static void AppendSequence(Coordinate[] coordinates, List<Vertex> result, int sequence)
    {
        int count = coordinates.Length;
        bool closed = count > 2 && coordinates[0].Equals2D(coordinates[^1]);
        if (closed) count--;
        for (int i = 0; i < count; i++)
            result.Add(new Vertex(new VertexAddress(sequence, i), coordinates[i].X, coordinates[i].Y));
    }

    private static NtsGeometry Rebuild(NtsGeometry geometry, VertexAddress address, double x, double y, ref int sequence)
    {
        GeometryFactory factory = geometry.Factory;
        switch (geometry)
        {
            case Polygon polygon:
            {
                LinearRing shell = (LinearRing)Rebuild(polygon.ExteriorRing, address, x, y, ref sequence);
                var holes = new LinearRing[polygon.NumInteriorRings];
                for (int i = 0; i < holes.Length; i++)
                    holes[i] = (LinearRing)Rebuild(polygon.GetInteriorRingN(i), address, x, y, ref sequence);
                return factory.CreatePolygon(shell, holes);
            }
            case LinearRing ring:
                return factory.CreateLinearRing(UpdateCoordinates(ring.Coordinates, address, x, y, ref sequence));
            case LineString line:
                return factory.CreateLineString(UpdateCoordinates(line.Coordinates, address, x, y, ref sequence));
            case NtsPoint point:
            {
                Coordinate[] coordinates = UpdateCoordinates(point.Coordinates, address, x, y, ref sequence);
                return coordinates.Length == 0 ? factory.CreatePoint() : factory.CreatePoint(coordinates[0]);
            }
            case MultiPoint multiPoint:
            {
                var points = new NtsPoint[multiPoint.NumGeometries];
                for (int i = 0; i < points.Length; i++)
                    points[i] = (NtsPoint)Rebuild(multiPoint.GetGeometryN(i), address, x, y, ref sequence);
                return factory.CreateMultiPoint(points);
            }
            case MultiLineString multiLine:
            {
                var lines = new LineString[multiLine.NumGeometries];
                for (int i = 0; i < lines.Length; i++)
                    lines[i] = (LineString)Rebuild(multiLine.GetGeometryN(i), address, x, y, ref sequence);
                return factory.CreateMultiLineString(lines);
            }
            case MultiPolygon multiPolygon:
            {
                var polygons = new Polygon[multiPolygon.NumGeometries];
                for (int i = 0; i < polygons.Length; i++)
                    polygons[i] = (Polygon)Rebuild(multiPolygon.GetGeometryN(i), address, x, y, ref sequence);
                return factory.CreateMultiPolygon(polygons);
            }
            case GeometryCollection collection:
            {
                var parts = new NtsGeometry[collection.NumGeometries];
                for (int i = 0; i < parts.Length; i++)
                    parts[i] = Rebuild(collection.GetGeometryN(i), address, x, y, ref sequence);
                return factory.CreateGeometryCollection(parts);
            }
            default:
                throw new NotSupportedException($"Edição de vértices não dá suporte a {geometry.GeometryType}.");
        }
    }

    private static Coordinate[] UpdateCoordinates(
        Coordinate[] source, VertexAddress address, double x, double y, ref int sequence)
    {
        int currentSequence = sequence++;
        var coordinates = source.Select(coordinate => coordinate.Copy()).ToArray();
        if (currentSequence != address.Sequence) return coordinates;
        if (address.Coordinate >= coordinates.Length || coordinates.Length == 0)
            throw new ArgumentOutOfRangeException(nameof(address), "O índice do vértice está fora da geometria.");

        int targetIndex = address.Coordinate;
        if (coordinates.Length > 2 && coordinates[0].Equals2D(coordinates[^1]) && targetIndex == coordinates.Length - 1)
            targetIndex = 0;
        coordinates[targetIndex].X = x;
        coordinates[targetIndex].Y = y;
        if (coordinates.Length > 2 && coordinates[0].Equals2D(coordinates[^1]) && targetIndex == 0)
        {
            coordinates[^1].X = x;
            coordinates[^1].Y = y;
        }
        return coordinates;
    }

    private static void EnsureSuccess(int result, string operation)
    {
        if (result != 0)
            throw new IOException($"O GDAL falhou ao {operation}. {Gdal.GetLastErrorMsg()}");
    }
}
