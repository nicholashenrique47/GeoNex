using NetTopologySuite.Geometries;
using NetTopologySuite.Index;
using NetTopologySuite.Index.Strtree;
using SkiaSharp;

namespace GeoNex.Services;

public readonly record struct CategorizedPathChunk(string Category, SKPath Path, SKRect Bounds, int DrawOrder);

/// <summary>Reusable visitor so a frame can collect spatial matches without a result-list allocation per query.</summary>
public sealed class CategorizedPathQueryBuffer : IItemVisitor<CategorizedPathChunk>
{
    private readonly List<CategorizedPathChunk> _items = new();
    public IReadOnlyList<CategorizedPathChunk> Items => _items;

    public void VisitItem(CategorizedPathChunk item) => _items.Add(item);

    internal void Reset() => _items.Clear();

    internal void SortByDrawOrder() => _items.Sort(static (left, right) => left.DrawOrder.CompareTo(right.DrawOrder));
}

/// <summary>Spatially culls precompiled categorized path chunks while preserving category draw order.</summary>
public sealed class CategorizedPathSpatialIndex
{
    private readonly STRtree<CategorizedPathChunk> _index = new();

    public int Count { get; }

    public CategorizedPathSpatialIndex(IReadOnlyDictionary<string, List<SKPath>> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        int drawOrder = 0;
        int indexedCount = 0;
        foreach ((string category, List<SKPath> paths) in categories)
        {
            foreach (SKPath path in paths)
            {
                int order = drawOrder++;
                if (path.IsEmpty) continue;
                SKRect bounds = path.Bounds;
                if (!float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Top) ||
                    !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Bottom))
                    continue;

                _index.Insert(new Envelope(bounds.Left, bounds.Right, bounds.Top, bounds.Bottom),
                    new CategorizedPathChunk(category, path, bounds, order));
                indexedCount++;
            }
        }

        _index.Build();
        Count = indexedCount;
    }

    public void Query(SKRect viewport, CategorizedPathQueryBuffer destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Reset();
        if (viewport.IsEmpty || !float.IsFinite(viewport.Left) || !float.IsFinite(viewport.Top) ||
            !float.IsFinite(viewport.Right) || !float.IsFinite(viewport.Bottom))
            return;

        _index.Query(new Envelope(viewport.Left, viewport.Right, viewport.Top, viewport.Bottom), destination);
        destination.SortByDrawOrder();
    }
}
