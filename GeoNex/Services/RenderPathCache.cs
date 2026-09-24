using SkiaSharp;

namespace GeoNex.Services;

/// <summary>Bounded, shared geometry cache. Preview and final paths never replace each other.</summary>
public sealed class RenderPathCache : IDisposable
{
    public const long DefaultBudgetBytes = 128L * 1024 * 1024;
    private const int MaximumEntries = 64;
    private readonly object _gate = new();
    private long _budget;
    private readonly Func<long>? _budgetProvider;
    private readonly Dictionary<(long Owner, bool Interactive), Entry> _entries = new();
    private long _bytes, _clock;
    private bool _disposed;

    private sealed record Entry(SKPath Path, SKRect Coverage, float Zoom, bool Compact, long Bytes, bool ScaleIndependent, SKPoint? Origin)
    {
        public long LastUse { get; set; }
        public ProjectedPathGeometry? Projected { get; init; }
    }

    public RenderPathCache(long budgetBytes = DefaultBudgetBytes, Func<long>? budgetProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _budget = budgetBytes;
        _budgetProvider = budgetProvider;
    }

    // Conservative accounting for retained point/verb storage, not process RSS.
    // Caller-owned COW snapshots and Skia drawing scratch allocations are outside this budget.
    public static long EstimateBytes(SKPath path) => 512L + path.PointCount * 16L + path.VerbCount * 8L;
    public long RetainedBytes { get { lock (_gate) return _bytes; } }
    public long BudgetBytes { get { lock (_gate) return _budget; } }

    public bool TryGet(long owner, SKRect viewport, float zoom, bool interactive, bool compact, out SKPath? path,
        SKPoint? origin = null)
    {
        lock (_gate)
        {
            path = null;
            RefreshBudget();
            if (_disposed || !Valid(viewport, zoom) ||
                !_entries.TryGetValue((owner, interactive), out var entry) ||
                entry.Origin != origin ||
                (!entry.ScaleIndependent && entry.Zoom != zoom) ||
                (entry.ScaleIndependent && (zoom / entry.Zoom > 4 || zoom / entry.Zoom < .25f)) ||
                entry.Compact != compact ||
                viewport.Left < entry.Coverage.Left || viewport.Top < entry.Coverage.Top ||
                viewport.Right > entry.Coverage.Right || viewport.Bottom > entry.Coverage.Bottom) return false;
            path = new SKPath(entry.Path);
            entry.LastUse = ++_clock;
            return true;
        }
    }

    public void Store(long owner, SKPath path, SKRect coverage, float zoom, bool interactive, bool compact,
        bool scaleIndependent = false, SKPoint? origin = null, ProjectedPathGeometry? projected = null)
    {
        if (interactive || compact || !scaleIndependent || !origin.HasValue) projected = null;
        long bytes = EstimateBytes(path) + (projected?.RetainedBytes ?? 0);
        lock (_gate)
        {
            if (_disposed) return;
            RefreshBudget();
            var key = (owner, interactive);
            // Spare-memory pressure may disable doubles while still allowing the
            // original cheap path cache. Never sacrifice that existing fast path.
            if (bytes > _budget && projected != null)
            { bytes -= projected.RetainedBytes; projected = null; }
            // Never retain a stale entry for this quality after an uncacheable replacement.
            if (!Valid(coverage, zoom) || (origin.HasValue &&
                (!float.IsFinite(origin.Value.X) || !float.IsFinite(origin.Value.Y))) || bytes > _budget)
            {
                Remove(key);
                return;
            }
            var copy = new SKPath(path);
            try
            {
                Remove(key);
                while (_entries.Count >= MaximumEntries || _bytes > _budget - bytes)
                {
                    // Evict cheap previews before expensive final geometry, then least recently used.
                    var victim = FindVictim();
                    // A preview may be drawn without caching; do not evict finals just to retain it.
                    if (interactive && !victim.Interactive) return;
                    Remove(victim);
                }
                _entries.Add(key, new Entry(copy, coverage, zoom, compact, bytes,
                    scaleIndependent && !interactive && !compact, origin) { LastUse = ++_clock, Projected = projected });
                _bytes += bytes;
                copy = null;
            }
            finally { copy?.Dispose(); }
        }
    }

    // Rebase retained doubles, never an already rounded SKPath. Release the cache
    // lock before rebuilding; the immutable managed geometry survives eviction.
    public bool TryGetProjected(long owner, SKRect viewport, SKPoint cameraOrigin, float zoom,
        double baseX, double baseY, out SKPath? path, CancellationToken token = default)
    {
        ProjectedPathGeometry geometry;
        SKPathFillType fillType;
        lock (_gate)
        {
            path = null;
            RefreshBudget();
            if (_disposed || !Valid(viewport, zoom) ||
                !float.IsFinite(cameraOrigin.X) || !float.IsFinite(cameraOrigin.Y) ||
                !_entries.TryGetValue((owner, false), out var entry) ||
                !entry.ScaleIndependent || entry.Compact || !entry.Origin.HasValue || entry.Projected == null ||
                entry.Projected.BaseX != baseX || entry.Projected.BaseY != baseY ||
                zoom / entry.Zoom > 4 || zoom / entry.Zoom < .25f) return false;
            var anchor = entry.Origin.Value;
            double dx = (double)cameraOrigin.X - anchor.X, dy = (double)cameraOrigin.Y - anchor.Y;
            // Compare in double: adding offsets to float bounds can round an
            // uncovered sliver back inside the retained query's coverage.
            if (viewport.Left + dx < entry.Coverage.Left || viewport.Right + dx > entry.Coverage.Right ||
                viewport.Top + dy < entry.Coverage.Top || viewport.Bottom + dy > entry.Coverage.Bottom) return false;
            geometry = entry.Projected;
            fillType = entry.Path.FillType;
            entry.LastUse = ++_clock;
        }
        try { path = geometry.CreatePath(cameraOrigin, fillType, token); return true; }
        catch (OutOfMemoryException) { return false; }
    }

    private void RefreshBudget()
    {
        if (_disposed || _budgetProvider == null) return;
        _budget = Math.Max(0, _budgetProvider());
        while (_bytes > _budget && _entries.Count > 0) Remove(FindVictim());
    }

    private (long Owner, bool Interactive) FindVictim()
    {
        // Allocation-free LRU scan; the cache has at most 64 entries.
        (long Owner, bool Interactive) selected = default;
        long oldest = long.MaxValue;
        bool found = false;
        foreach (var pair in _entries)
        {
            if (!found || (pair.Key.Interactive && !selected.Interactive) ||
                (pair.Key.Interactive == selected.Interactive && pair.Value.LastUse < oldest))
            {
                selected = pair.Key;
                oldest = pair.Value.LastUse;
                found = true;
            }
        }
        return selected;
    }

    public void Invalidate(long owner)
    {
        lock (_gate)
        {
            Remove((owner, false));
            Remove((owner, true));
        }
    }

    private void Remove((long Owner, bool Interactive) key)
    {
        if (!_entries.Remove(key, out var entry)) return;
        _bytes -= entry.Bytes;
        entry.Path.Dispose();
    }

    private static bool Valid(SKRect bounds, float zoom) => float.IsFinite(zoom) && zoom > 0 &&
        !bounds.IsEmpty && float.IsFinite(bounds.Left) && float.IsFinite(bounds.Top) &&
        float.IsFinite(bounds.Right) && float.IsFinite(bounds.Bottom);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _entries.Values) entry.Path.Dispose();
            _entries.Clear();
            _bytes = 0;
        }
    }
}
