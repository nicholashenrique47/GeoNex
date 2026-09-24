using System.Reflection;
using GeoNex.Services;
using SkiaSharp;

internal static class OnlineProgressiveProductionContracts
{
    public static async Task Run(string assemblyPath)
    {
        GdalRuntimeBootstrap.Configure();
        Environment.SetEnvironmentVariable("GEONEX_RENDER_METRICS", "1");
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var mapType = assembly.GetType("GeoNex.Services.MapRenderingService", true)!;
        var serverType = assembly.GetType("GeoNex.Services.LocalMapServer", true)!;
        using var map = (IDisposable)Activator.CreateInstance(mapType)!;
        mapType.GetProperty("ProjetoSRS")!.SetValue(map, "EPSG:3857");
        mapType.GetProperty("OffsetMundoDefinido")!.SetValue(map, true);
        using var provider = new DelayedTileServer();
        provider.Release();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var partial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.BeforeResponse = uri =>
        {
            if (uri.AbsolutePath != "/tiles/7/60/60.png") return Task.CompletedTask;
            blocked.TrySetResult(); return release.Task;
        };
        string cache = Directory.CreateTempSubdirectory("GeoNex-progressive-endpoint-").FullName;
        string xml = OnlineBasemapPolicy.BuildGdalTmsXml(new("fixture", "fixture",
            provider.Url + "${z}/${x}/${y}.png", 8, 3, 2), new(cache, 64, 3600, 900));
        var source = OnlineRasterFrameReader.OpenSource(xml);
        mapType.GetMethod("PublishRaster")!.Invoke(map, new object[] { "fixture", source, xml });
        ((System.Collections.IDictionary)mapType.GetProperty("LimitesRasters")!.GetValue(map)!)["fixture"] =
            new SKRect(-800000, -800000, 800000, 800000);
        mapType.GetProperty("OrdemCamadas")!.SetValue(map, new List<string> { "fixture" });
        var server = Activator.CreateInstance(serverType, map)!;
        serverType.GetMethod("Start")!.Invoke(server, null);
        Action ready = () => partial.TrySetResult();
        serverType.GetEvent("OnOnlineFrameReady")!.AddEventHandler(server, ready);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            string url = (string)serverType.GetProperty("BaseUrl")!.GetValue(server)!;
            int frame = 0;
            async Task<(byte[] Pixels, string Pending)> Fetch()
            {
                using var response = await client.GetAsync(url + $"mapa/?w=1024&h=1024&dpi=1&nav=1&i=0&fid={++frame}&zoom=1&panx=0&pany=0");
                response.EnsureSuccessStatusCode();
                using var bitmap = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
                string timing = string.Join(" ", response.Headers.GetValues("Server-Timing"));
                Check(timing.Contains("io;dur=0.000"), "online I/O entered scene render gate");
                return (bitmap.Bytes, response.Headers.GetValues("X-GeoNex-Online-Pending").Single());
            }
            long vectorRevision = (long)mapType.GetProperty("VectorPresentationRevision")!.GetValue(map)!;
            using (var deferred = await client.GetAsync(url + $"mapa/?w=1024&h=1024&dpi=1&nav=1&i=0&deferOnline=1&fid={++frame}&zoom=1&panx=0&pany=0"))
                deferred.EnsureSuccessStatusCode();
            Check(!(bool)serverType.GetProperty("HasOnlineWork")!.GetValue(server)!, "deferred frame started downloads");
            object?[] deferredArgs = { null };
            using (var deferredFinal = (IDisposable?)mapType.GetMethod("AcquireGlobalCache")!.Invoke(map, deferredArgs))
                Check(deferredFinal == null, "deferred frame entered final cache");
            using (var deferredPreview = (IDisposable?)mapType.GetMethod("AcquireGlobalPreviewCache")!.Invoke(map, deferredArgs))
                Check(deferredPreview != null, "deferred frame unavailable for navigation");
            var first = await Fetch();
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await partial.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var interim = await Fetch();
            Check(interim.Pending == "1", "partial incorrectly marked complete");
            Check(!first.Pixels.SequenceEqual(interim.Pixels), "partial invisible in production endpoint");
            Check((bool)serverType.GetProperty("HasOnlineWork")!.GetValue(server)!, "slow tile no longer held");
            object?[] finalCacheArgs = { null };
            using (var premature = (IDisposable?)mapType.GetMethod("AcquireGlobalCache")!.Invoke(map, finalCacheArgs))
                Check(premature == null, "partial scene became eligible as final cache");
            var gate = (ValueTask<IDisposable>)serverType.GetMethod("PauseForRasterMaintenanceAsync")!.Invoke(server, null)!;
            using (await gate)
            {
                using var gesture = await client.GetAsync(url + $"mapa/?w=1024&h=1024&dpi=1&nav=1&i=1&fid={++frame}&zoom=1&panx=0&pany=0")
                    .WaitAsync(TimeSpan.FromSeconds(2));
                gesture.EnsureSuccessStatusCode();
                using var bitmap = SKBitmap.Decode(await gesture.Content.ReadAsByteArrayAsync());
                Check(bitmap.Bytes.SequenceEqual(interim.Pixels), "partial scene unavailable for gesture while renderer busy");
            }
            release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while ((bool)serverType.GetProperty("HasOnlineWork")!.GetValue(server)!) await Task.Delay(20, timeout.Token);
            var final = await Fetch();
            Check(final.Pending == "0", "final still pending");
            Check((long)mapType.GetProperty("VectorPresentationRevision")!.GetValue(map)! == vectorRevision,
                "online update invalidated vector caches");
            Check(provider.PeakActive <= 2, "production exceeded provider concurrency");
            Console.WriteLine("Online progressive production: PASS (partial PNG before held tile, gesture preview bypasses busy renderer, no partial-as-final reuse, pending/final flags, vector cache retained, <=2 connections)");
        }
        finally { release.TrySetResult(); serverType.GetMethod("Stop")!.Invoke(server, null); }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
