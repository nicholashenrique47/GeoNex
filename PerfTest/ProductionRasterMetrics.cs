using System.Diagnostics;
using System.Reflection;
using GeoNex.Services;
using OSGeo.GDAL;
using SkiaSharp;
using System.Xml.Linq;

// Exercises the actual HTTP renderer with a read-only raster, without importing,
// building overviews, or writing auxiliary files alongside the source.
internal static class ProductionRasterMetrics
{
    public static async Task Run(string assemblyPath, string source)
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
        Console.WriteLine(FormattableString.Invariant($"RASTER focus={focusX},{focusY} dpi={dpi}"));
        int[] zooms = (Environment.GetEnvironmentVariable("GEONEX_BENCH_RASTER_ZOOMS") ?? "1,8,64")
            .Split(',').Select(int.Parse).ToArray();
        if (zooms.Any(z => z < 1 || z > 1024)) throw new ArgumentOutOfRangeException("GEONEX_BENCH_RASTER_ZOOMS");
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
            for (int sample = 0; sample < 7; sample++)
            {
                // Shift each frame so neither scene nor per-layer cache masks work.
                mapType.GetMethod("InvalidateRasterPresentationCache")!.Invoke(map, null);
                double scale = Math.Min(1600 / scene.Width, 900 / scene.Height) * .8 * zoom;
                double panX = zoom == 1 ? 0 : -scene.Width * (focusX - .5) * scale;
                double panY = zoom == 1 ? 0 : -scene.Height * (focusY - .5) * scale;
                timer.Restart();
                using var response = await client.GetAsync(FormattableString.Invariant(
                    $"{url}mapa/?w=1600&h=900&dpi={dpi}&nav=1&i=0&fid={++frame}&zoom={zoom}&panx={panX + sample * panStepCss}&pany={panY}"));
                response.EnsureSuccessStatusCode();
                byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                double http = timer.Elapsed.TotalMilliseconds;
                using var image = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("Invalid raster PNG");
                if (!image.Pixels.Any(p => p.Alpha != 0 && (p.Red != 0 || p.Green != 0 || p.Blue != 0)))
                    throw new InvalidDataException("Empty raster focus: select a covered area with GEONEX_BENCH_RASTER_FOCUS_X/Y");
                Console.WriteLine(FormattableString.Invariant($"RASTER zoom={zoom} sample={sample} http_ms={http:F3} bytes={bytes.Length} {string.Join(" ", response.Headers.GetValues("Server-Timing"))}"));
                string name = $"zoom-{zoom}-frame-{sample}.png";
                if (!string.IsNullOrEmpty(output)) await File.WriteAllBytesAsync(Path.Combine(output, name), bytes);
                if (!string.IsNullOrEmpty(reference))
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
        }
        finally { serverType.GetMethod("Stop")!.Invoke(server, null); }
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
