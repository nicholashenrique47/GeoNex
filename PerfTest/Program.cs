using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using SkiaSharp;

string? nativeOverride = Environment.GetEnvironmentVariable("GEONEX_NATIVE_PATH");
if (!string.IsNullOrEmpty(nativeOverride))
{
    nint nativeHandle = NativeLibrary.Load(Path.GetFullPath(nativeOverride));
    NativeLibrary.SetDllImportResolver(typeof(Native).Assembly,
        (name, assembly, path) => name == "GeoNexNative.dll" ? nativeHandle : 0);
}

if (args.Contains("--index-contracts")) { IndexContracts.Run(); return; }
if (args.Contains("--cpu-contracts")) { NativeCpuContracts.Run(); return; }
if (args.Contains("--render-contracts")) { RenderContracts.Run(); return; }
if (args.Contains("--managed-interop-contracts")) { ManagedInteropContracts.Run(); return; }
if (args.Contains("--presentation-metrics")) { PresentationMetrics.Run(); return; }
if (args.Contains("--resource-budget-contracts")) { ResourceBudgetContracts.Run(); return; }
if (args.Contains("--resource-lease-contracts")) { ResourceLeaseContracts.Run(); return; }
if (args.Contains("--raster-contracts")) { RasterContracts.Run(); return; }
if (args.Contains("--raster-overview-contracts")) { RasterOverviewContracts.Run(); return; }
if (args.Contains("--ecw-contracts")) { EcwContracts.Run(); return; }
if (args.Contains("--online-basemap-contracts")) { OnlineBasemapContracts.Run(); return; }
if (args.Contains("--online-basemap-smoke")) { OnlineBasemapSmoke.Run(); return; }
if (args.Contains("--export-contracts")) { ExportContracts.Run(); return; }
if (args.Contains("--coordinate-contracts")) { CoordinateContracts.Run(); return; }
if (args.Contains("--telemetry-contracts")) { TelemetryContracts.Run(); return; }
if (args.Contains("--large-shp-contracts")) { LargeShapefileContracts.Run(); return; }
if (Array.IndexOf(args, "--large-shp-metrics") is int realIndex && realIndex >= 0)
{
    LargeShapefileContracts.Measure(args[realIndex + 1]);
    return;
}

const int FeatureCount = 78_000;
const int PointsPerFeature = 9;
const int RecordBytes = 8 + 4 + 32 + 4 + 4 + 4 + PointsPerFeature * 16;

var shp = new byte[100 + FeatureCount * RecordBytes];
var offsets = new long[FeatureCount];
BuildSyntheticLots(shp, offsets);

if (args.Contains("--baseline-contracts"))
{
    EngineBaseline.RunContracts(shp, offsets);
    return;
}

if (args.Contains("--capture-baseline"))
{
    EngineBaseline.Capture(shp, offsets);
    return;
}

if (args.Contains("--kernel-metrics"))
{
    NativeKernelMetrics.Run(shp, offsets);
    return;
}

if (args.Contains("--metrics"))
{
    int webpQuality = args.Contains("--webp70") ? 70 : args.Contains("--webp95") ? 95 : 0;
    PipelineMetrics.Run(shp, offsets, args.Contains("--v4"), webpQuality);
    return;
}

Console.WriteLine($"GeoNex native benchmark: {FeatureCount:N0} lotes / {FeatureCount * PointsPerFeature:N0} vertices");
Run("detalhado", shp, offsets, zoom: 6.0f, tolerancePixels: 0.30f, microLodPixels: 0.65f);
Run("micro-LOD", shp, offsets, zoom: 0.10f, tolerancePixels: 0.85f, microLodPixels: 1.25f);
RunFullPipeline(shp, offsets);
RunMicroAggregationComparison(shp, offsets);
RunSpatialIndexBenchmark();
RunNativeSpatialIndexBenchmark(shp, offsets);
RunProjectedIndexCorrectness(offsets);

if (args.Contains("--million", StringComparer.OrdinalIgnoreCase))
{
    const int largeFeatureCount = 1_000_000;
    var largeShp = new byte[100 + largeFeatureCount * RecordBytes];
    var largeOffsets = new long[largeFeatureCount];
    BuildSyntheticLots(largeShp, largeOffsets);
    Console.WriteLine($"Escala ampliada: {largeFeatureCount:N0} polígonos / {largeShp.Length / (1024.0 * 1024.0):F1} MB de SHP sintético");
    RunNativeSpatialIndexBenchmark(largeShp, largeOffsets);
}

static void BuildSyntheticLots(byte[] target, long[] offsets)
{
    ReadOnlySpan<(double X, double Y)> template =
    [
        (0, 0), (0.5, 0), (1, 0), (1, 0.5), (1, 1),
        (0.5, 1), (0, 1), (0, 0.5), (0, 0)
    ];

    for (int feature = 0; feature < offsets.Length; feature++)
    {
        int column = feature % 300;
        int row = feature / 300;
        int record = 100 + feature * RecordBytes;
        offsets[feature] = record;
        int data = record + 8;

        BinaryPrimitives.WriteInt32BigEndian(target.AsSpan(record, 4), feature + 1);
        BinaryPrimitives.WriteInt32BigEndian(target.AsSpan(record + 4, 4), (RecordBytes - 8) / 2);

        WriteInt(target, data, 5);
        WriteDouble(target, data + 4, column);
        WriteDouble(target, data + 12, row);
        WriteDouble(target, data + 20, column + 1);
        WriteDouble(target, data + 28, row + 1);
        WriteInt(target, data + 36, 1);
        WriteInt(target, data + 40, PointsPerFeature);
        WriteInt(target, data + 44, 0);

        int pointData = data + 48;
        for (int point = 0; point < template.Length; point++)
        {
            WriteDouble(target, pointData + point * 16, column + template[point].X);
            WriteDouble(target, pointData + point * 16 + 8, row + template[point].Y);
        }
    }
}

static void Run(string name, byte[] shp, long[] offsets, float zoom, float tolerancePixels, float microLodPixels)
{
    const int BatchSize = 4096;
    var output = new float[1_048_576];

    Execute(shp, offsets, output, 1, zoom, tolerancePixels, microLodPixels);

    long bestTicks = long.MaxValue;
    (long Floats, long Parts, long Vertices) result = default;
    for (int pass = 0; pass < 8; pass++)
    {
        var sw = Stopwatch.StartNew();
        result = Execute(shp, offsets, output, BatchSize, zoom, tolerancePixels, microLodPixels);
        sw.Stop();
        bestTicks = Math.Min(bestTicks, sw.ElapsedTicks);
    }

    double milliseconds = bestTicks * 1000.0 / Stopwatch.Frequency;
    Console.WriteLine($"{name,-10}: {milliseconds,8:F2} ms | partes={result.Parts:N0} vertices={result.Vertices:N0} floats={result.Floats:N0}");
}

static unsafe (long Floats, long Parts, long Vertices) Execute(
    byte[] shp, long[] offsets, float[] output, int batchSize,
    float zoom, float tolerancePixels, float microLodPixels, SKPath? destination = null)
{
    long totalFloats = 0;
    long totalParts = 0;
    long totalVertices = 0;

    fixed (byte* shpPtr = shp)
    fixed (long* offsetsPtr = offsets)
    fixed (float* outputPtr = output)
    {
        int cursor = 0;
        while (cursor < offsets.Length)
        {
            int requested = Math.Min(batchSize, offsets.Length - cursor);
            int written = Native.ProcessShapeBatchV2(
                shpPtr, offsetsPtr + cursor, requested, outputPtr, output.Length,
                0, 0, zoom, tolerancePixels, microLodPixels,
                0, -260, 300, 0,
                out int processed, out int required, out int parts, out int vertices);

            if (processed <= 0)
                throw new InvalidOperationException($"Batch nao avancou; precisa de {required:N0} floats.");

            cursor += processed;
            totalFloats += written;
            totalParts += parts;
            totalVertices += vertices;

            if (destination != null)
            {
                int outputIndex = 0;
                while (outputIndex < written)
                {
                    float command = output[outputIndex++];
                    if (command == -4.0f)
                    {
                        destination.AddRect(new SKRect(
                            output[outputIndex], output[outputIndex + 1],
                            output[outputIndex + 2], output[outputIndex + 3]));
                        outputIndex += 4;
                    }
                    else
                    {
                        int pointCount = (int)command;
                        int coordinateCount = pointCount * 2;
                        var points = MemoryMarshal.Cast<float, SKPoint>(
                            new ReadOnlySpan<float>(output, outputIndex, coordinateCount));
                        destination.AddPoly(points, close: true);
                        outputIndex += coordinateCount;
                    }
                }
            }
        }
    }

    return (totalFloats, totalParts, totalVertices);
}

static void RunFullPipeline(byte[] shp, long[] offsets)
{
    var output = new float[1_048_576];
    long bestTicks = long.MaxValue;

    for (int pass = 0; pass < 5; pass++)
    {
        using var surface = SKSurface.Create(new SKImageInfo(1920, 1080));
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(40, 140, 220, 90), IsAntialias = true };
        using var border = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(20, 80, 130), StrokeWidth = 0.15f, IsAntialias = true };

        var sw = Stopwatch.StartNew();
        Execute(shp, offsets, output, 4096, 6.0f, 0.30f, 0.65f, path);
        surface.Canvas.Translate(0, 1080);
        surface.Canvas.Scale(6.0f);
        surface.Canvas.DrawPath(path, fill);
        surface.Canvas.DrawPath(path, border);
        surface.Flush();
        sw.Stop();
        bestTicks = Math.Min(bestTicks, sw.ElapsedTicks);
    }

    double milliseconds = bestTicks * 1000.0 / Stopwatch.Frequency;
    Console.WriteLine($"Skia completo: {milliseconds,8:F2} ms | C++ + SKPath + fill + borda em 1920x1080");
}

static void RunMicroAggregationComparison(byte[] shp, long[] offsets)
{
    var output = new float[1_048_576];
    double legacyBest = double.MaxValue;
    double aggregatedBest = double.MaxValue;
    int legacyPoints = 0;
    int aggregatedPoints = 0;

    for (int pass = 0; pass < 6; ++pass)
    {
        using (var surface = SKSurface.Create(new SKImageInfo(1920, 1080)))
        using (var path = new SKPath { FillType = SKPathFillType.EvenOdd })
        using (var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(40, 140, 220, 90), IsAntialias = false })
        {
            var sw = Stopwatch.StartNew();
            Execute(shp, offsets, output, 4096, 0.10f, 0.85f, 1.25f, path);
            surface.Canvas.DrawPath(path, fill);
            surface.Flush();
            sw.Stop();
            legacyBest = Math.Min(legacyBest, sw.Elapsed.TotalMilliseconds);
            legacyPoints = path.PointCount;
        }

        using (var surface = SKSurface.Create(new SKImageInfo(1920, 1080)))
        using (var path = new SKPath { FillType = SKPathFillType.EvenOdd })
        using (var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(40, 140, 220, 90), IsAntialias = false })
        {
            var sw = Stopwatch.StartNew();
            ExecuteAggregated(shp, offsets, output, path);
            surface.Canvas.DrawPath(path, fill);
            surface.Flush();
            sw.Stop();
            aggregatedBest = Math.Min(aggregatedBest, sw.Elapsed.TotalMilliseconds);
            aggregatedPoints = path.PointCount;
        }
    }

    Console.WriteLine($"Micro-LOD legado: {legacyBest,8:F2} ms | pontos SKPath={legacyPoints:N0}");
    Console.WriteLine($"Micro-LOD grade : {aggregatedBest,8:F2} ms | pontos SKPath={aggregatedPoints:N0} | ganho={legacyBest / aggregatedBest:F1}x");
}

static unsafe void ExecuteAggregated(byte[] shp, long[] offsets, float[] output, SKPath destination)
{
    const float zoom = 0.10f;
    const float viewportLeft = 0;
    const float viewportTop = -260;
    const float viewportRight = 300;
    const float viewportBottom = 0;
    int gridWidth = (int)Math.Ceiling((viewportRight - viewportLeft) * zoom) + 2;
    int gridHeight = (int)Math.Ceiling((viewportBottom - viewportTop) * zoom) + 2;
    var grid = new byte[gridWidth * gridHeight];

    fixed (byte* shpPtr = shp)
    fixed (long* offsetsPtr = offsets)
    fixed (float* outputPtr = output)
    fixed (byte* gridPtr = grid)
    {
        int cursor = 0;
        while (cursor < offsets.Length)
        {
            int requested = Math.Min(4096, offsets.Length - cursor);
            int written = Native.ProcessShapeBatchV3(
                shpPtr, offsetsPtr + cursor, requested, outputPtr, output.Length,
                0, 0, zoom, 0.85f, 1.25f,
                viewportLeft, viewportTop, viewportRight, viewportBottom,
                gridPtr, gridWidth, gridHeight,
                out int processed, out int required, out _, out _, out _);
            if (processed <= 0)
                throw new InvalidOperationException($"V3 não avançou; precisa de {required:N0} floats.");

            int outputIndex = 0;
            while (outputIndex < written)
            {
                int pointCount = (int)output[outputIndex++];
                int coordinateCount = pointCount * 2;
                var points = MemoryMarshal.Cast<float, SKPoint>(
                    new ReadOnlySpan<float>(output, outputIndex, coordinateCount));
                destination.AddPoly(points, close: true);
                outputIndex += coordinateCount;
            }
            cursor += processed;
        }
    }

    float inverseZoom = 1.0f / zoom;
    for (int y = 0; y < gridHeight; ++y)
    {
        int row = y * gridWidth;
        int x = 0;
        while (x < gridWidth)
        {
            while (x < gridWidth && grid[row + x] == 0) ++x;
            if (x >= gridWidth) break;
            int start = x++;
            while (x < gridWidth && grid[row + x] != 0) ++x;
            destination.AddRect(new SKRect(
                viewportLeft + start * inverseZoom,
                viewportTop + y * inverseZoom,
                viewportLeft + x * inverseZoom,
                viewportTop + (y + 1) * inverseZoom));
        }
    }
}

static void RunSpatialIndexBenchmark()
{
    int[] capacities = [8, 16, 32, 64, 100];
    Console.WriteLine("STRtree (2.000 consultas de viewport):");
    foreach (int capacity in capacities)
    {
        var tree = new STRtree<int>(capacity);
        for (int feature = 0; feature < FeatureCount; feature++)
        {
            int x = feature % 300;
            int y = feature / 300;
            tree.Insert(new Envelope(x, x + 1, y, y + 1), feature);
        }
        tree.Build();

        long checksum = 0;
        var sw = Stopwatch.StartNew();
        for (int query = 0; query < 2_000; query++)
        {
            double x = (query * 37) % 270;
            double y = (query * 53) % 240;
            checksum += tree.Query(new Envelope(x, x + 30, y, y + 20)).Count;
        }
        sw.Stop();
        Console.WriteLine($"  capacidade {capacity,3}: {sw.Elapsed.TotalMilliseconds,7:F2} ms | checksum={checksum:N0}");
    }
}

static unsafe void RunNativeSpatialIndexBenchmark(byte[] shp, long[] offsets)
{
    Console.WriteLine("Índice espacial C++ (2.000 consultas de viewport):");
    fixed (byte* shpPointer = shp)
    fixed (long* offsetPointer = offsets)
    {
        var build = Stopwatch.StartNew();
        nint handle = Native.CreateShapeSpatialIndex(
            shpPointer, shp.Length, offsetPointer, null, null, offsets.Length, 12);
        build.Stop();
        if (handle == 0) throw new InvalidOperationException("Falha ao criar índice espacial C++.");

        try
        {
            long bytes = Native.GetShapeSpatialIndexBytes(handle, out int cells, out int entries, out int oversized);
            var results = new int[offsets.Length];
            long checksum = 0;
            var query = Stopwatch.StartNew();
            fixed (int* resultPointer = results)
            {
                for (int i = 0; i < 2_000; ++i)
                {
                    double x = (i * 37) % 270;
                    double y = (i * 53) % 240;
                    int copied = Native.QueryShapeSpatialIndex(
                        handle, x, y, x + 30, y + 20,
                        resultPointer, results.Length, out int required);
                    if (copied != required) throw new InvalidOperationException("Buffer de consulta insuficiente.");
                    checksum += copied;
                }
            }
            query.Stop();
            Console.WriteLine($"  build={build.Elapsed.TotalMilliseconds:F2} ms | query={query.Elapsed.TotalMilliseconds:F2} ms | checksum={checksum:N0}");
            Console.WriteLine($"  memória={bytes / (1024.0 * 1024.0):F2} MB | células={cells:N0} entradas={entries:N0} oversized={oversized:N0}");
        }
        finally
        {
            Native.DestroyShapeSpatialIndex(handle);
        }
    }
}

static unsafe void RunProjectedIndexCorrectness(long[] offsets)
{
    var bounds = new double[offsets.Length * 4];
    var kinds = new byte[offsets.Length];
    for (int feature = 0; feature < offsets.Length; ++feature)
    {
        int x = feature % 300;
        int y = feature / 300;
        int cursor = feature * 4;
        bounds[cursor] = 1_000 + x * 2;
        bounds[cursor + 1] = 2_000 + y * 2;
        bounds[cursor + 2] = 1_002 + x * 2;
        bounds[cursor + 3] = 2_002 + y * 2;
        kinds[feature] = 3;
    }

    fixed (long* offsetPointer = offsets)
    fixed (double* boundsPointer = bounds)
    fixed (byte* kindsPointer = kinds)
    {
        nint handle = Native.CreateShapeSpatialIndex(
            null, 0, offsetPointer, boundsPointer, kindsPointer, offsets.Length, 12);
        if (handle == 0) throw new InvalidOperationException("Falha ao criar índice com bounds reprojetados.");
        try
        {
            var results = new int[offsets.Length];
            long checksum = 0;
            fixed (int* resultPointer = results)
            {
                for (int query = 0; query < 2_000; ++query)
                {
                    double x = 1_000 + ((query * 37) % 270) * 2;
                    double y = 2_000 + ((query * 53) % 240) * 2;
                    checksum += Native.QueryShapeSpatialIndex(
                        handle, x, y, x + 60, y + 40,
                        resultPointer, results.Length, out _);
                }
            }

            if (checksum != 1_407_537)
                throw new InvalidOperationException($"Índice reprojetado incorreto: {checksum:N0}.");
            Console.WriteLine($"Índice reprojetado: checksum correto ({checksum:N0})");
        }
        finally
        {
            Native.DestroyShapeSpatialIndex(handle);
        }
    }
}

static void WriteInt(byte[] target, int offset, int value) =>
    BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset, 4), value);

static void WriteDouble(byte[] target, int offset, double value) =>
    BinaryPrimitives.WriteInt64LittleEndian(target.AsSpan(offset, 8), BitConverter.DoubleToInt64Bits(value));

internal static class Native
{
    [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint GetGeoNexNativeAbiVersion();

    [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int ProcessShapeBatchV2(
        byte* basePtr, long* offsets, int count, float* outBuffer, int maxFloats,
        double offsetX, double offsetY, float zoomReal, float tolerancePixels,
        float microLodPixels, float viewportLeft, float viewportTop,
        float viewportRight, float viewportBottom, out int processedFeatures,
        out int requiredFloats, out int emittedParts, out int emittedVertices);

    [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int ProcessShapeBatchV3(
        byte* basePtr, long* offsets, int count, float* outBuffer, int maxFloats,
        double offsetX, double offsetY, float zoomReal, float tolerancePixels,
        float microLodPixels, float viewportLeft, float viewportTop,
        float viewportRight, float viewportBottom, byte* microGrid,
        int microGridWidth, int microGridHeight, out int processedFeatures,
        out int requiredFloats, out int emittedParts, out int emittedVertices,
        out int emittedMicroFeatures);

    [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe nint CreateShapeSpatialIndex(
        byte* basePtr, long fileLength, long* offsets, double* bounds,
        byte* kinds, int count, int workerCount);

    [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void DestroyShapeSpatialIndex(nint handle);

    [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int QueryShapeSpatialIndex(
        nint handle, double queryMinX, double queryMinY, double queryMaxX, double queryMaxY,
        int* results, int resultCapacity, out int requiredCount);

    [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern long GetShapeSpatialIndexBytes(
        nint handle, out int gridCells, out int gridEntries, out int oversizedFeatures);
}
