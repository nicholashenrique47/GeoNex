using System.Runtime.InteropServices;
using GeoNex.Services;
using SkiaSharp;

internal static class OpaquePngContracts
{
    public static void Run()
    {
        string? prior = Environment.GetEnvironmentVariable("GEONEX_OPAQUE_PNG");
        try
        {
            Environment.SetEnvironmentVariable("GEONEX_OPAQUE_PNG", "1");
            foreach (var color in new[] { SKColorType.Rgba8888, SKColorType.Bgra8888 })
            foreach (var alpha in new[] { SKAlphaType.Premul, SKAlphaType.Unpremul })
            foreach (bool padded in new[] { false, true })
            {
                using var source = new SKBitmap();
                var info = new SKImageInfo(1025, 1024, color, alpha);
                if (!source.TryAllocPixels(info, info.RowBytes + (padded ? 28 : 0))) throw new OutOfMemoryException();
                // Padding deliberately has nonopaque bytes. Ignore padding but
                // check vector tails and the very last actual pixel in each row.
                var raw = source.GetPixelSpan(); raw.Clear();
                var random = new Random(721);
                for (int y = 0; y < source.Height; y++)
                {
                    var row = raw.Slice(y * source.RowBytes, info.RowBytes);
                    random.NextBytes(row);
                    var words = MemoryMarshal.Cast<byte, uint>(row);
                    for (int x = 0; x < words.Length; x++) words[x] |= 0xff000000u;
                }
                using var pixels = source.PeekPixels();
                Require(MapFrameEncoding.IsFullyOpaque(pixels, default), "Opaque frame/padding rejected");
                using var image = SKImage.FromBitmap(source);
                foreach (int level in new[] { 0, 1 })
                {
                    using var encoded = MapFrameEncoding.EncodeNavigationPng(image, level, default);
                    Require(encoded.ToArray()[25] == 2, "Opaque PNG did not use RGB");
                    using var legacy = MapFrameEncoding.EncodePng(image, level);
                    using var a = SKBitmap.Decode(encoded); using var b = SKBitmap.Decode(legacy);
                    Require(a.Bytes.SequenceEqual(b.Bytes), "RGB PNG changed decoded channels");
                }
                foreach (var position in new[] { (0, 0), (17, 500), (1024, 1023) })
                {
                    source.SetPixel(position.Item1, position.Item2, new SKColor(0, 0, 0, 254));
                    Require(!MapFrameEncoding.IsFullyOpaque(pixels, default), "A nonopaque pixel was skipped");
                    using var partial = SKImage.FromBitmap(source);
                    using var encoded = MapFrameEncoding.EncodeNavigationPng(partial, 0, default);
                    using var legacy = MapFrameEncoding.EncodePng(partial, 0);
                    Require(encoded.ToArray().SequenceEqual(legacy.ToArray()), "Transparency fallback changed encoder");
                    source.SetPixel(position.Item1, position.Item2, SKColors.Black);
                }
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                try { MapFrameEncoding.IsFullyOpaque(pixels, canceled.Token); throw new InvalidOperationException("Cancellation ignored"); }
                catch (OperationCanceledException) { }
            }
            CheckBudgetAndRollback();
            using var space = SKColorSpace.CreateSrgb();
            using var managed = new SKBitmap(new SKImageInfo(1024, 1024, SKColorType.Rgba8888, SKAlphaType.Premul, space));
            managed.Erase(SKColors.White);
            using var managedPixels = managed.PeekPixels();
            Require(!MapFrameEncoding.IsFullyOpaque(managedPixels, default), "Color-managed frame entered unprofiled RGB path");
            Console.WriteLine("Opaque PNG: PASS (RGBA/BGRA, premul/unpremul, padded rows, vector tails, first/middle/last alpha, exact RGB decode, budget/retention, rollback, cancellation)");
        }
        finally { Environment.SetEnvironmentVariable("GEONEX_OPAQUE_PNG", prior); }
    }

    private static void CheckBudgetAndRollback()
    {
        using var source = new SKBitmap(3712, 2312, SKColorType.Rgba8888, SKAlphaType.Premul);
        var words = MemoryMarshal.Cast<byte, uint>(source.GetPixelSpan());
        for (int i = 0; i < words.Length; i++) words[i] = 0xff000000u | ((uint)i * 1664525u & 0xffffffu);
        using var image = SKImage.FromBitmap(source);
        Require(MapFrameEncoding.NavigationCompressionLevel(image, 2048) == 0, "RGB frame does not fit unchanged budget");
        Require(MapFrameEncoding.NavigationCompressionLevel(image, 512) == 1, "Memory pressure ignored");
        using var cache = new EncodedFrameCache();
        long budget = EncodedFrameCache.Budget(2048);
        using var first = cache.Encode(image, 0, budget, default, out var firstId);
        Require(firstId != null && first.Resource.Size < budget && cache.RetainedBytes == first.Resource.Size, "RGB payload was not retained within budget");
        using (var hit = cache.Encode(image, 0, budget, default, out var hitId))
            Require(firstId == hitId && cache.Hits == 1, "Opaque identity frame was encoded again");
        Environment.SetEnvironmentVariable("GEONEX_OPAQUE_PNG", "0");
        Require(MapFrameEncoding.NavigationCompressionLevel(image, 2048) == 1, "Rollback changed old memory gate");
        using (var rollback = cache.Encode(image, 0, budget, default))
        using (var native = MapFrameEncoding.EncodePng(image, 0))
            Require(rollback.Resource.ToArray().SequenceEqual(native.ToArray()), "Rollback reused RGB cache payload");
        Environment.SetEnvironmentVariable("GEONEX_OPAQUE_PNG", "1");
        source.SetPixel(3711, 2311, SKColors.Transparent);
        using var partial = SKImage.FromBitmap(source);
        Require(MapFrameEncoding.NavigationCompressionLevel(partial, 2048) == 1, "Partial alpha exceeded RGBA memory budget");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
