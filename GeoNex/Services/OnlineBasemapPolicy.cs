using System.Globalization;
using System.Xml.Linq;

namespace GeoNex.Services;

public sealed record OnlineBasemapDefinition(
    string Key,
    string LayerName,
    string ServerUrl,
    int MaximumZoom,
    int BandCount,
    int MaximumConnections);

public readonly record struct OnlineBasemapCacheSettings(
    string Path,
    int MaximumSizeMb,
    int ExpirationSeconds,
    int CleanIntervalSeconds)
{
    public long MaximumSizeBytes => checked(MaximumSizeMb * 1024L * 1024L);
}

public static class OnlineBasemapPolicy
{
    public const string WebMercatorSrs = "EPSG:3857";
    public const string UserAgent = "GeoNex/1.0 (desktop GIS)";
    public const int RequestTimeoutSeconds = 15;
    public const int ConnectTimeoutSeconds = 5;

    private const int MinimumCacheMb = 64;
    private const int MaximumCacheMb = 4096;
    private const int SevenDaysInSeconds = 7 * 24 * 60 * 60;

    private static readonly IReadOnlyDictionary<string, OnlineBasemapDefinition> Definitions =
        new Dictionary<string, OnlineBasemapDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            // OSM asks clients to use this host, identify the application and avoid aggressive concurrency.
            ["OSM"] = new(
                "OSM",
                "OpenStreetMap",
                "https://tile.openstreetmap.org/${z}/${x}/${y}.png",
                19,
                3,
                2),
            ["ESRI"] = new(
                "ESRI",
                "ESRI Satellite",
                "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/${z}/${y}/${x}",
                // The service advertises level 23. Level 22 is the largest complete
                // global pyramid whose pixel width still fits GDAL's Int32 raster size.
                22,
                3,
                4),
            ["Google"] = new(
                "Google",
                "Google Satellite",
                "https://mt1.google.com/vt/lyrs=s&x=${x}&y=${y}&z=${z}",
                20,
                3,
                4)
        };

    public static bool TryResolve(string? key, out OnlineBasemapDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(key) && Definitions.TryGetValue(key, out definition!))
            return true;

        definition = null!;
        return false;
    }

    public static OnlineBasemapCacheSettings CreateCacheSettings(
        int availablePhysicalMb,
        Func<string, string?>? readEnvironment = null,
        string? defaultPath = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        defaultPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GeoNex",
            "Cache",
            "Basemaps");
        if (string.IsNullOrWhiteSpace(defaultPath))
            defaultPath = Path.Combine(Path.GetTempPath(), "GeoNex", "Cache", "Basemaps");

        string cachePath = readEnvironment("GEONEX_BASEMAP_CACHE_PATH")?.Trim() ?? "";
        if (cachePath.Length == 0) cachePath = defaultPath;

        int defaultCacheMb = availablePhysicalMb switch
        {
            < 2048 => 128,
            < 4096 => 256,
            _ => 512
        };
        int maximumSizeMb = int.TryParse(
            readEnvironment("GEONEX_BASEMAP_CACHE_MB"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int configuredMb)
            ? Math.Clamp(configuredMb, MinimumCacheMb, MaximumCacheMb)
            : defaultCacheMb;

        string fullCachePath;
        try { fullCachePath = Path.GetFullPath(cachePath); }
        catch { fullCachePath = Path.GetFullPath(defaultPath); }

        return new OnlineBasemapCacheSettings(
            fullCachePath,
            maximumSizeMb,
            SevenDaysInSeconds,
            15 * 60);
    }

    public static string BuildGdalTmsXml(
        OnlineBasemapDefinition definition,
        OnlineBasemapCacheSettings cache)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var document = new XElement("GDAL_WMS",
            new XElement("Service",
                new XAttribute("name", "TMS"),
                new XElement("ServerUrl", definition.ServerUrl)),
            new XElement("DataWindow",
                new XElement("UpperLeftX", "-20037508.342789244"),
                new XElement("UpperLeftY", "20037508.342789244"),
                new XElement("LowerRightX", "20037508.342789244"),
                new XElement("LowerRightY", "-20037508.342789244"),
                new XElement("TileLevel", definition.MaximumZoom),
                new XElement("TileCountX", 1),
                new XElement("TileCountY", 1),
                new XElement("YOrigin", "top")),
            new XElement("Projection", WebMercatorSrs),
            new XElement("BlockSizeX", 256),
            new XElement("BlockSizeY", 256),
            new XElement("BandsCount", definition.BandCount),
            new XElement("MaxConnections", definition.MaximumConnections),
            new XElement("Timeout", RequestTimeoutSeconds),
            new XElement("UserAgent", UserAgent),
            new XElement("ZeroBlockHttpCodes", "204,400,403,404,500,502,503,504"),
            new XElement("ZeroBlockOnServerException", "true"),
            // Only tiles needed by the visible viewport are fetched. The on-disk cache
            // then makes repeat pans/zooms local without pre-seeding adjacent regions.
            new XElement("AdviseRead", "false"),
            new XElement("Cache",
                new XElement("Path", cache.Path),
                new XElement("Depth", 2),
                new XElement("Expires", cache.ExpirationSeconds),
                new XElement("MaxSize", cache.MaximumSizeBytes),
                new XElement("CleanTimeout", cache.CleanIntervalSeconds),
                new XElement("Unique", "true")));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static bool CanUseDirectRasterIo(
        bool isOnlineTileDataset,
        bool hasRotation,
        string? sourceSrs,
        string? projectSrs,
        Func<string, string, bool> areEquivalent)
    {
        ArgumentNullException.ThrowIfNull(areEquivalent);
        return isOnlineTileDataset &&
            !hasRotation &&
            !string.IsNullOrWhiteSpace(sourceSrs) &&
            !string.IsNullOrWhiteSpace(projectSrs) &&
            areEquivalent(sourceSrs, projectSrs);
    }

    public static RasterDimensions CalculateRenderDimensions(int physicalWidth, int physicalHeight,
        int availablePhysicalMb, bool isInteracting) => isInteracting
        ? RasterRenderingPolicy.FitDimensions(physicalWidth, physicalHeight, 512L * 512, 512)
        : RasterRenderingPolicy.FitDimensions(physicalWidth, physicalHeight,
            RasterRenderingPolicy.CalculateMaxFramePixels(availablePhysicalMb));

    // Direct imagery is read in strips: two RGBA frames plus bounded scratch,
    // rather than the Warp pipeline's 20 bytes per pixel. Preserve HiDPI detail
    // on notebooks without allocating an unbounded intermediate source window.
    public static RasterDimensions CalculateDirectRenderDimensions(int width, int height, int availableMb)
    {
        long budget = Math.Max(128L, availableMb) * 1024 * 1024 / 8;
        long pixels = Math.Clamp((budget - 8L * 1024 * 1024) / 8,
            1920L * 1080, 32L * 1024 * 1024);
        return RasterRenderingPolicy.FitDimensions(width, height, pixels);
    }
}
