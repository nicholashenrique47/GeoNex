using GeoNex.Services;
using SkiaSharp;

internal static class OnlineProgressiveContracts
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    public static async Task Run()
    {
        GdalRuntimeBootstrap.Configure();
        var ordered = ProgressiveOnlineRaster.Regions(1536, 1536);
        Check(ordered.Length == 9 && ordered[0] == new SKRectI(512, 512, 1024, 1024), "center first");
        using var server = new DelayedTileServer();
        server.Release();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var visible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforeResponse = uri =>
        {
            // Hold the upper-left edge, not an arbitrary first URL which can sit
            // in the interpolation halo shared by every active render lane.
            if (uri.AbsolutePath != "/tiles/7/61/59.png") return Task.CompletedTask;
            blocked.TrySetResult(); return release.Task;
        };
        string cache = Path.Combine(Path.GetTempPath(), "GeoNex-progressive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        var definition = new OnlineBasemapDefinition("fixture", "fixture", server.Url + "${z}/${x}/${y}.png", 8, 3, 2);
        string xml = OnlineBasemapPolicy.BuildGdalTmsXml(definition, new(cache, 64, 3600, 900));
        // Several distinct tiles per region: a held URL must not be needed by every region.
        var bounds = new SKRect(-800000, -800000, 800000, 800000);
        const double ox = 123456, oy = 654321;
        int updates = 0;
        using var session = new OnlineRasterSession();
        using var renderer = new ProgressiveOnlineRaster();
        var work = Task.Run(() => renderer.Read(session, xml, "EPSG:3857", bounds, ox, oy, 1024, 1024,
            CancellationToken.None, bitmap =>
            {
                using (bitmap)
                {
                    Check(bitmap.Bytes.Any(b => b != 0), "partial pixels exist");
                    Interlocked.Increment(ref updates);
                    visible.TrySetResult();
                }
            }));
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await visible.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Check(!work.IsCompleted, "partial arrives before blocked tile completes");
            Check(server.PeakActive <= 2, "provider connection budget");
        }
        finally { release.TrySetResult(); }
        using var actual = await work.WaitAsync(TimeSpan.FromSeconds(15));
        int fetched = server.RequestedPaths.Count;
        int uniqueFetched = server.RequestedPaths.Distinct().Count();
        using var expected = OnlineRasterFrameReader.Read(xml, "EPSG:3857", bounds, ox, oy, 1024, 1024, CancellationToken.None);
        Check(actual.Bytes.SequenceEqual(expected.Bytes), "final pixels identical to original full-grid read");
        Check(server.RequestedPaths.Count == fetched, "reference uses populated disk cache");
        using var revisit = renderer.Read(session, xml, "EPSG:3857", bounds, ox, oy, 1024, 1024,
            CancellationToken.None, b => b.Dispose());
        Check(server.RequestedPaths.Count == fetched, "revisit has no extra HTTP");
        Check(revisit.Bytes.SequenceEqual(actual.Bytes), "revisit final identical");

        // Projected output must still finish with the same full-viewport Warp.
        var projected = new SKRect(-49, 26.9f, -48.9f, 27);
        using var utm = renderer.Read(session, xml, "EPSG:4326", projected, 0, 0, 800, 600,
            CancellationToken.None, b => b.Dispose());
        using var utmReference = OnlineRasterFrameReader.Read(xml, "EPSG:4326", projected, 0, 0, 800, 600, CancellationToken.None);
        Check(utm.Bytes.SequenceEqual(utmReference.Bytes), "projected final matches full Warp");
        using var sirgas = renderer.Read(session, xml, "EPSG:31982", new SKRect(-5000, -5000, 5000, 5000),
            500000, 7000000, 800, 600, CancellationToken.None, b => b.Dispose());
        using var sirgasReference = OnlineRasterFrameReader.Read(xml, "EPSG:31982", new SKRect(-5000, -5000, 5000, 5000),
            500000, 7000000, 800, 600, CancellationToken.None);
        Check(sirgas.Bytes.SequenceEqual(sirgasReference.Bytes), "SIRGAS UTM with local offsets matches full Warp");
        int sharpUpdates = 0;
        using var sharp = renderer.Read(session, xml, "EPSG:3857", bounds, ox, oy, 2560, 1920,
            CancellationToken.None, b =>
            {
                using (b)
                {
                    if (GdalRuntimeConfiguration.Apply().AvailablePhysicalMb >= 1024)
                        Check(b.Width == 2560 && b.Height == 1920, "HiDPI preview was unnecessarily downscaled");
                    sharpUpdates++;
                }
            });
        Check(sharpUpdates > 0, "HiDPI preview published");
        // One bad region cannot erase a successfully updated neighbor or the seed.
        int failedHttp = 0;
        server.ResponseStatus = uri => Interlocked.Increment(ref failedHttp) == 1 ? 403 : 200;
        string failureXml = xml.Replace(".png", ".png?failure=1", StringComparison.Ordinal);
        int goodParts = 0;
        bool failed = false;
        try
        {
            using var invalid = renderer.Read(session, failureXml, "EPSG:3857", bounds, ox, oy, 1024, 1024,
                CancellationToken.None, b =>
                {
                    using (b)
                    {
                        Check(b.Pixels.Any(c => c == SKColors.Magenta), "pending region lost previous imagery");
                        goodParts++;
                    }
                }, (c, w, h) => c.Clear(SKColors.Magenta));
        }
        catch (IOException) { failed = true; }
        Check(failed && goodParts > 0, "failed region retains successful partials");
        server.ResponseStatus = null;
        await CheckObsoletePartial();
        Console.WriteLine($"Online progressive: PASS (partial before stalled tile, center-first, connections <=2, final pixels exact, cache revisit HTTP=0, projection, partial failure, stale disposal; cold HTTP={fetched}, unique={uniqueFetched}, updates={updates})");
    }

    private sealed class Result : IDisposable
    { public int Disposed; public void Dispose() => Interlocked.Increment(ref Disposed); }

    private static async Task CheckObsoletePartial()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int partials = 0;
        var before = new Result(); var after = new Result();
        using var worker = new LatestRenderWorker<Result>(() => { }, e => done.TrySetException(e));
        worker.RequestProgressive("layer", "old", (token, publish) =>
        {
            publish(before); started.TrySetResult();
            if (!release.Wait(8000)) throw new TimeoutException();
            publish(after); return new Result();
        }, b => throw new Exception("obsolete final published"), b => { partials++; b.Dispose(); });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        worker.Request("layer", "new", _ => new Result(), b => { b.Dispose(); done.TrySetResult(); });
        release.Set(); await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(partials == 1 && before.Disposed == 1 && after.Disposed == 1, "obsolete partial disposed once");
    }
}
