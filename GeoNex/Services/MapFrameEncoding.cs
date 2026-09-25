using SkiaSharp;
using System.IO.Compression;

namespace GeoNex.Services;

public static class MapFrameEncoding
{
    internal static bool PaletteEnabled => Environment.GetEnvironmentVariable("GEONEX_PALETTE_PNG") != "0";

    // Only navigation opts into exact indexed PNG. Print/export keeps its encoder.
    internal static SKData EncodeNavigationPng(SKImage image, int compressionLevel, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (compressionLevel == 1 && PaletteEnabled)
        {
            using var pixels = image.PeekPixels();
            if (pixels != null)
            {
                var indexed = ExactPalettePngEncoder.TryEncode(pixels, VectorRuntimeResources.Current.CacheBytes / 16, token);
                if (indexed != null) return indexed;
            }
        }
        token.ThrowIfCancellationRequested();
        return EncodePng(image, compressionLevel);
    }

    // Localhost frames favor latency over minimum file size. Still lossless RGBA.
    public static SKData EncodePng(SKImage image)
        => EncodePng(image, 1);

    public static SKData EncodePng(SKImage image, int compressionLevel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(compressionLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(compressionLevel, 1);
        using SKPixmap? pixels = image.PeekPixels();
        return (pixels?.Encode(new SKPngEncoderOptions(SelectFilter(pixels, compressionLevel), compressionLevel))
            ?? image.Encode(SKEncodedImageFormat.Png, 100))
            ?? throw new InvalidOperationException("Não foi possível codificar o frame PNG.");
    }

    internal static unsafe SKPngEncoderFilterFlags SelectFilter(SKPixmap pixels, int compressionLevel)
    {
        // Compare the same small, scattered scanline windows with the real
        // encoder. Flat parcel colors can compress better without Sub, whereas
        // gradients often benefit from it. Require a material sample advantage;
        // this selects lossless filtering only, never modifies source pixels.
        const int width = 256, rows = 16, rowBytes = width * 4;
        if (compressionLevel != 1 || (long)pixels.Width * pixels.Height < 1024 * 1024 ||
            pixels.Width < width || pixels.ColorSpace != null || pixels.GetPixels() == IntPtr.Zero ||
            pixels.ColorType is not (SKColorType.Rgba8888 or SKColorType.Bgra8888))
            return SKPngEncoderFilterFlags.Sub;
        Span<byte> sample = stackalloc byte[rows * rowBytes];
        for (int row = 0; row < rows; row++)
        {
            int y = (int)((long)(2 * row + 1) * pixels.Height / (2 * rows));
            int x = (int)((long)(pixels.Width - width) * (row % 4) / 3);
            new ReadOnlySpan<byte>((byte*)pixels.GetPixels() + (long)y * pixels.RowBytes + (long)x * 4, rowBytes)
                .CopyTo(sample.Slice(row * rowBytes, rowBytes));
        }
        fixed (byte* pointer = sample)
        {
            using var probe = new SKPixmap(new SKImageInfo(width, rows, pixels.ColorType, pixels.AlphaType),
                (IntPtr)pointer, rowBytes);
            using var none = probe.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.None, 1));
            using var sub = probe.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub, 1));
            return none != null && sub != null && none.Size * 8 < sub.Size * 7
                ? SKPngEncoderFilterFlags.None : SKPngEncoderFilterFlags.Sub;
        }
    }

    // Only the local navigation endpoint opts in. A stored-deflate PNG trades
    // transfer bytes for latency, never geometry, resolution, RGB or alpha.
    // Bound transient server/browser buffers before sampling image complexity.
    public static unsafe int NavigationCompressionLevel(SKImage image, int availableMemoryMb)
    {
        long pixelCount = (long)image.Width * image.Height;
        long byteBudget = Math.Min(32L * 1024 * 1024, Math.Max(0L, availableMemoryMb) * 1024 * 1024 / 32);
        if (pixelCount < 512 * 512 || pixelCount * 4 + image.Height + 65536 > byteBudget) return 1;
        using var pixels = image.PeekPixels();
        if (pixels == null || pixels.GetPixels() == IntPtr.Zero ||
            (pixels.ColorType != SKColorType.Rgba8888 && pixels.ColorType != SKColorType.Bgra8888)) return 1;
        int changed = 0, samples = 0;
        int stepX = Math.Max(1, image.Width / 512);
        int stepY = Math.Max(1, image.Height / 32);
        for (int y = stepY / 2; y < image.Height; y += stepY)
        {
            uint* row = (uint*)((byte*)pixels.GetPixels() + (long)y * pixels.RowBytes);
            for (int x = 1; x < image.Width; x += stepX)
            {
                if (row[x] != row[x - 1]) changed++;
                samples++;
            }
        }
        if (changed * 5 <= samples) return 1;
        // Experimental: LOTES saves transfer bytes but currently costs more CPU
        // latency. Keep the measured navigation fast path as the default.
        if (Environment.GetEnvironmentVariable("GEONEX_PNG_COMPRESSION_PROBE") != "1") return 0;

        // Adjacent colors can vary everywhere while gradients, parcel fills and
        // repeated rows still compress well. Probe bounded Sub-filtered windows
        // instead of sending a raw-size PNG solely because many edges exist.
        const int sampleRows = 16, sampleWidth = 256, bytesPerPixel = 4;
        const int sampleBytes = sampleRows * sampleWidth * bytesPerPixel;
        if (image.Width < sampleWidth) return 1;
        Span<byte> probe = stackalloc byte[sampleBytes];
        for (int rowIndex = 0; rowIndex < sampleRows; rowIndex++)
        {
            int y = (int)((long)(2 * rowIndex + 1) * image.Height / (2 * sampleRows));
            int startX = (int)((long)(image.Width - sampleWidth) * (rowIndex % 4) / 3);
            byte* row = (byte*)pixels.GetPixels() + (long)y * pixels.RowBytes;
            for (int x = 0; x < sampleWidth; x++)
            for (int channel = 0; channel < bytesPerPixel; channel++)
            {
                int source = (startX + x) * bytesPerPixel + channel;
                byte left = startX + x == 0 ? (byte)0 : row[source - bytesPerPixel];
                probe[(rowIndex * sampleWidth + x) * bytesPerPixel + channel] = unchecked((byte)(row[source] - left));
            }
        }
        using var compressed = new MemoryStream(sampleBytes + 1024);
        using (var encoder = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            encoder.Write(probe);
        return compressed.Length * 4 <= sampleBytes * 3 ? 1 : 0;
    }
}
