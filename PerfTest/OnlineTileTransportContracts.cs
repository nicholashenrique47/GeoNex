using System.Diagnostics;
using System.Net;
using System.Xml.Linq;
using GeoNex.Services;
using SkiaSharp;

internal static class OnlineTileTransportContracts
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static string TileUrl(string xml, int x = 1, int y = 1) =>
        XDocument.Parse(xml).Root!.Element("Service")!.Element("ServerUrl")!.Value
            .Replace("${z}", "2").Replace("${x}", x.ToString()).Replace("${y}", y.ToString());

    public static async Task Run()
    {
        GdalRuntimeBootstrap.Configure();
        using var server = new DelayedTileServer();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        string cache = Directory.CreateTempSubdirectory("GeoNex-transport-contracts-").FullName;
        string xml = OnlineBasemapPolicy.BuildGdalTmsXml(new("fixture", "fixture",
            server.Url + "${z}/${x}/${y}.png", 2, 3, 2), new(cache, 64, 3600, 900));
        byte[] tile;
        using (var transport = new OnlineTileTransport())
        {
            string url = TileUrl(transport.Register(xml));
            var requests = Enumerable.Range(0, 8).Select(_ => client.GetByteArrayAsync(url)).ToArray();
            try
            {
                await server.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (transport.MergedRequests < 7) await Task.Delay(10, timeout.Token);
                Check(server.RequestedPaths.Count == 1, "duplicate upstream requests before release");
            }
            finally { server.Release(); }
            var bytes = await Task.WhenAll(requests);
            tile = bytes[0];
            Check(bytes.All(b => b.SequenceEqual(tile)), "shared response bytes differ");
            Check(transport.Downloads == 1, "shared tile downloaded more than once");
        }
        using (var transport = new OnlineTileTransport())
        {
            string rewritten = transport.Register(xml);
            Check((await client.GetByteArrayAsync(TileUrl(rewritten))).SequenceEqual(tile), "restart cache image differs");
            Check(transport.Downloads == 0 && transport.CacheHits == 1, "loopback port changed cache identity");

            const double m = 20037508.342789244;
            SKRect Bounds(int x) => new((float)(-m + x * m / 2 + m / 8), (float)(-m * .375),
                (float)(-m + x * m / 2 + 3 * m / 8), (float)(-m * .125));
            int fetched = server.RequestedPaths.Count;
            using (var native = OnlineRasterFrameReader.Read(xml, "EPSG:3857", Bounds(1), 0, 0, 128, 128, CancellationToken.None))
                Check(native.Pixels.Any(p => p.Alpha > 0), "native cache read empty");
            Check(server.RequestedPaths.Count == fetched, "GDAL cannot reuse transport cache");
            using (var native = OnlineRasterFrameReader.Read(xml, "EPSG:3857", Bounds(2), 0, 0, 128, 128, CancellationToken.None)) { }
            Check(server.RequestedPaths.Count == fetched + 1, "native fixture should read one new tile");
            await client.GetByteArrayAsync(TileUrl(rewritten, 2));
            Check(server.RequestedPaths.Count == fetched + 1 && transport.Downloads == 0, "transport cannot reuse GDAL cache");

            server.BeforeResponse = _ => Task.Delay(80);
            var distinct = Enumerable.Range(0, 4).Select(x => client.GetByteArrayAsync(TileUrl(rewritten, x, 3)));
            await Task.WhenAll(distinct);
            Check(server.PeakActive <= 2, "provider connection budget exceeded");
            server.BeforeResponse = null;

            server.StatusCode = 503;
            using (var bad = await client.GetAsync(TileUrl(rewritten, 3, 2)))
                Check(bad.StatusCode == HttpStatusCode.ServiceUnavailable, "upstream error hidden");
            server.StatusCode = 200;
            fetched = server.RequestedPaths.Count;
            await client.GetByteArrayAsync(TileUrl(rewritten, 3, 2));
            Check(server.RequestedPaths.Count == fetched + 1, "failed response cached");

            server.CacheControl = "no-store";
            fetched = server.RequestedPaths.Count;
            await client.GetByteArrayAsync(TileUrl(rewritten, 0, 0));
            await client.GetByteArrayAsync(TileUrl(rewritten, 0, 0));
            Check(server.RequestedPaths.Count == fetched + 2, "no-store ignored");
            server.CacheControl = "max-age=1";
            await client.GetByteArrayAsync(TileUrl(rewritten, 1, 0));
            await Task.Delay(1100);
            fetched = server.RequestedPaths.Count;
            await client.GetByteArrayAsync(TileUrl(rewritten, 1, 0));
            Check(server.RequestedPaths.Count == fetched + 1, "server cache expiry ignored");
            server.ResponseBytes = tile[..(tile.Length / 2)];
            using (var invalidImage = await client.GetAsync(TileUrl(rewritten, 2, 0)))
                Check(invalidImage.StatusCode == HttpStatusCode.BadGateway, "truncated image accepted");
            server.ResponseBytes = null;
            fetched = server.RequestedPaths.Count;
            await client.GetByteArrayAsync(TileUrl(rewritten, 2, 0));
            Check(server.RequestedPaths.Count == fetched + 1, "truncated image poisoned cache");
            fetched = server.RequestedPaths.Count;
            using (var invalid = await client.GetAsync(TileUrl(rewritten, -1)))
                Check(invalid.StatusCode == HttpStatusCode.NotFound, "invalid tile address accepted");
            Check(server.RequestedPaths.Count == fetched, "invalid address reached upstream");
        }
        using var shutdown = new OnlineTileTransport();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforeResponse = _ => { arrived.TrySetResult(); return release.Task; };
        var pending = client.GetAsync(TileUrl(shutdown.Register(xml), 3, 0));
        try
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var timer = Stopwatch.StartNew();
            shutdown.Dispose();
            Check(timer.ElapsedMilliseconds < 500, "shutdown blocked on provider");
            try { using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (HttpRequestException) { }
        }
        finally { release.TrySetResult(); }
        Console.WriteLine("Online tile transport: PASS (dedup, persistent cache, bidirectional GDAL cache, <=2 requests, errors, no-store, expiry, corrupt images, invalid address, nonblocking shutdown)");
    }
}
