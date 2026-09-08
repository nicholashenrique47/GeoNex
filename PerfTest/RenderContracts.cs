using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;

internal static unsafe class RenderContracts
{
    [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint GetGeoNexNativeAbiVersion();
    [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ProcessShapeBatchV4(byte* data, long fileLength, nint cancellation,
        long* offsets, int count, float* output, int capacity,
        double offsetX, double offsetY, float zoom, float tolerance, float micro,
        float left, float top, float right, float bottom, byte* grid, int width, int height,
        out int processed, out int required, out int parts, out int vertices, out int microFeatures);
    [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint CreateRenderCancellation();
    [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void CancelRender(nint handle);
    [DllImport("GeoNexNative.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DestroyRenderCancellation(nint handle);

    public static void Run()
    {
        Assert(GetGeoNexNativeAbiVersion() == 4, "native ABI version 4");
        // Outer ring + hole + a disjoint part, independent of winding convention.
        (double, double)[][] rings = [
            [(10,10),(110,10),(110,110),(10,110),(10,10)],
            [(30,30),(30,70),(70,70),(70,30),(30,30)],
            [(120,20),(140,20),(140,40),(120,40),(120,20)]];
        byte[] shp = Shape(rings);
        var output = new float[1024];
        int written = Execute(shp, output, 1024, 0, 0, 0, out int processed, out int required);
        Assert(written == 27 && processed == 1 && required == 0, "exact multipart protocol");
        using var actual = new SKPath { FillType = SKPathFillType.Winding };
        AppendCommands(actual, output, written);
        using var expected = new SKPath { FillType = SKPathFillType.EvenOdd };
        foreach (var ring in rings) expected.AddPoly(ring.Select(p => new SKPoint((float)p.Item1, -(float)p.Item2)).ToArray(), true);
        using var actualBitmap = Draw(actual);
        using var expectedBitmap = Draw(expected);
        Assert(actualBitmap.Bytes.SequenceEqual(expectedBitmap.Bytes), "exact visual multipart/hole match");

        // A single EvenOdd path cancels the overlap between independent features.
        // Winding must match opaque, independent feature draws while retaining holes.
        byte[] overlapA = Shape([[(10,10),(90,10),(90,90),(10,90),(10,10)]]);
        byte[] overlapB = Shape([[(50,50),(130,50),(130,130),(50,130),(50,50)]]);
        using var mergedOverlap = new SKPath { FillType = SKPathFillType.Winding };
        AppendCommands(mergedOverlap, output, Execute(overlapA, output, output.Length, 0, 0, 0, out _, out _));
        AppendCommands(mergedOverlap, output, Execute(overlapB, output, output.Length, 0, 0, 0, out _, out _));
        using var separateA = new SKPath { FillType = SKPathFillType.EvenOdd };
        using var separateB = new SKPath { FillType = SKPathFillType.EvenOdd };
        separateA.AddPoly([
            new SKPoint(10, -10), new SKPoint(90, -10), new SKPoint(90, -90),
            new SKPoint(10, -90), new SKPoint(10, -10)], true);
        separateB.AddPoly([
            new SKPoint(50, -50), new SKPoint(130, -50), new SKPoint(130, -130),
            new SKPoint(50, -130), new SKPoint(50, -50)], true);
        using var mergedOverlapBitmap = Draw(mergedOverlap);
        using var separateOverlapBitmap = Draw(separateA, separateB);
        Assert(mergedOverlapBitmap.Bytes.SequenceEqual(separateOverlapBitmap.Bytes),
            "overlapping independent polygons must not cancel");
        string imagePath = Path.GetFullPath("artifacts/native-perf/render-contract.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        using (var image = SKImage.FromBitmap(actualBitmap))
        using (var png = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var file = File.Create(imagePath)) png.SaveTo(file);

        written = Execute(shp, output, 1, 0, 0, 0, out processed, out required);
        Assert(written == 0 && processed == 0 && required == 27, "capacity/retry atomic feature");
        written = Execute(shp, output, required, 0, 0, 0, out processed, out _);
        Assert(written == 27 && processed == 1, "retry makes progress");

        byte[] bad = (byte[])shp.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(144, 4), int.MaxValue);
        Assert(Execute(bad, output, 1024, 0, 0, 0, out _, out _) == -2, "invalid parts rejected");
        Assert(Execute(shp[..120], output, 1024, 0, 0, 0, out _, out _) == -2, "truncated record rejected");
        byte[] grid = new byte[32 * 32];
        byte[] micro = Shape([[(10,-10),(10.2,-10),(10.2,-10.2),(10,-10.2),(10,-10)]]);
        Execute(micro, output, 1024, 0, 0, 1, out processed, out _, grid);
        Assert(processed == 1 && grid[10 * 32 + 10] == 1 && grid.Count(b => b != 0) == 1,
            "micro mask origin must not include 2px culling margin");
        Array.Clear(grid);
        byte[] outside = Shape([[(-1,-10),(-.8,-10),(-.8,-10.2),(-1,-10.2),(-1,-10)]]);
        Execute(outside, output, 1024, 0, 0, 1, out _, out _, grid);
        Assert(grid.All(b => b == 0), "guard band must not clamp outside feature onto edge");

        nint cancellation = CreateRenderCancellation();
        Assert(cancellation != 0, "cancellation allocation");
        try
        {
            CancelRender(cancellation);
            Assert(Execute(shp, output, 1024, cancellation, 0, 0, out _, out _) == -1, "pre-cancelled render");
        }
        finally { DestroyRenderCancellation(cancellation); }
        // Adversarial zigzag creates long RDP scans. Cancellation must interrupt within a feature.
        var largeRing = new (double, double)[200_003];
        for (int i = 0; i < largeRing.Length - 2; i++) largeRing[i] = (i * .0005, i % 2 == 0 ? 10 : 20);
        largeRing[^2] = (0, 0); largeRing[^1] = largeRing[0];
        byte[] large = Shape([largeRing]);
        cancellation = CreateRenderCancellation();
        try
        {
            nint owner = cancellation;
            var worker = Task.Run(() => Execute(large, new float[1_048_576], 1_048_576, owner, .01f, 0, out _, out _));
            Thread.Sleep(20);
            var timer = Stopwatch.StartNew();
            CancelRender(cancellation);
            Assert(worker.Wait(TimeSpan.FromSeconds(10)), "native cancellation watchdog");
            Assert(worker.Result == -1, "in-flight RDP cancelled");
            Console.WriteLine($"Cancellation observed after {timer.Elapsed.TotalMilliseconds:F3} ms (host scheduling included)");
        }
        finally { DestroyRenderCancellation(cancellation); }
        Console.WriteLine("PASS render: ABI, exact pixels, holes/multipart, overlap, capacity retry, truncated/invalid, micro position, cancellation");
    }

    private static void AppendCommands(SKPath path, float[] output, int written)
    {
        for (int i = 0; i < written;)
        {
            int count = (int)output[i++];
            path.AddPoly(MemoryMarshal.Cast<float, SKPoint>(output.AsSpan(i, count * 2)), true);
            i += count * 2;
        }
    }

    private static SKBitmap Draw(params SKPath[] paths)
    {
        var bitmap = new SKBitmap(160, 160);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White); canvas.Translate(0, 150);
        using var paint = new SKPaint { Color = SKColors.DarkCyan, IsAntialias = false };
        foreach (SKPath path in paths) canvas.DrawPath(path, paint);
        return bitmap;
    }

    private static int Execute(byte[] data, float[] output, int capacity, nint cancellation,
        float tolerance, float micro, out int processed, out int required, byte[]? mask = null)
    {
        long offset = 100;
        fixed (byte* bytes = data)
        fixed (float* commands = output)
        fixed (byte* grid = mask)
            return ProcessShapeBatchV4(bytes, data.LongLength, cancellation, &offset, 1, commands, capacity,
                0, 0, 1, tolerance, micro, 0, mask == null ? -160 : 0, 160, 160,
                grid, 32, 32, out processed, out required, out _, out _, out _);
    }

    private static byte[] Shape((double X, double Y)[][] rings)
    {
        int points = rings.Sum(r => r.Length);
        int recordLength = 44 + rings.Length * 4 + points * 16;
        byte[] data = new byte[108 + recordLength];
        void Int(int index, int value) => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(index, 4), value);
        void Double(int index, double value) => BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(index, 8), BitConverter.DoubleToInt64Bits(value));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(104, 4), recordLength / 2);
        Int(108, 5); Int(144, rings.Length); Int(148, points);
        var all = rings.SelectMany(r => r).ToArray();
        Double(112, all.Min(p => p.X)); Double(120, all.Min(p => p.Y));
        Double(128, all.Max(p => p.X)); Double(136, all.Max(p => p.Y));
        int first = 0;
        for (int r = 0; r < rings.Length; r++) { Int(152 + r * 4, first); first += rings[r].Length; }
        for (int i = 0; i < all.Length; i++) { Double(152 + rings.Length * 4 + i * 16, all[i].X); Double(160 + rings.Length * 4 + i * 16, all[i].Y); }
        return data;
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
