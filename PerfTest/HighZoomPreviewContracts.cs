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
                    long hits = (long)serverType.GetProperty("EncodedFrameCacheHits")!.GetValue(server)!;
                    using var repeated = await client.GetAsync(query).WaitAsync(TimeSpan.FromSeconds(2));
                    repeated.EnsureSuccessStatusCode();
                    using var repeatedImage = SKBitmap.Decode(await repeated.Content.ReadAsByteArrayAsync());
                    if (!repeatedImage.Pixels.SequenceEqual(expected) ||
                        (long)serverType.GetProperty("EncodedFrameCacheHits")!.GetValue(server)! != hits + 1)
                        throw new InvalidOperationException("Identity preview did not reuse exact PNG");
                    hits++;
                    using var shifted = await client.GetAsync(query.Replace("panx=0", "panx=64")).WaitAsync(TimeSpan.FromSeconds(2));
                    using var moved = SKBitmap.Decode(await shifted.Content.ReadAsByteArrayAsync());
                    int shift = (int)(64 * dpi);
                    var pixels = moved.Pixels;
                    for (int y = 0; y < moved.Height; y++)
                    for (int x = shift; x < moved.Width; x++)
                        if (pixels[y * moved.Width + x] != expected[y * moved.Width + x - shift])
                            throw new InvalidOperationException("High-zoom pan misaligned cached polygons");
                    if ((long)serverType.GetProperty("EncodedFrameCacheHits")!.GetValue(server)! != hits)
                        throw new InvalidOperationException("Moving preview entered identity PNG cache");
                    // Replace the image at the same camera/revision: camera identity
                    // must never substitute for validating all output pixels.
                    var replacement = new SKBitmap(viewport.PhysicalWidth, viewport.PhysicalHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                    replacement.Erase(new SKColor(81, 23, 147, 93)); replacement.SetImmutable();
                    expected = replacement.Pixels;
                    mapType.GetMethod("PublishGlobalCache")!.Invoke(map,
                        new object[] { replacement, scale, 0f, 0f, productionViewport, productionFrame, revision, 1f });
                    using var replaced = await client.GetAsync(query).WaitAsync(TimeSpan.FromSeconds(2));
                    replaced.EnsureSuccessStatusCode();
                    using var replacementImage = SKBitmap.Decode(await replaced.Content.ReadAsByteArrayAsync());
                    if (!replacementImage.Pixels.SequenceEqual(expected))
                        throw new InvalidOperationException("Same-camera replacement served stale PNG");

                    // Simulate an old preview still inside a native encoder while
                    // the final renderer is also busy. Its successor must stay in
                    // the preview lane instead of joining the full-render queue.
                    var previewGate = (SemaphoreSlim)serverType.GetField("_previewGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
                    var generationField = serverType.GetField("_latestRenderGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    await previewGate.WaitAsync();
                    Task<HttpResponseMessage> waiting;
                    try
                    {
                        long previous = (long)generationField.GetValue(server)!;
                        waiting = client.GetAsync(query);
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        while ((long)generationField.GetValue(server)! == previous)
                            await Task.Delay(5, deadline.Token);
                        await Task.Delay(100); // Give the accepted request time to encounter contention.
                        if (waiting.IsCompleted) throw new InvalidOperationException("Preview bypassed its occupied gate");
                        var obsolete = waiting;
                        waiting = client.GetAsync(query);
                        using var canceled = await obsolete.WaitAsync(TimeSpan.FromSeconds(2));
                        if (canceled.StatusCode != System.Net.HttpStatusCode.NoContent)
                            throw new InvalidOperationException("Obsolete preview waiter was not canceled");
                    }
                    finally { previewGate.Release(); }
                    long resumedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    using var resumed = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
                    resumed.EnsureSuccessStatusCode();
                    using var resumedImage = SKBitmap.Decode(await resumed.Content.ReadAsByteArrayAsync());
                    if (!resumedImage.Pixels.SequenceEqual(expected))
                        throw new InvalidOperationException("Contended high-zoom preview changed pixels");
                    Console.WriteLine(FormattableString.Invariant($"Preview contention dpi={dpi} scale={scale} resumed_ms={System.Diagnostics.Stopwatch.GetElapsedTime(resumedAt).TotalMilliseconds:F2} full_renderer_still_locked=True max_delta=0"));
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
            // Final-quality requests must not wait for the preview lane. The
            // synthetic cache was invalidated above; render the empty scene.
            var heldPreviewGate = (SemaphoreSlim)serverType.GetField("_previewGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
            await heldPreviewGate.WaitAsync();
            try
            {
                using var final = await client.GetAsync($"{url}mapa/?w=400&h=300&dpi=1&nav=1&i=0&fid=3&zoom=1&panx=0&pany=0").WaitAsync(TimeSpan.FromSeconds(2));
                final.EnsureSuccessStatusCode();
                using var finalImage = SKBitmap.Decode(await final.Content.ReadAsByteArrayAsync())
                    ?? throw new InvalidOperationException("Final request did not return a valid PNG");
            }
            finally { heldPreviewGate.Release(); }
            Console.WriteLine("High zoom preview: PASS (busy scene/preview gates, canceled obsolete waiters, final bypasses preview gate, exact pixels/gap, identity PNG reuse, moving preview bypass, same-camera replacement, pan, DPI 1/2, scales 4/64, raster arrival retains preview but invalidates final, edits discard both)");
        }
        finally { serverType.GetMethod("Stop")!.Invoke(server, null); }
    }
}
