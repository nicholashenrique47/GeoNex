using SkiaSharp;
using System.IO.Compression;

namespace GeoNex.Services;

public static class MapFrameEncoding
{
    // Localhost frames favor latency over minimum file size. Still lossless RGBA.
    public static SKData EncodePng(SKImage image)
        => EncodePng(image, 1);

    public static SKData EncodePng(SKImage image, int compressionLevel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(compressionLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(compressionLevel, 1);
        using SKPixmap? pixels = image.PeekPixels();
        return (pixels?.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub, compressionLevel))
            ?? image.Encode(SKEncodedImageFormat.Png, 100))
            ?? throw new InvalidOperationException("Não foi possível codificar o frame PNG.");
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
