using System.Xml.Linq;
using GeoNex.Services;

internal static class OnlineBasemapContracts
{
    public static void Run()
    {
        Assert(OnlineBasemapPolicy.TryResolve("osm", out var osm), "case-insensitive provider lookup");
        Assert(osm.ServerUrl.StartsWith("https://tile.openstreetmap.org/", StringComparison.Ordinal),
            "official OSM tile host");
        Assert(osm.MaximumZoom == 19 && osm.MaximumConnections == 2 && osm.BandCount == 3,
            "OSM pyramid and conservative connection budget");
        Assert(OnlineBasemapPolicy.TryResolve("ESRI", out var esri) && esri.MaximumZoom == 22,
            "ESRI deep pyramid within GDAL raster limits");
        Assert(OnlineBasemapPolicy.TryResolve("Google", out var google) && google.MaximumZoom == 20,
            "Google pyramid level");
        Assert(!OnlineBasemapPolicy.TryResolve("unknown", out _), "unknown provider rejected");

        const string cachePath = @"C:\GeoNex cache\tiles & metadata";
        var cache = new OnlineBasemapCacheSettings(cachePath, 256, 604800, 900);
        XDocument xml = XDocument.Parse(OnlineBasemapPolicy.BuildGdalTmsXml(osm, cache));
        XElement root = xml.Root ?? throw new InvalidOperationException("missing GDAL_WMS root");
        Assert(root.Element("Service")?.Attribute("name")?.Value == "TMS", "TMS mini-driver");
        Assert(root.Descendants("ServerUrl").Single().Value == osm.ServerUrl, "escaped tile URL round-trip");
        Assert(root.Descendants("TileLevel").Single().Value == "19", "provider zoom pyramid");
        Assert(root.Descendants("YOrigin").Single().Value == "top", "XYZ tile origin");
        Assert(root.Descendants("Projection").Single().Value == "EPSG:3857", "Web Mercator projection");
        Assert(root.Descendants("Path").Single().Value == cachePath, "escaped cache path round-trip");
        Assert(root.Descendants("MaxSize").Single().Value == (256L * 1024 * 1024).ToString(),
            "cache size emitted in bytes");
        Assert(root.Descendants("Expires").Single().Value == "604800", "minimum seven-day tile lifetime");
        Assert(root.Descendants("AdviseRead").Single().Value == "false", "no bulk prefetch");
        Assert(!xml.ToString().Contains("UnsafeSSL", StringComparison.OrdinalIgnoreCase),
            "TLS verification remains enabled");

        Assert(CreateCache(1500).MaximumSizeMb == 128, "low-memory cache budget");
        Assert(CreateCache(3000).MaximumSizeMb == 256, "notebook cache budget");
        Assert(CreateCache(8000).MaximumSizeMb == 512, "desktop cache budget");
        Assert(CreateCache(3000, "2048").MaximumSizeMb == 2048, "explicit cache override");

        Assert(OnlineBasemapPolicy.CanUseDirectRasterIo(
            true, false, "EPSG:3857", "project", (_, _) => true),
            "same-SRS online tiles use direct RasterIO");
        Assert(!OnlineBasemapPolicy.CanUseDirectRasterIo(
            true, true, "EPSG:3857", "project", (_, _) => true),
            "rotation keeps Warp fallback");
        Assert(!OnlineBasemapPolicy.CanUseDirectRasterIo(
            true, false, "EPSG:3857", "project", (_, _) => false),
            "reprojection keeps Warp fallback");
        Assert(OnlineBasemapPolicy.CalculateRenderDimensions(1920, 1080, 1500, false) == new RasterDimensions(1920, 1080),
            "notebook final keeps physical screen resolution");
        Assert(OnlineBasemapPolicy.CalculateRenderDimensions(3840, 2160, 3000, false) == new RasterDimensions(3840, 2160),
            "HiDPI/4K final does not inherit the old 1600px cap");
        var preview = OnlineBasemapPolicy.CalculateRenderDimensions(3840, 2160, 3000, true);
        Assert(OnlineBasemapPolicy.CalculateDirectRenderDimensions(3840, 2160, 768) == new RasterDimensions(3840, 2160),
            "strip reader preserves 4K with bounded scratch on a notebook");
        var bounded = OnlineBasemapPolicy.CalculateDirectRenderDimensions(32768, 32768, 256);
        Assert((long)bounded.Width * bounded.Height * 8 + 8L * 1024 * 1024 <= 256L * 1024 * 1024 / 8,
            "direct reader bounds two frames and strip scratch under pressure");
        Assert(preview.Width <= 512 && preview.Height <= 512, "interaction stays bounded");
        var pressure = OnlineBasemapPolicy.CalculateRenderDimensions(7680, 4320, 256, false);
        Assert((long)pressure.Width * pressure.Height <= RasterRenderingPolicy.CalculateMaxFramePixels(256),
            "low-memory final respects pixel budget");
        Assert(OnlineBasemapPolicy.CalculateRenderDimensions(256, 256, 3000, false) == new RasterDimensions(256, 256),
            "never download more resolution than requested");
        string previewKey = RasterRenderingPolicy.CacheKeyForQuality("same-camera", true);
        string finalKey = RasterRenderingPolicy.CacheKeyForQuality("same-camera", false);
        var simulatedCache = new Dictionary<string, RasterDimensions> { [previewKey] = preview };
        Assert(!simulatedCache.ContainsKey(finalKey), "preview cannot satisfy final refinement at identical camera");
        simulatedCache[finalKey] = OnlineBasemapPolicy.CalculateRenderDimensions(3840, 2160, 3000, false);
        Assert(simulatedCache[finalKey].Width == 3840 && simulatedCache[previewKey].Width == 512,
            "preview and final retain separate quality identities");

        Console.WriteLine("Online basemap contracts: PASS (pyramids, cache, TLS, RasterIO, physical resolution/DPI, pressure, preview/final isolation)");
    }

    private static OnlineBasemapCacheSettings CreateCache(int availableMb, string? configuredMb = null) =>
        OnlineBasemapPolicy.CreateCacheSettings(
            availableMb,
            name => name == "GEONEX_BASEMAP_CACHE_MB" ? configuredMb : null,
            @"C:\GeoNex-test-cache");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Online basemap contract failed: {message}");
    }
}
