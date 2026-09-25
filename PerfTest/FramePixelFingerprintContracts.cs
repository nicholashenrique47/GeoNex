using System.Diagnostics;
using GeoNex.Services;
using SkiaSharp;

internal static class FramePixelFingerprintContracts
{
    public static void Run()
    {
        var info = new SKImageInfo(1025, 2047, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var dense = new SKBitmap(info);
        using var padded = new SKBitmap();
        if (!padded.TryAllocPixels(info, info.RowBytes + 32)) throw new OutOfMemoryException();
        new Random(17).NextBytes(dense.GetPixelSpan());
        padded.GetPixelSpan().Fill(171);
        for (int y = 0; y < info.Height; y++)
            dense.GetPixelSpan().Slice(y * dense.RowBytes, info.RowBytes)
                .CopyTo(padded.GetPixelSpan().Slice(y * padded.RowBytes, info.RowBytes));
        using var a = dense.PeekPixels(); using var b = padded.PeekPixels();
        string reference = EncodedFrameCache.PixelDigest(a, 1, default);
        foreach (int workers in new[] { 0, 1, 2, 4, int.MaxValue })
        {
            Require(EncodedFrameCache.PixelDigest(a, workers, default) == reference, "Worker count changed fingerprint");
            Require(EncodedFrameCache.PixelDigest(b, workers, default) == reference, "Row padding changed fingerprint");
        }
        // Mutate every channel at both ends of each row group, including the tail.
        int rowsPerChunk = 1024 * 1024 / info.RowBytes;
        for (int first = 0; first < info.Height; first += rowsPerChunk)
        foreach (int y in new[] { first, Math.Min(first + rowsPerChunk, info.Height) - 1 })
        foreach (int channel in Enumerable.Range(0, 4))
        {
            int position = y * dense.RowBytes + (y == first ? 0 : info.RowBytes - 4) + channel;
            dense.GetPixelSpan()[position] ^= 1;
            Require(EncodedFrameCache.PixelDigest(a, 4, default) != reference, "A pixel was omitted from fingerprint");
            dense.GetPixelSpan()[position] ^= 1;
        }
        Require(EncodedFrameCache.PixelDigest(a, 2, default) == reference, "Mutation leaked into original frame");
        int groupBytes = rowsPerChunk * dense.RowBytes;
        byte[] firstGroup = dense.GetPixelSpan()[..groupBytes].ToArray();
        dense.GetPixelSpan().Slice(groupBytes, groupBytes).CopyTo(dense.GetPixelSpan()[..groupBytes]);
        firstGroup.CopyTo(dense.GetPixelSpan().Slice(groupBytes, groupBytes));
        Require(EncodedFrameCache.PixelDigest(a, 4, default) != reference, "Group order was omitted from fingerprint");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        foreach (int workers in new[] { 1, 2, 4 })
        {
            try { EncodedFrameCache.PixelDigest(a, workers, canceled.Token); throw new InvalidOperationException("Cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        using var small = new SKBitmap(31, 17, SKColorType.Rgba8888, SKAlphaType.Premul);
        small.Erase(SKColors.Cyan);
        using var smallPixels = small.PeekPixels();
        Require(EncodedFrameCache.PixelDigest(smallPixels, 4, default) ==
            EncodedFrameCache.PixelDigest(smallPixels, 1, default, false), "Small-frame fallback changed");
        foreach (int height in new[] { 1023, 1024, 1025 })
        {
            using var boundary = new SKBitmap(2048, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            boundary.Erase(SKColors.Magenta);
            using var pixels = boundary.PeekPixels();
            Require(EncodedFrameCache.PixelDigest(pixels, 1, default) == EncodedFrameCache.PixelDigest(pixels, 4, default),
                "Threshold changed digest across worker counts");
            if (height < 1024)
                Require(EncodedFrameCache.PixelDigest(pixels, 4, default) == EncodedFrameCache.PixelDigest(pixels, 1, default, false),
                    "Below-threshold frame entered grouped hashing");
        }
        Console.WriteLine("Frame pixel fingerprint: PASS (every group/channel boundary and tail, worker-independent digest, padded rows, small frames, cancellation)");
    }

    public static void Measure(string file)
    {
        using var bitmap = SKBitmap.Decode(file) ?? throw new InvalidDataException("Invalid frame");
        using var pixels = bitmap.PeekPixels();
        var samples = Enumerable.Range(0, 4).Select(_ => new List<double>()).ToArray();
        int[] workers = { 1, 1, 2, 4 };
        string? groupedReference = null;
        for (int round = 0; round < 9; round++)
        foreach (int mode in round % 2 == 0 ? new[] { 0, 1, 2, 3 } : new[] { 3, 2, 1, 0 })
        {
            long start = Stopwatch.GetTimestamp();
            string digest = EncodedFrameCache.PixelDigest(pixels, workers[mode], default, mode != 0);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (mode != 0)
            {
                groupedReference ??= digest;
                Require(groupedReference == digest, "Parallel execution changed digest");
            }
            if (round > 0) samples[mode].Add(ms);
        }
        for (int mode = 0; mode < samples.Length; mode++)
        {
            var values = samples[mode].Order().ToArray();
            Console.WriteLine(FormattableString.Invariant($"HASH grouped={mode != 0} workers={workers[mode]} size={bitmap.Width}x{bitmap.Height} median_ms={(values[3] + values[4]) / 2:F3}"));
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
