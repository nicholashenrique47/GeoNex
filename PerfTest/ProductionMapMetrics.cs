using System.Diagnostics;
using System.Reflection;
using GeoNex.Services;
using OSGeo.GDAL;
using SkiaSharp;

internal static class ProductionMapMetrics
{
    public static async Task Run(string assemblyPath, string source, bool basemap, bool delayed = false, bool polygonImages = false)
    {
        assemblyPath = Path.GetFullPath(assemblyPath);
        source = Path.GetFullPath(source);
        string previousDirectory = Environment.CurrentDirectory;
        // Production import writes a relative diagnostic log. Isolate benchmark output.
        Environment.CurrentDirectory = Directory.CreateTempSubdirectory("GeoNexMapMetrics-").FullName;
        try { await RunCore(assemblyPath, source, basemap, delayed, polygonImages); }
        finally { Environment.CurrentDirectory = previousDirectory; }
    }

    private static async Task RunCore(string assemblyPath, string source, bool basemap, bool delayed, bool polygonImages)
    {
        Environment.SetEnvironmentVariable("GEONEX_RENDER_METRICS", "1");
        GdalRuntimeBootstrap.Configure();
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var mapType = assembly.GetType("GeoNex.Services.MapRenderingService", true)!;
        var projectType = assembly.GetType("GeoNex.Services.ProjetoService", true)!;
        var serverType = assembly.GetType("GeoNex.Services.LocalMapServer", true)!;
        using var map = (IDisposable)Activator.CreateInstance(mapType)!;
        // Match the on-the-fly projection used when a web basemap is loaded first,
        // without network I/O. Keep every dataset and the original file untouched.
        mapType.GetProperty("ProjetoSRS")!.SetValue(map, "EPSG:3857");
        mapType.GetProperty("OffsetMundoDefinido")!.SetValue(map, true);
        var project = Activator.CreateInstance(projectType)!;
        var clock = Stopwatch.StartNew();
        string compiled = (string)projectType.GetMethod("CompilarParaShapefileNativo")!.Invoke(project, new object[] { source })!;
        Console.WriteLine($"BENCH conversion_ms={clock.Elapsed.TotalMilliseconds:F2} source={source} compiled={compiled}");
        clock.Restart();
        projectType.GetMethod("CarregarShapefileParaMotorMapas")!.Invoke(project, new object[] { compiled, "benchmark", map });
        Console.WriteLine($"BENCH load_ms={clock.Elapsed.TotalMilliseconds:F2}");
        var layerBounds = (SKRect)mapType.GetProperty("LimitesGlobaisVetor")!.GetValue(map)!;
        using var fixture = delayed ? new DelayedTileServer() : null;
        if (basemap)
        {
            if (!OnlineBasemapPolicy.TryResolve("Google", out var definition)) throw new InvalidOperationException("Google provider missing");
            string xml = OnlineBasemapPolicy.BuildGdalTmsXml(definition,
                OnlineBasemapPolicy.CreateCacheSettings(GdalRuntimeConfiguration.Apply().AvailablePhysicalMb));
            if (fixture != null)
            {
                var document = System.Xml.Linq.XDocument.Parse(xml);
                document.Root!.Element("Service")!.Element("ServerUrl")!.Value = fixture.Url + "${z}/${x}/${y}.png";
                document.Root.Element("Cache")?.Remove();
                xml = document.ToString();
            }
            string vsiPath = $"/vsimem/geonex-metrics-{Guid.NewGuid():N}.xml";
            Gdal.FileFromMemBuffer(vsiPath, System.Text.Encoding.UTF8.GetBytes(xml));
            try
            {
                var dataset = Gdal.Open(vsiPath, Access.GA_ReadOnly) ?? throw new InvalidDataException(Gdal.GetLastErrorMsg());
                try { mapType.GetMethod("PublishRaster")!.Invoke(map, new object[] { definition.LayerName, dataset, xml }); }
                catch { dataset.Dispose(); throw; }
                var bounds = (System.Collections.IDictionary)mapType.GetProperty("LimitesRasters")!.GetValue(map)!;
                bounds[definition.LayerName] = new SKRect(-20037508, -20037508, 20037508, 20037508);
                var order = (System.Collections.IList)mapType.GetProperty("OrdemCamadas")!.GetValue(map)!;
                order.Insert(0, definition.LayerName);
            }
            finally { Gdal.Unlink(vsiPath); }
        }
        var server = Activator.CreateInstance(serverType, map)!;
        serverType.GetMethod("Start")!.Invoke(server, null);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            string url = (string)serverType.GetProperty("BaseUrl")!.GetValue(server)!;
            if (basemap)
            {
                await CheckAsyncOnline(client, url, mapType, map, serverType, server, layerBounds, fixture);
                return;
            }
            long frame = 0;
            foreach (int zoom in new[] { 1, 4, 16, 64 })
            {
                byte[]? expectedPixels = null;
                byte[]? cachedPixels = null;
                for (int sample = 0; sample < 4; sample++)
                {
                    // Scene invalidation defeats the whole-frame fast path but retains
                    // immutable geometry caches: measure actual polygon rendering.
                    if (polygonImages)
                    {
                        Environment.SetEnvironmentVariable("GEONEX_POLYGON_IMAGE_CACHE", sample == 0 ? "0" : "1");
                        long vectorRevision = (long)mapType.GetProperty("VectorPresentationRevision")!.GetValue(map)!;
                        mapType.GetMethod("InvalidateRasterPresentationCache")!.Invoke(map, null);
                        if ((long)mapType.GetProperty("VectorPresentationRevision")!.GetValue(map)! != vectorRevision)
                            throw new InvalidOperationException("Raster arrival invalidated vectors");
                    }
                    else mapType.GetMethod("RequestRedraw")!.Invoke(map, null);
                    clock.Restart();
                    var scene = (SKRect)mapType.GetMethod("GetSceneBounds")!.Invoke(map, null)!;
                    if (!LayerCameraPolicy.TryFit(scene, layerBounds, 1600, 900, out var camera)) throw new InvalidOperationException("Invalid camera");
                    using var response = await client.GetAsync(FormattableString.Invariant(
                        $"{url}mapa/?w=1600&h=900&dpi=1&nav=1&i=0&fid={++frame}&zoom={camera.Zoom * zoom}&panx={camera.PanX * zoom}&pany={camera.PanY * zoom}"));
                    response.EnsureSuccessStatusCode();
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                    double httpMs = clock.Elapsed.TotalMilliseconds;
                    using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("Invalid frame");
                    double totalMs = clock.Elapsed.TotalMilliseconds;
                    byte[] pixels = bitmap.Bytes;
                    if (expectedPixels != null)
                    {
                        int difference = expectedPixels.Zip(pixels, (a, b) => Math.Abs(a - b)).Max();
                        if (difference > (polygonImages ? 2 : 0))
                        {
                            var worst = Enumerable.Range(0, pixels.Length).OrderByDescending(i => Math.Abs(expectedPixels[i] - pixels[i])).Take(3);
                            foreach (int i in worst) Console.WriteLine($"PIXEL x={(i / 4) % bitmap.Width} y={(i / 4) / bitmap.Width} channel={i % 4} ref={string.Join(',', expectedPixels.Skip(i / 4 * 4).Take(4))} actual={string.Join(',', pixels.Skip(i / 4 * 4).Take(4))} alpha={bitmap.AlphaType}");
                            throw new InvalidOperationException($"Camera pixels changed at zoom {zoom}: delta={difference}");
                        }
                        if (polygonImages) Console.WriteLine($"IMAGE pixels_max_delta={difference} hits={serverType.GetProperty("PolygonImageCacheHits")!.GetValue(server)} builds={serverType.GetProperty("PolygonImageCacheBuilds")!.GetValue(server)} bytes={serverType.GetProperty("PolygonImageCacheBytes")!.GetValue(server)}");
                    }
                    if (polygonImages && sample > 0)
                    {
                        if (cachedPixels != null && !cachedPixels.SequenceEqual(pixels)) throw new InvalidOperationException("Image hit changed pixels");
                        cachedPixels ??= pixels;
                    }
                    expectedPixels ??= pixels;
                    string timing = string.Join(" ", response.Headers.GetValues("Server-Timing"));
                    Console.WriteLine($"BENCH zoom={zoom} sample={sample} http_ms={httpMs:F2} decode_total_ms={totalMs:F2} bytes={bytes.Length} size={bitmap.Width}x{bitmap.Height} {timing}");
                    if (!polygonImages && zoom == 4 && sample == 3) CompareEncoding(bitmap);
                }
            }
            if (polygonImages)
            {
                if ((long)serverType.GetProperty("PolygonImageCacheHits")!.GetValue(server)! == 0)
                    throw new InvalidOperationException("No production cache hits (dataset or memory budget ineligible)");
                long before = (long)mapType.GetProperty("VectorPresentationRevision")!.GetValue(map)!;
                mapType.GetMethod("RequestRedraw")!.Invoke(map, null);
                if ((long)mapType.GetProperty("VectorPresentationRevision")!.GetValue(map)! <= before)
                    throw new InvalidOperationException("Ordinary changes failed to invalidate vector images");
                var scene = (SKRect)mapType.GetMethod("GetSceneBounds")!.Invoke(map, null)!;
                if (!LayerCameraPolicy.TryFit(scene, layerBounds, 1600, 900, out var camera)) throw new InvalidOperationException("Invalid camera");
                foreach (string mode in new[] { "c=1", "rot=17", "i=1" })
                {
                    byte[]? reference = null;
                    long buildsBefore = (long)serverType.GetProperty("PolygonImageCacheBuilds")!.GetValue(server)!;
                    for (int pass = 0; pass < 2; pass++)
                    {
                        Environment.SetEnvironmentVariable("GEONEX_POLYGON_IMAGE_CACHE", pass.ToString());
                        mapType.GetMethod("InvalidateRasterPresentationCache")!.Invoke(map, null);
                        byte[] png = await client.GetByteArrayAsync(FormattableString.Invariant(
                            $"{url}mapa/?w=1600&h=900&dpi=1&nav=1&{mode}&fid={++frame}&zoom={camera.Zoom * 4}&panx={camera.PanX * 4}&pany={camera.PanY * 4}"));
                        using var image = SKBitmap.Decode(png) ?? throw new InvalidDataException("Invalid bypass frame");
                        if (reference != null && !reference.SequenceEqual(image.Bytes)) throw new InvalidOperationException($"Bypass changed pixels: {mode}");
                        reference ??= image.Bytes;
                    }
                    if ((long)serverType.GetProperty("PolygonImageCacheBuilds")!.GetValue(server)! != buildsBefore)
                        throw new InvalidOperationException($"Ineligible mode built image cache: {mode}");
                    Console.WriteLine($"PASS polygon image bypass: {mode}, pixels identical");
                }
                Console.WriteLine("PASS production polygon-image-cache, full PNG endpoint and conservative edit invalidation");
            }
        }
        finally { serverType.GetMethod("Stop")!.Invoke(server, null); }
    }

    private static async Task CheckAsyncOnline(HttpClient client, string url, Type mapType, object map,
        Type serverType, object server, SKRect layerBounds, DelayedTileServer? fixture)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action onReady = () => ready.TrySetResult();
        serverType.GetEvent("OnOnlineFrameReady")!.AddEventHandler(server, onReady);
        try
        {
            var scene = (SKRect)mapType.GetMethod("GetSceneBounds")!.Invoke(map, null)!;
            if (!LayerCameraPolicy.TryFit(scene, layerBounds, 1600, 900, out var camera)) throw new InvalidOperationException("Invalid camera");
            long frame = 0;
            async Task<byte[]> Fetch(int zoom)
            {
                var clock = Stopwatch.StartNew();
                using var response = await client.GetAsync(FormattableString.Invariant(
                    $"{url}mapa/?w=1600&h=900&dpi=1&nav=1&i=0&fid={++frame}&zoom={camera.Zoom * zoom}&panx={camera.PanX * zoom}&pany={camera.PanY * zoom}"));
                response.EnsureSuccessStatusCode();
                byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                string timing = string.Join(" ", response.Headers.GetValues("Server-Timing"));
                if (!timing.Contains("io;dur=0.000")) throw new InvalidOperationException("Online I/O blocked the vector renderer");
                using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("Invalid PNG");
                Console.WriteLine($"ASYNC zoom={zoom} http_decode_ms={clock.Elapsed.TotalMilliseconds:F2} {timing}");
                return bitmap.Bytes;
            }
            var before = await Fetch(4).WaitAsync(TimeSpan.FromSeconds(5));
            int finalZoom = 4;
            if (fixture != null)
            {
                await fixture.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // Tiles are held indefinitely. Both full-quality vector zooms must
                // finish BEFORE we allow the provider to answer.
                await Fetch(8).WaitAsync(TimeSpan.FromSeconds(3));
                before = await Fetch(32).WaitAsync(TimeSpan.FromSeconds(3));
                finalZoom = 32;
                // Hide while native tile I/O is held: no obsolete bitmap/retry
                // may publish. Re-show must remain usable with the same source.
                var originalOrder = (List<string>)mapType.GetProperty("OrdemCamadas")!.GetValue(map)!;
                mapType.GetProperty("OrdemCamadas")!.SetValue(map,
                    originalOrder.Where(layer => layer != "Google Satellite").ToList());
                serverType.GetMethod("DiscardObsoleteOnlineWork")!.Invoke(server, null);
                fixture.Release();
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    while ((bool)serverType.GetProperty("HasOnlineWork")!.GetValue(server)!)
                        await Task.Delay(10, timeout.Token);
                object?[] hiddenArgs = { "Google Satellite", null };
                using (var unexpected = (IDisposable?)mapType.GetMethod("AcquireRasterCache")!.Invoke(map, hiddenArgs))
                    if (unexpected != null) throw new InvalidOperationException("Hidden basemap published an obsolete image");
                if (ready.Task.IsCompleted) throw new InvalidOperationException("Hidden source signaled a ready frame");
                mapType.GetProperty("OrdemCamadas")!.SetValue(map, originalOrder);
                before = await Fetch(finalZoom);
                Console.WriteLine("PASS production online scheduler: hide during I/O, discard active/pending, no stale publication, re-show");
            }
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(45));
            byte[] after = await Fetch(finalZoom);
            if (before.SequenceEqual(after)) throw new InvalidOperationException("Online imagery was not refined");
            if (fixture != null)
            {
                var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Action signal = () => refreshed.TrySetResult();
                serverType.GetEvent("OnOnlineFrameReady")!.AddEventHandler(server, signal);
                try
                {
                    await Fetch(4);
                    await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(45));
                    Environment.SetEnvironmentVariable("GEONEX_POLYGON_IMAGE_CACHE", "0");
                    mapType.GetMethod("InvalidateRasterPresentationCache")!.Invoke(map, null);
                    byte[] direct = await Fetch(4);
                    Environment.SetEnvironmentVariable("GEONEX_POLYGON_IMAGE_CACHE", "1");
                    mapType.GetMethod("InvalidateRasterPresentationCache")!.Invoke(map, null);
                    byte[] build = await Fetch(4);
                    long hitsBefore = (long)serverType.GetProperty("PolygonImageCacheHits")!.GetValue(server)!;
                    mapType.GetMethod("InvalidateRasterPresentationCache")!.Invoke(map, null);
                    byte[] hit = await Fetch(4);
                    int delta = direct.Zip(build, (a, b) => Math.Abs(a - b)).Max();
                    double rms = Math.Sqrt(direct.Zip(build, (a, b) => (double)(a - b) * (a - b)).Average());
                    int alphaDelta = Enumerable.Range(0, direct.Length / 4).Max(i => Math.Abs(direct[i * 4 + 3] - build[i * 4 + 3]));
                    Console.WriteLine($"ONLINE IMAGE pixels_max_delta={delta} rms={rms:F4} alpha_delta={alphaDelta} hits={serverType.GetProperty("PolygonImageCacheHits")!.GetValue(server)}");
                    // Layer flattening preserves its transparent image exactly, but
                    // 8-bit SrcOver is not associative over an opaque destination.
                    // Bound RGB residuals separately from alpha/coverage and require
                    // exact repeated output. This is not a geometry/LOD tolerance.
                    if (delta > 8 || rms > .5 || alphaDelta != 0 || !build.SequenceEqual(hit))
                        throw new InvalidOperationException("Online layer composition exceeds quantization contract");
                    if ((long)serverType.GetProperty("PolygonImageCacheHits")!.GetValue(server)! <= hitsBefore)
                        throw new InvalidOperationException("Online refinement did not reuse vector image");
                }
                finally { serverType.GetEvent("OnOnlineFrameReady")!.RemoveEventHandler(server, signal); }
                string CacheKey()
                {
                    object?[] args = { "Google Satellite", null };
                    using var lease = (IDisposable?)mapType.GetMethod("AcquireRasterCache")!.Invoke(map, args);
                    return (string)args[1]!.GetType().GetProperty("CacheKey")!.GetValue(args[1])!;
                }
                string good = CacheKey();
                int failedZoom = 64;
                foreach (int status in new[] { 403, 204, 500 })
                {
                    long errors = (long)serverType.GetProperty("OnlineReadFailures")!.GetValue(server)!;
                    fixture.StatusCode = status;
                    await Fetch(failedZoom);
                    failedZoom *= 2;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    while ((long)serverType.GetProperty("OnlineReadFailures")!.GetValue(server)! == errors)
                        await Task.Delay(10, timeout.Token);
                    if (CacheKey() != good) throw new InvalidOperationException($"HTTP {status} replaced the good cache");
                }
            }
            Console.WriteLine("Async online production: PASS (vector frames independent of tile I/O, newest camera refinement, valid cache retained on HTTP failure)");
            long reuses = (long)serverType.GetProperty("OnlineDatasetReuses")!.GetValue(server)!;
            if (fixture != null && reuses == 0) throw new InvalidOperationException("Production did not reuse online datasets");
            Console.WriteLine($"Online dataset session: opened={serverType.GetProperty("OnlineDatasetOpens")!.GetValue(server)} reused={reuses}");
        }
        finally { fixture?.Release(); serverType.GetEvent("OnOnlineFrameReady")!.RemoveEventHandler(server, onReady); }
    }

    private static void CompareEncoding(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var pixels = image.PeekPixels();
        Console.WriteLine($"ENCODE adaptive_level={MapFrameEncoding.NavigationCompressionLevel(image, 1024)}");
        foreach (var filter in new[] { SKPngEncoderFilterFlags.Sub })
        foreach (int level in new[] { 0, 1 })
        {
            for (int sample = 0; sample < 3; sample++)
            {
                var clock = Stopwatch.StartNew();
                using var encoded = pixels!.Encode(new SKPngEncoderOptions(filter, level))
                    ?? throw new InvalidDataException("PNG encoding failed");
                double encodeMs = clock.Elapsed.TotalMilliseconds;
                using var decoded = SKBitmap.Decode(encoded);
                double totalMs = clock.Elapsed.TotalMilliseconds;
                if (!bitmap.Bytes.SequenceEqual(decoded.Bytes)) throw new InvalidDataException("PNG pixels changed");
                Console.WriteLine($"ENCODE filter={filter} level={level} ms={encodeMs:F2} decode_total_ms={totalMs:F2} bytes={encoded.Size}");
            }
        }
    }
}
