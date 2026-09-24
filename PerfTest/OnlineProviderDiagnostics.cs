using System.Diagnostics;
using GeoNex.Services;
using SkiaSharp;

internal static class OnlineProviderDiagnostics
{
    public static void Run(string provider, string srs = "EPSG:3857", bool directOnly = false)
    {
        GdalRuntimeBootstrap.Configure();
        if (!OnlineBasemapPolicy.TryResolve(provider, out var definition)) throw new ArgumentException("Unknown provider");
        string cache = Directory.CreateTempSubdirectory("GeoNex-provider-check-").FullName;
        string xml = OnlineBasemapPolicy.BuildGdalTmsXml(definition, new(cache, 64, 3600, 900));
        double[] center = { -48.5747, -25.8828, 0 };
        using var transform = SrsFactory.CreateTransform("EPSG:4326", srs);
        transform.TransformPoint(center);
        var bounds = new SKRect(-700, -525, 700, 525);
        using var session = new OnlineRasterSession();
        using var progressive = new ProgressiveOnlineRaster();
        foreach (string mode in directOnly ? new[] { "direct-cold", "direct-warm" } : new[] { "progressive-cold", "direct-warm", "progressive-warm" })
        {
            var timer = Stopwatch.StartNew();
            long first = -1; int updates = 0;
            try
            {
                using var bitmap = mode.StartsWith("direct", StringComparison.Ordinal)
                    ? session.Read(xml, srs, bounds, center[0], center[1], 1024, 768, CancellationToken.None)
                    : progressive.Read(session, xml, srs, bounds, center[0], center[1], 1024, 768, CancellationToken.None,
                        b => { if (first < 0) first = timer.ElapsedMilliseconds; updates++; b.Dispose(); });
                timer.Stop(); // Measure rendering; PNG export below is diagnostic work only.
                bool content = bitmap.Pixels.Any(c => c.Red > 0 || c.Green > 0 || c.Blue > 0);
                if (!content) throw new IOException("Empty image");
                using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(cache, mode + ".png"), encoded.ToArray());
                Console.WriteLine($"PROVIDER {provider} {srs} {mode} PASS first_ms={first} final_ms={timer.ElapsedMilliseconds} updates={updates}");
            }
            catch (Exception e) { Console.WriteLine($"PROVIDER {provider} {srs} {mode} FAIL ms={timer.ElapsedMilliseconds}: {e}"); throw; }
        }
        Console.WriteLine($"Transport: downloads={progressive.TileDownloads} shared={progressive.SharedTileRequests} disk_hits={progressive.TileCacheHits}");
        Console.WriteLine("Images: " + cache);
    }
}
