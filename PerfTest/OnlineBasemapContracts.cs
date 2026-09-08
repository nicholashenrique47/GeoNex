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
        Assert(OnlineBasemapPolicy.CalculateRenderDimensionCap(1500, false) == 1280 &&
               OnlineBasemapPolicy.CalculateRenderDimensionCap(3000, false) == 1600 &&
               OnlineBasemapPolicy.CalculateRenderDimensionCap(8000, false) == 2048 &&
               OnlineBasemapPolicy.CalculateRenderDimensionCap(8000, true) == 512,
            "adaptive online frame caps");

        Console.WriteLine("Online basemap contracts: PASS (pyramids, cache, TLS, direct RasterIO, notebook caps)");
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
