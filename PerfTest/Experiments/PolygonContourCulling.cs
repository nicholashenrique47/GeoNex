using System.Buffers;
using SkiaSharp;

namespace GeoNex.Services;

/// <summary>Discards only closed linear contours entirely outside a conservative paint extent.</summary>
internal static class PolygonContourCulling
{
    public static SKPath? Cull(SKPath source, SKRect extent, CancellationToken token = default, bool deduplicate = false)
    {
        if (source.FillType is not (SKPathFillType.Winding or SKPathFillType.EvenOdd) ||
            extent.IsEmpty || !float.IsFinite(extent.Left) || !float.IsFinite(extent.Top) ||
            !float.IsFinite(extent.Right) || !float.IsFinite(extent.Bottom)) return null;
        var result = new SKPath { FillType = source.FillType };
        SKPoint[] buffer = ArrayPool<SKPoint>.Shared.Rent(256);
        var points = new SKPoint[4];
        int count = 0, discarded = 0, verbs = 0;
        var seen = new Dictionary<int, List<SKPoint[]>>();
        float minX = 0, maxX = 0, minY = 0, maxY = 0;
        try
        {
            using var iterator = source.CreateRawIterator();
            while (true)
            {
                if ((verbs++ & 4095) == 0) token.ThrowIfCancellationRequested();
                SKPathVerb verb = iterator.Next(points);
                if (verb == SKPathVerb.Done) break;
                if (verb == SKPathVerb.Close)
                {
                    if (count == 0) continue;
                    if (maxX < extent.Left || minX > extent.Right || maxY < extent.Top || minY > extent.Bottom)
                        discarded += count;
                    else
                    {
                        bool duplicate = false;
                        if (deduplicate)
                        {
                            var hash = new HashCode(); hash.Add(count);
                            for (int i = 0; i < count; i++) hash.Add(buffer[i]);
                            int key = hash.ToHashCode();
                            if (!seen.TryGetValue(key, out var candidates)) seen[key] = candidates = new();
                            foreach (var candidate in candidates)
                                if (buffer.AsSpan(0, count).SequenceEqual(candidate)) { duplicate = true; break; }
                            if (!duplicate) candidates.Add(buffer.AsSpan(0, count).ToArray());
                        }
                        if (duplicate) discarded += count;
                        else result.AddPoly(buffer.AsSpan(0, count), true);
                    }
                    count = 0;
                    continue;
                }
                // Curves and open contours retain the original path/painting semantics.
                if (verb is not (SKPathVerb.Move or SKPathVerb.Line) || (verb == SKPathVerb.Move && count != 0))
                    return null;
                SKPoint point = points[verb == SKPathVerb.Move ? 0 : 1];
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return null;
                if (count == buffer.Length)
                {
                    var grown = ArrayPool<SKPoint>.Shared.Rent(checked(buffer.Length * 2));
                    buffer.AsSpan(0, count).CopyTo(grown);
                    ArrayPool<SKPoint>.Shared.Return(buffer);
                    buffer = grown;
                }
                buffer[count++] = point;
                if (count == 1) { minX = maxX = point.X; minY = maxY = point.Y; }
                else { minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X); minY = Math.Min(minY, point.Y); maxY = Math.Max(maxY, point.Y); }
            }
            if (count != 0 || discarded == 0) return null;
            token.ThrowIfCancellationRequested();
            var culled = result; result = null;
            return culled;
        }
        finally { result?.Dispose(); ArrayPool<SKPoint>.Shared.Return(buffer); }
    }
}
