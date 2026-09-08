using System.Diagnostics;
using System.Globalization;

internal static class NativeKernelMetrics
{
    public static unsafe void Run(byte[] shapefile, long[] offsets)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.WriteLine("scenario,p50_ms,p95_ms,p99_ms,written_floats");
        foreach ((string name, float zoom, float tolerance, float micro) in new[]
        {
            ("detail", 6f, .30f, .65f),
            ("overview", .1f, .85f, 1.25f),
            ("exact", 6f, 0f, 0f)
        })
        {
            const int width = 1920;
            const int height = 1080;
            float top = -260;
            float bottom = top + height / zoom;
            var output = new float[1_048_576];
            var grid = new byte[(width + 2) * (height + 2)];
            var samples = new List<double>(100);
            int lastWritten = 0;

            for (int pass = -10; pass < 100; ++pass)
            {
                Array.Clear(grid);
                long started = Stopwatch.GetTimestamp();
                fixed (byte* data = shapefile)
                fixed (long* records = offsets)
                fixed (float* commands = output)
                fixed (byte* mask = grid)
                {
                    int totalWritten = 0;
                    for (int first = 0; first < offsets.Length;)
                    {
                        int written = RenderContracts.ProcessShapeBatchV4(
                            data, shapefile.LongLength, 0,
                            records + first, Math.Min(4096, offsets.Length - first),
                            commands, output.Length,
                            0, 0, zoom, tolerance, micro,
                            0, top, width / zoom, bottom,
                            mask, width + 2, height + 2,
                            out int processed, out int required, out _, out _, out _);
                        if (written < 0 || processed <= 0)
                            throw new InvalidOperationException($"Native kernel status={written}, required={required}.");
                        first += processed;
                        totalWritten += written;
                    }
                    lastWritten = totalWritten;
                }

                if (pass >= 0) samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            double[] sorted = samples.Order().ToArray();
            Console.WriteLine($"{name},{sorted[49]:F3},{sorted[94]:F3},{sorted[98]:F3},{lastWritten}");
        }
    }
}
