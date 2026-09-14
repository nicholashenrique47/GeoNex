using OSGeo.GDAL;
using SkiaSharp;

namespace GeoNex.Services;

/// <summary>Retains GDAL decoded tile blocks across camera changes, within GDAL's shared RAM budget.</summary>
public sealed class OnlineRasterSession : IDisposable
{
    private const int MaximumSources = 2;
    private const long MaximumAgeMilliseconds = 5 * 60 * 1000;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _reads = new(1, 1);
    private readonly LeasedResourceRegistry<string, Dataset> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Opened, long Used)> _ages = new(StringComparer.Ordinal);
    private readonly Func<long> _clock;
    private bool _disposed;
    private long _opened, _reused;
    public long OpenedCount => Interlocked.Read(ref _opened);
    public long ReusedCount => Interlocked.Read(ref _reused);
    public int RetainedCount { get { lock (_gate) return _ages.Count; } }

    public OnlineRasterSession(Func<long>? clock = null) => _clock = clock ?? (() => Environment.TickCount64);

    public SKBitmap Read(string xml, string targetSrs, SKRect bounds, double offsetX, double offsetY,
        int width, int height, CancellationToken token)
    {
        // A dataset is never accessed concurrently, even if a caller other than
        // the single latest-camera worker uses this class in future.
        _reads.Wait(token);
        try
        {
            using var lease = Acquire(xml);
            try { return OnlineRasterFrameReader.ReadDataset(lease.Resource, targetSrs, bounds, offsetX, offsetY, width, height, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                // Do not retain a GDAL failed block across retries. The published
                // last-good bitmap belongs to MapRenderingService and is untouched.
                lock (_gate) { if (!_disposed) { _ages.Remove(xml); _sources.Remove(xml); } }
                throw;
            }
        }
        finally { _reads.Release(); }
    }

    private ResourceLease<Dataset> Acquire(string xml)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long now = _clock();
            foreach (var expired in _ages.Where(p => now - p.Value.Opened >= MaximumAgeMilliseconds).Select(p => p.Key).ToArray())
            { _ages.Remove(expired); _sources.Remove(expired); }
            var cached = _sources.Acquire(xml);
            if (cached != null)
            {
                _ages[xml] = (_ages[xml].Opened, now);
                Interlocked.Increment(ref _reused);
                return cached;
            }
            while (_ages.Count >= MaximumSources)
            {
                var oldest = _ages.MinBy(p => p.Value.Used).Key;
                _ages.Remove(oldest); _sources.Remove(oldest);
            }
            // Opening a TMS XML parses metadata only; network RasterIO is outside
            // this lock and retains a lease, so shutdown never waits for tiles.
            var source = OnlineRasterFrameReader.OpenSource(xml);
            try { _sources.Publish(xml, source); }
            catch { source.Dispose(); throw; }
            _ages[xml] = (now, now);
            Interlocked.Increment(ref _opened);
            return _sources.Acquire(xml)!;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _ages.Clear();
            _sources.Dispose(); // A dataset in RasterIO closes only after its lease returns.
        }
    }
}
