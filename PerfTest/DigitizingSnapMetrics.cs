using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using GeoNex.Services;
using OSGeo.GDAL;
using SkiaSharp;

internal static class DigitizingSnapMetrics
{
    public static void Run(string assemblyPath, string source)
    {
        assemblyPath = Path.GetFullPath(assemblyPath);
        source = Path.GetFullPath(source);
        string previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = Directory.CreateTempSubdirectory("GeoNexSnapMetrics-").FullName;
        try { RunCore(assemblyPath, source); }
        finally { Environment.CurrentDirectory = previousDirectory; }
    }

    private static void RunCore(string assemblyPath, string source)
    {
        assemblyPath = Path.GetFullPath(assemblyPath);
        source = Path.GetFullPath(source);
        GdalRuntimeBootstrap.Configure();
        var assembly = Assembly.LoadFrom(assemblyPath);
        var mapType = assembly.GetType("GeoNex.Services.MapRenderingService", true)!;
        var projectType = assembly.GetType("GeoNex.Services.ProjetoService", true)!;
        using var map = (IDisposable)Activator.CreateInstance(mapType)!;
        mapType.GetProperty("ProjetoSRS")!.SetValue(map, "EPSG:3857");
        mapType.GetProperty("OffsetMundoDefinido")!.SetValue(map, true);
        var project = Activator.CreateInstance(projectType)!;
        string compiled = (string)projectType.GetMethod("CompilarParaShapefileNativo")!
            .Invoke(project, new object[] { source })!;
        projectType.GetMethod("CarregarShapefileParaMotorMapas")!
            .Invoke(project, new object[] { compiled, "snap-benchmark", map });

        var layers = (System.Collections.IDictionary)mapType.GetProperty("FeaturesPorCamada")!.GetValue(map)!;
        if (layers["snap-benchmark"] is not System.Collections.IList features || features.Count == 0)
            throw new InvalidOperationException("The snap benchmark layer has no features.");

        float scale = float.TryParse(Environment.GetEnvironmentVariable("GEONEX_BENCH_SCALE"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var requestedScale)
            ? requestedScale : 16;
        if (!float.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));

        object centerFeature = features[features.Count / 2]!;
        SKPoint center = (SKPoint)centerFeature.GetType().GetField("CentroidLocal")!.GetValue(centerFeature)!;
        const int width = 1600, height = 900, samples = 2048, warmup = 512;
        float halfWidth = width / scale / 2f, halfHeight = height / scale / 2f;
        float tolerance = 15f / scale;
        var random = new Random(0x474e58);
        var points = new SKPoint[512];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new SKPoint(
                center.X + ((float)random.NextDouble() * 2 - 1) * halfWidth,
                center.Y + ((float)random.NextDouble() * 2 - 1) * halfHeight);
        }

        int hits = 0;
        var findMethod = mapType.GetMethod("EncontrarVerticeProximo", BindingFlags.Public | BindingFlags.Instance)!;
        var instance = Expression.Parameter(typeof(object), "instance");
        var pointArgument = Expression.Parameter(typeof(SKPoint), "point");
        var toleranceArgument = Expression.Parameter(typeof(float), "tolerance");
        var vertexArgument = Expression.Parameter(typeof(bool), "vertices");
        var edgeArgument = Expression.Parameter(typeof(bool), "edges");
        var midpointArgument = Expression.Parameter(typeof(bool), "midpoints");
        var intersectionArgument = Expression.Parameter(typeof(bool), "intersections");
        var directCall = Expression.Call(Expression.Convert(instance, mapType), findMethod,
            pointArgument, toleranceArgument, vertexArgument, edgeArgument, midpointArgument, intersectionArgument);
        var findSnap = Expression.Lambda<Func<object, SKPoint, float, bool, bool, bool, bool, SKPoint?>>(
            directCall, instance, pointArgument, toleranceArgument, vertexArgument, edgeArgument,
            midpointArgument, intersectionArgument).Compile();
        for (int i = 0; i < warmup; i++)
            if (findSnap(map, points[i % points.Length], tolerance, true, false, false, false).HasValue) hits++;

        var elapsed = new long[samples];
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < samples; i++)
        {
            long start = Stopwatch.GetTimestamp();
            var snap = findSnap(map, points[i % points.Length], tolerance, true, false, false, false);
            elapsed[i] = Stopwatch.GetTimestamp() - start;
            if (snap.HasValue) hits++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(elapsed);

        static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
        Console.WriteLine(FormattableString.Invariant($"SNAP_METRICS features={features.Count} scale={scale:R} dpi=1 warmup={warmup} samples={samples} hits={hits} p50_ms={Milliseconds(elapsed[samples / 2]):F4} p95_ms={Milliseconds(elapsed[(int)(samples * .95)]):F4} p99_ms={Milliseconds(elapsed[(int)(samples * .99)]):F4} max_ms={Milliseconds(elapsed[^1]):F4} allocated_bytes={allocated} allocated_per_query={allocated / (double)samples:F2}"));
    }
}
