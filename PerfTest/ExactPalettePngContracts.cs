using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using GeoNex.Services;
using SkiaSharp;

internal static class ExactPalettePngContracts
{
    private const long Budget = 16L * 1024 * 1024;
    public static void Run()
    {
        foreach (var format in new[] { SKColorType.Rgba8888, SKColorType.Bgra8888 })
        foreach (bool padded in new[] { false, true })
        foreach (int colors in new[] { 1, 2, 255, 256 })
        {
            using var bitmap = new SKBitmap();
            var info = new SKImageInfo(2048, 1025, format, SKAlphaType.Premul);
            Require(bitmap.TryAllocPixels(info, info.RowBytes + (padded ? 32 : 0)), "Allocation failed");
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
            {
                canvas.Clear(SKColors.Transparent);
                for (int i = 0; i < colors; i++)
                {
                    paint.Color = new SKColor((byte)(i * 37), (byte)(i * 73), (byte)(i * 13), (byte)i);
                    int top = i * info.Height / colors, bottom = (i + 1) * info.Height / colors;
                    canvas.DrawRect(0, top, info.Width, bottom - top, paint);
                }
            }
            using var pixels = bitmap.PeekPixels();
            using var encoded = ExactPalettePngEncoder.TryEncode(pixels, Budget, default)
                ?? throw new InvalidOperationException("Exact palette was rejected");
            ValidatePng(encoded.ToArray(), info.Width, info.Height);
            using var native = pixels.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub, 1));
            using var a = SKBitmap.Decode(native); using var b = SKBitmap.Decode(encoded);
            Require(a.Bytes.SequenceEqual(b.Bytes), $"Palette changed decoded RGBA: {format}, padded={padded}, colors={colors}");
            foreach (long lowBudget in new[] { -1L, 0, 1, 2L * 1024 * 1024 })
                using (var ignored = ExactPalettePngEncoder.TryEncode(pixels, lowBudget, default))
                    Require(ignored == null, "Low-memory frame attempted palette encoding");
        }
        using var excess = new SKBitmap(2048, 1025, SKColorType.Rgba8888, SKAlphaType.Premul);
        excess.Erase(SKColors.White);
        for (int x = 0; x < 257; x++) excess.SetPixel(x, excess.Height - 1, new SKColor((byte)x, (byte)(x >> 8), 17));
        using var excessPixels = excess.PeekPixels();
        using (var rejected = ExactPalettePngEncoder.TryEncode(excessPixels, Budget, default))
            Require(rejected == null, "Unsampled 257th color was quantized or omitted");
        using var image = SKImage.FromBitmap(excess);
        using var fallback = MapFrameEncoding.EncodeNavigationPng(image, 1, default);
        using var original = MapFrameEncoding.EncodePng(image, 1);
        Require(fallback.ToArray().SequenceEqual(original.ToArray()), "Too many colors changed fallback encoder");
        var checker = MemoryMarshal.Cast<byte, uint>(excess.GetPixelSpan());
        for (int i = 0; i < checker.Length; i++) checker[i] = (i & 1) == 0 ? 0xff000000u : 0xffffffffu;
        using (var dense = ExactPalettePngEncoder.TryEncode(excessPixels, Budget, default))
            Require(dense == null, "Dense alternating pixels bypassed CPU guard");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { using var ignored = ExactPalettePngEncoder.TryEncode(excessPixels, Budget, canceled.Token); throw new InvalidOperationException("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        foreach (var alpha in new[] { SKAlphaType.Opaque, SKAlphaType.Unpremul })
        {
            using var unsupported = new SKBitmap(2048, 1025, SKColorType.Rgba8888, alpha);
            using var pixels = unsupported.PeekPixels();
            using var ignored = ExactPalettePngEncoder.TryEncode(pixels, Budget, default);
            Require(ignored == null, "Unsupported alpha entered palette encoder");
        }
        using (var space = SKColorSpace.CreateSrgb())
        using (var managed = new SKBitmap(new SKImageInfo(2048, 1025, SKColorType.Rgba8888, SKAlphaType.Premul, space)))
        using (var pixels = managed.PeekPixels())
        using (var ignored = ExactPalettePngEncoder.TryEncode(pixels, Budget, default))
            Require(ignored == null, "Color profile was discarded");
        using (var small = new SKBitmap(31, 17, SKColorType.Rgba8888, SKAlphaType.Premul))
        using (var pixels = small.PeekPixels())
        using (var ignored = ExactPalettePngEncoder.TryEncode(pixels, Budget, default))
            Require(ignored == null, "Small frame entered palette encoder");
        CheckOutputBudget();
        Console.WriteLine("Exact palette PNG: PASS (16 exact frames, 1/2/255/256 colors, RGBA/BGRA/padding/alpha 0..255, independent CRC/zlib/index validation, late 257th color, CPU/RAM guards, fallback, cancellation)");
    }

    private static void CheckOutputBudget()
    {
        using var bitmap = new SKBitmap(2048, 4096, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan());
        var random = new Random(32);
        for (int i = 0; i < pixels.Length; i += 16)
            pixels.Slice(i, 16).Fill(0xff000000u | (uint)random.Next(256));
        using var pixmap = bitmap.PeekPixels();
        using var limited = ExactPalettePngEncoder.TryEncode(pixmap, 2L * 1024 * 1024 + bitmap.Width + 1, default);
        Require(limited == null, "Compressed output exceeded budget");
        using var ample = ExactPalettePngEncoder.TryEncode(pixmap, Budget, default);
        Require(ample != null, "Budget fixture was rejected for another reason");
    }

    private static void ValidatePng(byte[] bytes, int width, int height)
    {
        Require(bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "PNG signature");
        int offset = 8, colors = 0; var chunks = new List<string>();
        byte[]? compressed = null;
        while (offset < bytes.Length)
        {
            int count = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset));
            Require(count >= 0 && count <= bytes.Length - offset - 12, "PNG chunk bounds");
            var typeAndData = bytes.AsSpan(offset + 4, count + 4);
            uint crc = uint.MaxValue;
            foreach (byte value in typeAndData)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320u;
            }
            Require(~crc == BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + count)), "PNG CRC");
            string type = System.Text.Encoding.ASCII.GetString(typeAndData[..4]); chunks.Add(type);
            var data = typeAndData[4..];
            if (type == "IHDR") Require(count == 13 && BinaryPrimitives.ReadInt32BigEndian(data) == width &&
                BinaryPrimitives.ReadInt32BigEndian(data[4..]) == height && data[8] == 8 && data[9] == 3 &&
                data[10..].IndexOfAnyExcept((byte)0) < 0, "Indexed IHDR");
            if (type == "PLTE") { colors = count / 3; Require(count % 3 == 0 && colors is >= 1 and <= 256, "PLTE entries"); }
            if (type == "tRNS") Require(count == colors, "Palette alpha entries");
            if (type == "IDAT") compressed = data.ToArray();
            if (type == "IEND") Require(count == 0, "IEND data");
            offset += count + 12;
        }
        Require(chunks.SequenceEqual(new[] { "IHDR", "PLTE", "tRNS", "IDAT", "IEND" }), "PNG chunk order");
        using var input = new MemoryStream(compressed!);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        byte[] row = new byte[width + 1];
        for (int y = 0; y < height; y++)
        {
            zlib.ReadExactly(row); Require(row[0] == 0, "PNG filter");
            foreach (byte index in row.AsSpan(1)) Require(index < colors, "Undefined palette index");
        }
        Require(zlib.ReadByte() == -1, "Unexpected scanline data");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
