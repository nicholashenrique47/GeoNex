using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml.Linq;
using SkiaSharp;

namespace GeoNex.Services;

/// <summary>Bounded center-first previews. Each lane owns its GDAL handles; final pixels use the original reader.</summary>
public sealed class ProgressiveOnlineRaster : IDisposable
{
    private const int RegionSize = 512;
    private const int PublishIntervalMilliseconds = 150;
    private readonly OnlineRasterSession[] _lanes = { new(), new(), new(), new() };
    private readonly Lazy<OnlineTileTransport> _transport = new(() => new OnlineTileTransport());
    private long _regions, _updates;
    public long CompletedRegions => Interlocked.Read(ref _regions);
    public long PublishedPreviews => Interlocked.Read(ref _updates);
    public long TileDownloads => _transport.IsValueCreated ? _transport.Value.Downloads : 0;
    public long SharedTileRequests => _transport.IsValueCreated ? _transport.Value.MergedRequests : 0;
    public long TileCacheHits => _transport.IsValueCreated ? _transport.Value.CacheHits : 0;

    public SKBitmap Read(OnlineRasterSession finalSession, string xml, string srs, SKRect bounds,
        double offsetX, double offsetY, int width, int height, CancellationToken token,
        Action<SKBitmap> publish, Action<SKCanvas, int, int>? seed = null)
    {
        // Only registered tiled sources with persistent cache use this path. The
        // final RasterIO then consumes the downloaded tiles rather than fetching them again.
        var config = XDocument.Parse(xml);
        if (config.Root?.Element("Cache") != null)
        {
            xml = _transport.Value.Register(xml);
            config = XDocument.Parse(xml);
        }
        bool sharedTransport = config.Root?.Element("GeoNexTileTransport")?.Value == "shared";
        if ((!sharedTransport && config.Root?.Element("Cache") == null) || (long)width * height <= RegionSize * RegionSize)
            return finalSession.Read(xml, srs, bounds, offsetX, offsetY, width, height, token);
        int connections = int.TryParse(config.Root?.Element("MaxConnections")?.Value, out int count) ? count : 2;
        var available = GdalRuntimeConfiguration.Apply().AvailablePhysicalMb;
        int laneCount = Math.Clamp(connections, 1, available < 2048 ? 2 : _lanes.Length);
        // The adapter enforces the provider limit globally. Let GDAL submit local
        // requests concurrently so duplicate tiles don't occupy every network lane.
        if (!sharedTransport) config.Root!.SetElementValue("MaxConnections", 1);
        string laneXml = config.ToString(SaveOptions.DisableFormatting);
        var readSize = SrsFactory.IsSame(OnlineBasemapPolicy.WebMercatorSrs, srs)
            ? OnlineBasemapPolicy.CalculateDirectRenderDimensions(width, height, available)
            : OnlineBasemapPolicy.CalculateRenderDimensions(width, height, available, false);
        // Preserve physical pixels on HiDPI displays. The transient-memory budget
        // covers the mutable surface plus published copies instead of imposing a
        // fixed 4MP ceiling that blurred even machines with ample available RAM.
        var size = OnlineBasemapPolicy.CalculateRenderDimensions(readSize.Width, readSize.Height, available, false);
        using var preview = new SKBitmap(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(preview);
        canvas.Clear(SKColors.Transparent);
        seed?.Invoke(canvas, size.Width, size.Height);
        canvas.ResetMatrix();
        var regions = new ConcurrentQueue<SKRectI>(Regions(readSize.Width, readSize.Height));
        var errors = new ConcurrentQueue<Exception>();
        var composition = new object();
        long lastUpdate = 0;
        bool first = true;
        bool dirty = false;
        var clock = Stopwatch.StartNew();
        // At most four render lanes, bounded by provider/RAM. No per-region tasks
        // accumulate; a slow edge can occupy one lane while the other still advances.
        var workers = Enumerable.Range(0, laneCount).Select(lane => Task.Run(() =>
        {
            while (!token.IsCancellationRequested && regions.TryDequeue(out var region))
            {
                try
                {
                    // Offset the origin in double precision; keep the local patch
                    // small to avoid introducing float world-coordinate drift.
                    double x = bounds.Left + bounds.Width * (region.Left / (double)readSize.Width);
                    double y = bounds.Top + bounds.Height * (region.Top / (double)readSize.Height);
                    var patchBounds = new SKRect(0, 0,
                        (float)(bounds.Width * (region.Width / (double)readSize.Width)),
                        (float)(bounds.Height * (region.Height / (double)readSize.Height)));
                    using var patch = _lanes[lane].Read(laneXml, srs, patchBounds,
                        offsetX + x, offsetY - y, region.Width, region.Height, token);
                    lock (composition)
                    {
                        token.ThrowIfCancellationRequested();
                        // Keep previous imagery under unfilled regions. Each published
                        // bitmap is immutable, never the surface still being painted.
                        float sx = size.Width / (float)readSize.Width, sy = size.Height / (float)readSize.Height;
                        canvas.DrawBitmap(patch, new SKRect(region.Left * sx, region.Top * sy, region.Right * sx, region.Bottom * sy));
                        dirty = true;
                        Interlocked.Increment(ref _regions);
                        if (first || clock.ElapsedMilliseconds - lastUpdate >= PublishIntervalMilliseconds)
                        {
                            var snapshot = preview.Copy() ?? throw new IOException("Cannot allocate online preview.");
                            publish(snapshot); // Ownership is consumed by the worker even when obsolete.
                            Interlocked.Increment(ref _updates);
                            first = false;
                            dirty = false;
                            lastUpdate = clock.ElapsedMilliseconds;
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error) { errors.Enqueue(error); }
            }
        })).ToArray();
        Task.WaitAll(workers); // Only the background online worker waits; never the scene/UI gate.
        token.ThrowIfCancellationRequested();
        if (dirty)
        {
            publish(preview.Copy() ?? throw new IOException("Cannot allocate online preview."));
            Interlocked.Increment(ref _updates);
        }
        if (!errors.IsEmpty)
        {
            throw new IOException("Incomplete online imagery; retaining available regions.", errors.First());
        }
        // One full-grid pass removes preview seams and preserves the existing Warp,
        // interpolation, fractional zoom and HiDPI contracts. Disk cache is shared by URL.
        return finalSession.Read(xml, srs, bounds, offsetX, offsetY, width, height, token);
    }

    internal static SKRectI[] Regions(int width, int height)
    {
        var regions = new List<SKRectI>();
        for (int y = 0; y < height; y += RegionSize)
            for (int x = 0; x < width; x += RegionSize)
                regions.Add(new SKRectI(x, y, Math.Min(x + RegionSize, width), Math.Min(y + RegionSize, height)));
        return regions.OrderBy(r => Math.Max(Math.Abs((r.Left + r.Right) / 2.0 - width / 2.0),
            Math.Abs((r.Top + r.Bottom) / 2.0 - height / 2.0))).ToArray();
    }

    public void Dispose()
    {
        if (_transport.IsValueCreated) _transport.Value.Dispose();
        foreach (var lane in _lanes) lane.Dispose();
    }
}
