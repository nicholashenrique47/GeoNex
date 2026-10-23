using NetTopologySuite.Geometries;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.IO;
using NetTopologySuite.LinearReferencing;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Operation.Valid;
using NetTopologySuite.Simplify;
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

    public static AttributesTable ReadAttributes(string path, long featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Ogr.RegisterAll();

        using DataSource dataSource = Ogr.Open(path, 0)
            ?? throw new IOException($"O GDAL não conseguiu abrir '{Path.GetFileName(path)}' para leitura de atributos. {Gdal.GetLastErrorMsg()}");
        using Layer layer = dataSource.GetLayerByIndex(0)
            ?? throw new InvalidDataException("A fonte não contém uma camada vetorial.");
        using OSGeo.OGR.Feature feature = layer.GetFeature(featureId)
            ?? throw new InvalidDataException($"A feição {featureId} não existe mais na fonte.");
        using FeatureDefn definition = layer.GetLayerDefn()
            ?? throw new InvalidDataException("Não foi possível ler a definição dos atributos da camada.");

        var attributes = new AttributesTable();
        for (int i = 0; i < definition.GetFieldCount(); i++)
        {
            if (!feature.IsFieldSetAndNotNull(i)) continue;
            using FieldDefn field = definition.GetFieldDefn(i);
            attributes.Add(field.GetNameRef(), feature.GetFieldAsString(i) ?? string.Empty);
        }
        return attributes;
    }

    public static string? GetGeometryValidationMessage(NtsGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var error = new IsValidOp(geometry).ValidationError;
        if (error is null) return null;

        string reason = error.ErrorType switch
        {
            TopologyValidationErrors.SelfIntersection or TopologyValidationErrors.RingSelfIntersection => "O contorno tem uma auto-interseção.",
            TopologyValidationErrors.HoleOutsideShell => "Um furo está fora do contorno externo.",
            TopologyValidationErrors.NestedHoles => "Há furos aninhados ou sobrepostos.",
            TopologyValidationErrors.DisconnectedInteriors => "O interior do polígono está desconectado.",
            TopologyValidationErrors.NestedShells => "Há contornos externos aninhados.",
            TopologyValidationErrors.DuplicateRings => "Há contornos duplicados.",
            TopologyValidationErrors.TooFewPoints => "Um contorno tem poucos vértices distintos.",
            TopologyValidationErrors.InvalidCoordinate => "A geometria contém uma coordenada inválida.",
            TopologyValidationErrors.RingNotClosed => "Um anel não está fechado.",
            _ => error.Message
        };
        return error.Coordinate is { } coordinate
            ? $"{reason} Local: X {coordinate.X:G12}, Y {coordinate.Y:G12}."
            : reason;
    }

    public static NtsGeometry FixGeometry(NtsGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.IsEmpty)
            throw new InvalidOperationException("Uma geometria vazia não pode ser reparada.");
        if (geometry.IsValid)
            throw new InvalidOperationException("A geometria já é válida; nenhuma alteração foi aplicada.");

        bool keepMulti = geometry is MultiPoint or MultiLineString or MultiPolygon;
        NtsGeometry repaired = GeometryFixer.Fix(geometry, keepMulti);
        if (repaired.IsEmpty || !repaired.IsValid)
            throw new InvalidOperationException("O reparo não produziu uma geometria válida e não vazia.");
        if (repaired.GeometryType != geometry.GeometryType &&
            !(geometry is Polygon && repaired is MultiPolygon))
            throw new InvalidOperationException("O reparo alteraria o tipo da geometria de forma incompatível com a feição original.");
        return repaired;
    }

    public static IReadOnlyList<NtsGeometry> CutPolygonByLine(NtsGeometry polygon, LineString cutter)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        ArgumentNullException.ThrowIfNull(cutter);
        if (polygon is not (Polygon or MultiPolygon) || polygon.IsEmpty || !polygon.IsValid)
            throw new InvalidOperationException("O corte exige um polígono válido e não vazio.");
        if (cutter.IsEmpty || !cutter.IsValid || cutter.Length <= 0)
            throw new InvalidOperationException("A linha de corte precisa conter pelo menos dois pontos distintos.");

        NtsGeometry linework = polygon.Boundary.Union(cutter);
        var polygonizer = new Polygonizer();
        polygonizer.Add(linework);
        Polygon[] parts = polygonizer.GetPolygons()
            .Cast<Polygon>()
            .Where(part => polygon.Covers(part))
            .OrderByDescending(part => part.Area)
            .ThenBy(part => part.Centroid.X)
            .ThenBy(part => part.Centroid.Y)
            .ToArray();
        if (parts.Length < 2)
            throw new InvalidOperationException("A linha precisa atravessar o polígono e alcançar sua borda em pelo menos dois pontos.");

        NtsGeometry combined = UnaryUnionOp.Union(parts);
        double differenceArea = polygon.SymmetricDifference(combined).Area;
        if (!combined.IsValid || differenceArea > Math.Max(1e-12, polygon.Area * 1e-10))
            throw new InvalidOperationException("O corte não preservaria toda a área original; nenhuma alteração foi aplicada.");

        if (polygon is MultiPolygon)
            return parts.Select(part => (NtsGeometry)polygon.Factory.CreateMultiPolygon([part])).ToArray();
        return parts;
    }

    public static IReadOnlyList<NtsGeometry> SplitLineByLine(NtsGeometry source, LineString cutter)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cutter);
        if (source is not (LineString or MultiLineString) || source.IsEmpty || !source.IsValid || source.Length <= 0)
            throw new InvalidOperationException("O corte exige uma linha válida e não vazia.");
        if (cutter.IsEmpty || !cutter.IsValid || cutter.Length <= 0)
            throw new InvalidOperationException("A linha de corte precisa conter pelo menos dois pontos distintos.");

        NtsGeometry noded = source.Union(cutter);
        var linework = new List<LineString>();
        CollectLineStrings(noded, linework);
        LineString[] parts = linework
            .Where(part => part.Length > 0 && source.Covers(part))
            .OrderByDescending(part => part.Length)
            .ThenBy(part => part.Centroid.X)
            .ThenBy(part => part.Centroid.Y)
            .ToArray();
        if (parts.Length < 2)
            throw new InvalidOperationException("A linha de corte precisa cruzar a feição linear em um ponto que a divida.");

        NtsGeometry combined = UnaryUnionOp.Union(parts);
        double differenceLength = source.SymmetricDifference(combined).Length;
        if (!combined.IsValid || differenceLength > Math.Max(1e-12, source.Length * 1e-10))
            throw new InvalidOperationException("O corte não preservaria toda a linha original; nenhuma alteração foi aplicada.");

        if (source is MultiLineString)
            return parts.Select(part => (NtsGeometry)source.Factory.CreateMultiLineString([part])).ToArray();
        return parts;
    }

    public static IReadOnlyList<NtsGeometry> SplitGeometryByLine(NtsGeometry source, LineString cutter)
        => source switch
        {
            Polygon or MultiPolygon => CutPolygonByLine(source, cutter),
            LineString or MultiLineString => SplitLineByLine(source, cutter),
            _ => throw new InvalidOperationException("O corte está disponível para polígonos e linhas.")
        };

    public static NtsGeometry ReshapePolygon(NtsGeometry source, LineString reshapeLine)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(reshapeLine);
        if (source is not (Polygon or MultiPolygon) || source.IsEmpty || !source.IsValid)
            throw new InvalidOperationException("A remodelagem exige um polígono válido e não vazio.");
        if (reshapeLine.IsEmpty || reshapeLine.NumPoints < 2 || reshapeLine.Length <= 0 || !reshapeLine.IsValid || !reshapeLine.IsSimple)
            throw new InvalidOperationException("O traço de remodelagem precisa ser uma linha simples e válida.");

        NtsGeometry intersections = source.Boundary.Intersection(reshapeLine);
        if (ContainsLineComponent(intersections))
            throw new InvalidOperationException("O traço não pode coincidir com a borda da feição.");
        var boundaryPoints = new List<NtsPoint>();
        CollectPointComponents(intersections, boundaryPoints);
        NtsPoint[] distinctBoundaryPoints = boundaryPoints
            .DistinctBy(point => (point.X, point.Y))
            .ToArray();
        if (distinctBoundaryPoints.Length != 2)
            throw new InvalidOperationException("O traço precisa cruzar a borda da feição exatamente duas vezes.");

        var indexedLine = new LengthIndexedLine(reshapeLine);
        double firstIndex = indexedLine.Project(distinctBoundaryPoints[0].Coordinate);
        double secondIndex = indexedLine.Project(distinctBoundaryPoints[1].Coordinate);
        if (Math.Abs(firstIndex - secondIndex) <= Math.Max(1e-12, reshapeLine.Length * 1e-12))
            throw new InvalidOperationException("Os cruzamentos do traço estão próximos demais para remodelar a feição.");
        if (firstIndex > secondIndex) (firstIndex, secondIndex) = (secondIndex, firstIndex);
        if (indexedLine.ExtractLine(firstIndex, secondIndex) is not LineString clippedLine || clippedLine.Length <= 0)
            throw new InvalidOperationException("Não foi possível extrair o trecho do traço entre os cruzamentos.");

        var polygonizer = new Polygonizer();
        polygonizer.Add(source.Boundary.Union(clippedLine));
        double lengthTolerance = Math.Max(1e-9, clippedLine.Length * 1e-9);
        Polygon[] lobes = polygonizer.GetPolygons()
            .Cast<Polygon>()
            .Where(part => part.IsValid && part.Area > 0 &&
                Math.Abs(part.Boundary.Intersection(clippedLine).Length - clippedLine.Length) <= lengthTolerance)
            .ToArray();
        if (lobes.Length == 0)
            throw new InvalidOperationException("O traço não forma uma região fechada com a borda da feição.");

        Polygon? insideLobe = lobes
            .Where(part => source.Covers(part.InteriorPoint))
            .OrderBy(part => part.Area)
            .FirstOrDefault();
        Polygon? outsideLobe = lobes
            .Where(part => source.Disjoint(part.InteriorPoint))
            .OrderBy(part => part.Area)
            .FirstOrDefault();
        bool removeLobe = insideLobe is not null;
        Polygon lobe = insideLobe ?? outsideLobe
            ?? throw new InvalidOperationException("Não foi possível determinar a região delimitada pelo traço.");
        NtsGeometry result = removeLobe ? source.Difference(lobe) : source.Union(lobe);

        if (source is MultiPolygon && result is Polygon singlePart)
            result = source.Factory.CreateMultiPolygon([singlePart]);
        else if (source is Polygon && result is MultiPolygon multipleParts && multipleParts.NumGeometries == 1)
            result = (NtsGeometry)multipleParts.GetGeometryN(0).Copy();
        if (result.IsEmpty || !result.IsValid || result.GeometryType != source.GeometryType || result.EqualsExact(source))
            throw new InvalidOperationException("A remodelagem não produziu uma alteração poligonal válida; a feição original foi preservada.");
        return result;
    }

    private static bool ContainsLineComponent(NtsGeometry geometry)
    {
        if (geometry is LineString line) return !line.IsEmpty;
        if (geometry is not GeometryCollection collection) return false;
        for (int i = 0; i < collection.NumGeometries; i++)
            if (ContainsLineComponent(collection.GetGeometryN(i))) return true;
        return false;
    }

    private static void CollectPointComponents(NtsGeometry geometry, List<NtsPoint> points)
    {
        if (geometry is NtsPoint point)
        {
            points.Add(point);
            return;
        }
        if (geometry is not GeometryCollection collection) return;
        for (int i = 0; i < collection.NumGeometries; i++)
            CollectPointComponents(collection.GetGeometryN(i), points);
    }

    private static void CollectLineStrings(NtsGeometry geometry, List<LineString> linework)
    {
        if (geometry is LineString line)
        {
            linework.Add(line);
            return;
        }
        if (geometry is not GeometryCollection collection) return;
        for (int i = 0; i < collection.NumGeometries; i++)
            CollectLineStrings(collection.GetGeometryN(i), linework);
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
        NtsGeometry result = MoveVertexPreview(geometry, address, x, y);
        if (!result.IsValid)
            throw new InvalidOperationException("A edição criaria uma geometria inválida. O vértice voltou à posição anterior.");
        return result;
    }

    /// <summary>Builds a drag preview without running the full topology validation on every pointer frame.</summary>
    public static NtsGeometry MoveVertexPreview(NtsGeometry geometry, VertexAddress address, double x, double y)
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
        return result;
    }

    public static IReadOnlyList<NtsGeometry> MoveCoincidentVertices(
        IReadOnlyList<NtsGeometry> geometries, int primaryIndex, VertexAddress address, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(geometries);
        if (primaryIndex < 0 || primaryIndex >= geometries.Count)
            throw new ArgumentOutOfRangeException(nameof(primaryIndex));
        NtsGeometry primary = geometries[primaryIndex]
            ?? throw new ArgumentException("A feição principal não possui geometria.", nameof(geometries));
        Dictionary<VertexAddress, Vertex> primaryVertices = GetVertices(primary)
            .ToDictionary(vertex => vertex.Address);
        if (!primaryVertices.TryGetValue(address, out Vertex anchor))
            throw new ArgumentOutOfRangeException(nameof(address), "O vértice selecionado não pertence à feição principal.");
        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new ArgumentOutOfRangeException(nameof(x), "A nova coordenada precisa ser finita.");
        return MoveCoincidentVerticesByDelta(geometries, primaryIndex, [address], x - anchor.X, y - anchor.Y);
    }

    public static IReadOnlyList<NtsGeometry> MoveCoincidentVerticesByDelta(
        IReadOnlyList<NtsGeometry> geometries,
        int primaryIndex,
        IReadOnlyCollection<VertexAddress> addresses,
        double deltaX,
        double deltaY)
        => MoveCoincidentVerticesByDeltaCore(geometries, primaryIndex, addresses, deltaX, deltaY, validate: true);

    public static IReadOnlyList<NtsGeometry> MoveCoincidentVerticesByDeltaPreview(
        IReadOnlyList<NtsGeometry> geometries,
        int primaryIndex,
        IReadOnlyCollection<VertexAddress> addresses,
        double deltaX,
        double deltaY)
        => MoveCoincidentVerticesByDeltaCore(geometries, primaryIndex, addresses, deltaX, deltaY, validate: false);

    private static IReadOnlyList<NtsGeometry> MoveCoincidentVerticesByDeltaCore(
        IReadOnlyList<NtsGeometry> geometries,
        int primaryIndex,
        IReadOnlyCollection<VertexAddress> addresses,
        double deltaX,
        double deltaY,
        bool validate)
    {
        ArgumentNullException.ThrowIfNull(geometries);
        ArgumentNullException.ThrowIfNull(addresses);
        if (geometries.Count == 0) throw new ArgumentException("Selecione pelo menos uma geometria.", nameof(geometries));
        if (primaryIndex < 0 || primaryIndex >= geometries.Count)
            throw new ArgumentOutOfRangeException(nameof(primaryIndex));
        if (addresses.Count == 0) throw new ArgumentException("Selecione pelo menos um vértice.", nameof(addresses));
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            throw new ArgumentOutOfRangeException(nameof(deltaX), "O deslocamento precisa ser finito.");

        NtsGeometry primary = geometries[primaryIndex]
            ?? throw new ArgumentException("A feição principal não possui geometria.", nameof(geometries));
        Dictionary<VertexAddress, Vertex> primaryVertices = GetVertices(primary)
            .ToDictionary(vertex => vertex.Address);
        Vertex[] anchors = addresses.Distinct().Select(address =>
            primaryVertices.TryGetValue(address, out Vertex vertex)
                ? vertex
                : throw new ArgumentOutOfRangeException(nameof(addresses), "Um vértice selecionado não pertence à feição principal."))
            .ToArray();

        var movedAddresses = Enumerable.Range(0, geometries.Count)
            .Select(_ => new HashSet<VertexAddress>())
            .ToArray();
        foreach (VertexAddress address in addresses) movedAddresses[primaryIndex].Add(address);
        for (int i = 0; i < geometries.Count; i++)
        {
            NtsGeometry geometry = geometries[i]
                ?? throw new ArgumentException("Uma feição selecionada não possui geometria.", nameof(geometries));
            if (i == primaryIndex) continue;
            foreach (Vertex vertex in GetVertices(geometry))
            {
                if (anchors.Any(anchor =>
                    Math.Abs(vertex.X - anchor.X) <= CoordinateTolerance(anchor) &&
                    Math.Abs(vertex.Y - anchor.Y) <= CoordinateTolerance(anchor)))
                    movedAddresses[i].Add(vertex.Address);
            }
        }

        NtsGeometry[] moved = geometries.ToArray();
        for (int i = 0; i < moved.Length; i++)
        {
            if (movedAddresses[i].Count == 0) continue;
            moved[i] = MoveVerticesByDeltaCore(geometries[i], movedAddresses[i], deltaX, deltaY, validate);
        }
        return moved;
    }

    public static NtsGeometry MoveVerticesByDelta(
        NtsGeometry geometry,
        IReadOnlyCollection<VertexAddress> addresses,
        double deltaX,
        double deltaY)
    {
        NtsGeometry result = MoveVerticesByDeltaCore(geometry, addresses, deltaX, deltaY, validate: false);
        if (result.IsEmpty || !result.IsValid)
            throw new InvalidOperationException("O movimento conjunto criaria uma geometria vazia ou inválida.");
        return result;
    }

    public static NtsGeometry MoveVerticesByDeltaPreview(
        NtsGeometry geometry,
        IReadOnlyCollection<VertexAddress> addresses,
        double deltaX,
        double deltaY)
        => MoveVerticesByDeltaCore(geometry, addresses, deltaX, deltaY, validate: false);

    private static NtsGeometry MoveVerticesByDeltaCore(
        NtsGeometry geometry,
        IReadOnlyCollection<VertexAddress> addresses,
        double deltaX,
        double deltaY,
        bool validate)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0) throw new ArgumentException("Selecione pelo menos um vértice.", nameof(addresses));
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            throw new ArgumentOutOfRangeException(nameof(deltaX), "O deslocamento precisa ser finito.");

        var moves = addresses.ToHashSet();
        if (moves.Count == 0) throw new ArgumentException("Selecione pelo menos um vértice.", nameof(addresses));
        int sequence = 0;
        var applied = new HashSet<VertexAddress>();
        NtsGeometry result = RebuildVerticesByDelta(geometry, moves, applied, deltaX, deltaY, ref sequence);
        if (applied.Count != moves.Count)
            throw new ArgumentOutOfRangeException(nameof(addresses), "Um vértice selecionado não pôde ser localizado.");
        if (validate && (result.IsEmpty || !result.IsValid))
            throw new InvalidOperationException("O movimento conjunto criaria uma geometria vazia ou inválida.");
        return result;
    }

    public static NtsGeometry Translate(NtsGeometry geometry, double deltaX, double deltaY)
    {
        NtsGeometry result = TranslatePreview(geometry, deltaX, deltaY);
        if (result.IsEmpty || !result.IsValid)
            throw new InvalidOperationException("O deslocamento produziu uma geometria inválida.");
        return result;
    }

    public static NtsGeometry TranslatePreview(NtsGeometry geometry, double deltaX, double deltaY)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            throw new ArgumentOutOfRangeException(nameof(deltaX), "O deslocamento precisa ser finito.");
        return AffineTransformation.TranslationInstance(deltaX, deltaY).Transform(geometry);
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

    public static NtsGeometry Simplify(NtsGeometry geometry, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (!double.IsFinite(tolerance) || tolerance <= 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance), "A tolerância precisa ser finita e maior que zero.");
        if (geometry.IsEmpty || !geometry.IsValid)
            throw new InvalidOperationException("A simplificação exige uma geometria válida e não vazia.");

        NtsGeometry result = TopologyPreservingSimplifier.Simplify(geometry, tolerance);
        if (result.IsEmpty || !result.IsValid || result.GeometryType != geometry.GeometryType)
            throw new InvalidOperationException("A simplificação alteraria o tipo ou invalidaria a geometria; nenhuma alteração foi aplicada.");
        return result;
    }

    public static NtsGeometry OffsetLine(NtsGeometry geometry, double distance)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry is not (LineString or MultiLineString))
            throw new ArgumentException("O deslocamento paralelo exige uma linha ou multilinha.", nameof(geometry));
        if (geometry.IsEmpty || !geometry.IsValid)
            throw new InvalidOperationException("O deslocamento exige uma geometria linear válida e não vazia.");
        if (!double.IsFinite(distance) || distance == 0)
            throw new ArgumentOutOfRangeException(nameof(distance), "A distância precisa ser finita e diferente de zero.");

        NtsGeometry result = OffsetCurve.GetCurveJoined(geometry, distance);
        if (result.IsEmpty || !result.IsValid || result.GeometryType != geometry.GeometryType)
            throw new InvalidOperationException("O deslocamento não produziu uma linha válida do mesmo tipo; a geometria original foi preservada.");
        return result;
    }

    public static NtsGeometry AddInteriorRing(NtsGeometry geometry, Polygon interiorRing)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(interiorRing);
        if (geometry is not (Polygon or MultiPolygon) || geometry.IsEmpty || !geometry.IsValid)
            throw new InvalidOperationException("A adição de anel exige um polígono válido e não vazio.");
        if (interiorRing.IsEmpty || !interiorRing.IsValid || interiorRing.Area <= 0)
            throw new InvalidOperationException("O contorno do anel precisa formar um polígono válido e fechado.");

        Polygon[] parts = geometry is Polygon polygon
            ? [polygon]
            : ((MultiPolygon)geometry).Geometries.Cast<Polygon>().ToArray();
        int targetIndex = Array.FindIndex(parts, part =>
            part.Contains(interiorRing) && part.Boundary.Disjoint(interiorRing.Boundary));
        if (targetIndex < 0)
            throw new InvalidOperationException("Desenhe o anel totalmente dentro da área do polígono, sem tocar o contorno.");

        for (int partIndex = 0; partIndex < parts.Length; partIndex++)
        {
            Polygon part = parts[partIndex];
            foreach (LineString existingRing in part.InteriorRings)
            {
                Polygon existingHole = geometry.Factory.CreatePolygon((LinearRing)existingRing.Copy());
                if (existingHole.Intersects(interiorRing))
                    throw new InvalidOperationException("O novo anel não pode cruzar nem sobrepor um furo existente.");
            }
            if (partIndex != targetIndex && part.Intersects(interiorRing))
                throw new InvalidOperationException("O novo anel não pode tocar nem cruzar outra parte do multipolígono.");
        }

        Polygon target = parts[targetIndex];
        LinearRing[] holes = target.InteriorRings
            .Select(ring => (LinearRing)ring.Copy())
            .Append((LinearRing)interiorRing.ExteriorRing.Copy())
            .ToArray();
        parts[targetIndex] = geometry.Factory.CreatePolygon((LinearRing)target.ExteriorRing.Copy(), holes);
        NtsGeometry result = geometry is Polygon
            ? parts[0]
            : geometry.Factory.CreateMultiPolygon(parts);
        if (result.IsEmpty || !result.IsValid || result.GeometryType != geometry.GeometryType)
            throw new InvalidOperationException("O anel produziria uma geometria inválida; nenhuma alteração foi aplicada.");
        return result;
    }

    public static NtsGeometry RemoveInteriorRing(NtsGeometry geometry, VertexAddress address)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry is not (Polygon or MultiPolygon) || geometry.IsEmpty || !geometry.IsValid)
            throw new InvalidOperationException("A remoção de anel exige um polígono válido e não vazio.");
        if (address.Sequence < 0 || address.Coordinate < 0)
            throw new ArgumentOutOfRangeException(nameof(address));

        int sequence = 0;
        bool removed = false;
        NtsGeometry result = RemoveInteriorRingFromSequence(geometry, address.Sequence, address.Coordinate, ref sequence, ref removed);
        if (!removed)
            throw new InvalidOperationException("Passe o cursor sobre um vértice de um anel interno, não sobre o contorno externo.");
        if (result.IsEmpty || !result.IsValid || result.GeometryType != geometry.GeometryType)
            throw new InvalidOperationException("A remoção produziria uma geometria inválida.");
        return result;
    }

    private static NtsGeometry RemoveInteriorRingFromSequence(
        NtsGeometry geometry, int targetSequence, int targetCoordinate, ref int sequence, ref bool removed)
    {
        switch (geometry)
        {
            case Polygon polygon:
            {
                sequence++;
                var holes = new List<LinearRing>(polygon.NumInteriorRings);
                for (int i = 0; i < polygon.NumInteriorRings; i++)
                {
                    LineString hole = polygon.GetInteriorRingN(i);
                    int currentSequence = sequence++;
                    int uniqueCoordinates = hole.NumPoints > 1 && hole.IsClosed ? hole.NumPoints - 1 : hole.NumPoints;
                    if (!removed && currentSequence == targetSequence && targetCoordinate < uniqueCoordinates)
                    {
                        removed = true;
                        continue;
                    }
                    holes.Add((LinearRing)hole.Copy());
                }
                return geometry.Factory.CreatePolygon((LinearRing)polygon.ExteriorRing.Copy(), holes.ToArray());
            }
            case MultiPolygon multiPolygon:
            {
                var parts = new Polygon[multiPolygon.NumGeometries];
                for (int i = 0; i < parts.Length; i++)
                    parts[i] = (Polygon)RemoveInteriorRingFromSequence(
                        multiPolygon.GetGeometryN(i), targetSequence, targetCoordinate, ref sequence, ref removed);
                return geometry.Factory.CreateMultiPolygon(parts);
            }
            default:
                throw new NotSupportedException($"Remoção de anéis não dá suporte a {geometry.GeometryType}.");
        }
    }

    public static NtsGeometry MergePolygons(IReadOnlyList<NtsGeometry> geometries, int primaryIndex)
    {
        ArgumentNullException.ThrowIfNull(geometries);
        if (geometries.Count < 2)
            throw new ArgumentException("Selecione pelo menos dois polígonos para mesclar.", nameof(geometries));
        if (primaryIndex < 0 || primaryIndex >= geometries.Count)
            throw new ArgumentOutOfRangeException(nameof(primaryIndex));
        if (geometries.Any(geometry => geometry is not (Polygon or MultiPolygon) || geometry.IsEmpty || !geometry.IsValid))
            throw new InvalidOperationException("A mesclagem exige polígonos válidos e não vazios.");

        NtsGeometry merged = UnaryUnionOp.Union(geometries);
        NtsGeometry primary = geometries[primaryIndex];
        if (primary is MultiPolygon && merged is Polygon polygon)
            merged = polygon.Factory.CreateMultiPolygon([polygon]);
        else if (primary is Polygon && merged is MultiPolygon multiPolygon && multiPolygon.NumGeometries == 1)
            merged = (NtsGeometry)multiPolygon.GetGeometryN(0).Copy();

        if (merged is not (Polygon or MultiPolygon) || merged.GeometryType != primary.GeometryType)
            throw new InvalidOperationException("O resultado seria multipartes e não pode ser armazenado no tipo da feição principal. Escolha polígonos conectados ou uma feição multipolígono como principal.");
        if (merged.IsEmpty || !merged.IsValid)
            throw new InvalidOperationException("A mesclagem produziria uma geometria inválida.");
        return merged;
    }

    public static NtsGeometry MergeLines(IReadOnlyList<NtsGeometry> geometries, int primaryIndex)
    {
        ArgumentNullException.ThrowIfNull(geometries);
        if (geometries.Count < 2)
            throw new ArgumentException("Selecione pelo menos duas linhas para mesclar.", nameof(geometries));
        if (primaryIndex < 0 || primaryIndex >= geometries.Count)
            throw new ArgumentOutOfRangeException(nameof(primaryIndex));
        if (geometries.Any(geometry => geometry is not (LineString or MultiLineString) ||
            geometry.IsEmpty || !geometry.IsValid || geometry.Length <= 0))
            throw new InvalidOperationException("A mesclagem exige linhas válidas e não vazias.");

        var merger = new LineMerger();
        foreach (NtsGeometry geometry in geometries)
            merger.Add(geometry);
        LineString[] parts = merger.GetMergedLineStrings()
            .Cast<LineString>()
            .OrderByDescending(part => part.Length)
            .ThenBy(part => part.Centroid.X)
            .ThenBy(part => part.Centroid.Y)
            .ToArray();
        if (parts.Length == 0)
            throw new InvalidOperationException("A mesclagem não produziu segmentos lineares.");

        NtsGeometry primary = geometries[primaryIndex];
        NtsGeometry merged;
        if (primary is LineString)
        {
            if (parts.Length != 1)
                throw new InvalidOperationException("As linhas selecionadas não formam uma única linha conectada; escolha uma feição multilinha como principal para preservar partes desconectadas.");
            merged = parts[0];
        }
        else
        {
            merged = primary.Factory.CreateMultiLineString(parts);
        }

        double originalLength = geometries.Sum(geometry => geometry.Length);
        if (merged.IsEmpty || !merged.IsValid || merged.GeometryType != primary.GeometryType ||
            Math.Abs(merged.Length - originalLength) > Math.Max(1e-12, originalLength * 1e-10))
            throw new InvalidOperationException("A mesclagem alteraria o tipo ou o comprimento das linhas selecionadas.");
        return merged;
    }

    public static NtsGeometry ReflectAcrossVerticalAxis(NtsGeometry geometry)
        => ReflectAroundCentroid(geometry, -1, 1);

    public static NtsGeometry ReflectAcrossHorizontalAxis(NtsGeometry geometry)
        => ReflectAroundCentroid(geometry, 1, -1);

    private static NtsGeometry ReflectAroundCentroid(NtsGeometry geometry, double scaleX, double scaleY)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.IsEmpty) throw new InvalidOperationException("Uma geometria vazia não pode ser refletida.");
        var center = geometry.Centroid;
        NtsGeometry result = AffineTransformation.ScaleInstance(scaleX, scaleY, center.X, center.Y).Transform(geometry);
        if (result.IsEmpty || !result.IsValid)
            throw new InvalidOperationException("A reflexão produziu uma geometria inválida.");
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

    public static NtsGeometry RemoveVertices(NtsGeometry geometry, IReadOnlyCollection<VertexAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(addresses);
        if (geometry.IsEmpty || !geometry.IsValid)
            throw new InvalidOperationException("A remoção exige uma geometria válida e não vazia.");

        HashSet<VertexAddress> requested = addresses.ToHashSet();
        if (requested.Count == 0)
            throw new ArgumentException("Selecione pelo menos um vértice para remover.", nameof(addresses));
        HashSet<VertexAddress> existing = GetVertices(geometry).Select(vertex => vertex.Address).ToHashSet();
        if (!requested.IsSubsetOf(existing))
            throw new ArgumentOutOfRangeException(nameof(addresses), "Um vértice selecionado não pertence à geometria.");

        int sequence = 0;
        var removed = new HashSet<VertexAddress>();
        NtsGeometry result = RemoveFromSequences(geometry, requested, removed, ref sequence);
        if (!removed.SetEquals(requested))
            throw new ArgumentOutOfRangeException(nameof(addresses), "Um vértice selecionado não pôde ser localizado.");
        if (result.IsEmpty || !result.IsValid || result.GeometryType != geometry.GeometryType)
            throw new InvalidOperationException("A remoção deixaria uma geometria vazia, inválida ou de outro tipo. Nenhuma alteração foi aplicada.");
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

    private static NtsGeometry RemoveFromSequences(
        NtsGeometry geometry,
        IReadOnlySet<VertexAddress> addresses,
        HashSet<VertexAddress> removed,
        ref int sequence)
    {
        GeometryFactory factory = geometry.Factory;
        switch (geometry)
        {
            case Polygon polygon:
            {
                LinearRing shell = (LinearRing)RemoveFromSequences(polygon.ExteriorRing, addresses, removed, ref sequence);
                var holes = new LinearRing[polygon.NumInteriorRings];
                for (int i = 0; i < holes.Length; i++)
                    holes[i] = (LinearRing)RemoveFromSequences(polygon.GetInteriorRingN(i), addresses, removed, ref sequence);
                return factory.CreatePolygon(shell, holes);
            }
            case LinearRing ring:
            {
                int currentSequence = sequence++;
                return factory.CreateLinearRing(RemoveCoordinates(
                    ring.Coordinates, currentSequence, addresses, removed, minimumVertices: 3));
            }
            case LineString line:
            {
                int currentSequence = sequence++;
                return factory.CreateLineString(RemoveCoordinates(
                    line.Coordinates, currentSequence, addresses, removed, minimumVertices: 2));
            }
            case NtsPoint point:
            {
                int currentSequence = sequence++;
                if (addresses.Contains(new VertexAddress(currentSequence, 0)))
                    throw new InvalidOperationException("Não é possível remover o único vértice de um ponto.");
                return (NtsGeometry)point.Copy();
            }
            case MultiPoint multiPoint:
            {
                var points = new NtsPoint[multiPoint.NumGeometries];
                for (int i = 0; i < points.Length; i++)
                    points[i] = (NtsPoint)RemoveFromSequences(multiPoint.GetGeometryN(i), addresses, removed, ref sequence);
                return factory.CreateMultiPoint(points);
            }
            case MultiLineString multiLine:
            {
                var lines = new LineString[multiLine.NumGeometries];
                for (int i = 0; i < lines.Length; i++)
                    lines[i] = (LineString)RemoveFromSequences(multiLine.GetGeometryN(i), addresses, removed, ref sequence);
                return factory.CreateMultiLineString(lines);
            }
            case MultiPolygon multiPolygon:
            {
                var polygons = new Polygon[multiPolygon.NumGeometries];
                for (int i = 0; i < polygons.Length; i++)
                    polygons[i] = (Polygon)RemoveFromSequences(multiPolygon.GetGeometryN(i), addresses, removed, ref sequence);
                return factory.CreateMultiPolygon(polygons);
            }
            case GeometryCollection collection:
            {
                var parts = new NtsGeometry[collection.NumGeometries];
                for (int i = 0; i < parts.Length; i++)
                    parts[i] = RemoveFromSequences(collection.GetGeometryN(i), addresses, removed, ref sequence);
                return factory.CreateGeometryCollection(parts);
            }
            default:
                throw new NotSupportedException($"Edição de vértices não dá suporte a {geometry.GeometryType}.");
        }
    }

    private static Coordinate[] RemoveCoordinates(
        Coordinate[] source,
        int sequence,
        IReadOnlySet<VertexAddress> addresses,
        HashSet<VertexAddress> removed,
        int minimumVertices)
    {
        VertexAddress[] selected = addresses.Where(address => address.Sequence == sequence).ToArray();
        if (selected.Length == 0) return source.Select(coordinate => coordinate.Copy()).ToArray();

        bool hasClosure = source.Length > 2 && source[0].Equals2D(source[^1]);
        int uniqueCount = hasClosure ? source.Length - 1 : source.Length;
        if (selected.Any(address => address.Coordinate < 0 || address.Coordinate >= uniqueCount))
            throw new ArgumentOutOfRangeException(nameof(addresses), "O índice do vértice está fora da geometria.");
        HashSet<int> selectedCoordinates = selected.Select(address => address.Coordinate).ToHashSet();
        if (uniqueCount - selectedCoordinates.Count < minimumVertices)
            throw new InvalidOperationException(minimumVertices == 3
                ? "Um anel precisa manter pelo menos três vértices."
                : "Uma linha precisa manter pelo menos dois vértices.");

        var remaining = source.Take(uniqueCount)
            .Where((_, index) => !selectedCoordinates.Contains(index))
            .Select(coordinate => coordinate.Copy())
            .ToList();
        if (hasClosure) remaining.Add(remaining[0].Copy());
        foreach (VertexAddress address in selected) removed.Add(address);
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

    private static double CoordinateTolerance(Vertex vertex) =>
        Math.Max(1e-9, Math.Max(Math.Abs(vertex.X), Math.Abs(vertex.Y)) * 1e-12);

    private static NtsGeometry RebuildVertices(
        NtsGeometry geometry,
        IReadOnlyDictionary<VertexAddress, Coordinate> moves,
        HashSet<VertexAddress> applied,
        ref int sequence)
    {
        GeometryFactory factory = geometry.Factory;
        switch (geometry)
        {
            case Polygon polygon:
            {
                LinearRing shell = (LinearRing)RebuildVertices(polygon.ExteriorRing, moves, applied, ref sequence);
                var holes = new LinearRing[polygon.NumInteriorRings];
                for (int i = 0; i < holes.Length; i++)
                    holes[i] = (LinearRing)RebuildVertices(polygon.GetInteriorRingN(i), moves, applied, ref sequence);
                return factory.CreatePolygon(shell, holes);
            }
            case LinearRing ring:
            {
                int currentSequence = sequence++;
                return factory.CreateLinearRing(UpdateCoordinates(ring.Coordinates, currentSequence, moves, applied));
            }
            case LineString line:
            {
                int currentSequence = sequence++;
                return factory.CreateLineString(UpdateCoordinates(line.Coordinates, currentSequence, moves, applied));
            }
            case NtsPoint point:
            {
                int currentSequence = sequence++;
                Coordinate[] coordinates = UpdateCoordinates(point.Coordinates, currentSequence, moves, applied);
                return coordinates.Length == 0 ? factory.CreatePoint() : factory.CreatePoint(coordinates[0]);
            }
            case GeometryCollection collection:
            {
                var parts = new NtsGeometry[collection.NumGeometries];
                for (int i = 0; i < parts.Length; i++)
                    parts[i] = RebuildVertices(collection.GetGeometryN(i), moves, applied, ref sequence);
                return collection is MultiPoint
                    ? factory.CreateMultiPoint(parts.Cast<NtsPoint>().ToArray())
                    : collection is MultiLineString
                        ? factory.CreateMultiLineString(parts.Cast<LineString>().ToArray())
                        : collection is MultiPolygon
                            ? factory.CreateMultiPolygon(parts.Cast<Polygon>().ToArray())
                            : factory.CreateGeometryCollection(parts);
            }
            default:
                throw new NotSupportedException($"Edição de vértices não dá suporte a {geometry.GeometryType}.");
        }
    }

    private static NtsGeometry RebuildVerticesByDelta(
        NtsGeometry geometry,
        HashSet<VertexAddress> moves,
        HashSet<VertexAddress> applied,
        double deltaX,
        double deltaY,
        ref int sequence)
    {
        GeometryFactory factory = geometry.Factory;
        switch (geometry)
        {
            case Polygon polygon:
            {
                LinearRing shell = (LinearRing)RebuildVerticesByDelta(
                    polygon.ExteriorRing, moves, applied, deltaX, deltaY, ref sequence);
                var holes = new LinearRing[polygon.NumInteriorRings];
                for (int i = 0; i < holes.Length; i++)
                    holes[i] = (LinearRing)RebuildVerticesByDelta(
                        polygon.GetInteriorRingN(i), moves, applied, deltaX, deltaY, ref sequence);
                return factory.CreatePolygon(shell, holes);
            }
            case LinearRing ring:
            {
                int currentSequence = sequence++;
                return factory.CreateLinearRing(UpdateCoordinatesByDelta(
                    ring.Coordinates, currentSequence, moves, applied, deltaX, deltaY));
            }
            case LineString line:
            {
                int currentSequence = sequence++;
                return factory.CreateLineString(UpdateCoordinatesByDelta(
                    line.Coordinates, currentSequence, moves, applied, deltaX, deltaY));
            }
            case NtsPoint point:
            {
                int currentSequence = sequence++;
                Coordinate[] coordinates = UpdateCoordinatesByDelta(
                    point.Coordinates, currentSequence, moves, applied, deltaX, deltaY);
                return coordinates.Length == 0 ? factory.CreatePoint() : factory.CreatePoint(coordinates[0]);
            }
            case GeometryCollection collection:
            {
                var parts = new NtsGeometry[collection.NumGeometries];
                for (int i = 0; i < parts.Length; i++)
                    parts[i] = RebuildVerticesByDelta(
                        collection.GetGeometryN(i), moves, applied, deltaX, deltaY, ref sequence);
                return collection is MultiPoint
                    ? factory.CreateMultiPoint(parts.Cast<NtsPoint>().ToArray())
                    : collection is MultiLineString
                        ? factory.CreateMultiLineString(parts.Cast<LineString>().ToArray())
                        : collection is MultiPolygon
                            ? factory.CreateMultiPolygon(parts.Cast<Polygon>().ToArray())
                            : factory.CreateGeometryCollection(parts);
            }
            default:
                throw new NotSupportedException($"Edição de vértices não dá suporte a {geometry.GeometryType}.");
        }
    }

    private static Coordinate[] UpdateCoordinatesByDelta(
        Coordinate[] source,
        int sequence,
        HashSet<VertexAddress> moves,
        HashSet<VertexAddress> applied,
        double deltaX,
        double deltaY)
    {
        Coordinate[] coordinates = source.Select(coordinate => coordinate.Copy()).ToArray();
        bool closed = coordinates.Length > 2 && coordinates[0].Equals2D(coordinates[^1]);
        int uniqueCount = closed ? coordinates.Length - 1 : coordinates.Length;
        for (int i = 0; i < uniqueCount; i++)
        {
            var address = new VertexAddress(sequence, i);
            if (!moves.Contains(address)) continue;
            double x = coordinates[i].X + deltaX;
            double y = coordinates[i].Y + deltaY;
            if (!double.IsFinite(x) || !double.IsFinite(y))
                throw new ArgumentOutOfRangeException(nameof(deltaX), "O movimento produziria coordenadas não finitas.");
            coordinates[i].X = x;
            coordinates[i].Y = y;
            if (closed && i == 0)
            {
                coordinates[^1].X = x;
                coordinates[^1].Y = y;
            }
            applied.Add(address);
        }
        return coordinates;
    }

    private static Coordinate[] UpdateCoordinates(
        Coordinate[] source,
        int sequence,
        IReadOnlyDictionary<VertexAddress, Coordinate> moves,
        HashSet<VertexAddress> applied)
    {
        Coordinate[] coordinates = source.Select(coordinate => coordinate.Copy()).ToArray();
        bool closed = coordinates.Length > 2 && coordinates[0].Equals2D(coordinates[^1]);
        int uniqueCount = closed ? coordinates.Length - 1 : coordinates.Length;
        for (int i = 0; i < uniqueCount; i++)
        {
            var address = new VertexAddress(sequence, i);
            if (!moves.TryGetValue(address, out Coordinate? target)) continue;
            coordinates[i].X = target.X;
            coordinates[i].Y = target.Y;
            if (closed && i == 0)
            {
                coordinates[^1].X = target.X;
                coordinates[^1].Y = target.Y;
            }
            applied.Add(address);
        }
        return coordinates;
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
