using System.Diagnostics;
using System.Reflection;
using System.Collections;
using GeoNex.Services;
using OSGeo.GDAL;
using SkiaSharp;
using System.Xml.Linq;

// Exercises the actual HTTP renderer with a read-only raster and optional vector
// overlay. It never modifies the raster source or builds raster overviews.
internal static class ProductionRasterMetrics
{
    public static async Task Run(string assemblyPath, string source, string? vectorSource = null)
    {
        GdalRuntimeBootstrap.Configure();
        Gdal.SetConfigOption("GDAL_PAM_ENABLED", "NO");
        Environment.SetEnvironmentVariable("GEONEX_RENDER_METRICS", "1");
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var mapType = assembly.GetType("GeoNex.Services.MapRenderingService", true)!;
        var serverType = assembly.GetType("GeoNex.Services.LocalMapServer", true)!;
        bool onlineLifecycle = Environment.GetEnvironmentVariable("GEONEX_BENCH_ONLINE") == "1";
        string? onlineXml = null;
        if (onlineLifecycle)
        {
            var config = XDocument.Load(Path.GetFullPath(source));
            string? tileServerTemplate = Environment.GetEnvironmentVariable("GEONEX_BENCH_TILE_SERVER_URL");
            if (!string.IsNullOrWhiteSpace(tileServerTemplate))
                config.Root?.Element("Service")?.SetElementValue("ServerUrl", tileServerTemplate);
            if (config.Root?.Element("Cache") == null)
            {
                string cachePath = Path.Combine(Path.GetTempPath(), $"GeoNexTileMetrics-{Guid.NewGuid():N}");
                Directory.CreateDirectory(cachePath);
                config.Root!.Add(new XElement("Cache",
                    new XElement("Path", cachePath), new XElement("Depth", 2),
                    new XElement("Expires", 86400), new XElement("MaxSize", 67108864),
                    new XElement("CleanTimeout", 900), new XElement("Unique", "true")));
            }
            onlineXml = config.ToString(SaveOptions.DisableFormatting);
        }
        using var map = (IDisposable)Activator.CreateInstance(mapType)!;
        var timer = Stopwatch.StartNew();
        string rasterPath = Path.GetFullPath(source);
        string? requestedDriver = Environment.GetEnvironmentVariable("GEONEX_BENCH_GDAL_DRIVER");
        string[] openOptions = (Environment.GetEnvironmentVariable("GEONEX_BENCH_GDAL_OPEN_OPTIONS") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Dataset? dataset = string.IsNullOrWhiteSpace(requestedDriver)
            ? Gdal.Open(rasterPath, Access.GA_ReadOnly)
            : Gdal.OpenEx(rasterPath, 2, [requestedDriver], openOptions, null);
        if (dataset is null) throw new IOException(Gdal.GetLastErrorMsg());
        try { mapType.GetMethod("PublishRaster")!.Invoke(map, ["raster", dataset, onlineXml]); }
        catch { dataset.Dispose(); throw; }
        Console.WriteLine(FormattableString.Invariant($"RASTER open_ms={timer.Elapsed.TotalMilliseconds:F3} source={source}"));
        double[] transform = new double[6]; dataset.GetGeoTransform(transform);
        var bounds = RasterGeometry.GetBounds(RasterGeometry.GetBoundaryPoints(transform, dataset.RasterXSize, dataset.RasterYSize));
        double cx = (bounds.MinX + bounds.MaxX) / 2, cy = (bounds.MinY + bounds.MaxY) / 2;
        mapType.GetProperty("OffsetMundoX")!.SetValue(map, cx);
        mapType.GetProperty("OffsetMundoY")!.SetValue(map, cy);
        mapType.GetProperty("OffsetMundoDefinido")!.SetValue(map, true);
        mapType.GetProperty("ProjetoSRS")!.SetValue(map, dataset.GetProjection());
        var scene = new SKRect((float)(bounds.MinX - cx), (float)(cy - bounds.MaxY),
            (float)(bounds.MaxX - cx), (float)(cy - bounds.MinY));
        ((IDictionary<string, SKRect>)mapType.GetProperty("LimitesRasters")!.GetValue(map)!).Add("raster", scene);
        ((IList<string>)mapType.GetProperty("OrdemCamadas")!.GetValue(map)!).Add("raster");
        if (!string.IsNullOrWhiteSpace(vectorSource))
        {
            mapType.GetProperty("ProjetoSRS")!.SetValue(map, dataset.GetProjection());
            var projectType = assembly.GetType("GeoNex.Services.ProjetoService", true)!;
            var project = Activator.CreateInstance(projectType)!;
            var conversionClock = Stopwatch.StartNew();
            MethodInfo compileMethod = projectType.GetMethods().Single(method =>
                method.Name == "CompilarParaShapefileNativo" &&
                method.GetParameters() is { Length: 1 } parameters &&
                parameters[0].ParameterType == typeof(string));
            string vectorPath = Path.GetFullPath(vectorSource);
            string compiled = (string)compileMethod.Invoke(project, [vectorPath])!;
            Console.WriteLine($"MIXED vector_conversion_ms={conversionClock.Elapsed.TotalMilliseconds:F2} source={vectorPath}");
            conversionClock.Restart();
            projectType.GetMethod("CarregarShapefileParaMotorMapas")!.Invoke(project,
                [compiled, "benchmark", map, false, null, true]);
            Console.WriteLine($"MIXED vector_load_ms={conversionClock.Elapsed.TotalMilliseconds:F2}");
            var styles = (IDictionary)mapType.GetProperty("EstilosPorCamada")!.GetValue(map)!;
            object style = styles["benchmark"] ?? Activator.CreateInstance(
                assembly.GetType("GeoNex.Services.EstiloCamada", true)!)!;
            styles["benchmark"] = style;
            style.GetType().GetProperty("CorBorda")!.SetValue(style, "#00ffff");
            style.GetType().GetProperty("CorPreenchimento")!.SetValue(style, "transparent");
            style.GetType().GetProperty("PreenchimentoTransparente")!.SetValue(style, true);
            style.GetType().GetProperty("EspessuraBorda")!.SetValue(style, 0.75f);
            style.GetType().GetProperty("Opacidade")!.SetValue(style, 1f);
            var featureLayers = mapType.GetProperty("FeaturesPorCamada")!.GetValue(map)!;
            object features = featureLayers.GetType().GetProperty("Item")!.GetValue(featureLayers, ["benchmark"])!;
            int featureCount = (int)features.GetType().GetProperty("Count")!.GetValue(features)!;
            Console.WriteLine(FormattableString.Invariant(
                $"MIXED raster_scene={scene} combined_scene={mapType.GetMethod("GetSceneBounds")!.Invoke(map, null)} features={featureCount}"));
        }
        using (var driver = dataset.GetDriver())
        using (var band = dataset.GetRasterBand(1))
        {
            band.GetBlockSize(out int blockX, out int blockY);
            Console.WriteLine($"RASTER driver={driver.ShortName} size={dataset.RasterXSize}x{dataset.RasterYSize} bands={dataset.RasterCount} block={blockX}x{blockY} overviews={band.GetOverviewCount()}");
        }
        int dpi = int.TryParse(Environment.GetEnvironmentVariable("GEONEX_BENCH_DPI"), out int requested) ? requested : 2;
        if (dpi is < 1 or > 4) throw new ArgumentOutOfRangeException("GEONEX_BENCH_DPI");
        string? output = Environment.GetEnvironmentVariable("GEONEX_BENCH_FRAME_DIRECTORY");
        string? reference = Environment.GetEnvironmentVariable("GEONEX_BENCH_REFERENCE_DIRECTORY");
        double Focus(string axis) => double.TryParse(Environment.GetEnvironmentVariable("GEONEX_BENCH_RASTER_FOCUS_" + axis),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) &&
            double.IsFinite(value) && value is >= 0 and <= 1 ? value : .5;
        double focusX = Focus("X"), focusY = Focus("Y");
        Console.WriteLine(FormattableString.Invariant($"RASTER focus={focusX},{focusY} dpi={dpi} vector={(string.IsNullOrWhiteSpace(vectorSource) ? "none" : Path.GetFileName(vectorSource))}"));
        int[] zooms = (Environment.GetEnvironmentVariable("GEONEX_BENCH_RASTER_ZOOMS") ?? "1,8,64")
            .Split(',').Select(int.Parse).ToArray();
        if (zooms.Any(z => z < 1 || z > 1024)) throw new ArgumentOutOfRangeException("GEONEX_BENCH_RASTER_ZOOMS");
        int seedZoom = int.TryParse(Environment.GetEnvironmentVariable("GEONEX_BENCH_SEED_ZOOM"), out int requestedSeedZoom)
            ? requestedSeedZoom : 0;
        if (seedZoom is < 0 or > 1024) throw new ArgumentOutOfRangeException("GEONEX_BENCH_SEED_ZOOM");
        bool interactivePan = Environment.GetEnvironmentVariable("GEONEX_BENCH_INTERACTIVE") == "1";
        bool globalPreviewPan = interactivePan && Environment.GetEnvironmentVariable("GEONEX_BENCH_GLOBAL_PREVIEW") == "1";
        bool progressiveStartup = Environment.GetEnvironmentVariable("GEONEX_BENCH_PROGRESSIVE_STARTUP") == "1";
        bool startupNoPadding = Environment.GetEnvironmentVariable("GEONEX_BENCH_STARTUP_NO_PADDING") == "1";
        if (progressiveStartup && (interactivePan || zooms.Length != 1))
            throw new ArgumentException("Progressive startup benchmark requires one zoom and GEONEX_BENCH_INTERACTIVE unset.");
        int sampleCount = int.TryParse(Environment.GetEnvironmentVariable("GEONEX_BENCH_SAMPLES"), out int requestedSamples)
            ? requestedSamples : 7;
        if (sampleCount is < 1 or > 50) throw new ArgumentOutOfRangeException("GEONEX_BENCH_SAMPLES");
        int panStepCss = int.TryParse(Environment.GetEnvironmentVariable("GEONEX_BENCH_PAN_STEP_CSS"), out int requestedStep)
            ? requestedStep : 32;
        if (panStepCss < 0 || panStepCss > 4096) throw new ArgumentOutOfRangeException("GEONEX_BENCH_PAN_STEP_CSS");
        if (!string.IsNullOrEmpty(output)) Directory.CreateDirectory(output);
        var server = Activator.CreateInstance(serverType, map)!;
        serverType.GetMethod("Start")!.Invoke(server, null);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            string url = (string)serverType.GetProperty("BaseUrl")!.GetValue(server)!;
            if (onlineLifecycle)
            {
                await RunOnlineLifecycle(client, url, mapType, map, serverType, server, scene,
                    zooms, focusX, focusY, dpi, panStepCss, output);
                return;
            }
            int frame = 0;
            foreach (int zoom in zooms)
            {
                var frameMilliseconds = new List<double>(sampleCount);
                var combinedScene = (SKRect)mapType.GetMethod("GetSceneBounds")!.Invoke(map, null)!;
                double scale = Math.Min(1600 / scene.Width, 900 / scene.Height) * .8 * zoom;
                double sceneScale = Math.Min(1600 / combinedScene.Width, 900 / combinedScene.Height) * .8;
                double cameraZoom = scale / sceneScale;
                double focusLocalX = scene.Left + scene.Width * focusX;
                double focusLocalY = scene.Top + scene.Height * focusY;
                double panX = zoom == 1 ? (combinedScene.MidX - scene.MidX) * scale
                    : (combinedScene.MidX - focusLocalX) * scale;
                double panY = zoom == 1 ? (combinedScene.MidY - scene.MidY) * scale
                    : (combinedScene.MidY - focusLocalY) * scale;

                if (progressiveStartup)
                {
                    await RunProgressiveStartup(client, url, zoom, cameraZoom, panX, panY, dpi,
                        frame, output, reference, startupNoPadding);
                    return;
                }

                if (interactivePan)
                {
                    int settledZoom = seedZoom == 0 ? zoom : seedZoom;
                    double settledScale = Math.Min(1600 / scene.Width, 900 / scene.Height) * .8 * settledZoom;
                    double settledPanX = settledZoom == 1 ? (combinedScene.MidX - scene.MidX) * settledScale
                        : (combinedScene.MidX - focusLocalX) * settledScale;
                    double settledPanY = settledZoom == 1 ? (combinedScene.MidY - scene.MidY) * settledScale
                        : (combinedScene.MidY - focusLocalY) * settledScale;
                    // Seed a full-quality per-layer frame, then evict only the
                    // scene cache so each following request measures layer reuse.
                    mapType.GetMethod("InvalidateGlobalCache")!.Invoke(map, null);
                    using var settled = await client.GetAsync(FormattableString.Invariant(
                        $"{url}mapa/?w=1600&h=900&dpi={dpi}&nav=1&i=0&fid={++frame}&zoom={settledScale / sceneScale}&panx={settledPanX}&pany={settledPanY}"));
                    settled.EnsureSuccessStatusCode();
                    await settled.Content.ReadAsByteArrayAsync();
                    if (seedZoom != 0)
                        Console.WriteLine(FormattableString.Invariant(
                            $"RASTER transition seed_zoom={settledZoom} target_zoom={zoom} seed_pan={settledPanX:F1},{settledPanY:F1} target_pan={panX:F1},{panY:F1}"));
                    object?[] rasterArgs = { "raster", null };
                    using var rasterLease = (IDisposable?)mapType.GetMethod("AcquireRasterCache")!.Invoke(map, rasterArgs);
                    if (rasterLease != null)
                    {
                        object cachedRaster = rasterLease.GetType().GetProperty("Resource")!.GetValue(rasterLease)!;
                        Type cachedRasterType = cachedRaster.GetType();
                        Console.WriteLine($"RASTER settled_cache zoom={zoom} size={cachedRasterType.GetProperty("Width")!.GetValue(cachedRaster)}x{cachedRasterType.GetProperty("Height")!.GetValue(cachedRaster)}");
                    }
                }

                for (int sample = 0; sample < sampleCount; sample++)
                {
                    // Drop the composed scene but preserve the per-layer raster
                    // cache, matching a camera gesture after a settled frame.
                    if (interactivePan && !globalPreviewPan)
                        mapType.GetMethod("InvalidateGlobalCache")!.Invoke(map, null);
                    else if (!interactivePan)
                        mapType.GetMethod("InvalidateRasterPresentationCache")!.Invoke(map, null);
                    timer.Restart();
                    using var response = await client.GetAsync(FormattableString.Invariant(
                        $"{url}mapa/?w=1600&h=900&dpi={dpi}&nav=1&i={(interactivePan ? 1 : 0)}&fid={++frame}&zoom={cameraZoom}&panx={panX + sample * panStepCss}&pany={panY}"));
                    response.EnsureSuccessStatusCode();
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                    double http = timer.Elapsed.TotalMilliseconds;
                    frameMilliseconds.Add(http);
                    using var image = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("Invalid raster PNG");
                    int? gesturePadding = interactivePan
                        ? NavigationFramePolicy.GesturePadding(1600, 900, dpi)
                        : null;
                    var content = ValidateVisibleRasterContent(image, dpi, gesturePadding);
                    Console.WriteLine(FormattableString.Invariant(
                        $"RASTER content zoom={zoom} sample={sample} visible_alpha={content.Coverage:P1} sampled_colors={content.SampledColors}"));
                    Console.WriteLine(FormattableString.Invariant($"RASTER zoom={zoom} sample={sample} interactive={interactivePan} http_ms={http:F3} bytes={bytes.Length} {string.Join(" ", response.Headers.GetValues("Server-Timing"))}"));
                    string name = $"zoom-{zoom}-frame-{sample}.png";
                    if (!string.IsNullOrEmpty(output)) await File.WriteAllBytesAsync(Path.Combine(output, name), bytes);
                    if (!interactivePan && !string.IsNullOrEmpty(reference))
                    {
                        using var expected = SKBitmap.Decode(Path.Combine(reference, name)) ?? throw new InvalidDataException("Missing reference");
                        // RGB PNG truthfully declares opaque alpha, whereas its
                        // RGBA reference may declare premultiplication. Compare
                        // storage channel order/dimensions and every decoded byte.
                        if (expected.Width != image.Width || expected.Height != image.Height ||
                            expected.ColorType != image.ColorType || !expected.Bytes.SequenceEqual(image.Bytes))
                            throw new InvalidDataException($"Raster pixels changed: {name}");
                        Console.WriteLine($"RASTER pixels zoom={zoom} sample={sample} exact=True");
                    }
                }
                double[] warm = frameMilliseconds.Skip(1).Order().ToArray();
                if (warm.Length > 0)
                {
                    int p95Index = Math.Clamp((int)Math.Ceiling(warm.Length * .95) - 1, 0, warm.Length - 1);
                    double p50 = warm.Length % 2 == 0
                        ? (warm[warm.Length / 2 - 1] + warm[warm.Length / 2]) / 2
                        : warm[warm.Length / 2];
                    Console.WriteLine(FormattableString.Invariant(
                        $"RASTER summary zoom={zoom} interactive={interactivePan} http_p50_ms={p50:F3} http_p95_ms={warm[p95Index]:F3} warm_samples={warm.Length}"));
                }
            }
        }
        finally { serverType.GetMethod("Stop")!.Invoke(server, null); }
    }

    private static async Task RunProgressiveStartup(HttpClient client, string url, int zoom,
        double cameraZoom, double panX, double panY, int dpi, int frame, string? output, string? reference,
        bool startupNoPadding)
    {
        float previewDpi = RasterRenderingPolicy.SelectInteractivePreviewDpi(dpi, true,
            rasterContent: true, startupPreview: startupNoPadding);
        int navigationPadding = NavigationFramePolicy.Padding(1600, 900, dpi);
        int previewPadding = startupNoPadding ? 0 : navigationPadding;
        Console.WriteLine(FormattableString.Invariant(
            $"STARTUP camera zoom={zoom} camera_zoom={cameraZoom:F6} pan={panX:F1},{panY:F1} requested_dpi={dpi} preview_dpi={previewDpi:F2} preview_padding_css={previewPadding}"));

        await Capture("preview", interactive: true);
        await Capture("final", interactive: false);

        async Task Capture(string stage, bool interactive)
        {
            int currentFrame = frame + (interactive ? 1 : 2);
            bool unpaddedStartupPreview = interactive && startupNoPadding;
            int framePadding = unpaddedStartupPreview ? 0 : navigationPadding;
            string requestUrl = FormattableString.Invariant(
                $"{url}mapa/?w=1600&h=900&dpi={dpi}&nav=1&i={(interactive ? 1 : 0)}&startup={(unpaddedStartupPreview ? 1 : 0)}&fid={currentFrame}&zoom={cameraZoom:R}&panx={panX:R}&pany={panY:R}");
            var timer = Stopwatch.StartNew();
            using var response = await client.GetAsync(requestUrl);
            response.EnsureSuccessStatusCode();
            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            double httpMs = timer.Elapsed.TotalMilliseconds;
            var decodeTimer = Stopwatch.StartNew();
            using var image = SKBitmap.Decode(bytes) ?? throw new InvalidDataException($"Invalid {stage} PNG");
            double decodeMs = decodeTimer.Elapsed.TotalMilliseconds;
            var content = ValidateVisibleRasterContent(image, dpi, framePadding);
            string timings = response.Headers.TryGetValues("Server-Timing", out var values)
                ? string.Join(" ", values)
                : "server_timing=unavailable";
            Console.WriteLine(FormattableString.Invariant(
                $"STARTUP stage={stage} http_ms={httpMs:F3} decode_ms={decodeMs:F3} bytes={bytes.Length} image={image.Width}x{image.Height} visible_alpha={content.Coverage:P1} sampled_colors={content.SampledColors} {timings}"));

            string fileName = $"startup-{stage}.png";
            if (!string.IsNullOrEmpty(output))
                await File.WriteAllBytesAsync(Path.Combine(output, fileName), bytes);

            if (!interactive && !string.IsNullOrEmpty(reference))
            {
                using var expected = SKBitmap.Decode(Path.Combine(reference, fileName))
                    ?? throw new InvalidDataException($"Missing reference: {fileName}");
                if (expected.Width != image.Width || expected.Height != image.Height ||
                    expected.ColorType != image.ColorType || !expected.Bytes.SequenceEqual(image.Bytes))
                    throw new InvalidDataException($"Final raster pixels changed: {fileName}");
                Console.WriteLine("STARTUP final_pixels_exact=True");
            }
        }
    }

    private static (double Coverage, int SampledColors) ValidateVisibleRasterContent(SKBitmap image,
        float dpi, int? paddingCssOverride = null)
    {
        const int visibleWidthCss = 1600, visibleHeightCss = 900, gridWidth = 64, gridHeight = 36;
        int paddingCss = paddingCssOverride ?? NavigationFramePolicy.Padding(visibleWidthCss, visibleHeightCss, dpi);
        double renderScaleX = image.Width / (double)(visibleWidthCss + 2 * paddingCss);
        double renderScaleY = image.Height / (double)(visibleHeightCss + 2 * paddingCss);
        int left = (int)Math.Round(paddingCss * renderScaleX, MidpointRounding.AwayFromZero);
        int top = (int)Math.Round(paddingCss * renderScaleY, MidpointRounding.AwayFromZero);
        int visibleWidth = (int)Math.Round(visibleWidthCss * renderScaleX, MidpointRounding.AwayFromZero);
        int visibleHeight = (int)Math.Round(visibleHeightCss * renderScaleY, MidpointRounding.AwayFromZero);
        int covered = 0;
        var colors = new HashSet<uint>();
        SKColor[] pixels = image.Pixels;
        for (int gy = 0; gy < gridHeight; gy++)
        for (int gx = 0; gx < gridWidth; gx++)
        {
            int x = Math.Min(image.Width - 1, left + (int)((gx + 0.5) * visibleWidth / gridWidth));
            int y = Math.Min(image.Height - 1, top + (int)((gy + 0.5) * visibleHeight / gridHeight));
            SKColor pixel = pixels[y * image.Width + x];
            if (pixel.Alpha == 0) continue;
            covered++;
            colors.Add(((uint)pixel.Red << 16) | ((uint)pixel.Green << 8) | pixel.Blue);
        }

        double coverage = covered / (double)(gridWidth * gridHeight);
        if (coverage < 0.05 || colors.Count < 64)
            throw new InvalidDataException(FormattableString.Invariant(
                $"Invalid raster focus: visible coverage={coverage:P1}, sampled colors={colors.Count}. Choose a covered area with GEONEX_BENCH_RASTER_FOCUS_X/Y."));
        return (coverage, colors.Count);
    }

    private static async Task RunOnlineLifecycle(HttpClient client, string url, Type mapType, object map,
        Type serverType, object server, SKRect scene, int[] zooms, double focusX, double focusY,
        int dpi, int panStepCss, string? output)
    {
        int frame = 0;
        foreach (int zoom in zooms)
        for (int sample = 0; sample < 7; sample++)
        {
            mapType.GetMethod("InvalidateRasterPresentationCache")!.Invoke(map, null);
            double scale = Math.Min(1600 / scene.Width, 900 / scene.Height) * .8 * zoom;
            double panX = zoom == 1 ? 0 : -scene.Width * (focusX - .5) * scale;
            double panY = zoom == 1 ? 0 : -scene.Height * (focusY - .5) * scale;
            double cameraX = panX + sample * panStepCss, cameraY = panY;
            string requestUrl = FormattableString.Invariant(
                $"{url}mapa/?w=1600&h=900&dpi={dpi}&nav=1&i=0&fid={++frame}&zoom={zoom}&panx={cameraX}&pany={cameraY}");
            var clock = Stopwatch.StartNew();
            using (var response = await client.GetAsync(requestUrl))
                response.EnsureSuccessStatusCode();
            double firstResponseMs = clock.Elapsed.TotalMilliseconds;
            double? firstPartialMs = null;
            while (true)
            {
                object?[] args = { "raster", null };
                using var lease = (IDisposable?)mapType.GetMethod("AcquireRasterCache")!.Invoke(map, args);
                if (lease != null)
                {
                    object metadata = args[1]!;
                    Type metadataType = metadata.GetType();
                    string key = (string)metadataType.GetProperty("CacheKey")!.GetValue(metadata)!;
                    double cachedX = (double)metadataType.GetProperty("PanX")!.GetValue(metadata)!;
                    double cachedY = (double)metadataType.GetProperty("PanY")!.GetValue(metadata)!;
                    if (cachedX == cameraX && cachedY == cameraY)
                    {
                        if (key.EndsWith(":partial", StringComparison.Ordinal))
                            firstPartialMs ??= clock.Elapsed.TotalMilliseconds;
                        else break;
                    }
                }
                if (clock.Elapsed > TimeSpan.FromMinutes(2))
                    throw new TimeoutException($"Online tile render timed out: sample={sample}");
                await Task.Delay(2);
            }
            double fullReadyMs = clock.Elapsed.TotalMilliseconds;

            clock.Restart();
            using var finalResponse = await client.GetAsync(FormattableString.Invariant(
                $"{url}mapa/?w=1600&h=900&dpi={dpi}&nav=1&i=0&fid={++frame}&zoom={zoom}&panx={cameraX}&pany={cameraY}"));
            finalResponse.EnsureSuccessStatusCode();
            byte[] bytes = await finalResponse.Content.ReadAsByteArrayAsync();
            double finalFrameMs = clock.Elapsed.TotalMilliseconds;
            using var image = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("Invalid online tile PNG");
            if (!image.Pixels.Any(p => p.Alpha != 0 && (p.Red != 0 || p.Green != 0 || p.Blue != 0)))
                throw new InvalidDataException("Final online tile frame is empty");
            string name = $"zoom-{zoom}-frame-{sample}.png";
            if (!string.IsNullOrEmpty(output)) await File.WriteAllBytesAsync(Path.Combine(output, name), bytes);
            long tileDownloads = (long)serverType.GetProperty("OnlineTileDownloads")!.GetValue(server)!;
            long tileCacheHits = (long)serverType.GetProperty("OnlineTileCacheHits")!.GetValue(server)!;
            long validationSkips = (long)serverType.GetProperty("OnlineTileCacheValidationSkips")!.GetValue(server)!;
            string timings = string.Join(" ", finalResponse.Headers.GetValues("Server-Timing"));
            Console.WriteLine(FormattableString.Invariant(
                $"RASTER online zoom={zoom} sample={sample} first_http_ms={firstResponseMs:F3} first_partial_ms={(firstPartialMs ?? 0):F3} final_ready_ms={fullReadyMs:F3} cached_frame_ms={finalFrameMs:F3} bytes={bytes.Length} tile_downloads={tileDownloads} tile_cache_hits={tileCacheHits} cache_validation_skips={validationSkips} {timings}"));
        }
    }
}
