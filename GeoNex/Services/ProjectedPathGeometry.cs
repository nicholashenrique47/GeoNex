using System.Buffers;
using SkiaSharp;

namespace GeoNex.Services;

// Immutable projected doubles: a new camera rounds each vertex exactly as a fresh
// GDAL result would, without repeating projection or accumulating float offsets.
public sealed class ProjectedPathGeometry
{
    private readonly record struct Ring(int Block, int Start, int Count, bool Closed);
    private readonly record struct Block(double[] X, double[] Y);
    private readonly Ring[] _rings;
    private readonly Block[] _blocks;
    private readonly int _maximumRing;
    private ProjectedPathGeometry(Ring[] rings, Block[] blocks, int maximumRing, long bytes, double baseX, double baseY)
    { _rings = rings; _blocks = blocks; _maximumRing = maximumRing; RetainedBytes = bytes; BaseX = baseX; BaseY = baseY; }
    public long RetainedBytes { get; }
    public double BaseX { get; }
    public double BaseY { get; }

    public SKPath CreatePath(SKPoint origin, SKPathFillType fillType, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = new SKPath { FillType = fillType };
        SKPoint[]? points = null;
        try
        {
            points = ArrayPool<SKPoint>.Shared.Rent(_maximumRing);
            double ox = BaseX + (double)origin.X, oy = BaseY - (double)origin.Y;
            foreach (var ring in _rings)
            {
                token.ThrowIfCancellationRequested();
                var block = _blocks[ring.Block];
                for (int i = 0; i < ring.Count; i++)
                {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    points[i] = new SKPoint((float)(block.X[ring.Start + i] - ox),
                        -(float)(block.Y[ring.Start + i] - oy));
                }
                path.AddPoly(points.AsSpan(0, ring.Count), ring.Closed);
            }
            token.ThrowIfCancellationRequested();
            return path;
        }
        catch { path.Dispose(); throw; }
        finally { if (points != null) ArrayPool<SKPoint>.Shared.Return(points); }
    }

    public sealed class Builder
    {
        private readonly long _budget;
        private readonly double _baseX, _baseY;
        private List<Ring>? _rings = new();
        private List<Block>? _blocks = new();
        private int _used, _maximumRing;
        private long _bytes = 256;
        public Builder(long budget, double baseX, double baseY)
        { _budget = Math.Clamp(budget, 0, 32L * 1024 * 1024); _baseX = baseX; _baseY = baseY; }
        public void Disable() { _rings = null; _blocks = null; }
        public void Append(double[] x, double[] y, int start, int count, bool closed, CancellationToken token)
        {
            if (_rings == null) return;
            if (start < 0 || count < 0 || start > x.Length - count || start > y.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
            // Ring struct is 16 bytes. Charge worst-case list capacity (2x) plus
            // its final immutable copy before adding anything to the capture.
            const int ringBytes = 48;
            if (count == 0 || ringBytes > _budget - _bytes) { Disable(); return; }
            try
            {
                if (_blocks!.Count == 0 || _blocks[^1].X.Length - _used < count)
                {
                    // Pack small rings together instead of allocating two arrays
                    // for every parcel. Full allocated capacity counts as retained.
                    long availablePoints = (_budget - _bytes - ringBytes - 128) / 16;
                    if (availablePoints < count) { Disable(); return; }
                    int capacity = (int)Math.Min(Math.Max(16_384, count), availablePoints);
                    var next = new Block(new double[capacity], new double[capacity]);
                    _blocks.Add(next);
                    _bytes += capacity * 16L + 128; // Arrays and both block tables.
                    _used = 0;
                }
                var block = _blocks[^1];
                for (int i = 0; i < count; i++)
                {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    double px = x[start + i], py = y[start + i];
                    if (!double.IsFinite(px) || !double.IsFinite(py)) { Disable(); return; }
                    block.X[_used + i] = px; block.Y[_used + i] = py;
                }
                _rings.Add(new Ring(_blocks.Count - 1, _used, count, closed));
                _used += count;
                _maximumRing = Math.Max(_maximumRing, count);
                _bytes += ringBytes;
            }
            catch (OutOfMemoryException) { Disable(); }
        }
        public ProjectedPathGeometry? Build()
        {
            try
            {
                return _rings is { Count: > 0 }
                    ? new ProjectedPathGeometry(_rings.ToArray(), _blocks!.ToArray(), _maximumRing, _bytes, _baseX, _baseY) : null;
            }
            catch (OutOfMemoryException) { return null; }
            finally { Disable(); }
        }
    }
}
