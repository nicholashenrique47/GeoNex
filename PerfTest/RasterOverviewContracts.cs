using System.Diagnostics;
using System.Security.Cryptography;
using GeoNex.Services;
using OSGeo.GDAL;

internal static class RasterOverviewContracts
{
    public static void Run()
    {
        GdalRuntimeBootstrap.Configure();

        string directory = Path.Combine(Path.GetTempPath(), $"geonex-overview-{Guid.NewGuid():N}");
        string rasterPath = Path.Combine(directory, "continuous.tif");
        Directory.CreateDirectory(directory);
        string? previousDelay = Environment.GetEnvironmentVariable("GEONEX_OVERVIEW_DELAY_MS");
        string? previousEnabled = Environment.GetEnvironmentVariable("GEONEX_BUILD_OVERVIEWS");
        string? previousResampling = Environment.GetEnvironmentVariable("GEONEX_OVERVIEW_RESAMPLING");
        try
        {
            Environment.SetEnvironmentVariable("GEONEX_BUILD_OVERVIEWS", "1");
            Environment.SetEnvironmentVariable("GEONEX_OVERVIEW_RESAMPLING", null);
            Driver driver = Gdal.GetDriverByName("GTiff")
                ?? throw new InvalidOperationException("GTiff driver is unavailable.");
            using (Dataset dataset = driver.Create(
                rasterPath, 1024, 1024, 1, DataType.GDT_Float32,
                ["TILED=YES", "COMPRESS=DEFLATE"]))
            using (Band band = dataset.GetRasterBand(1))
            {
                var row = new float[1024];
                for (int x = 0; x < row.Length; ++x) row[x] = x / 1023f;
                for (int y = 0; y < 1024; ++y)
                    Assert(band.WriteRaster(0, y, 1024, 1, row, 1024, 1, 0, 0) == CPLErr.CE_None,
                        "synthetic raster write");
                dataset.FlushCache();
            }

            byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(rasterPath));
            Environment.SetEnvironmentVariable("GEONEX_OVERVIEW_DELAY_MS", "0");
            Assert(RasterOverviewBuilder.Schedule(rasterPath, new object()), "overview job scheduled");

            var timeout = Stopwatch.StartNew();
            while (RasterOverviewBuilder.IsActive(rasterPath) && timeout.Elapsed < TimeSpan.FromSeconds(30))
                Thread.Sleep(25);
            Assert(!RasterOverviewBuilder.IsActive(rasterPath), "overview job completed");
            Assert(File.Exists(rasterPath + ".ovr"), "external .ovr created");
            Assert(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(rasterPath))),
                "source raster remains byte-identical");

            using (Dataset reopened = Gdal.Open(rasterPath, Access.GA_ReadOnly))
            using (Band reopenedBand = reopened.GetRasterBand(1))
                Assert(reopenedBand.GetOverviewCount() == 2, "expected overview levels are visible after reopen");

            VerifyPublication(directory, rasterPath, cancel: false, conflict: false);
            VerifyPublication(directory, rasterPath, cancel: true, conflict: false);
            VerifyPublication(directory, rasterPath, cancel: false, conflict: true);
            VerifyPixels(directory, rasterPath, "AVERAGE");
            VerifyCategorical(directory);
            VerifyContinuousNoData(directory);
            VerifyAdmission(directory, rasterPath);
            VerifyGuards(directory, rasterPath);
            VerifyReadLock();
            Assert(Directory.GetDirectories(directory, ".geonex-overview-*").Length == 0, "staging directories cleaned");
            Console.WriteLine("Raster overview contracts: PASS (isolated build, atomic publication, cancellation, conflict, source hash, exact levels, palette, nodata, wait telemetry)");
        }
        finally
        {
            RasterOverviewBuilder.CancelAll();
            var cleanupWait = Stopwatch.StartNew();
            while (RasterOverviewBuilder.IsActive(rasterPath) && cleanupWait.Elapsed < TimeSpan.FromSeconds(5))
                Thread.Sleep(25);
            Environment.SetEnvironmentVariable("GEONEX_OVERVIEW_DELAY_MS", previousDelay);
            Environment.SetEnvironmentVariable("GEONEX_BUILD_OVERVIEWS", previousEnabled);
            Environment.SetEnvironmentVariable("GEONEX_OVERVIEW_RESAMPLING", previousResampling);
            if (!RasterOverviewBuilder.IsActive(rasterPath) && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyPublication(string directory, string original, bool cancel, bool conflict)
    {
        string path = Path.Combine(directory, $"publication-{cancel}-{conflict}.tif");
        File.Copy(original, path);
        object gate = new();
        int redraws = 0;
        lock (gate)
        {
            Assert(RasterOverviewBuilder.Schedule(path, gate, () => Interlocked.Increment(ref redraws)), "schedule isolated job");
            Assert(!RasterOverviewBuilder.Schedule(path, gate), "deduplicate source");
            Wait(() => RasterOverviewBuilder.Metrics.Stage == "awaiting-publication", "build finishes while render lock remains held");
            Assert(!File.Exists(path + ".ovr"), "partial overview never visible");
            using (Dataset read = Gdal.Open(original, Access.GA_ReadOnly))
            using (Band band = read.GetRasterBand(1))
            {
                var pixels = new float[16];
                using (RasterReadLock.Enter(gate, CancellationToken.None))
                    Assert(band.ReadRaster(0, 0, 16, 1, pixels, 16, 1, 0, 0) == CPLErr.CE_None,
                        "unrelated raster can read while maintenance waits to publish");
                Assert(pixels[0] == 0 && Math.Abs(pixels[15] - 15f / 1023) < 1e-7, "read pixels unchanged");
            }
            if (conflict) File.Copy(original + ".ovr", path + ".ovr");
            if (cancel)
            {
                RasterOverviewBuilder.Cancel(path);
                Wait(() => !RasterOverviewBuilder.IsActive(path), "cancel does not wait for render lock");
                Assert(!File.Exists(path + ".ovr"), "canceled build not published");
            }
        }
        Wait(() => !RasterOverviewBuilder.IsActive(path), "publication finished");
        Assert(redraws == (cancel || conflict ? 0 : 1), "redraw only after successful publication");
        if (conflict)
            Assert(File.ReadAllBytes(original + ".ovr").SequenceEqual(File.ReadAllBytes(path + ".ovr")), "existing sidecar never overwritten");
        Assert(File.ReadAllBytes(original).SequenceEqual(File.ReadAllBytes(path)), "source untouched");
    }

    private static void VerifyPixels(string directory, string sourcePath, string resampling)
    {
        string reference = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tif");
        File.Copy(sourcePath, reference);
        using (Dataset dataset = Gdal.Open(reference, Access.GA_ReadOnly))
            Assert(dataset.BuildOverviews(resampling, RasterRenderingPolicy.BuildOverviewFactors(dataset.RasterXSize, dataset.RasterYSize), null, null) == 0, "reference overviews");
        using Dataset expected = Gdal.Open(reference, Access.GA_ReadOnly);
        using Dataset actual = Gdal.Open(sourcePath, Access.GA_ReadOnly);
        Assert(actual.GetProjection() == expected.GetProjection(), "CRS preserved");
        for (int b = 1; b <= actual.RasterCount; b++)
        {
            using Band actualBand = actual.GetRasterBand(b);
            using Band expectedBand = expected.GetRasterBand(b);
            Assert(actualBand.GetOverviewCount() == expectedBand.GetOverviewCount(), "level counts equal reference");
            for (int level = 0; level < actualBand.GetOverviewCount(); level++)
            {
                using Band a = actualBand.GetOverview(level);
                using Band e = expectedBand.GetOverview(level);
                Assert(a.XSize == e.XSize && a.YSize == e.YSize && a.DataType == e.DataType, "level shape and type");
                var ap = new double[a.XSize * a.YSize];
                var ep = new double[ap.Length];
                Assert(a.ReadRaster(0, 0, a.XSize, a.YSize, ap, a.XSize, a.YSize, 0, 0) == CPLErr.CE_None, "read staged pixels");
                Assert(e.ReadRaster(0, 0, e.XSize, e.YSize, ep, e.XSize, e.YSize, 0, 0) == CPLErr.CE_None, "read reference pixels");
                Assert(ap.SequenceEqual(ep), "all overview pixels exactly match direct GDAL");
                a.GetNoDataValue(out double an, out int ah);
                e.GetNoDataValue(out double en, out int eh);
                Assert(ah == eh && (ah == 0 || an.Equals(en)), "overview nodata");
                using ColorTable? ac = a.GetRasterColorTable();
                using ColorTable? ec = e.GetRasterColorTable();
                Assert(ac?.GetCount() == ec?.GetCount(), "overview palette length");
                if (ac != null && ec != null)
                    for (int c = 0; c < ac.GetCount(); c++)
                    {
                        ColorEntry av = ac.GetColorEntry(c), ev = ec.GetColorEntry(c);
                        Assert(av.c1 == ev.c1 && av.c2 == ev.c2 && av.c3 == ev.c3 && av.c4 == ev.c4, "overview palette colors");
                    }
            }
        }
    }

    private static void VerifyCategorical(string directory)
    {
        string path = Path.Combine(directory, "classes.tif");
        using (Driver driver = Gdal.GetDriverByName("GTiff"))
        using (Dataset source = driver.Create(path, 1025, 1027, 1, DataType.GDT_Byte, ["TILED=YES"]))
        using (Band band = source.GetRasterBand(1))
        using (var colors = new ColorTable(PaletteInterp.GPI_RGB))
        {
            colors.SetColorEntry(0, new ColorEntry { c1 = 0, c2 = 0, c3 = 0, c4 = 255 });
            colors.SetColorEntry(1, new ColorEntry { c1 = 17, c2 = 99, c3 = 201, c4 = 255 });
            band.SetRasterColorTable(colors);
            band.SetNoDataValue(0);
            source.SetGeoTransform([100, 2, 0, 200, 0, -2]);
            source.SetProjection("EPSG:3857");
            var row = Enumerable.Range(0, 1025).Select(x => (byte)(x % 3 == 0 ? 0 : 1)).ToArray();
            for (int y = 0; y < 1027; y++)
                Assert(band.WriteRaster(0, y, row.Length, 1, row, row.Length, 1, 0, 0) == CPLErr.CE_None, "categorical fixture");
        }
        byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
        Assert(RasterOverviewBuilder.Schedule(path, new object()), "categorical schedule");
        Wait(() => !RasterOverviewBuilder.IsActive(path), "categorical completion");
        Assert(File.Exists(path + ".ovr"), "categorical sidecar");
        VerifyPixels(directory, path, "NEAREST");
        Assert(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "categorical source unchanged");
    }

    private static void VerifyReadLock()
    {
        object gate = new();
        var collector = new RenderTelemetryCollector(enabled: true);
        RenderFrameTrace trace = collector.BeginFrame(1, 1, Stopwatch.GetTimestamp())!;
        using var cancel = new CancellationTokenSource();
        using var attempted = new ManualResetEventSlim();
        Task<bool> waiter;
        lock (gate)
        {
            waiter = Task.Run(() =>
            {
                attempted.Set();
                try { using var scope = RasterReadLock.Enter(gate, cancel.Token, trace, "fixture"); return false; }
                catch (OperationCanceledException) { return true; }
            });
            Assert(attempted.Wait(TimeSpan.FromSeconds(5)), "waiter started");
            cancel.Cancel();
            Assert(waiter.Wait(TimeSpan.FromSeconds(5)) && waiter.Result, "stale read cancels without lock release");
        }
        using (RasterReadLock.Enter(gate, CancellationToken.None, trace, "fixture")) { }
        Assert(trace.Snapshot().Spans.Count(s => s.Stage == "gdal_wait") == 2, "separate wait spans");
        Assert(RenderTelemetryStatistics.ToLogLine(trace.Snapshot()).Contains("gdal_wait_ms="), "wait exposed in logs");
        Assert(Monitor.TryEnter(gate), "lock released");
        Monitor.Exit(gate);
    }

    private static void VerifyGuards(string directory, string original)
    {
        byte[] existing = SHA256.HashData(File.ReadAllBytes(original + ".ovr"));
        Assert(RasterOverviewBuilder.Schedule(original, new object()), "existing source admitted for inspection");
        Wait(() => !RasterOverviewBuilder.IsActive(original), "existing overview check");
        Assert(RasterOverviewBuilder.Metrics.Stage == "skipped-existing-overviews", "existing overviews skipped");
        Assert(existing.SequenceEqual(SHA256.HashData(File.ReadAllBytes(original + ".ovr"))), "existing sidecar unchanged");
        string path = Path.Combine(directory, "auxiliary.tif");
        File.Copy(original, path);
        File.WriteAllText(path + ".aux.xml", "<PAMDataset />", System.Text.Encoding.UTF8);
        Assert(RasterOverviewBuilder.Schedule(path, new object()), "auxiliary source admitted for inspection");
        Wait(() => !RasterOverviewBuilder.IsActive(path), "auxiliary source check");
        Assert(RasterOverviewBuilder.Metrics.Stage == "skipped-auxiliary-source" && !File.Exists(path + ".ovr"), "auxiliary preparation deferred");
        Environment.SetEnvironmentVariable("GEONEX_BUILD_OVERVIEWS", "0");
        try { Assert(!RasterOverviewBuilder.Schedule(path, new object()), "disable flag"); }
        finally { Environment.SetEnvironmentVariable("GEONEX_BUILD_OVERVIEWS", "1"); }
    }

    private static void VerifyContinuousNoData(string directory)
    {
        string path = Path.Combine(directory, "nodata.tif");
        using (Driver driver = Gdal.GetDriverByName("GTiff"))
        using (Dataset source = driver.Create(path, 1025, 1027, 3, DataType.GDT_Float32, ["TILED=YES"]))
        {
            for (int b = 1; b <= 3; b++)
            {
                using Band band = source.GetRasterBand(b);
                band.SetNoDataValue(-9999);
                var row = Enumerable.Range(0, 1025).Select(x => x % 5 == 0 ? -9999f : x * b / 11f).ToArray();
                for (int y = 0; y < 1027; y++)
                    Assert(band.WriteRaster(0, y, row.Length, 1, row, row.Length, 1, 0, 0) == CPLErr.CE_None, "continuous nodata fixture");
            }
        }
        byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
        Assert(RasterOverviewBuilder.Schedule(path, new object()), "nodata schedule");
        Wait(() => !RasterOverviewBuilder.IsActive(path), "nodata completion");
        Assert(File.Exists(path + ".ovr"), "nodata sidecar");
        VerifyPixels(directory, path, "AVERAGE");
        Assert(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "nodata source unchanged");
    }

    private static void VerifyAdmission(string directory, string original)
    {
        string active = Path.Combine(directory, "admission.tif");
        File.Copy(original, active);
        object gate = new();
        var paths = Enumerable.Range(0, 32).Select(i => Path.Combine(directory, $"queued-{i}.tif")).ToArray();
        lock (gate)
        {
            Assert(RasterOverviewBuilder.Schedule(active, gate), "active admission job");
            Wait(() => RasterOverviewBuilder.Metrics.Stage == "awaiting-publication", "active build staged");
            for (int i = 0; i < 31; i++) Assert(RasterOverviewBuilder.Schedule(paths[i], gate), "bounded queue accepts slots");
            Assert(!RasterOverviewBuilder.Schedule(paths[31], gate), "queue rejects excess without opening datasets");
            RasterOverviewBuilder.CancelAll();
            Wait(() => !RasterOverviewBuilder.IsActive(active) && paths.All(p => !RasterOverviewBuilder.IsActive(p)), "active and queued jobs cancel without render lock");
            Assert(!File.Exists(active + ".ovr"), "admission cancellation not published");
        }
        // Cancel/dispose may interleave; repeat removal calls after retirement as well.
        Parallel.For(0, 100, _ => { RasterOverviewBuilder.Cancel(active); RasterOverviewBuilder.CancelAll(); });
    }

    private static void Wait(Func<bool> condition, string message)
    {
        Assert(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(30)), message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Raster overview contract failed: {message}");
    }
}
