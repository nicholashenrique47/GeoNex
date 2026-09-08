using System.Diagnostics;

// Runs against either the baseline or optimized DLL: no new exports required.
// The printed digest includes result ORDER, not merely a count/checksum.
internal static unsafe class IndexContracts
{
    private readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY);

    public static void Run(string[]? args = null)
    {
        args ??= Environment.GetCommandLineArgs();
        Console.WriteLine("Index contracts: brute force, CSR order digest, capacity, concurrency");
        ulong digest = 14695981039346656037UL;
        foreach (string distribution in new[] { "grid", "clustered", "oversized", "degenerate" })
            CheckDistribution(distribution, ref digest);
        Console.WriteLine($"  PASS: deterministic ordered digest={digest:x16}");

        int count = args.Contains("--index-eight-million", StringComparer.OrdinalIgnoreCase)
            ? 8_000_000 : args.Contains("--million", StringComparer.OrdinalIgnoreCase)
                ? 1_000_000 : 78_000;
        foreach (string distribution in new[] { "grid", "clustered", "oversized" })
            Benchmark(count, distribution);
    }

    private static void CheckDistribution(string distribution, ref ulong digest)
    {
        const int count = 4096;
        var data = MakeData(count, distribution, includeInvalid: true);
        nint handle = Create(data.Bounds, data.Kinds);
        try
        {
            var boxes = new List<Box>
            {
                new(-1, -1, 1001, 1001), new(0, 0, 1000, 1000),
                new(0, 0, 0, 0), new(500, 500, 500, 500),
                new(1000, 1000, 1000, 1000), new(1001, 1001, 1002, 1002),
                new(499, 0, 501, 1000), new(0, 499, 1000, 501),
                new(100, 100, 99, 99),
            };
            var random = new Random(7331);
            for (int i = 0; i < 96; ++i)
            {
                double x = random.NextDouble() * 1200 - 100;
                double y = random.NextDouble() * 1200 - 100;
                boxes.Add(new Box(x, y, x + random.NextDouble() * 700,
                    y + random.NextDouble() * 700));
            }

            var expectedOrdered = new int[boxes.Count][];
            for (int query = 0; query < boxes.Count; ++query)
            {
                Box box = boxes[query];
                int[] actual = Query(handle, box, count);
                int[] expected = BruteForce(data.Bounds, data.Kinds, box);
                if (!actual.Order().SequenceEqual(expected))
                    throw new InvalidOperationException($"{distribution}: spatial mismatch, query {query}");
                if (actual.Distinct().Count() != actual.Length)
                    throw new InvalidOperationException($"{distribution}: duplicate feature");
                expectedOrdered[query] = actual;
                digest = (digest ^ (uint)actual.Length) * 1099511628211UL;
                foreach (int feature in actual) digest = (digest ^ (uint)feature) * 1099511628211UL;
                CheckCapacity(handle, box, actual);
            }

            // Every worker uses independent output buffers against one index.
            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
            {
                for (int iteration = 0; iteration < 3; ++iteration)
                    for (int query = 0; query < boxes.Count; ++query)
                    {
                        int selected = (query + worker * 7) % boxes.Count;
                        if (!Query(handle, boxes[selected], count).SequenceEqual(expectedOrdered[selected]))
                            throw new InvalidOperationException($"{distribution}: concurrent result/order mismatch");
                    }
            });
            Native.GetShapeSpatialIndexBytes(handle, out _, out _, out int oversized);
            if (distribution == "oversized" && oversized == 0)
                throw new InvalidOperationException("Oversized test does not exercise oversized features.");
            Console.WriteLine($"  {distribution}: {boxes.Count} reference queries + capacity + 8 concurrent readers PASS; oversized={oversized}");
        }
        finally { Native.DestroyShapeSpatialIndex(handle); }
    }

    private static void CheckCapacity(nint handle, Box box, int[] expected)
    {
        foreach (int capacity in new[] { -1, 0, 1, 7, expected.Length, expected.Length + 3 })
        {
            var output = Enumerable.Repeat(-987654321, Math.Max(capacity, 0) + 2).ToArray();
            fixed (int* outputPointer = output)
            {
                int copied = Native.QueryShapeSpatialIndex(handle, box.MinX, box.MinY, box.MaxX, box.MaxY,
                    outputPointer + 1, capacity, out int required);
                int expectedCopied = Math.Min(expected.Length, Math.Max(capacity, 0));
                if (required != expected.Length || copied != expectedCopied ||
                    !output.AsSpan(1, copied).SequenceEqual(expected.AsSpan(0, copied)) ||
                    output[0] != -987654321 || output.Skip(copied + 1).Any(x => x != -987654321))
                    throw new InvalidOperationException("Query capacity/prefix/guard contract violated.");
            }
        }
        int reported = Native.QueryShapeSpatialIndex(handle, box.MinX, box.MinY, box.MaxX, box.MaxY,
            null, 0, out int total);
        if (reported != 0 || total != expected.Length)
            throw new InvalidOperationException("Count-only query contract violated.");
    }

    private static void Benchmark(int count, string distribution)
    {
        var data = MakeData(count, distribution, includeInvalid: false);
        var build = Stopwatch.StartNew();
        nint handle = Create(data.Bounds, data.Kinds);
        build.Stop();
        try
        {
            long indexBytes = Native.GetShapeSpatialIndexBytes(handle, out int cells, out int entries, out int oversized);
            Console.WriteLine($"Index benchmark: N={count:N0} {distribution}, build={build.Elapsed.TotalMilliseconds:F2} ms, reportedNative={indexBytes / 1048576.0:F2} MiB, cells={cells}, entries={entries}, oversized={oversized}");
            Console.WriteLine("  Warm in-process synthetic bounds; reportedNative excludes legacy queryScratch. Process memory is observational, not isolated allocation accounting.");
            var output = new int[count];
            // Commit the output before observing growth from the query itself.
            Array.Fill(output, -1);
            var modes = new (string Name, Box Box)[]
            {
                ("detail", new(300, 400, 330, 420)),
                ("quarter", new(200, 200, 700, 700)),
                ("broad", new(50, 50, 950, 950)),
                ("world", new(-1, -1, 1001, 1001)),
            };
            fixed (int* pointer = output)
            {
                foreach (var mode in modes)
                {
                    using var process = Process.GetCurrentProcess();
                    process.Refresh();
                    long beforePrivate = process.PrivateMemorySize64;
                    long beforeWorking = process.WorkingSet64;
                    long firstStart = Stopwatch.GetTimestamp();
                    int expected = Call(handle, mode.Box, pointer, output.Length, out int required);
                    double firstMs = Stopwatch.GetElapsedTime(firstStart).TotalMilliseconds;
                    if (expected != required) throw new InvalidOperationException("Benchmark output too small.");
                    process.Refresh();
                    long privateDelta = process.PrivateMemorySize64 - beforePrivate;
                    long workingDelta = process.WorkingSet64 - beforeWorking;
                    for (int warm = 0; warm < 3; ++warm) Call(handle, mode.Box, pointer, output.Length, out _);
                    var timings = new double[25];
                    long checksum = 0;
                    for (int sample = 0; sample < timings.Length; ++sample)
                    {
                        long started = Stopwatch.GetTimestamp();
                        int copied = Call(handle, mode.Box, pointer, output.Length, out required);
                        timings[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        if (copied != expected || required != expected)
                            throw new InvalidOperationException("Benchmark count changed.");
                        checksum += copied;
                    }
                    Array.Sort(timings);
                    Console.WriteLine($"  {mode.Name,-7}: first={firstMs:F3} ms, p50={Percentile(timings, .50):F3} p95={Percentile(timings, .95):F3} p99={Percentile(timings, .99):F3} ms, matches={expected:N0}, firstQueryPrivateDelta={privateDelta / 1048576.0:F2} MiB, workingDelta={workingDelta / 1048576.0:F2} MiB, checksum={checksum}");
                }
            }
            BenchmarkConcurrent(handle, count);
        }
        finally { Native.DestroyShapeSpatialIndex(handle); }
    }

    private static void BenchmarkConcurrent(nint handle, int count)
    {
        var box = new Box(50, 50, 950, 950);
        foreach (int workers in new[] { 1, Math.Min(Environment.ProcessorCount, 4) }.Distinct())
        {
            // Allocate outputs once; no shared output/scratch between workers.
            var buffers = Enumerable.Range(0, workers).Select(_ => new int[count]).ToArray();
            foreach (var buffer in buffers) Array.Fill(buffer, -1);
            var times = new double[9];
            long checksum = 0;
            for (int pass = -1; pass < times.Length; ++pass)
            {
                long begin = Stopwatch.GetTimestamp();
                Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
                {
                    long local = 0;
                    fixed (int* pointer = buffers[worker])
                        for (int query = 0; query < 4; ++query)
                            local += Call(handle, box, pointer, count, out _);
                    Interlocked.Add(ref checksum, local);
                });
                if (pass >= 0) times[pass] = Stopwatch.GetElapsedTime(begin).TotalMilliseconds / (workers * 4);
            }
            Array.Sort(times);
            Console.WriteLine($"  concurrent workers={workers}: amortized ms/query p50={Percentile(times, .50):F3} p95={Percentile(times, .95):F3}; checksum={checksum}");
        }
    }

    private static double Percentile(double[] sorted, double quantile) =>
        sorted[Math.Clamp((int)Math.Ceiling(quantile * sorted.Length) - 1, 0, sorted.Length - 1)];

    private static int Call(nint handle, Box box, int* output, int capacity, out int required) =>
        Native.QueryShapeSpatialIndex(handle, box.MinX, box.MinY, box.MaxX, box.MaxY, output, capacity, out required);

    private static int[] Query(nint handle, Box box, int capacity)
    {
        var results = new int[capacity];
        fixed (int* pointer = results)
        {
            int copied = Call(handle, box, pointer, capacity, out int required);
            if (copied != required) throw new InvalidOperationException("Reference query output too small.");
            return results.AsSpan(0, copied).ToArray();
        }
    }

    private static nint Create(double[] bounds, byte[] kinds)
    {
        var offsets = new long[kinds.Length];
        fixed (long* offsetPointer = offsets)
        fixed (double* boundsPointer = bounds)
        fixed (byte* kindsPointer = kinds)
        {
            nint handle = Native.CreateShapeSpatialIndex(null, 0, offsetPointer, boundsPointer, kindsPointer,
                kinds.Length, Math.Max(1, Math.Min(Environment.ProcessorCount, 8)));
            if (handle == 0) throw new InvalidOperationException("Index creation failed.");
            return handle;
        }
    }

    private static int[] BruteForce(double[] bounds, byte[] kinds, Box query)
    {
        var matches = new List<int>();
        if (query.MinX > query.MaxX || query.MinY > query.MaxY) return matches.ToArray();
        for (int feature = 0; feature < kinds.Length; ++feature)
        {
            int i = feature * 4;
            double minX = bounds[i], minY = bounds[i + 1], maxX = bounds[i + 2], maxY = bounds[i + 3];
            if (kinds[feature] != 0 && double.IsFinite(minX) && double.IsFinite(minY) &&
                double.IsFinite(maxX) && double.IsFinite(maxY) && minX <= maxX && minY <= maxY &&
                minX <= query.MaxX && maxX >= query.MinX && minY <= query.MaxY && maxY >= query.MinY)
                matches.Add(feature);
        }
        return matches.ToArray();
    }

    private static (double[] Bounds, byte[] Kinds) MakeData(int count, string distribution, bool includeInvalid)
    {
        var bounds = new double[checked(count * 4)];
        var kinds = new byte[count];
        var random = new Random(1729);
        int columns = (int)Math.Ceiling(Math.Sqrt(count));
        double step = 1000.0 / columns;
        for (int feature = 0; feature < count; ++feature)
        {
            double x = (feature % columns) * step, y = (feature / columns) * step;
            double width = step, height = step;
            if (distribution == "clustered")
            {
                double center = feature % 2 == 0 ? 100 : 750;
                x = center + random.NextDouble() * 100;
                y = center + random.NextDouble() * 100;
                width = random.NextDouble() * 4;
                height = random.NextDouble() * 4;
            }
            if (distribution == "oversized" && feature % 8 == 0)
            {
                x = random.NextDouble() * 400;
                y = random.NextDouble() * 400;
                width = 300 + random.NextDouble() * 300;
                height = 300 + random.NextDouble() * 300;
            }
            if (distribution == "degenerate") { x = 500; width = 0; height = 0; }
            int i = feature * 4;
            bounds[i] = x; bounds[i + 1] = y;
            bounds[i + 2] = x + width; bounds[i + 3] = y + height;
            kinds[feature] = feature % 11 == 0 ? (byte)1 : (byte)3;
        }
        if (includeInvalid)
        {
            bounds[(count - 1) * 4] = double.NaN;
            bounds[(count - 2) * 4] = double.PositiveInfinity;
            bounds[(count - 3) * 4 + 2] = -100;
            kinds[count - 4] = 0;
        }
        return (bounds, kinds);
    }
}
