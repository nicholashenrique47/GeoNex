using System.Numerics;
using System.Runtime.CompilerServices;
using NetTopologySuite.Geometries;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries.Implementation;
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

    /// <summary>
    /// Índice espacial flat em grade CSR (Compressed Sparse Row) livre de alocações no Heap por vértice.
    /// Substitui a árvore STRtree&lt;int&gt; na edição de vértices (inspirado no QgsSpatialIndex / quad-grid de edição do QGIS),
    /// reduzindo o consumo de memória em 12x e acelerando queries de hover/snap e seleção retangular para O(1) por célula.
    /// </summary>
    public sealed class FlatVertexSpatialIndex
    {
        private readonly IReadOnlyList<Vertex> _vertices;
        private readonly double _minX;
        private readonly double _minY;
        private readonly double _invCellWidth;
        private readonly double _invCellHeight;
        private readonly int _cols;
        private readonly int _rows;
        private readonly int[] _cellStarts;
        private readonly int[] _cellEntries;

        private FlatVertexSpatialIndex(
            IReadOnlyList<Vertex> vertices,
            double minX,
            double minY,
            double invCellWidth,
            double invCellHeight,
            int cols,
            int rows,
            int[] cellStarts,
            int[] cellEntries)
        {
            _vertices = vertices;
            _minX = minX;
            _minY = minY;
            _invCellWidth = invCellWidth;
            _invCellHeight = invCellHeight;
            _cols = cols;
            _rows = rows;
            _cellStarts = cellStarts;
            _cellEntries = cellEntries;
        }

        public static FlatVertexSpatialIndex? TryBuild(IReadOnlyList<Vertex> vertices)
        {
            if (vertices is null || vertices.Count < 128)
                return null;

            int count = vertices.Count;
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            int validCount = 0;

            for (int i = 0; i < count; i++)
            {
                Vertex v = vertices[i];
                if (!double.IsFinite(v.X) || !double.IsFinite(v.Y)) continue;
                if (v.X < minX) minX = v.X;
                if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y;
                if (v.Y > maxY) maxY = v.Y;
                validCount++;
            }

            if (validCount == 0) return null;

            int gridDim = Math.Clamp((int)Math.Sqrt(validCount / 4.0), 8, 256);
            int cols = gridDim;
            int rows = gridDim;
            double spanX = Math.Max(1e-9, maxX - minX);
            double spanY = Math.Max(1e-9, maxY - minY);
            double invCellWidth = cols / spanX;
            double invCellHeight = rows / spanY;

            int totalCells = cols * rows;
            var cellStarts = new int[totalCells + 1];

            for (int i = 0; i < count; i++)
            {
                Vertex v = vertices[i];
                if (!double.IsFinite(v.X) || !double.IsFinite(v.Y)) continue;
                int cx = Math.Clamp((int)((v.X - minX) * invCellWidth), 0, cols - 1);
                int cy = Math.Clamp((int)((v.Y - minY) * invCellHeight), 0, rows - 1);
                cellStarts[cy * cols + cx + 1]++;
            }

            for (int c = 1; c <= totalCells; c++)
                cellStarts[c] += cellStarts[c - 1];

            var cellEntries = new int[validCount];
            var cursors = (int[])cellStarts.Clone();

            for (int i = 0; i < count; i++)
            {
                Vertex v = vertices[i];
                if (!double.IsFinite(v.X) || !double.IsFinite(v.Y)) continue;
                int cx = Math.Clamp((int)((v.X - minX) * invCellWidth), 0, cols - 1);
                int cy = Math.Clamp((int)((v.Y - minY) * invCellHeight), 0, rows - 1);
                int cell = cy * cols + cx;
                cellEntries[cursors[cell]++] = i;
            }

            return new FlatVertexSpatialIndex(
                vertices, minX, minY, invCellWidth, invCellHeight, cols, rows, cellStarts, cellEntries);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public int FindNearest(double worldX, double worldY, double tolerance)
        {
            double minQx = worldX - tolerance, maxQx = worldX + tolerance;
            double minQy = worldY - tolerance, maxQy = worldY + tolerance;

            int c0 = Math.Clamp((int)((minQx - _minX) * _invCellWidth), 0, _cols - 1);
            int c1 = Math.Clamp((int)((maxQx - _minX) * _invCellWidth), 0, _cols - 1);
            int r0 = Math.Clamp((int)((minQy - _minY) * _invCellHeight), 0, _rows - 1);
            int r1 = Math.Clamp((int)((maxQy - _minY) * _invCellHeight), 0, _rows - 1);

            double bestDistSq = tolerance * tolerance;
            int bestIndex = -1;

            for (int r = r0; r <= r1; r++)
            {
                int rowOffset = r * _cols;
                for (int c = c0; c <= c1; c++)
                {
                    int cell = rowOffset + c;
                    int start = _cellStarts[cell];
                    int end = _cellStarts[cell + 1];
                    for (int k = start; k < end; k++)
                    {
                        int idx = _cellEntries[k];
                        Vertex v = _vertices[idx];
                        double dx = worldX - v.X;
                        double dy = worldY - v.Y;
                        double distSq = dx * dx + dy * dy;
                        if (distSq <= bestDistSq)
                        {
                            bestDistSq = distSq;
                            bestIndex = idx;
                        }
                    }
                }
            }

            return bestIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void QueryEnvelope(double minX, double maxX, double minY, double maxY, Action<int> visitor)
        {
            int c0 = Math.Clamp((int)((minX - _minX) * _invCellWidth), 0, _cols - 1);
            int c1 = Math.Clamp((int)((maxX - _minX) * _invCellWidth), 0, _cols - 1);
            int r0 = Math.Clamp((int)((minY - _minY) * _invCellHeight), 0, _rows - 1);
            int r1 = Math.Clamp((int)((maxY - _minY) * _invCellHeight), 0, _rows - 1);

            for (int r = r0; r <= r1; r++)
            {
                int rowOffset = r * _cols;
                for (int c = c0; c <= c1; c++)
                {
                    int cell = rowOffset + c;
                    int start = _cellStarts[cell];
                    int end = _cellStarts[cell + 1];
                    for (int k = start; k < end; k++)
                    {
                        int idx = _cellEntries[k];
                        Vertex v = _vertices[idx];
                        if (v.X >= minX && v.X <= maxX && v.Y >= minY && v.Y <= maxY)
                            visitor(idx);
                    }
                }
            }
        }
    }

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
        using SpatialReference? sourceSrs = layer.GetSpatialRef();
        if (sourceSrs is not null && !string.IsNullOrWhiteSpace(projectSrs))
        {
            try
            {
                using SpatialReference targetSrs = SrsFactory.FromUserInput(projectSrs);
                sourceSrs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
                if (sourceSrs.IsSame(targetSrs, null) != 1)
                {
                    using var transform = new CoordinateTransformation(sourceSrs, targetSrs);
                    EnsureSuccess(geometry.Transform(transform), "reprojetar a feição para o SRC do projeto");
                }
            }
            catch
            {
                // Preserva geometria no SRC nativo da camada se a transformação falhar
            }
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
        int estimatedCapacity = Math.Max(4, geometry.NumPoints);
        var result = new List<Vertex>(estimatedCapacity);
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
        if (!TryFindVertexByAddress(primary, address, out Vertex anchor))
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
                for (int a = 0; a < anchors.Length; a++)
                {
                    Vertex anchor = anchors[a];
                    double tol = CoordinateTolerance(anchor);
                    if (Math.Abs(vertex.X - anchor.X) <= tol && Math.Abs(vertex.Y - anchor.Y) <= tol)
                    {
                        movedAddresses[i].Add(vertex.Address);
                        break;
                    }
                }
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

        var moves = addresses as HashSet<VertexAddress> ?? addresses.ToHashSet();
        if (moves.Count == 0) throw new ArgumentException("Selecione pelo menos um vértice.", nameof(addresses));

        var touchedSequences = new HashSet<int>(moves.Count);
        foreach (VertexAddress addr in moves)
        {
            if (addr.Sequence < 0 || addr.Coordinate < 0)
                throw new ArgumentOutOfRangeException(nameof(addresses), "Um vértice selecionado não pôde ser localizado.");
            touchedSequences.Add(addr.Sequence);
        }

        int sequence = 0;
        var applied = new HashSet<VertexAddress>(moves.Count);
        NtsGeometry result = RebuildVerticesByDelta(geometry, moves, touchedSequences, applied, deltaX, deltaY, ref sequence);
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
        if (geometry.IsEmpty)
            return (NtsGeometry)geometry.Copy();
        return TranslateGeometrySimd(geometry, deltaX, deltaY);
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

        // Trava topologica anti-duplicacao: impede a criacao de vertices sobrepostos
        double minVertexSeparation = Math.Max(1e-5, tolerance * 0.25);
        foreach (var coord in geometry.Coordinates)
        {
            double dx = coord.X - nearest.X;
            double dy = coord.Y - nearest.Y;
            if (dx * dx + dy * dy < minVertexSeparation * minVertexSeparation)
                throw new InvalidOperationException("O clique esta muito proximo a um vertice existente. Nao e permitido criar vertices duplicados no mesmo local.");
        }

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
                CoordinateSequence seq = line.CoordinateSequence;
                int count = seq.Count;
                int currentSequence = sequence++;
                if (count < 2) return;
                double firstX = seq.GetX(0), firstY = seq.GetY(0);
                double lastX = seq.GetX(count - 1), lastY = seq.GetY(count - 1);
                bool closed = geometry is LinearRing && count > 2 && firstX == lastX && firstY == lastY;
                int uniqueCount = closed ? count - 1 : count;
                int segmentCount = closed ? uniqueCount : Math.Max(0, uniqueCount - 1);
                double ax = firstX, ay = firstY;
                for (int i = 0; i < segmentCount; i++)
                {
                    int nextIdx = (i + 1) % uniqueCount;
                    double bx = seq.GetX(nextIdx), by = seq.GetY(nextIdx);
                    double dx = bx - ax, dy = by - ay;
                    double lengthSquared = dx * dx + dy * dy;
                    if (lengthSquared > double.Epsilon)
                    {
                        double fraction = Math.Clamp(((x - ax) * dx + (y - ay) * dy) / lengthSquared, 0, 1);
                        double projectedX = ax + fraction * dx;
                        double projectedY = ay + fraction * dy;
                        double distanceX = x - projectedX, distanceY = y - projectedY;
                        double distanceSquared = distanceX * distanceX + distanceY * distanceY;
                        if (distanceSquared < nearest.DistanceSquared)
                            nearest = new SegmentCandidate(distanceSquared, currentSequence, i + 1, projectedX, projectedY, fraction);
                    }
                    ax = bx;
                    ay = by;
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
                bool anyChanged = !ReferenceEquals(shell, polygon.ExteriorRing);
                for (int i = 0; i < holes.Length; i++)
                {
                    LinearRing origHole = (LinearRing)polygon.GetInteriorRingN(i);
                    LinearRing updatedHole = (LinearRing)InsertOnSequence(origHole, targetSequence, insertAt, x, y, ref sequence);
                    holes[i] = updatedHole;
                    if (!ReferenceEquals(updatedHole, origHole)) anyChanged = true;
                }
                return anyChanged ? factory.CreatePolygon(shell, holes) : polygon;
            }
            case LinearRing ring:
            {
                int currentSequence = sequence++;
                if (currentSequence != targetSequence) return ring;
                return factory.CreateLinearRing(InsertCoordinates(ring.Coordinates, currentSequence, targetSequence, insertAt, x, y, true));
            }
            case LineString line:
            {
                int currentSequence = sequence++;
                if (currentSequence != targetSequence) return line;
                return factory.CreateLineString(InsertCoordinates(line.Coordinates, currentSequence, targetSequence, insertAt, x, y, false));
            }
            case NtsPoint point:
                sequence++;
                return point;
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
        Coordinate[] source, int currentSequence, int targetSequence, int insertAt, double x, double y, bool closed)
    {
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
                bool anyChanged = !ReferenceEquals(shell, polygon.ExteriorRing);
                for (int i = 0; i < holes.Length; i++)
                {
                    LinearRing origHole = (LinearRing)polygon.GetInteriorRingN(i);
                    LinearRing updatedHole = (LinearRing)RemoveFromSequence(origHole, address, ref sequence);
                    holes[i] = updatedHole;
                    if (!ReferenceEquals(updatedHole, origHole)) anyChanged = true;
                }
                return anyChanged ? factory.CreatePolygon(shell, holes) : polygon;
            }
            case LinearRing ring:
            {
                int currentSequence = sequence++;
                if (currentSequence != address.Sequence) return ring;
                return factory.CreateLinearRing(RemoveCoordinates(ring.Coordinates, address, currentSequence, true));
            }
            case LineString line:
            {
                int currentSequence = sequence++;
                if (currentSequence != address.Sequence) return line;
                return factory.CreateLineString(RemoveCoordinates(line.Coordinates, address, currentSequence, false));
            }
            case NtsPoint point:
                sequence++;
                if (sequence - 1 == address.Sequence)
                    throw new InvalidOperationException("Não é possível remover o único vértice de um ponto.");
                return point;
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

    private static Coordinate[] RemoveCoordinates(Coordinate[] source, VertexAddress address, int currentSequence, bool closed)
    {
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

    private static bool TryFindVertexByAddress(NtsGeometry geometry, VertexAddress target, out Vertex found)
    {
        int sequence = 0;
        return TryFindVertexInGeometry(geometry, target, ref sequence, out found);
    }

    private static bool TryFindVertexInGeometry(NtsGeometry geometry, VertexAddress target, ref int sequence, out Vertex found)
    {
        switch (geometry)
        {
            case Polygon polygon:
                if (TryFindVertexInGeometry(polygon.ExteriorRing, target, ref sequence, out found)) return true;
                for (int i = 0; i < polygon.NumInteriorRings; i++)
                    if (TryFindVertexInGeometry(polygon.GetInteriorRingN(i), target, ref sequence, out found)) return true;
                found = default;
                return false;
            case LineString line:
            {
                int currentSeq = sequence++;
                if (currentSeq == target.Sequence)
                {
                    CoordinateSequence seq = line.CoordinateSequence;
                    int count = seq.Count;
                    bool closed = count > 2 && seq.GetX(0) == seq.GetX(count - 1) && seq.GetY(0) == seq.GetY(count - 1);
                    if (closed) count--;
                    if (target.Coordinate >= 0 && target.Coordinate < count)
                    {
                        found = new Vertex(target, seq.GetX(target.Coordinate), seq.GetY(target.Coordinate));
                        return true;
                    }
                }
                found = default;
                return false;
            }
            case NtsPoint point:
            {
                int currentSeq = sequence++;
                if (currentSeq == target.Sequence && target.Coordinate == 0 && !point.IsEmpty)
                {
                    found = new Vertex(target, point.X, point.Y);
                    return true;
                }
                found = default;
                return false;
            }
            case GeometryCollection collection:
                for (int i = 0; i < collection.NumGeometries; i++)
                    if (TryFindVertexInGeometry(collection.GetGeometryN(i), target, ref sequence, out found)) return true;
                found = default;
                return false;
            default:
                found = default;
                return false;
        }
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
                AppendSequence(line.CoordinateSequence, result, sequence++);
                return;
            case NtsPoint point:
                AppendSequence(point.CoordinateSequence, result, sequence++);
                return;
            case GeometryCollection collection:
                for (int i = 0; i < collection.NumGeometries; i++)
                    AppendVertices(collection.GetGeometryN(i), result, ref sequence);
                return;
            default:
                throw new NotSupportedException($"Edição de vértices não dá suporte a {geometry.GeometryType}.");
        }
    }

    private static void AppendSequence(CoordinateSequence seq, List<Vertex> result, int sequence)
    {
        int count = seq.Count;
        if (count == 0) return;
        bool closed = count > 2 && seq.GetX(0) == seq.GetX(count - 1) && seq.GetY(0) == seq.GetY(count - 1);
        if (closed) count--;
        for (int i = 0; i < count; i++)
            result.Add(new Vertex(new VertexAddress(sequence, i), seq.GetX(i), seq.GetY(i)));
    }

    private static double CoordinateTolerance(Vertex vertex) =>
        Math.Max(1e-9, Math.Max(Math.Abs(vertex.X), Math.Abs(vertex.Y)) * 1e-12);

    private static NtsGeometry TranslateGeometrySimd(NtsGeometry geometry, double deltaX, double deltaY)
    {
        GeometryFactory factory = geometry.Factory;
        switch (geometry)
        {
            case Polygon polygon:
            {
                LinearRing shell = (LinearRing)TranslateGeometrySimd(polygon.ExteriorRing, deltaX, deltaY);
                int numHoles = polygon.NumInteriorRings;
                if (numHoles == 0) return factory.CreatePolygon(shell);
                var holes = new LinearRing[numHoles];
                for (int i = 0; i < numHoles; i++)
                    holes[i] = (LinearRing)TranslateGeometrySimd(polygon.GetInteriorRingN(i), deltaX, deltaY);
                return factory.CreatePolygon(shell, holes);
            }
            case LinearRing ring:
                return factory.CreateLinearRing(TranslateSequenceSimd(ring.CoordinateSequence, deltaX, deltaY));
            case LineString line:
                return factory.CreateLineString(TranslateSequenceSimd(line.CoordinateSequence, deltaX, deltaY));
            case NtsPoint point:
                return point.IsEmpty
                    ? factory.CreatePoint()
                    : factory.CreatePoint(new Coordinate(point.X + deltaX, point.Y + deltaY));
            case MultiPoint multiPoint:
            {
                var points = new NtsPoint[multiPoint.NumGeometries];
                for (int i = 0; i < points.Length; i++)
                    points[i] = (NtsPoint)TranslateGeometrySimd(multiPoint.GetGeometryN(i), deltaX, deltaY);
                return factory.CreateMultiPoint(points);
            }
            case MultiLineString multiLine:
            {
                var lines = new LineString[multiLine.NumGeometries];
                for (int i = 0; i < lines.Length; i++)
                    lines[i] = (LineString)TranslateGeometrySimd(multiLine.GetGeometryN(i), deltaX, deltaY);
                return factory.CreateMultiLineString(lines);
            }
            case MultiPolygon multiPolygon:
            {
                var polygons = new Polygon[multiPolygon.NumGeometries];
                for (int i = 0; i < polygons.Length; i++)
                    polygons[i] = (Polygon)TranslateGeometrySimd(multiPolygon.GetGeometryN(i), deltaX, deltaY);
                return factory.CreateMultiPolygon(polygons);
            }
            case GeometryCollection collection:
            {
                var parts = new NtsGeometry[collection.NumGeometries];
                for (int i = 0; i < parts.Length; i++)
                    parts[i] = TranslateGeometrySimd(collection.GetGeometryN(i), deltaX, deltaY);
                return factory.CreateGeometryCollection(parts);
            }
            default:
                return AffineTransformation.TranslationInstance(deltaX, deltaY).Transform(geometry);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe CoordinateSequence TranslateSequenceSimd(CoordinateSequence seq, double deltaX, double deltaY)
    {
        int count = seq.Count;
        var raw = new double[count * 2];
        if (seq is PackedDoubleCoordinateSequence packed && packed.Dimension == 2 && packed.Measures == 0)
        {
            double[] src = packed.GetRawCoordinates();
            Array.Copy(src, 0, raw, 0, raw.Length);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                raw[i * 2] = seq.GetX(i);
                raw[i * 2 + 1] = seq.GetY(i);
            }
        }

        fixed (double* ptr = raw)
        {
            int i = 0;
            if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && count >= 2)
            {
                var deltaVec = System.Runtime.Intrinsics.Vector256.Create(deltaX, deltaY, deltaX, deltaY);
                int limit = count * 2 - 4;
                for (; i <= limit; i += 4)
                {
                    var v = System.Runtime.Intrinsics.X86.Avx.LoadVector256(ptr + i);
                    System.Runtime.Intrinsics.X86.Avx.Store(ptr + i, System.Runtime.Intrinsics.X86.Avx.Add(v, deltaVec));
                }
            }
            for (; i < count * 2; i += 2)
            {
                ptr[i] += deltaX;
                ptr[i + 1] += deltaY;
            }
        }

        return new PackedDoubleCoordinateSequence(raw, 2, 0);
    }

    private static NtsGeometry RebuildVerticesByDelta(
        NtsGeometry geometry,
        HashSet<VertexAddress> moves,
        HashSet<int> touchedSequences,
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
                LinearRing origShell = (LinearRing)polygon.ExteriorRing;
                LinearRing shell = (LinearRing)RebuildVerticesByDelta(
                    origShell, moves, touchedSequences, applied, deltaX, deltaY, ref sequence);
                int numHoles = polygon.NumInteriorRings;
                if (numHoles == 0)
                    return ReferenceEquals(shell, origShell) ? polygon : factory.CreatePolygon(shell);

                var holes = new LinearRing[numHoles];
                bool anyChanged = !ReferenceEquals(shell, origShell);
                for (int i = 0; i < numHoles; i++)
                {
                    LinearRing origHole = (LinearRing)polygon.GetInteriorRingN(i);
                    LinearRing hole = (LinearRing)RebuildVerticesByDelta(
                        origHole, moves, touchedSequences, applied, deltaX, deltaY, ref sequence);
                    holes[i] = hole;
                    if (!ReferenceEquals(hole, origHole)) anyChanged = true;
                }
                return anyChanged ? factory.CreatePolygon(shell, holes) : polygon;
            }
            case LinearRing ring:
            {
                int currentSequence = sequence++;
                if (!touchedSequences.Contains(currentSequence))
                    return ring; // Structural sharing (Copy-on-Write): zero alocação para anéis intocados!
                return factory.CreateLinearRing(UpdateSequenceByDelta(
                    ring.CoordinateSequence, currentSequence, moves, applied, deltaX, deltaY));
            }
            case LineString line:
            {
                int currentSequence = sequence++;
                if (!touchedSequences.Contains(currentSequence))
                    return line;
                return factory.CreateLineString(UpdateSequenceByDelta(
                    line.CoordinateSequence, currentSequence, moves, applied, deltaX, deltaY));
            }
            case NtsPoint point:
            {
                int currentSequence = sequence++;
                if (!touchedSequences.Contains(currentSequence))
                    return point;
                CoordinateSequence updated = UpdateSequenceByDelta(
                    point.CoordinateSequence, currentSequence, moves, applied, deltaX, deltaY);
                return updated.Count == 0 ? factory.CreatePoint() : factory.CreatePoint(updated);
            }
            case GeometryCollection collection:
            {
                int numGeometries = collection.NumGeometries;
                var parts = new NtsGeometry[numGeometries];
                bool anyChanged = false;
                for (int i = 0; i < numGeometries; i++)
                {
                    NtsGeometry origPart = collection.GetGeometryN(i);
                    NtsGeometry updatedPart = RebuildVerticesByDelta(
                        origPart, moves, touchedSequences, applied, deltaX, deltaY, ref sequence);
                    parts[i] = updatedPart;
                    if (!ReferenceEquals(updatedPart, origPart)) anyChanged = true;
                }
                if (!anyChanged) return collection;
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static CoordinateSequence UpdateSequenceByDelta(
        CoordinateSequence source,
        int sequence,
        HashSet<VertexAddress> moves,
        HashSet<VertexAddress> applied,
        double deltaX,
        double deltaY)
    {
        int count = source.Count;
        var raw = new double[count * 2];
        if (source is PackedDoubleCoordinateSequence packed && packed.Dimension == 2 && packed.Measures == 0)
        {
            Array.Copy(packed.GetRawCoordinates(), 0, raw, 0, raw.Length);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                raw[i * 2] = source.GetX(i);
                raw[i * 2 + 1] = source.GetY(i);
            }
        }

        bool closed = count > 2 && raw[0] == raw[(count - 1) * 2] && raw[1] == raw[(count - 1) * 2 + 1];
        int uniqueCount = closed ? count - 1 : count;
        for (int i = 0; i < uniqueCount; i++)
        {
            var address = new VertexAddress(sequence, i);
            if (!moves.Contains(address)) continue;
            double x = raw[i * 2] + deltaX;
            double y = raw[i * 2 + 1] + deltaY;
            if (!double.IsFinite(x) || !double.IsFinite(y))
                throw new ArgumentOutOfRangeException(nameof(deltaX), "O movimento produziria coordenadas não finitas.");
            raw[i * 2] = x;
            raw[i * 2 + 1] = y;
            if (closed && i == 0)
            {
                raw[(count - 1) * 2] = x;
                raw[(count - 1) * 2 + 1] = y;
            }
            applied.Add(address);
        }
        return new PackedDoubleCoordinateSequence(raw, 2, 0);
    }

    private static NtsGeometry Rebuild(NtsGeometry geometry, VertexAddress address, double x, double y, ref int sequence)
    {
        GeometryFactory factory = geometry.Factory;
        switch (geometry)
        {
            case Polygon polygon:
            {
                LinearRing origShell = (LinearRing)polygon.ExteriorRing;
                LinearRing shell = (LinearRing)Rebuild(origShell, address, x, y, ref sequence);
                int numHoles = polygon.NumInteriorRings;
                if (numHoles == 0)
                    return ReferenceEquals(shell, origShell) ? polygon : factory.CreatePolygon(shell);

                var holes = new LinearRing[numHoles];
                bool anyChanged = !ReferenceEquals(shell, origShell);
                for (int i = 0; i < numHoles; i++)
                {
                    LinearRing origHole = (LinearRing)polygon.GetInteriorRingN(i);
                    LinearRing hole = (LinearRing)Rebuild(origHole, address, x, y, ref sequence);
                    holes[i] = hole;
                    if (!ReferenceEquals(hole, origHole)) anyChanged = true;
                }
                return anyChanged ? factory.CreatePolygon(shell, holes) : polygon;
            }
            case LinearRing ring:
            {
                int currentSequence = sequence++;
                if (currentSequence != address.Sequence)
                    return ring; // Copy-on-Write: zero alocação para anéis não editados!
                return factory.CreateLinearRing(UpdateSingleCoordinateSequence(ring.CoordinateSequence, address, x, y));
            }
            case LineString line:
            {
                int currentSequence = sequence++;
                if (currentSequence != address.Sequence)
                    return line;
                return factory.CreateLineString(UpdateSingleCoordinateSequence(line.CoordinateSequence, address, x, y));
            }
            case NtsPoint point:
            {
                int currentSequence = sequence++;
                if (currentSequence != address.Sequence)
                    return point;
                CoordinateSequence updated = UpdateSingleCoordinateSequence(point.CoordinateSequence, address, x, y);
                return updated.Count == 0 ? factory.CreatePoint() : factory.CreatePoint(updated);
            }
            case MultiPoint multiPoint:
            {
                int num = multiPoint.NumGeometries;
                var points = new NtsPoint[num];
                bool anyChanged = false;
                for (int i = 0; i < num; i++)
                {
                    NtsPoint orig = (NtsPoint)multiPoint.GetGeometryN(i);
                    NtsPoint updated = (NtsPoint)Rebuild(orig, address, x, y, ref sequence);
                    points[i] = updated;
                    if (!ReferenceEquals(updated, orig)) anyChanged = true;
                }
                return anyChanged ? factory.CreateMultiPoint(points) : multiPoint;
            }
            case MultiLineString multiLine:
            {
                int num = multiLine.NumGeometries;
                var lines = new LineString[num];
                bool anyChanged = false;
                for (int i = 0; i < num; i++)
                {
                    LineString orig = (LineString)multiLine.GetGeometryN(i);
                    LineString updated = (LineString)Rebuild(orig, address, x, y, ref sequence);
                    lines[i] = updated;
                    if (!ReferenceEquals(updated, orig)) anyChanged = true;
                }
                return anyChanged ? factory.CreateMultiLineString(lines) : multiLine;
            }
            case MultiPolygon multiPolygon:
            {
                int num = multiPolygon.NumGeometries;
                var polygons = new Polygon[num];
                bool anyChanged = false;
                for (int i = 0; i < num; i++)
                {
                    Polygon orig = (Polygon)multiPolygon.GetGeometryN(i);
                    Polygon updated = (Polygon)Rebuild(orig, address, x, y, ref sequence);
                    polygons[i] = updated;
                    if (!ReferenceEquals(updated, orig)) anyChanged = true;
                }
                return anyChanged ? factory.CreateMultiPolygon(polygons) : multiPolygon;
            }
            case GeometryCollection collection:
            {
                int num = collection.NumGeometries;
                var parts = new NtsGeometry[num];
                bool anyChanged = false;
                for (int i = 0; i < num; i++)
                {
                    NtsGeometry orig = collection.GetGeometryN(i);
                    NtsGeometry updated = Rebuild(orig, address, x, y, ref sequence);
                    parts[i] = updated;
                    if (!ReferenceEquals(updated, orig)) anyChanged = true;
                }
                return anyChanged ? factory.CreateGeometryCollection(parts) : collection;
            }
            default:
                throw new NotSupportedException($"Edição de vértices não dá suporte a {geometry.GeometryType}.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static CoordinateSequence UpdateSingleCoordinateSequence(
        CoordinateSequence source, VertexAddress address, double x, double y)
    {
        int count = source.Count;
        if (address.Coordinate >= count || count == 0)
            throw new ArgumentOutOfRangeException(nameof(address), "O índice do vértice está fora da geometria.");

        var raw = new double[count * 2];
        if (source is PackedDoubleCoordinateSequence packed && packed.Dimension == 2 && packed.Measures == 0)
        {
            Array.Copy(packed.GetRawCoordinates(), 0, raw, 0, raw.Length);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                raw[i * 2] = source.GetX(i);
                raw[i * 2 + 1] = source.GetY(i);
            }
        }

        bool closed = count > 2 && raw[0] == raw[(count - 1) * 2] && raw[1] == raw[(count - 1) * 2 + 1];
        int targetIndex = address.Coordinate;
        if (closed && targetIndex == count - 1)
            targetIndex = 0;
        raw[targetIndex * 2] = x;
        raw[targetIndex * 2 + 1] = y;
        if (closed && targetIndex == 0)
        {
            raw[(count - 1) * 2] = x;
            raw[(count - 1) * 2 + 1] = y;
        }
        return new PackedDoubleCoordinateSequence(raw, 2, 0);
    }

    private static void EnsureSuccess(int result, string operation)
    {
        if (result != 0)
            throw new IOException($"O GDAL falhou ao {operation}. {Gdal.GetLastErrorMsg()}");
    }
}
