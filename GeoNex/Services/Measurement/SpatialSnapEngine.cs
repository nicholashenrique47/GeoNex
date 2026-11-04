using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using NetTopologySuite.Geometries;

namespace GeoNex.Services.Measurement;

public enum SnapType
{
    None,
    Vertex,
    Midpoint,
    NearestEdge
}

public readonly struct SnapPointResult
{
    public bool HasSnap { get; }
    public Coordinate Point { get; }
    public SnapType Type { get; }
    public double ScreenDistancePixels { get; }
    public string LayerName { get; }

    public SnapPointResult(bool hasSnap, Coordinate point, SnapType type, double screenDistancePixels, string layerName = "")
    {
        HasSnap = hasSnap;
        Point = point;
        Type = type;
        ScreenDistancePixels = screenDistancePixels;
        LayerName = layerName;
    }

    public static SnapPointResult Empty => new(false, new Coordinate(0, 0), SnapType.None, double.MaxValue, string.Empty);
}

/// <summary>
/// Motor de atração magnética espacial com indexação de grade uniforme O(1) de alta performance.
/// </summary>
public sealed class SpatialSnapEngine
{
    private readonly ConcurrentDictionary<long, List<(Coordinate Coord, string LayerName)>> _vertexGrid = new();
    private readonly List<(Coordinate P1, Coordinate P2, string LayerName)> _segments = new();
    private double _cellSize = 25.0; // Espaço do mundo (metros ou unidades do CRS)

    public void Clear()
    {
        _vertexGrid.Clear();
        _segments.Clear();
    }

    /// <summary>
    /// Indexa conjuntos de geometrias divididos por camada para atração magnética ultrarrápida.
    /// </summary>
    public void PopulateIndex(IEnumerable<(string LayerName, IEnumerable<Geometry> Geometries)> layers, double metersPerPixel, double snapTolerancePixels = 15.0)
    {
        Clear();
        _cellSize = Math.Max(1.0, snapTolerancePixels * metersPerPixel * 1.5);

        foreach (var (layerName, geoms) in layers)
        {
            if (geoms == null) continue;
            foreach (var geom in geoms)
            {
                if (geom == null || geom.IsEmpty) continue;
                IndexGeometry(geom, layerName);
            }
        }
    }

    private void IndexGeometry(Geometry geom, string layerName)
    {
        var coords = geom.Coordinates;
        for (int i = 0; i < coords.Length; i++)
        {
            var pt = coords[i];
            long key = ComputeKey(pt.X, pt.Y, _cellSize);
            var bucket = _vertexGrid.GetOrAdd(key, _ => new List<(Coordinate, string)>(4));
            lock (bucket)
            {
                bucket.Add((pt, layerName));
            }

            if (i > 0)
            {
                lock (_segments)
                {
                    if (_segments.Count < 20000)
                    {
                        _segments.Add((coords[i - 1], pt, layerName));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Executa consulta em tempo constante O(1) inspecionando o bloco 3x3 de células ao redor do cursor.
    /// </summary>
    public SnapPointResult QuerySnap(
        Coordinate cursorWorld,
        double metersPerPixel,
        double tolerancePixels = 15.0,
        bool snapVertices = true,
        bool snapEdges = true)
    {
        if (_vertexGrid.IsEmpty && _segments.Count == 0) return SnapPointResult.Empty;

        double maxDistWorld = tolerancePixels * metersPerPixel;
        double bestDistSq = maxDistWorld * maxDistWorld;
        Coordinate? bestPoint = null;
        SnapType bestType = SnapType.None;
        string bestLayer = string.Empty;

        // 1. Prioridade A: Vértices nas 9 células vizinhas
        if (snapVertices)
        {
            int cx = (int)Math.Floor(cursorWorld.X / _cellSize);
            int cy = (int)Math.Floor(cursorWorld.Y / _cellSize);

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    long key = PackKey(cx + dx, cy + dy);
                    if (_vertexGrid.TryGetValue(key, out var bucket))
                    {
                        lock (bucket)
                        {
                            for (int i = 0; i < bucket.Count; i++)
                            {
                                var item = bucket[i];
                                double distX = item.Coord.X - cursorWorld.X;
                                double distY = item.Coord.Y - cursorWorld.Y;
                                double dSq = distX * distX + distY * distY;

                                if (dSq < bestDistSq)
                                {
                                    bestDistSq = dSq;
                                    bestPoint = item.Coord;
                                    bestType = SnapType.Vertex;
                                    bestLayer = item.LayerName;
                                }
                            }
                        }
                    }
                }
            }
        }

        // Se encontrou vértice dentro da tolerância, retorna imediatamente
        if (bestPoint != null)
        {
            double px = Math.Sqrt(bestDistSq) / metersPerPixel;
            return new SnapPointResult(true, bestPoint, bestType, px, bestLayer);
        }

        // 2. Prioridade B: Ponto mais próximo na aresta ou ponto médio
        if (snapEdges && _segments.Count > 0)
        {
            lock (_segments)
            {
                for (int i = 0; i < _segments.Count; i++)
                {
                    var seg = _segments[i];
                    var projected = ProjectPointOnSegment(cursorWorld, seg.P1, seg.P2, out double fraction);
                    double distX = projected.X - cursorWorld.X;
                    double distY = projected.Y - cursorWorld.Y;
                    double dSq = distX * distX + distY * distY;

                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestPoint = projected;
                        bestType = (Math.Abs(fraction - 0.5) < 0.05) ? SnapType.Midpoint : SnapType.NearestEdge;
                        bestLayer = seg.LayerName;
                    }
                }
            }
        }

        if (bestPoint != null)
        {
            double px = Math.Sqrt(bestDistSq) / metersPerPixel;
            return new SnapPointResult(true, bestPoint, bestType, px, bestLayer);
        }

        return SnapPointResult.Empty;
    }

    private static Coordinate ProjectPointOnSegment(Coordinate p, Coordinate a, Coordinate b, out double fraction)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq <= 1e-12)
        {
            fraction = 0;
            return new Coordinate(a.X, a.Y);
        }

        fraction = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0.0, 1.0);
        return new Coordinate(a.X + fraction * dx, a.Y + fraction * dy);
    }

    private static long ComputeKey(double x, double y, double sz) => PackKey((int)Math.Floor(x / sz), (int)Math.Floor(y / sz));
    private static long PackKey(int x, int y) => ((long)x << 32) | (uint)y;
}
