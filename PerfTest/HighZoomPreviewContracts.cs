using System.Reflection;
using GeoNex.Services;
using SkiaSharp;

internal static class HighZoomPreviewContracts
{
    public static async Task Run(string assemblyPath)
    {
        GdalRuntimeBootstrap.Configure();
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var mapType = assembly.GetType("GeoNex.Services.MapRenderingService", true)!;
        var serverType = assembly.GetType("GeoNex.Services.LocalMapServer", true)!;
        using var map = (IDisposable)Activator.CreateInstance(mapType)!;
        object server = Activator.CreateInstance(serverType, map)!;
        serverType.GetMethod("Start")!.Invoke(server, null);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string url = (string)serverType.GetProperty("BaseUrl")!.GetValue(server)!;
            foreach (float dpi in new[] { 1f, 2f })
            foreach (float scale in new[] { 4f, 64f })
            {
                int padding = NavigationFramePolicy.Padding(400, 300, dpi);
                var viewport = MapViewportMetrics.Create(400 + 2 * padding, 300 + 2 * padding, dpi);
                var frame = MapCoordinateFrame.Create(viewport, new SKPoint(-5400000, 3000000), scale);
                if (!RenderPrecisionPolicy.NeedsLocalOrigin(frame.LocalCenter, scale * dpi))
                    throw new InvalidOperationException("Fixture must require precise geometry");
                var bitmap = new SKBitmap(viewport.PhysicalWidth, viewport.PhysicalHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(bitmap))
                using (var fill = new SKPaint { Color = SKColors.Cyan })
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.DrawRect(100, 100, 40, 50, fill);
                    canvas.DrawRect(142, 100, 40, 50, fill); // Visible gap cannot collapse during cache reuse.
                }
                SKColor[] expected = bitmap.Pixels;
                var revision = (long)mapType.GetProperty("SceneRevision")!.GetValue(map)!;
                // Reflection converts frame/viewport to the production assembly's value types.
                var viewportType = assembly.GetType("GeoNex.Services.MapViewportMetrics", true)!;
                object productionViewport = viewportType.GetMethod("Create")!.Invoke(null, new object[] { viewport.CssWidth, viewport.CssHeight, dpi })!;
                var frameType = assembly.GetType("GeoNex.Services.MapCoordinateFrame", true)!;
                object productionFrame = frameType.GetMethod("Create")!.Invoke(null,
                    new object[] { productionViewport, frame.LocalCenter, scale, 0f })!;
                mapType.GetMethod("PublishGlobalCache")!.Invoke(map,
                    new object[] { bitmap, scale, 0f, 0f, productionViewport, productionFrame, revision, 1f });
                var pause = (ValueTask<IDisposable>)serverType.GetMethod("PauseForRasterMaintenanceAsync")!.Invoke(server, null)!;
                using (await pause)
                {
                    // This must finish while the full renderer is locked, including at high zoom.
                    string query = FormattableString.Invariant($"{url}mapa/?w=400&h=300&dpi={dpi}&nav=1&i=1&fid=1&zoom=1&panx=0&pany=0");
                    using var response = await client.GetAsync(query).WaitAsync(TimeSpan.FromSeconds(2));
                    response.EnsureSuccessStatusCode();
                    using var decoded = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
                    if (!decoded.Pixels.SequenceEqual(expected)) throw new InvalidOperationException("High-zoom identity preview changed pixels");
                    using var shifted = await client.GetAsync(query.Replace("panx=0", "panx=64")).WaitAsync(TimeSpan.FromSeconds(2));
                    using var moved = SKBitmap.Decode(await shifted.Content.ReadAsByteArrayAsync());
                    int shift = (int)(64 * dpi);
                    var pixels = moved.Pixels;
                    for (int y = 0; y < moved.Height; y++)
                    for (int x = shift; x < moved.Width; x++)
                        if (pixels[y * moved.Width + x] != expected[y * moved.Width + x - shift])
                            throw new InvalidOperationException("High-zoom pan misaligned cached polygons");
                }
                mapType.GetMethod("InvalidateRasterPresentationCache")!.Invoke(map, null);
                object?[] finalArgs = { null };
                using (var staleFinal = (IDisposable?)mapType.GetMethod("AcquireGlobalCache")!.Invoke(map, finalArgs))
                    if (staleFinal != null) throw new InvalidOperationException("Raster arrival left a final cache eligible");
                var pendingPause = (ValueTask<IDisposable>)serverType.GetMethod("PauseForRasterMaintenanceAsync")!.Invoke(server, null)!;
                using (await pendingPause)
                {
                    string query = FormattableString.Invariant($"{url}mapa/?w=400&h=300&dpi={dpi}&nav=1&i=1&fid=2&zoom=1&panx=0&pany=0");
                    using var response = await client.GetAsync(query).WaitAsync(TimeSpan.FromSeconds(2));
                    response.EnsureSuccessStatusCode();
                    using var decoded = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
                    if (!decoded.Pixels.SequenceEqual(expected)) throw new InvalidOperationException("Raster arrival discarded the gesture image");
                }
                mapType.GetMethod("RequestRedraw")!.Invoke(map, null);
                object?[] args = { null };
                using var invalidated = (IDisposable?)mapType.GetMethod("AcquireGlobalCache")!.Invoke(map, args);
                if (invalidated != null) throw new InvalidOperationException("Edited scene retained preview cache");
                object?[] previewArgs = { null };
                using var invalidatedPreview = (IDisposable?)mapType.GetMethod("AcquireGlobalPreviewCache")!.Invoke(map, previewArgs);
                if (invalidatedPreview != null) throw new InvalidOperationException("Edited scene retained obsolete gesture image");
            }
            Console.WriteLine("High zoom preview: PASS (busy scene gate, exact pixels/gap, pan, DPI 1/2, scales 4/64, raster arrival retains preview but invalidates final, edits discard both)");
        }
        finally { serverType.GetMethod("Stop")!.Invoke(server, null); }
    }
}
