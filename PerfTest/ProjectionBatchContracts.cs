using System.Collections.Concurrent;
using System.Diagnostics;
using GeoNex.Services;

internal static class ProjectionBatchContracts
{
    public static void Run()
    {
        GdalRuntimeBootstrap.Configure();
        using var source = SrsFactory.FromEPSG(4326);
        source.ExportToWkt(out string wkt, null);
        Check(ProjectionBatchRunner.WorkerCount(8, 16) == 1, "small source stays sequential");
        Check(ProjectionBatchRunner.WorkerCount(int.MaxValue, int.MaxValue) == 16, "bounded workers / integer limits");
        int scratch = 0;
        ProjectionBatchRunner.Run(8, 16, wkt, "EPSG:3857", (_, count, x, y, z, _) => scratch = x.Length + y.Length + z.Length);
        Check(scratch <= 3 * 64, "eight features use <=1.5 KiB pooled coordinates");
        bool emptyCalled = false;
        ProjectionBatchRunner.Run(0, 4, wkt, "EPSG:3857", (_, _, _, _, _, _) => emptyCalled = true);
        Check(!emptyCalled, "empty input does not allocate workers");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool cancellationSeen = false;
        try { ProjectionBatchRunner.Run(8, 2, wkt, "EPSG:3857", (_, _, _, _, _, _) => { }, canceled.Token); }
        catch (OperationCanceledException) { cancellationSeen = true; }
        Check(cancellationSeen, "cancellation before processing");
        OSGeo.OSR.CoordinateTransformation? failedTransform = null;
        bool failed = false;
        try
        {
            ProjectionBatchRunner.Run(8, 2, wkt, "EPSG:3857", (_, _, _, _, _, transform) =>
            { failedTransform = transform; throw new InvalidDataException("injected failure"); });
        }
        catch (InvalidDataException) { failed = true; }
        Check(failed && failedTransform != null &&
            OSGeo.OSR.CoordinateTransformation.getCPtr(failedTransform).Handle == 0, "failure disposes native worker");
        using var duringWork = new CancellationTokenSource();
        bool canceledAtEnd = false;
        try
        {
            ProjectionBatchRunner.Run(8, 1, wkt, "EPSG:3857", (_, _, _, _, _, _) => duringWork.Cancel(), duringWork.Token);
        }
        catch (OperationCanceledException) { canceledAtEnd = true; }
        Check(canceledAtEnd, "cancellation during final batch is observed");

        const int features = 262_147;
        foreach (string destination in new[] { "EPSG:3857", "EPSG:31982" })
        {
            var baseline = Project(1, destination);
            foreach (int workers in new[] { 2, 4, 6 })
                Check(System.Runtime.InteropServices.MemoryMarshal.AsBytes(baseline.AsSpan()).SequenceEqual(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(Project(workers, destination).AsSpan())),
                    "bit-exact serial/parallel coordinate output");
        }
        var timings = new Dictionary<int, List<double>> { [1] = new(), [4] = new() };
        for (int pass = 0; pass < 7; pass++)
        foreach (int workers in pass % 2 == 0 ? new[] { 1, 4 } : new[] { 4, 1 })
        {
            var watch = Stopwatch.StartNew(); Project(workers, "EPSG:3857"); watch.Stop();
            if (pass > 0) timings[workers].Add(watch.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"Projection batches: PASS (1,310,735 points, Mercator/UTM, exact coordinates/order, independent transforms, small buffers, cancellation); serial_median_ms={timings[1].Order().ElementAt(3):F2} parallel4_median_ms={timings[4].Order().ElementAt(3):F2}; 6 warm samples, includes allocation/transform setup, not UI latency");

        double[] Project(int workers, string destination)
        {
            var output = new double[features * 10];
            var active = new ConcurrentDictionary<nint, byte>();
            var owned = new ConcurrentBag<OSGeo.OSR.CoordinateTransformation>();
            int completed = 0;
            ProjectionBatchRunner.Run(features, workers, wkt, destination, (first, count, x, y, z, transform) =>
            {
                // Native handles may be reused after disposal, but never used concurrently.
                nint handle = OSGeo.OSR.CoordinateTransformation.getCPtr(transform).Handle;
                owned.Add(transform);
                Check(active.TryAdd(handle, 0), "transform is not shared across workers");
                try
                {
                    int points = count * 5;
                    for (int i = 0; i < points; i++)
                    {
                        int ordinal = first * 5 + i;
                        x[i] = -49 + (ordinal % 1000) * .00001;
                        y[i] = -27 + (ordinal / 1000) * .00001;
                        z[i] = 0;
                    }
                    transform.TransformPoints(points, x, y, z);
                    for (int i = 0; i < points; i++)
                    {
                        int target = (first * 5 + i) * 2;
                        output[target] = x[i]; output[target + 1] = y[i];
                    }
                    Interlocked.Add(ref completed, count);
                }
                finally { active.TryRemove(handle, out _); }
            });
            Check(completed == features, "every feature is processed exactly once");
            foreach (var transform in owned)
                Check(OSGeo.OSR.CoordinateTransformation.getCPtr(transform).Handle == 0, "worker transforms disposed");
            return output;
        }
    }

    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }
}
