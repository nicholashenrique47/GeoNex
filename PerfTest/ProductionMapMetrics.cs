using System.Diagnostics;
using System.Reflection;
using GeoNex.Services;
using OSGeo.GDAL;
using SkiaSharp;

internal static class ProductionMapMetrics
{
    public static async Task Run(string assemblyPath, string source, bool basemap, bool delayed = false, bool polygonImages = false, bool highZoom = false, bool paintExperiments = false)
    {
        assemblyPath = Path.GetFullPath(assemblyPath);
        source = Path.GetFullPath(source);
        string previousDirectory = Environment.CurrentDirectory;
        // Production import writes a relative diagnostic log. Isolate benchmark output.
        Environment.CurrentDirectory = Directory.CreateTempSubdirectory("GeoNexMapMetrics-").FullName;
        try { await RunCore(assemblyPath, source, basemap, delayed, polygonImages, highZoom, paintExperiments); }
        finally { Environment.CurrentDirectory = previousDirectory; }
    }

    private static async Task RunCore(string assemblyPath, string source, bool basemap, bool delayed, bool polygonImages, bool highZoom, bool paintExperiments)
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
            if (highZoom)
            {
                float dpi = float.TryParse(Environment.GetEnvironmentVariable("GEONEX_BENCH_DPI"),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var requestedDpi)
                    ? requestedDpi : 1;
                if (!float.IsFinite(dpi) || dpi < .5f || dpi > 4) throw new ArgumentOutOfRangeException("GEONEX_BENCH_DPI");
                float targetScale = float.TryParse(Environment.GetEnvironmentVariable("GEONEX_BENCH_SCALE"),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var requestedScale)
                    ? requestedScale : 4;
                if (!float.IsFinite(targetScale) || targetScale <= 0) throw new ArgumentOutOfRangeException("GEONEX_BENCH_SCALE");
                var layers = (System.Collections.IDictionary)mapType.GetProperty("FeaturesPorCamada")!.GetValue(map)!;
                var features = (System.Collections.IList)layers["benchmark"]!;
                object focus = features[features.Count / 2]!;
                var center = (SKPoint)focus.GetType().GetField("CentroidLocal")!.GetValue(focus)!;
                float cx = center.X, cy = center.Y;
                var scene = (SKRect)mapType.GetMethod("GetSceneBounds")!.Invoke(map, null)!;
                var target = new SKRect(cx - 640 / targetScale, cy - 360 / targetScale,
                    cx + 640 / targetScale, cy + 360 / targetScale);
                if (!LayerCameraPolicy.TryFit(scene, target, 1600, 900, out var camera)) throw new InvalidOperationException("Invalid camera");
                Console.WriteLine($"HIGH_ZOOM features={features.Count} center={cx},{cy} scale={targetScale} dpi={dpi}");
                foreach (int sample in Enumerable.Range(0, 7))
                {
                    // Emulate repeated online tile publications without network timing noise.
                    bool finalPan = Environment.GetEnvironmentVariable("GEONEX_BENCH_FINAL_PAN") == "1";
                    if (sample > 0 && (finalPan || Environment.GetEnvironmentVariable("GEONEX_BENCH_RASTER_REFRESH") == "1"))
                        mapType.GetMethod("InvalidateRasterPresentationCache")!.Invoke(map, null);
                    int interactive = sample == 0 || finalPan ? 0 : 1;
                    float panStep = float.TryParse(Environment.GetEnvironmentVariable("GEONEX_BENCH_PAN_STEP"),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var requestedPanStep)
                        ? requestedPanStep : 4;
                    if (!float.IsFinite(panStep)) throw new ArgumentOutOfRangeException("GEONEX_BENCH_PAN_STEP");
                    float pan = (sample % 3) * panStep;
                    clock.Restart();
                    using var response = await client.GetAsync(FormattableString.Invariant(
                        $"{url}mapa/?w=1600&h=900&dpi={dpi}&nav=1&i={interactive}&fid={sample + 1}&zoom={camera.Zoom}&panx={camera.PanX + pan}&pany={camera.PanY}"));
                    response.EnsureSuccessStatusCode();
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                    Console.WriteLine($"HIGH_ZOOM sample={sample} http_ms={clock.Elapsed.TotalMilliseconds:F2} {string.Join(" ", response.Headers.GetValues("Server-Timing"))}");
                    using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("Invalid high-zoom PNG");
                    if (!bitmap.Pixels.Any(p => p.Alpha != 0)) throw new InvalidOperationException("Empty focus area");
                    string? frameDirectory = Environment.GetEnvironmentVariable("GEONEX_BENCH_FRAME_DIRECTORY");
                    if (!string.IsNullOrWhiteSpace(frameDirectory))
                    {
                        Directory.CreateDirectory(frameDirectory);
                        await File.WriteAllBytesAsync(Path.Combine(frameDirectory, $"frame-{sample}.png"), bytes);
                    }
                    string? referenceDirectory = Environment.GetEnvironmentVariable("GEONEX_BENCH_REFERENCE_DIRECTORY");
                    if (!string.IsNullOrWhiteSpace(referenceDirectory))
                    {
                        using var reference = SKBitmap.Decode(Path.Combine(referenceDirectory, $"frame-{sample}.png"));
                        if (reference == null || reference.Info != bitmap.Info) throw new InvalidDataException("Reference frame mismatch");
                        byte[] expected = reference.Bytes, actual = bitmap.Bytes;
                        int maximum = 0, changed = 0, visibleChanges = 0;
                        int visibleWidth = (int)Math.Round(1600 * dpi), visibleHeight = (int)Math.Round(900 * dpi);
                        int left = (bitmap.Width - visibleWidth) / 2, top = (bitmap.Height - visibleHeight) / 2;
                        double squared = 0;
                        for (int i = 0; i < actual.Length; i++)
                        {
                            int difference = Math.Abs(expected[i] - actual[i]);
                            maximum = Math.Max(maximum, difference);
                            if (difference != 0)
                            {
                                changed++;
                                int px = (i / 4) % bitmap.Width, py = (i / 4) / bitmap.Width;
                                if (px >= left && px < left + visibleWidth && py >= top && py < top + visibleHeight) visibleChanges++;
                            }
                            squared += difference * difference;
                        }
                        Console.WriteLine($"HIGH_ZOOM pixels sample={sample} max_delta={maximum} changed_channels={changed}/{actual.Length} visible_changes={visibleChanges} rms={Math.Sqrt(squared / actual.Length):F6}");
                        // Strict by default. The explicit analytic-AA comparison
                        // permits only one 8-bit level, including the overscan.
                        bool analyticTolerance = Environment.GetEnvironmentVariable("GEONEX_BENCH_PIXEL_TOLERANCE") == "1";
                        if (analyticTolerance ? maximum > 1 : visibleChanges != 0)
                            throw new InvalidOperationException("High-zoom frame exceeds pixel tolerance");
                    }
                    if (sample == 0 || !string.IsNullOrWhiteSpace(frameDirectory))
                    {
                        object?[] cacheArgs = { null };
                        using var global = (IDisposable?)mapType.GetMethod("AcquireGlobalCache")!.Invoke(map, cacheArgs);
                        object metadata = cacheArgs[0]!;
                        object productionFrame = metadata.GetType().GetProperty("Frame")!.GetValue(metadata)!;
                        var origin = (SKPoint)productionFrame.GetType().GetProperty("LocalCenter")!.GetValue(productionFrame)!;
                        float scale = (float)metadata.GetType().GetProperty("Zoom")!.GetValue(metadata)!;
                        Console.WriteLine(FormattableString.Invariant($"HIGH_ZOOM frame_center_x={origin.X:R} frame_center_y={origin.Y:R} css_scale={scale:R}"));
                        var viewport = MapViewportMetrics.Create(
                            (int)metadata.GetType().GetProperty("CssWidth")!.GetValue(metadata)!,
                            (int)metadata.GetType().GetProperty("CssHeight")!.GetValue(metadata)!, dpi);
                        var localBounds = MapCoordinateFrame.Create(viewport, SKPoint.Empty, scale).LocalViewportBounds;
                        using var shapeLease = (IDisposable)mapType.GetMethod("AcquireShapefile")!.Invoke(map, new object[] { "benchmark" })!;
                        object shape = shapeLease.GetType().GetProperty("Resource")!.GetValue(shapeLease)!;
                        object?[] pathArgs = { localBounds, origin, scale, false, null, false };
                        bool pathAvailable = (bool)shape.GetType().GetMethod("TryGetPreciseRenderPath")!.Invoke(shape, pathArgs)!;
                        if (!pathAvailable && shape.GetType().GetMethod("TryGetProjectedRenderPath") is { } projected)
                        {
                            object?[] projectedArgs = { localBounds, origin, scale,
                                mapType.GetProperty("OffsetMundoX")!.GetValue(map), mapType.GetProperty("OffsetMundoY")!.GetValue(map), null, CancellationToken.None };
                            pathAvailable = (bool)projected.Invoke(shape, projectedArgs)!;
                            pathArgs[4] = projectedArgs[5];
                        }
                        if (pathAvailable)
                        {
                            using var path = (SKPath)pathArgs[4]!;
                            var points = path.Points;
                            Console.WriteLine($"HIGH_ZOOM path_points={path.PointCount} outside_frame={points.Count(p => !localBounds.Contains(p))} path_bounds={path.Bounds} frame_bounds={localBounds}");
                            if (!string.IsNullOrWhiteSpace(frameDirectory))
                            {
                                await File.WriteAllBytesAsync(Path.Combine(frameDirectory, $"path-{sample}.xy"),
                                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(points.AsSpan()).ToArray());
                                CapturedPolygonMetrics.Save(path, Path.Combine(frameDirectory, $"path-{sample}.gpath"));
                            }
                            if (paintExperiments)
                            {
                                float physicalScale = scale * viewport.PhysicalScaleX;
                                if (Environment.GetEnvironmentVariable("GEONEX_BENCH_PAINT_MODE") == "bands")
                                {
                                    PolygonBandPainting.Measure(path, bitmap.Width, bitmap.Height, physicalScale);
                                    continue;
                                }
                                MeasureContourCulling(path, localBounds, bitmap.Width, bitmap.Height, physicalScale);
                                MeasureContourCulling(path, path.Bounds, bitmap.Width, bitmap.Height, physicalScale, true);
                                PolygonBandPainting.Measure(path, bitmap.Width, bitmap.Height, physicalScale);
                                OpenGlPolygonMetrics.Measure(path, bitmap.Width, bitmap.Height, physicalScale);
                                PolygonPixelFormatMetrics.Measure(path, bitmap.Width, bitmap.Height, physicalScale);
                            }
                        }
                    }
                }
                return;
            }
            if (basemap)
            {
                await CheckAsyncOnline(client, url, mapType, map, serverType, server, layerBounds, fixture);
                return;
            }
            long frame = 0;
            string? lastFrameUrl = null, lastPayloadId = null;
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
                    lastFrameUrl = FormattableString.Invariant(
                        $"{url}mapa/?w=1600&h=900&dpi=1&nav=1&i=0&fid={++frame}&zoom={camera.Zoom * zoom}&panx={camera.PanX * zoom}&pany={camera.PanY * zoom}");
                    using var response = await client.GetAsync(lastFrameUrl);
                    lastPayloadId = response.Headers.TryGetValues("X-GeoNex-Payload-Id", out var ids) ? ids.Single() : null;
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
                        if (polygonImages) Console.WriteLine($"IMAGE pixels_max_delta={difference} hits={serverType.GetProperty("PolygonImageCacheHits")!.GetValue(server)} builds={serverType.GetProperty("PolygonImageCacheBuilds")!.GetValue(server)} bytes={serverType.GetProperty("PolygonImageCacheBytes")!.GetValue(server)} encoded_hits={serverType.GetProperty("EncodedFrameCacheHits")!.GetValue(server)} encoded_bytes={serverType.GetProperty("EncodedFrameCacheBytes")!.GetValue(server)}");
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
                if (lastPayloadId == null) throw new InvalidOperationException("Payload reuse fixture requires a retained PNG");
                using (var reused = await client.GetAsync(lastFrameUrl + "&reuse=" + lastPayloadId))
                {
                    byte[] body = await reused.Content.ReadAsByteArrayAsync();
                    if ((int)reused.StatusCode != 204 || body.Length != 0 ||
                        reused.Headers.GetValues("X-GeoNex-Reused").Single() != "1" ||
                        reused.Headers.GetValues("X-GeoNex-Payload-Id").Single() != lastPayloadId)
                        throw new InvalidOperationException("Identical frame did not produce explicit bodyless reuse response");
                }
                using (var mismatch = await client.GetAsync(lastFrameUrl + "&reuse=" + new string('0', 32)))
                    if ((int)mismatch.StatusCode != 200 || (await mismatch.Content.ReadAsByteArrayAsync()).Length == 0)
                        throw new InvalidOperationException("Unknown base did not fall back to full PNG");
                Console.WriteLine("PASS production frame reuse: matching identity=204/zero body, unknown identity=200/full PNG");
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

    private static void MeasureContourCulling(SKPath path, SKRect extent, int width, int height, float scale, bool deduplicate = false)
    {
        extent.Inflate(4 / scale, 4 / scale);
        var timer = Stopwatch.StartNew();
        using var culled = PolygonContourCulling.Cull(path, extent, deduplicate: deduplicate);
        double prepare = timer.Elapsed.TotalMilliseconds;
        if (culled == null) { Console.WriteLine("CULL no eligible contours"); return; }
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        using var fill = new SKPaint { Color = SKColors.Cyan.WithAlpha(25), IsAntialias = true };
        using var stroke = new SKPaint { Color = SKColors.Cyan.WithAlpha(200), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 1 / scale, StrokeJoin = SKStrokeJoin.Round };
        var matrix = MapCoordinateFrame.Create(MapViewportMetrics.Create(width, height, 1), SKPoint.Empty, scale).LocalToPhysicalMatrix;
        byte[]? reference = null;
        var directTimes = new List<double>(); var culledTimes = new List<double>();
        for (int round = 0; round < 4; round++)
        foreach (bool useCulled in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
        {
            canvas.Clear(SKColors.Transparent); canvas.SetMatrix(matrix); timer.Restart();
            // Duplicates may carry winding multiplicity. Always retain original fill.
            canvas.DrawPath(useCulled && !deduplicate ? culled : path, fill);
            canvas.DrawPath(useCulled ? culled : path, stroke); canvas.Flush(); timer.Stop();
            byte[] pixels = bitmap.Bytes;
            reference ??= pixels;
            if (!reference.SequenceEqual(pixels)) { Console.WriteLine($"CULL dedup={deduplicate} REJECTED: changed visible pixels"); return; }
            if (round > 0) (useCulled ? culledTimes : directTimes).Add(timer.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"CULL dedup={deduplicate} points={path.PointCount}->{culled.PointCount} prepare_ms={prepare:F2} draw_median_ms={directTimes.Order().ElementAt(1):F2}->{culledTimes.Order().ElementAt(1):F2} pixels=exact");
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

    public static void CompareEncoding(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var pixels = image.PeekPixels();
        // PNG decoding attaches sRGB metadata. Production map surfaces are
        // untagged; model that explicitly without changing any pixel bytes.
        using var rawPixels = new SKPixmap(new SKImageInfo(bitmap.Width, bitmap.Height,
            bitmap.ColorType, bitmap.AlphaType), bitmap.GetPixels(), bitmap.RowBytes);
        using var rawImage = SKImage.FromPixels(rawPixels);
        using (var cache = new EncodedFrameCache())
        {
            byte[]? expected = null;
            for (int sample = 0; sample < 6; sample++)
            {
                bool disabled = sample % 3 == 0;
                long hits = cache.Hits;
                var clock = Stopwatch.StartNew();
                using var encoded = cache.Encode(rawImage, 0, disabled ? 0 : EncodedFrameCache.Budget(1024), default);
                double encodeMs = clock.Elapsed.TotalMilliseconds;
                using var decoded = SKBitmap.Decode(encoded.Resource);
                double totalMs = clock.Elapsed.TotalMilliseconds;
                byte[] bytes = encoded.Resource.ToArray();
                expected ??= bytes;
                if (!expected.SequenceEqual(bytes) || !bitmap.Bytes.SequenceEqual(decoded.Bytes))
                    throw new InvalidOperationException("Encoded payload cache changed bytes/pixels");
                Console.WriteLine($"PAYLOAD sample={sample} disabled={disabled} hit={cache.Hits > hits} encode_ms={encodeMs:F2} decode_total_ms={totalMs:F2} bytes={bytes.Length}");
            }
            int expectedHits = expected!.Length <= EncodedFrameCache.Budget(1024) ? 2 : 0;
            if (cache.Hits != expectedHits) throw new InvalidOperationException("Payload cache did not respect its byte budget");
        }
        Console.WriteLine($"ENCODE adaptive_level={MapFrameEncoding.NavigationCompressionLevel(image, 1024)}");
        using (var untagged = rawImage.PeekPixels())
            Console.WriteLine($"ENCODE adaptive_filter={MapFrameEncoding.SelectFilter(untagged!, 1)}");
        for (int sample = 0; sample < 3; sample++)
        {
            var clock = Stopwatch.StartNew();
            using var encoded = MapFrameEncoding.EncodePng(rawImage, 1);
            double encodeMs = clock.Elapsed.TotalMilliseconds;
            using var decoded = SKBitmap.Decode(encoded);
            double totalMs = clock.Elapsed.TotalMilliseconds;
            if (!bitmap.Bytes.SequenceEqual(decoded.Bytes)) throw new InvalidDataException("Adaptive PNG pixels changed");
            Console.WriteLine($"ENCODE adaptive ms={encodeMs:F2} decode_total_ms={totalMs:F2} bytes={encoded.Size}");
        }
        foreach (var filter in new[] { SKPngEncoderFilterFlags.None, SKPngEncoderFilterFlags.Up, SKPngEncoderFilterFlags.Sub })
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
