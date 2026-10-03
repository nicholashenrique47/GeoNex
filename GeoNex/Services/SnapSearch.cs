using SkiaSharp;

namespace GeoNex.Services;

public enum SnapKind { Vertex, Midpoint, Edge, Intersection }

/// <summary>One nearest-candidate search across independent path contours.</summary>
public sealed class SnapSearch
{
    public readonly record struct Candidate(SKPoint Point, SnapKind Kind, double DistanceSquared);
    private readonly SKPoint _cursor;
    private readonly double _radiusSquared;
    private readonly bool _vertices, _edges, _midpoints, _intersections;
    private readonly SKPoint[] _segment = new SKPoint[4];
    private readonly List<(SKPoint A, SKPoint B)> _nearbySegments = new();
    private const int MaxIntersectionSegments = 512;
    public Candidate? Best { get; private set; }

    public SnapSearch(SKPoint cursor, float radius, bool vertices, bool edges, bool midpoints = false,
        bool intersections = false)
    {
        if (!float.IsFinite(cursor.X) || !float.IsFinite(cursor.Y) || !float.IsFinite(radius) || radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius), "Snap requires a finite cursor and positive radius.");
        _cursor = cursor;
        _radiusSquared = (double)radius * radius;
        _vertices = vertices; _edges = edges; _midpoints = midpoints; _intersections = intersections;
    }

    public void AddVertex(SKPoint point)
    {
        if (_vertices) Consider(point, SnapKind.Vertex);
    }

    private void Consider(SKPoint point, SnapKind kind)
    {
        double dx = (double)point.X - _cursor.X, dy = (double)point.Y - _cursor.Y;
        double distance = dx * dx + dy * dy;
        if (!double.IsFinite(distance) || distance > _radiusSquared) return;
        if (Best is not { } current || distance < current.DistanceSquared ||
            (distance == current.DistanceSquared && kind < current.Kind))
            Best = new(point, kind, distance);
    }

    private void AddSegment(SKPoint a, SKPoint b)
    {
        AddVertex(a); AddVertex(b);
        double dx = (double)b.X - a.X, dy = (double)b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (!(lengthSquared > 0) || !double.IsFinite(lengthSquared)) return;
        if (_midpoints) Consider(new((float)(a.X + dx * 0.5), (float)(a.Y + dy * 0.5)), SnapKind.Midpoint);
        if (_edges)
        {
            double fraction = Math.Clamp((((double)_cursor.X - a.X) * dx +
                ((double)_cursor.Y - a.Y) * dy) / lengthSquared, 0, 1);
            Consider(new((float)(a.X + fraction * dx), (float)(a.Y + fraction * dy)), SnapKind.Edge);
        }
        if (_intersections && _nearbySegments.Count < MaxIntersectionSegments && SegmentNearCursor(a, b))
            _nearbySegments.Add((a, b));
    }

    private bool SegmentNearCursor(SKPoint a, SKPoint b)
    {
        float left = MathF.Min(a.X, b.X), right = MathF.Max(a.X, b.X);
        float top = MathF.Min(a.Y, b.Y), bottom = MathF.Max(a.Y, b.Y);
        float radius = (float)Math.Sqrt(_radiusSquared);
        return right >= _cursor.X - radius && left <= _cursor.X + radius &&
            bottom >= _cursor.Y - radius && top <= _cursor.Y + radius;
    }

    /// <summary>
    /// Computes crossings only among nearby segments. Endpoints are already
    /// represented as vertices, so intersections lose endpoint ties naturally.
    /// </summary>
    public void CompleteIntersections()
    {
        if (!_intersections || _nearbySegments.Count < 2) return;
        for (int i = 0; i < _nearbySegments.Count - 1; i++)
        {
            var first = _nearbySegments[i];
            double rx = first.B.X - first.A.X, ry = first.B.Y - first.A.Y;
            for (int j = i + 1; j < _nearbySegments.Count; j++)
            {
                var second = _nearbySegments[j];
                double sx = second.B.X - second.A.X, sy = second.B.Y - second.A.Y;
                double denominator = rx * sy - ry * sx;
                if (denominator == 0) continue;
                double qx = second.A.X - first.A.X, qy = second.A.Y - first.A.Y;
                double t = (qx * sy - qy * sx) / denominator;
                double u = (qx * ry - qy * rx) / denominator;
                if (t < 0 || t > 1 || u < 0 || u > 1) continue;
                Consider(new((float)(first.A.X + t * rx), (float)(first.A.Y + t * ry)), SnapKind.Intersection);
            }
        }
    }

    /// <summary>Includes an unfinished sketch without creating a native path.</summary>
    public void AddPolyline(IReadOnlyList<SKPoint> points, bool close = false)
    {
        for (int i = 0; i < points.Count; i++)
        {
            if (i == 0) AddVertex(points[i]);
            else AddSegment(points[i - 1], points[i]);
        }
        if (close && points.Count > 2) AddSegment(points[^1], points[0]);
    }

    /// <summary>
    /// Visits each verb once, with constant scratch memory. Move starts a new
    /// part; Close tests its real closing edge. Curve control points and chords
    /// are not treated as linear GIS edges.
    /// </summary>
    public void AddPath(SKPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var iterator = path.CreateRawIterator();
        SKPoint start = default, last = default;
        bool contour = false;
        for (var verb = iterator.Next(_segment); verb != SKPathVerb.Done; verb = iterator.Next(_segment))
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    start = last = _segment[0]; contour = true; AddVertex(last); break;
                case SKPathVerb.Line:
                    AddSegment(_segment[0], _segment[1]); last = _segment[1]; break;
                case SKPathVerb.Close:
                    if (contour) AddSegment(last, start);
                    last = start; break;
                case SKPathVerb.Quad:
                case SKPathVerb.Conic:
                    last = _segment[2]; AddVertex(last); break;
                case SKPathVerb.Cubic:
                    last = _segment[3]; AddVertex(last); break;
            }
        }
    }
}
