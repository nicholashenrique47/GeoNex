using System.Buffers.Binary;
using System.Runtime.InteropServices;

// Differential contracts for runtime ISA dispatch. Run the process again with
// GEONEX_FORCE_SCALAR=1 to validate startup override (selection is immutable).
internal static unsafe class NativeCpuContracts
{
    public static void Run()
    {
        int backend = GetNativePointParserBackend();
        int available = IsNativeAvx2Available();
        bool forceScalar = Environment.GetEnvironmentVariable("GEONEX_FORCE_SCALAR") == "1";
        if (backend != (available != 0 && !forceScalar ? 1 : 0))
            throw new InvalidOperationException("Native ISA dispatch selected an incompatible backend.");

        var random = new Random(741852);
        int comparisons = 0;
        foreach (int count in new[] { 0, 1, 2, 3, 7, 16, 17, 257, 4097 })
        foreach (int alignment in new[] { 0, 1, 3, 4, 7 })
        foreach (double offset in new[] { 0.0, 1_000_000.25, -1e12 })
        {
            var input = new byte[alignment + count * 16];
            var automatic = Enumerable.Repeat(123456.0f, count * 2 + 2).ToArray();
            var scalar = Enumerable.Repeat(123456.0f, count * 2 + 2).ToArray();
            for (int i = 0; i < count * 2; i++)
            {
                double value = (i % 11) switch
                {
                    0 => offset,
                    1 => -0.0,
                    2 => double.Epsilon,
                    3 => 1e300,
                    4 => -1e300,
                    5 => double.PositiveInfinity,
                    6 => double.NegativeInfinity,
                    7 => double.NaN,
                    _ => offset + (random.NextDouble() - 0.5) * 100000,
                };
                BinaryPrimitives.WriteDoubleLittleEndian(input.AsSpan(alignment + i * 8, 8), value);
            }
            fixed (byte* source = input)
            fixed (float* autoOutput = automatic)
            fixed (float* scalarOutput = scalar)
            {
                ParseShapefilePartAVX2(source + alignment, autoOutput + 1, count, offset, -offset);
                ParseShapefilePartScalar(source + alignment, scalarOutput + 1, count, offset, -offset);
            }
            for (int i = 0; i < automatic.Length; i++)
            {
                bool equivalentNaN = float.IsNaN(automatic[i]) && float.IsNaN(scalar[i]);
                if (!equivalentNaN && BitConverter.SingleToInt32Bits(automatic[i]) != BitConverter.SingleToInt32Bits(scalar[i]))
                    throw new InvalidOperationException($"Native parser mismatch: count={count}, alignment={alignment}, offset={offset}, float={i}.");
                comparisons++;
            }
            if (automatic[0] != 123456.0f || automatic[^1] != 123456.0f)
                throw new InvalidOperationException("Native parser wrote beyond output point range.");
        }

        float sentinel = 123456.0f;
        ParseShapefilePartAVX2(null, &sentinel, 3, 0, 0);
        ParseShapefilePartAVX2((byte*)&sentinel, &sentinel, -1, 0, 0);
        ParseShapefilePartAVX2((byte*)&sentinel, null, 3, 0, 0);
        if (sentinel != 123456.0f) throw new InvalidOperationException("Invalid parser input modified output.");
        Console.WriteLine($"Native CPU contracts: PASS ({comparisons:N0} values; backend={(backend == 1 ? "AVX2" : "scalar")}; AVX2 available={available != 0})");
    }

    [DllImport("GeoNexNative", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetNativePointParserBackend();
    [DllImport("GeoNexNative", CallingConvention = CallingConvention.Cdecl)]
    private static extern int IsNativeAvx2Available();
    [DllImport("GeoNexNative", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ParseShapefilePartAVX2(byte* input, float* output, int count, double offsetX, double offsetY);
    [DllImport("GeoNexNative", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ParseShapefilePartScalar(byte* input, float* output, int count, double offsetX, double offsetY);
}
