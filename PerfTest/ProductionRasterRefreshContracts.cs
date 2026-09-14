using System.Reflection;
using System.Security.Cryptography;
using GeoNex.Services;
using OSGeo.GDAL;
using SkiaSharp;

internal static class ProductionRasterRefreshContracts
{
    public static async Task Run(string assemblyPath)
    {
        GdalRuntimeBootstrap.Configure();
        Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        string directory = Directory.CreateTempSubdirectory("GeoNexRasterRefresh-").FullName;
        string? delay = Environment.GetEnvironmentVariable("GEONEX_OVERVIEW_DELAY_MS");
        string? enabled = Environment.GetEnvironmentVariable("GEONEX_BUILD_OVERVIEWS");
        try
        {
            Environment.SetEnvironmentVariable("GEONEX_OVERVIEW_DELAY_MS", "0");
            Environment.SetEnvironmentVariable("GEONEX_BUILD_OVERVIEWS", "1");
            string source = CreateRaster(directory);
            byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
            await VerifyLivePublication(assembly, source);
            await VerifyWarpAndGuards(assembly, source);
            Assert(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "original file unchanged");
            Console.WriteLine("Production raster refresh: PASS (actual DLL, builder callback, frame barrier, leases, cache invalidation, exact source/warp pixels, stale identity/CRS/file/disposal guards)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEONEX_OVERVIEW_DELAY_MS", delay);
            Environment.SetEnvironmentVariable("GEONEX_BUILD_OVERVIEWS", enabled);
            // This directory was created exclusively by this test, never supplied by a user.
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateRaster(string directory)
    {
        string path = Path.Combine(directory, "source.tif");
        using Driver driver = Gdal.GetDriverByName("GTiff");
        using Dataset ds = driver.Create(path, 1024, 1024, 1, DataType.GDT_Float32, ["TILED=YES"]);
        ds.SetProjection("EPSG:4326");
        ds.SetGeoTransform([-50, .0001, 0, -20, 0, -.0001]);
        using Band band = ds.GetRasterBand(1);
        var row = Enumerable.Range(0, 1024).Select(x => x / 1023f).ToArray();
        for (int y = 0; y < 1024; y++)
            Assert(band.WriteRaster(0, y, 1024, 1, row, 1024, 1, 0, 0) == CPLErr.CE_None, "fixture write");
        return path;
    }

    private static async Task VerifyLivePublication(Assembly assembly, string path)
    {
        using var map = new Harness(assembly);
        await map.VerifyMaintenanceDoesNotCancel();
        map.Set("ProjetoSRS", "EPSG:4326");
        Dataset original = Gdal.Open(path, Access.GA_ReadOnly);
        map.Publish(original);
        using var old = map.Lease("AcquireRaster");
        using (Band band = original.GetRasterBand(1)) Assert(band.GetOverviewCount() == 0, "old session predates overviews");
        object ticket = map.Capture(path);
        map.Call("PublishRasterCache", "raster", original, new SKBitmap(4, 4), "old", 0f, 0f, 1f, null);
        using var oldCache = map.CacheLease();
        long revision = (long)map.Get("SceneRevision");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? frame = await map.Pause();
        Type builder = assembly.GetType("GeoNex.Services.RasterOverviewBuilder", true)!;
        try
        {
            Func<Task> refresh = async () =>
            {
                entered.SetResult();
                try { using var pause = await map.Pause(); completed.SetResult(map.Refresh(ticket)); }
                catch (Exception ex) { completed.TrySetException(ex); throw; }
            };
            Assert((bool)builder.GetMethod("Schedule")!.Invoke(null, [path, map.GdalGate, null, refresh])!, "production builder scheduled");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert(!completed.Task.IsCompleted, "refresh waits for active frame lease");
            using (var stillOld = map.Lease("AcquireRaster")) Assert(ReferenceEquals(stillOld!.Resource, original), "source unchanged during frame");
            frame.Dispose();
            frame = null;
            Assert(await completed.Task.WaitAsync(TimeSpan.FromSeconds(30)), "refresh published");
            Assert(SpinWait.SpinUntil(() => !(bool)builder.GetMethod("IsActive")!.Invoke(null, [path])!, TimeSpan.FromSeconds(10)), "callback awaited and job retired");
            using var fresh = map.Lease("AcquireRaster");
            Assert(!ReferenceEquals(fresh!.Resource, original), "new dataset published");
            using (Band band = fresh.Resource.GetRasterBand(1)) Assert(band.GetOverviewCount() == 2, "new levels used without reopening layer");
            using var noWarp = map.Lease("AcquireWarpedRaster");
            Assert(noWarp == null, "same CRS avoids VRT");
            using var cacheAfter = map.CacheLease();
            Assert(cacheAfter == null && ((SKBitmap)oldCache!.Resource).Width == 4, "old cache retired but leased pixels alive");
            Assert((long)map.Get("SceneRevision") > revision && map.Redraws == 1, "scene invalidated once");
            using Dataset reference = Gdal.Open(path, Access.GA_ReadOnly);
            ComparePixels(fresh.Resource, reference);
            ComparePixels(original, reference, size: 1024); // old session remains valid at native resolution
            using var pauseAgain = await map.Pause();
            Assert(!map.Refresh(ticket), "old callback cannot refresh a new generation twice");
        }
        finally
        {
            frame?.Dispose();
            builder.GetMethod("Cancel")!.Invoke(null, [path]);
            Assert(SpinWait.SpinUntil(() => !(bool)builder.GetMethod("IsActive")!.Invoke(null, [path])!, TimeSpan.FromSeconds(30)), "builder retired before fixture cleanup");
        }
    }

    private static async Task VerifyWarpAndGuards(Assembly assembly, string path)
    {
        using var map = new Harness(assembly);
        Dataset original = Gdal.Open(path, Access.GA_ReadOnly);
        map.Publish(original);
        using Dataset referenceSource = Gdal.Open(path, Access.GA_ReadOnly);
        Dataset oldVrt = Warp(original, "EPSG:3857");
        map.Call("PublishWarpedRaster", "raster", original, oldVrt);
        using var retainedSource = map.Lease("AcquireRaster");
        using var retainedWarp = map.Lease("AcquireWarpedRaster");
        object ticket = map.Capture(path);
        map.Set("ProjetoSRS", "EPSG:3395"); // changed while the overview job was running
        using (var pause = await map.Pause()) Assert(map.Refresh(ticket), "refresh to current project CRS");
        using var freshWarp = map.Lease("AcquireWarpedRaster");
        using Dataset referenceWarp = Warp(referenceSource, "EPSG:3395");
        ComparePixels(freshWarp!.Resource, referenceWarp);
        using Dataset oldReferenceWarp = Warp(referenceSource, "EPSG:3857");
        ComparePixels(retainedWarp!.Resource, oldReferenceWarp);

        object failureTicket = map.Capture(path);
        using var beforeFailure = map.Lease("AcquireRaster");
        map.Set("ProjetoSRS", "INVALID_CRS_FOR_CONTRACT");
        using (var pause = await map.Pause()) Assert(!map.Refresh(failureTicket), "invalid CRS preserves session");
        using (var afterFailure = map.Lease("AcquireRaster")) Assert(ReferenceEquals(beforeFailure!.Resource, afterFailure!.Resource), "failure does not replace source");
        map.Set("ProjetoSRS", "EPSG:4326");

        object stampTicket = map.Capture(path);
        DateTime originalTime = File.GetLastWriteTimeUtc(path);
        try
        {
            File.SetLastWriteTimeUtc(path, originalTime.AddSeconds(5));
            using var pause = await map.Pause();
            Assert(!map.Refresh(stampTicket), "changed source fingerprint rejected");
        }
        finally { File.SetLastWriteTimeUtc(path, originalTime); }

        object removedTicket = map.Capture(path);
        map.Call("RemoveRaster", "raster");
        using (var pause = await map.Pause()) Assert(!map.Refresh(removedTicket), "removed layer not resurrected");
        Dataset replacement = Gdal.Open(path, Access.GA_ReadOnly);
        map.Publish(replacement);
        using (var pause = await map.Pause()) Assert(!map.Refresh(removedTicket), "same-name replacement not overwritten");
        object disposedTicket = map.Capture(path);
        map.DisposeMap();
        using (var pause = await map.Pause()) Assert(!map.Refresh(disposedTicket), "disposed map safely rejects callback");
    }

    private static Dataset Warp(Dataset source, string crs)
    {
        using var options = new GDALWarpAppOptions(["-of", "VRT", "-t_srs", crs, "-r", "cubicspline"]);
        return Gdal.Warp("", [source], options, null, null) ?? throw new InvalidOperationException("Reference warp failed.");
    }

    private static void ComparePixels(Dataset actual, Dataset expected, int size = 128)
    {
        Assert(actual.RasterXSize == expected.RasterXSize && actual.RasterYSize == expected.RasterYSize, "output grid dimensions");
        Assert(actual.GetProjection() == expected.GetProjection(), "output CRS");
        using Band a = actual.GetRasterBand(1);
        using Band e = expected.GetRasterBand(1);
        var ap = new float[size * size];
        var ep = new float[ap.Length];
        Assert(a.ReadRaster(0, 0, a.XSize, a.YSize, ap, size, size, 0, 0) == CPLErr.CE_None, "actual read");
        Assert(e.ReadRaster(0, 0, e.XSize, e.YSize, ep, size, size, 0, 0) == CPLErr.CE_None, "reference read");
        Assert(ap.SequenceEqual(ep), "all sampled pixels exactly equal independent GDAL reference");
    }

    private sealed class Lease : IDisposable
    {
        private readonly IDisposable _lease;
        public dynamic Resource { get; }
        public Lease(object lease) { _lease = (IDisposable)lease; Resource = lease.GetType().GetProperty("Resource")!.GetValue(lease)!; }
        public void Dispose() => _lease.Dispose();
    }

    private sealed class Harness : IDisposable
    {
        private readonly object _map;
        private readonly Type _type;
        private readonly object _server;
        private readonly Type _serverType;
        public int Redraws { get; private set; }
        public object GdalGate => _type.GetField("GdalRasterLock")!.GetValue(_map)!;
        public Harness(Assembly assembly)
        {
            _type = assembly.GetType("GeoNex.Services.MapRenderingService", true)!;
            _map = Activator.CreateInstance(_type)!;
            _type.GetEvent("OnMapInvalidated")!.AddEventHandler(_map, (Action)(() => Redraws++));
            _serverType = assembly.GetType("GeoNex.Services.LocalMapServer", true)!;
            _server = Activator.CreateInstance(_serverType, _map)!;
        }
        public object? Call(string name, params object?[] args) => _type.GetMethod(name)!.Invoke(_map, args);
        public object Get(string name) => _type.GetProperty(name)!.GetValue(_map)!;
        public void Set(string name, object value) => _type.GetProperty(name)!.SetValue(_map, value);
        public void Publish(Dataset ds) => Call("PublishRaster", "raster", ds, null);
        public object Capture(string path) => Call("CaptureRasterOverviewRefresh", "raster", path)!;
        public bool Refresh(object ticket) => (bool)Call("TryRefreshRasterOverviews", ticket)!;
        public Lease? Lease(string method) { object? value = Call(method, "raster"); return value == null ? null : new Lease(value); }
        public Lease? CacheLease() { object? value = Call("AcquireRasterCache", "raster", null); return value == null ? null : new Lease(value); }
        public ValueTask<IDisposable> Pause() => (ValueTask<IDisposable>)_serverType.GetMethod("PauseForRasterMaintenanceAsync")!.Invoke(_server, null)!;
        public async Task VerifyMaintenanceDoesNotCancel()
        {
            FieldInfo active = _serverType.GetField("_activeRender", BindingFlags.NonPublic | BindingFlags.Instance)!;
            FieldInfo generation = _serverType.GetField("_latestRenderGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!;
            using var token = new CancellationTokenSource();
            active.SetValue(_server, token);
            object previousGeneration = generation.GetValue(_server)!;
            try
            {
                using var pause = await Pause();
                Assert(!token.IsCancellationRequested && Equals(previousGeneration, generation.GetValue(_server)),
                    "maintenance does not cancel print/frame or advance request generation");
            }
            finally { active.SetValue(_server, null); }
        }
        public void DisposeMap() => ((IDisposable)_map).Dispose();
        public void Dispose() { _serverType.GetMethod("Stop")!.Invoke(_server, null); DisposeMap(); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Raster refresh: " + message);
    }
}
