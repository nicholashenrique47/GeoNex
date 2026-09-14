using GeoNex.Services;
using SkiaSharp;
using System.Xml.Linq;

internal static class OnlineSessionContracts
{
    public static async Task Run()
    {
        GdalRuntimeBootstrap.Configure();
        using var tiles = new DelayedTileServer();
        tiles.Release();
        string Xml(DelayedTileServer server, int variant = 0)
        {
            var definition = new OnlineBasemapDefinition("fixture", "fixture",
                server.Url + "${z}/${x}/${y}.png?v=" + variant, 20, 3, 2);
            var xml = XDocument.Parse(OnlineBasemapPolicy.BuildGdalTmsXml(definition,
                new OnlineBasemapCacheSettings(Path.GetTempPath(), 64, 3600, 900)));
            xml.Root!.Element("Cache")?.Remove(); // Prove RAM reuse, not disk cache.
            return xml.ToString();
        }
        string config = Xml(tiles);
        double span = 40075016.685578488 / (1 << 20);
        double ox = -20037508.342789244 + (123456.5 * span);
        double oy = 20037508.342789244 - (345678.5 * span);
        var bounds = new SKRect(-8, -8, 8, 8);
        long now = 0;
        using var session = new OnlineRasterSession(() => now);
        SKBitmap Read(string? xml = null, double shift = 0) => session.Read(xml ?? config, "EPSG:3857", bounds,
            ox + shift, oy, 256, 256, CancellationToken.None);
        using var first = Read();
        int requests = tiles.RequestedZooms.Count;
        Check(requests > 0, "cold source downloads a tile");
        using var pan = Read(shift: 1);
        using var revisit = Read();
        Check(tiles.RequestedZooms.Count == requests, "pan and revisit must reuse decoded blocks without HTTP");
        Check(session.OpenedCount == 1 && session.ReusedCount == 2, "one dataset survives camera changes");
        Check(first.Bytes.SequenceEqual(revisit.Bytes), "revisit has identical pixels");
        using var reference = OnlineRasterFrameReader.Read(config, "EPSG:3857", bounds, ox + 1, oy, 256, 256, CancellationToken.None);
        Check(pan.Bytes.SequenceEqual(reference.Bytes), "pan equals a fresh independently opened source");
        using (Read(Xml(tiles, 1))) { }
        now++;
        using (Read()) { } // Touch first, so variant 1 is LRU.
        now++;
        using (Read(Xml(tiles, 2))) { }
        Check(session.RetainedCount == 2, "bounded source count");
        long opens = session.OpenedCount;
        using (Read()) { }
        Check(session.OpenedCount == opens, "recent source survives eviction");
        using (Read(Xml(tiles, 1))) { }
        Check(session.OpenedCount == opens + 1, "least recent source was evicted");
        now += 300001;
        using (Read()) { }
        Check(session.OpenedCount == opens + 2, "source freshness expires");
        tiles.StatusCode = 403;
        bool failed = false;
        try { using var invalid = Read(shift: 1000); }
        catch (Exception) { failed = true; }
        Check(failed && session.RetainedCount == 0, "failed blocks cannot poison retry");
        tiles.StatusCode = 200;
        using (Read(shift: 1000)) { }

        using var blockedTiles = new DelayedTileServer();
        var blockedSession = new OnlineRasterSession();
        var task = Task.Run(() => blockedSession.Read(Xml(blockedTiles), "EPSG:3857", bounds, ox, oy, 256, 256, CancellationToken.None));
        try
        {
            await blockedTiles.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            blockedSession.Dispose();
            Check(!task.IsCompleted, "shutdown returns while HTTP is blocked");
        }
        finally { blockedTiles.Release(); blockedSession.Dispose(); }
        using (await task.WaitAsync(TimeSpan.FromSeconds(10))) { }
        Check(blockedSession.RetainedCount == 0, "retired source released after active read");
        Console.WriteLine($"Online session: PASS (cold HTTP={requests}; pan/revisit additional HTTP=0; identical pixels, LRU, expiry, failure recovery, nonblocking shutdown)");
    }
    private static void Check(bool valid, string message)
    { if (!valid) throw new InvalidOperationException(message); }
}
