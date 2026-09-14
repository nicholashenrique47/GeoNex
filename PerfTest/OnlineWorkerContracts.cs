using GeoNex.Services;

internal static class OnlineWorkerContracts
{
    private sealed class Result(int id) : IDisposable
    {
        public int Id { get; } = id;
        public int Disposals;
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
    public static async Task Run()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Result? old = null, latest = null;
        int draws = 0, publishes = 0;
        using (var worker = new LatestRenderWorker<Result>(() => ready.TrySetResult(), e => ready.TrySetException(e)))
        {
            worker.Request("satellite", "old", token =>
            {
                Interlocked.Increment(ref draws); started.Set();
                if (!release.Wait(5000)) throw new TimeoutException();
                return old = new Result(0); // Models a noninterruptible native HTTP operation.
            }, result => { Interlocked.Increment(ref publishes); result.Dispose(); });
            if (!started.Wait(5000)) throw new TimeoutException();
            for (int i = 0; i < 50; i++)
                worker.Request("satellite", "old", token => throw new InvalidOperationException("duplicate active job"), result => result.Dispose());
            Check(worker.PendingCount == 0, "duplicate in-flight camera does not restart download");
            for (int i = 1; i <= 500; i++)
            {
                int id = i;
                worker.Request("satellite", id.ToString(), token =>
                { Interlocked.Increment(ref draws); return latest = new Result(id); },
                    result => { Interlocked.Increment(ref publishes); result.Dispose(); });
            }
            Check(worker.PendingCount == 1, "latest-only queue remains bounded during a blocked download");
            release.Set();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Check(draws == 2 && publishes == 1 && latest?.Id == 500, "only active and latest request execute; obsolete output cannot publish");
        Check(old?.Disposals == 1 && latest?.Disposals == 1, "exactly-once disposal");

        using var stopStarted = new ManualResetEventSlim();
        using var stopRelease = new ManualResetEventSlim();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownWorker = new LatestRenderWorker<Result>(() => throw new Exception("published after shutdown"), e => stopped.TrySetException(e));
        shutdownWorker.Request("a", "1", token =>
        {
            stopStarted.Set(); stopRelease.Wait(5000);
            try { token.ThrowIfCancellationRequested(); return new Result(1); }
            finally { stopped.TrySetResult(); }
        }, result => result.Dispose());
        Check(stopStarted.Wait(5000), "shutdown worker starts");
        shutdownWorker.Dispose(); // Must return while simulated native I/O is still blocked.
        stopRelease.Set(); await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int failures = 0;
        using var recoverWorker = new LatestRenderWorker<Result>(() => recovery.TrySetResult(), _ => { Interlocked.Increment(ref failures); failure.TrySetResult(); });
        recoverWorker.Request("a", "failed", _ => throw new IOException("HTTP fixture failure"), result => result.Dispose());
        await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 50; i++)
            recoverWorker.Request("a", "failed", _ => throw new IOException("must be in cooldown"), result => result.Dispose());
        recoverWorker.Request("a", "new-camera", _ => new Result(2), result => result.Dispose());
        await recovery.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(failures == 1, "failed camera backs off; a new camera can recover immediately");
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        using var retryWorker = new LatestRenderWorker<Result>(() => retried.TrySetResult(), _ => { });
        retryWorker.Request("a", "retry", _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("transient network failure");
            return new Result(3);
        }, result => result.Dispose());
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(6));
        Check(attempts == 2, "one bounded automatic recovery after transient failure");
        GdalRuntimeBootstrap.Configure();
        using var tiles = new DelayedTileServer();
        tiles.Release();
        var definition = new OnlineBasemapDefinition("fixture", "fixture", tiles.Url + "${z}/${x}/${y}.png", 20, 3, 2);
        var xml = System.Xml.Linq.XDocument.Parse(OnlineBasemapPolicy.BuildGdalTmsXml(definition,
            new OnlineBasemapCacheSettings(Path.GetTempPath(), 64, 3600, 900)));
        xml.Root!.Element("Cache")?.Remove();
        foreach (var scenario in new[] {
            ("EPSG:3857", new SkiaSharp.SKRect(-10000, -10000, 10000, 10000)),
            ("EPSG:4326", new SkiaSharp.SKRect(-49, 26.9f, -48.9f, 27)) })
        {
            using var bitmap = OnlineRasterFrameReader.Read(xml.ToString(), scenario.Item1, scenario.Item2, 0, 0, 320, 240, CancellationToken.None);
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap) ?? throw new InvalidOperationException("Online alpha image invalid");
            var center = bitmap.GetPixel(160, 120);
            Check(center.Alpha == 255 && center.Green > center.Red, "RGB/alpha preserved for direct and projected online imagery");
        }
        Console.WriteLine("Online worker: PASS (500 coalesced requests, stale rejection, ownership, nonblocking shutdown)");
        // Record actual HTTP zooms, including fractional zoom, HiDPI and overzoom.
        foreach (var scenario in new[] { (18.25, 1), (19.25, 1), (19.25, 2), (22.0, 1) })
        {
            tiles.RequestedZooms.Clear();
            double extent = 40075016.685578488 / Math.Pow(2, scenario.Item1);
            var bounds = new SkiaSharp.SKRect((float)(-extent / 2), (float)(-extent / 2), (float)(extent / 2), (float)(extent / 2));
            using var bitmap = OnlineRasterFrameReader.Read(xml.ToString(), "EPSG:3857", bounds,
                -5400000, -3000000, 256 * scenario.Item2, 256 * scenario.Item2, CancellationToken.None);
            int expectedZoom = Math.Min(20, (int)Math.Ceiling(scenario.Item1 + Math.Log2(scenario.Item2)));
            Check(!tiles.RequestedZooms.IsEmpty && tiles.RequestedZooms.All(z => z == expectedZoom),
                $"Actual tile level: expected {expectedZoom}, received {string.Join(',', tiles.RequestedZooms.Distinct())}");
        }
        Console.WriteLine("Online high zoom: PASS (actual requested tile levels, fractional zoom, DPI 2, capped overzoom)");
        using (var dataset = OSGeo.GDAL.Gdal.Open(xml.ToString(), OSGeo.GDAL.Access.GA_ReadOnly))
        {
            double[] geo = new double[6]; dataset.GetGeoTransform(geo);
            var method = typeof(OnlineRasterFrameReader).GetMethod("ReadDirect", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            foreach (double scale in new[] { .125, 1.0, 1.75, 4.25 })
            {
                double span = geo[1] * 511 * scale;
                object[] args = { dataset, geo, -5400000.0123, -3000000.0456, -5400000.0123 + span,
                    -3000000.0456 - span, 511, 511, CancellationToken.None, 128, true };
                using var strips = (SkiaSharp.SKBitmap)method.Invoke(null, args)!;
                args[^2] = 8192;
                using var whole = (SkiaSharp.SKBitmap)method.Invoke(null, args)!;
                Check(strips.Bytes.SequenceEqual(whole.Bytes), $"Strip seams/resampling differ at scale {scale}");
                args[^2] = 128; args[^1] = false;
                using var copied = (SkiaSharp.SKBitmap)method.Invoke(null, args)!;
                Check(strips.Bytes.SequenceEqual(copied.Bytes), $"Direct RGB pixels differ from Skia conversion at scale {scale}");
            }
        }
        Console.WriteLine("Online strip quality: PASS (pixel-identical to a single read, fractional pixels and overzoom)");
    }
    private static void Check(bool valid, string message)
    { if (!valid) throw new InvalidOperationException(message); }
}
