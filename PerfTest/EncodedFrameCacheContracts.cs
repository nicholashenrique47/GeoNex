using GeoNex.Services;
using SkiaSharp;

internal static class EncodedFrameCacheContracts
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    public static void Run()
    {
        using var cache = new EncodedFrameCache();
        using var bitmap = new SKBitmap(513, 257, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(new SKColor(17, 113, 71, 137));
        using var image = SKImage.FromBitmap(bitmap);
        long budget = EncodedFrameCache.Budget(1024);
        using var first = cache.Encode(image, 0, budget, default, out var firstId);
        Check(firstId?.Length == 32, "retained payload has no identity");
        byte[] expected = first.Resource.ToArray();
        using (var equalImage = SKImage.FromBitmap(bitmap))
        using (var hit = cache.Encode(equalImage, 0, budget, default, out var sameId))
            Check(sameId == firstId && cache.Hits == 1 && cache.Encodes == 1 && ReferenceEquals(first.Resource, hit.Resource), "identical pixels did not share immutable encoded data/token");
        bitmap.SetPixel(512, 256, SKColors.Red); // Last pixel, not a sampled position.
        using var changed = SKImage.FromBitmap(bitmap);
        using (var miss = cache.Encode(changed, 0, budget, default, out var changedId))
        using (var direct = MapFrameEncoding.EncodePng(changed, 0))
            Check(changedId != firstId && cache.Encodes == 2 && miss.Resource.ToArray().SequenceEqual(direct.ToArray()), "changed pixels reused stale payload/token");
        Check(first.Resource.ToArray().SequenceEqual(expected), "replacement disposed a live writer");
        using (var level = cache.Encode(changed, 1, budget, default))
            Check(cache.Encodes == 3, "compression mode reused wrong payload");
        using var shape = new SKBitmap(257, 513);
        shape.Erase(SKColors.Blue);
        using var shapeImage = SKImage.FromBitmap(shape);
        using (var size = cache.Encode(shapeImage, 1, budget, default))
            Check(cache.Encodes == 4, "dimensions reused wrong payload");
        using (var lowMemory = cache.Encode(image, 0, 1, default))
            Check(cache.RetainedBytes == 0 && lowMemory.Resource.Size > 0, "oversized result not available as uncached lease");
        using (var disabled = cache.Encode(image, 0, 0, default))
            Check(cache.RetainedBytes == 0, "disabled cache retained bytes");
        Check(EncodedFrameCache.Budget(-1) == 0 && EncodedFrameCache.Budget(255) == 0 &&
            EncodedFrameCache.Budget(int.MaxValue) == 32L * 1024 * 1024, "adaptive budget bounds failed");
        foreach (var alpha in new[] { SKAlphaType.Opaque, SKAlphaType.Premul, SKAlphaType.Unpremul })
        foreach (var color in new[] { SKColorType.Rgba8888, SKColorType.Bgra8888 })
        {
            using var variant = new SKBitmap(513, 257, color, alpha);
            variant.Erase(new SKColor(17, 113, 71, 137));
            using var variantImage = SKImage.FromBitmap(variant);
            using var payload = cache.Encode(variantImage, 0, budget, default);
            using var direct = MapFrameEncoding.EncodePng(variantImage, 0);
            Check(payload.Resource.ToArray().SequenceEqual(direct.ToArray()), "alpha/channel metadata changed encoded bytes");
        }
        using (var colorSpace = SKColorSpace.CreateSrgb())
        using (var managed = new SKBitmap(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace)))
        {
            managed.Erase(SKColors.Green);
            using var managedImage = SKImage.FromBitmap(managed);
            using var payload = cache.Encode(managedImage, 0, budget, default);
            Check(cache.RetainedBytes == 0, "unsupported color metadata was cached");
        }
        // Exercise the partitioned SHA path through actual PNG cache hits/misses.
        using (var large = new SKBitmap(2048, 1025, SKColorType.Rgba8888, SKAlphaType.Premul))
        {
            large.Erase(new SKColor(56, 189, 248, 89));
            using var initial = SKImage.FromBitmap(large);
            using var originalPayload = cache.Encode(initial, 1, budget, default, out var largeId);
            long largeHits = cache.Hits;
            using (var independent = SKImage.FromBitmap(large))
            using (var equal = cache.Encode(independent, 1, budget, default, out var equalId))
                Check(equalId == largeId && cache.Hits == largeHits + 1,
                    "partitioned fingerprint failed to reuse independent identical pixels");
            large.SetPixel(2047, 1024, SKColors.Red);
            using var modified = SKImage.FromBitmap(large);
            using var replacement = cache.Encode(modified, 1, budget, default, out var replacementId);
            using var direct = MapFrameEncoding.EncodeNavigationPng(modified, 1, default);
            Check(replacementId != largeId && replacement.Resource.ToArray().SequenceEqual(direct.ToArray()),
                "partitioned fingerprint reused stale last-pixel payload");
            string? priorPalette = Environment.GetEnvironmentVariable("GEONEX_PALETTE_PNG");
            try
            {
                Environment.SetEnvironmentVariable("GEONEX_PALETTE_PNG", "1");
                using var enabled = cache.Encode(modified, 1, budget, default, out var enabledId);
                Environment.SetEnvironmentVariable("GEONEX_PALETTE_PNG", "0");
                using var disabledPalette = cache.Encode(modified, 1, budget, default, out var disabledId);
                using var native = MapFrameEncoding.EncodePng(modified, 1);
                Check(enabledId != disabledId && disabledPalette.Resource.ToArray().SequenceEqual(native.ToArray()),
                    "Palette rollback reused previous encoding policy");
            }
            finally { Environment.SetEnvironmentVariable("GEONEX_PALETTE_PNG", priorPalette); }
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        long encodes = cache.Encodes;
        try { using var ignored = cache.Encode(image, 0, budget, canceled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        Check(cache.Encodes == encodes, "canceled request encoded pixels");
        // Hold the encoder gate on another thread's behalf to model a final
        // frame inside native PNG encoding, which is not cancellable mid-call.
        object gate = typeof(EncodedFrameCache).GetField("_gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(cache)!;
        Task<ResourceLease<SKData>?> preview;
        bool returned;
        Monitor.Enter(gate);
        try
        {
            preview = Task.Run(() => cache.TryEncodePreview(image, 0, budget, default, out _));
            returned = preview.Wait(TimeSpan.FromSeconds(2));
        }
        finally { Monitor.Exit(gate); }
        using (var fallback = preview.GetAwaiter().GetResult())
            Check(returned && fallback == null, "preview blocked behind an active encoder");
        Check(cache.Encodes == encodes, "busy preview modified encoder cache");
        Parallel.For(0, 12, _ => { using var hit = cache.Encode(image, 0, budget, default); Check(hit.Resource.Size > 0, "concurrent lease invalid"); });
        Check(cache.Encodes == encodes + 1, "concurrent duplicates encoded repeatedly");
        using var retained = cache.Encode(image, 0, budget, default);
        cache.Dispose();
        Check(retained.Resource.ToArray().SequenceEqual(expected), "shutdown disposed active writer");
        try { using var ignored = cache.Encode(image, 0, budget, default); throw new Exception("Disposed cache resurrected"); }
        catch (ObjectDisposedException) { }
        Console.WriteLine("Encoded frame cache: PASS (all-pixel identity, independent images, metadata, lossless bytes, leases, RAM, cancellation, nonblocking preview, concurrency/shutdown)");
    }
}
