using GeoNex.Services;
using SkiaSharp;
using System.Xml.Linq;

internal static class OnlineProjectedZoomContracts
{
    public static void Run()
    {
        GdalRuntimeBootstrap.Configure();
        using var tiles = new DelayedTileServer(); tiles.Release();
        var definition = new OnlineBasemapDefinition("fixture", "fixture", tiles.Url + "${z}/${x}/${y}.png", 20, 3, 2);
        var config = XDocument.Parse(OnlineBasemapPolicy.BuildGdalTmsXml(definition, new(Path.GetTempPath(), 64, 3600, 900)));
        config.Root!.Element("Cache")!.Remove();
        foreach (double zoom in new[] { 16.25, 17.25, 18.25, 19.25 })
        foreach (int dpi in new[] { 1, 2 })
        {
            tiles.RequestedZooms.Clear();
            double span = 360 / Math.Pow(2, zoom);
            var bounds = new SKRect((float)(-span / 2), (float)(-span / 2), (float)(span / 2), (float)(span / 2));
            using var result = OnlineRasterFrameReader.Read(config.ToString(), "EPSG:4326", bounds, -48, 0, 256 * dpi, 256 * dpi, CancellationToken.None);
            int expected = Math.Min(20, (int)Math.Ceiling(zoom + Math.Log2(dpi)));
            if (tiles.RequestedZooms.IsEmpty || tiles.RequestedZooms.Any(z => z != expected))
                throw new InvalidOperationException($"Projected zoom={zoom} dpi={dpi} received={string.Join(',', tiles.RequestedZooms.Distinct())} expected={expected}");
        }
        Console.WriteLine("Projected zoom: PASS (fractional zoom, HiDPI, provider maximum)");
    }
}
