using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace GeoNex.Services;

// Indexed PNG with exact RGBA entries, never color quantization.
internal static class ExactPalettePngEncoder
{
    private const int MaximumColors = 256;
    private const int ProbeRows = 16;
    private const int MinimumRunLength = 16;
    private const long MinimumPixels = 2L * 1024 * 1024;
    private const long MaximumPixels = 32L * 1024 * 1024;
    private const long MaximumBudget = 16L * 1024 * 1024;
    private const long EncoderOverhead = 1024 * 1024;
    private static readonly uint[] CrcTable = CreateCrcTable();
    internal static SKData? TryEncode(SKPixmap pixels, long budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long count = (long)pixels.Width * pixels.Height;
        budget = Math.Clamp(budget, 0, MaximumBudget);
        long remaining = budget - EncoderOverhead - pixels.Width - 1;
        if (pixels.Width <= 0 || pixels.Height <= 0 || count < MinimumPixels || count > MaximumPixels ||
            remaining < EncoderOverhead || pixels.GetPixels() == IntPtr.Zero || pixels.ColorSpace != null ||
            pixels.AlphaType != SKAlphaType.Premul ||
            pixels.ColorType is not (SKColorType.Rgba8888 or SKColorType.Bgra8888)) return null;
        // MemoryStream capacity is at most twice its bounded payload; SKData
        // receives one exact-size copy. Reserve native codec/scratch overhead.
        try { return Encode(pixels, (int)(remaining / 3), token); }
        catch (PaletteBudgetException) { return null; }
        catch (OutOfMemoryException) { return null; }
    }

    private static unsafe SKData? Encode(SKPixmap pixels, int maximumCompressedBytes, CancellationToken token)
    {
        var indexes = new Dictionary<uint, byte>(MaximumColors);
        bool Index(uint color, out byte index)
        {
            if (indexes.TryGetValue(color, out index)) return true;
            if (indexes.Count == MaximumColors) return false;
            index = (byte)indexes.Count; indexes.Add(color, index); return true;
        }
        // Cheap rejection of frames with many colors before encoding scanlines.
        long probeRuns = 0;
        nint address = pixels.GetPixels();
        for (int sample = 0; sample < ProbeRows; sample++)
        {
            token.ThrowIfCancellationRequested();
            int y = (int)((long)(sample * 2 + 1) * pixels.Height / (ProbeRows * 2));
            var row = new ReadOnlySpan<uint>((byte*)address + (long)y * pixels.RowBytes, pixels.Width);
            for (int x = 0; x < row.Length;)
            {
                uint color = row[x];
                if (!Index(color, out _)) return null;
                if (++probeRuns > (long)pixels.Width * ProbeRows / MinimumRunLength) return null;
                int run = row[x..].IndexOfAnyExcept(color);
                x += run < 0 ? row.Length - x : run;
            }
        }
        // Validate the entire palette before spending CPU on compression.
        for (int y = 0; y < pixels.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            var row = new ReadOnlySpan<uint>((byte*)address + (long)y * pixels.RowBytes, pixels.Width);
            for (int x = 0; x < row.Length;)
            {
                uint color = row[x];
                if (!Index(color, out _)) return null;
                int run = row[x..].IndexOfAnyExcept(color);
                x += run < 0 ? row.Length - x : run;
            }
        }
        byte[] scanline = new byte[pixels.Width + 1]; // Filter None, followed by exact palette indexes.
        using var compressed = new BoundedMemoryStream(maximumCompressedBytes);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int y = 0; y < pixels.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                var row = new ReadOnlySpan<uint>((byte*)address + (long)y * pixels.RowBytes, pixels.Width);
                for (int x = 0; x < row.Length;)
                {
                    uint color = row[x];
                    byte index = indexes[color]; // Every color was checked before compression.
                    int run = row[x..].IndexOfAnyExcept(color);
                    if (run < 0) run = row.Length - x;
                    scanline.AsSpan(x + 1, run).Fill(index); x += run;
                }
                zlib.Write(scanline);
            }
        }
        token.ThrowIfCancellationRequested();

        // Ask the same native encoder to unpremultiply only the unique colors.
        // This avoids duplicating Skia's alpha rounding in a custom conversion.
        using var paletteSource = new SKBitmap();
        if (!paletteSource.TryAllocPixels(new SKImageInfo(indexes.Count, 1, pixels.ColorType, pixels.AlphaType))) return null;
        var entries = MemoryMarshal.Cast<byte, uint>(paletteSource.GetPixelSpan());
        foreach (var entry in indexes) entries[entry.Value] = entry.Key;
        using var palettePixels = paletteSource.PeekPixels();
        using var palettePng = palettePixels.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.None, 1));
        if (palettePng == null) return null;
        using var codec = SKCodec.Create(palettePng);
        if (codec == null) return null;
        using var straight = new SKBitmap();
        if (!straight.TryAllocPixels(new SKImageInfo(indexes.Count, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul))) return null;
        if (codec.GetPixels(straight.Info, straight.GetPixels()) != SKCodecResult.Success)
            return null;
        byte[] rgb = new byte[indexes.Count * 3], alpha = new byte[indexes.Count];
        var rgba = straight.GetPixelSpan();
        for (int i = 0; i < indexes.Count; i++)
        { rgba.Slice(i * 4, 3).CopyTo(rgb.AsSpan(i * 3)); alpha[i] = rgba[i * 4 + 3]; }
        int length = checked(8 + 25 + 12 + rgb.Length + 12 + alpha.Length + 12 + (int)compressed.Length + 12);
        var result = SKData.Create(length) ?? throw new OutOfMemoryException();
        try
        {
            var output = new Span<byte>((void*)result.Data, length);
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10]; signature.CopyTo(output);
            int position = signature.Length;
            Span<byte> header = stackalloc byte[13]; header.Clear();
            BinaryPrimitives.WriteInt32BigEndian(header, pixels.Width);
            BinaryPrimitives.WriteInt32BigEndian(header[4..], pixels.Height); header[8] = 8; header[9] = 3;
            position += Chunk(output[position..], "IHDR"u8, header);
            position += Chunk(output[position..], "PLTE"u8, rgb);
            position += Chunk(output[position..], "tRNS"u8, alpha);
            position += Chunk(output[position..], "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
            Chunk(output[position..], "IEND"u8, ReadOnlySpan<byte>.Empty);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static int Chunk(Span<byte> output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        BinaryPrimitives.WriteInt32BigEndian(output, data.Length);
        type.CopyTo(output[4..]); data.CopyTo(output[8..]);
        uint crc = uint.MaxValue;
        foreach (byte b in type) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(output[(8 + data.Length)..], ~crc);
        return data.Length + 12;
    }

    private sealed class PaletteBudgetException : Exception;
    private sealed class BoundedMemoryStream(int maximumBytes) : MemoryStream
    {
        private void Check(int count)
        { if (Position + count > maximumBytes) throw new PaletteBudgetException(); }
        public override void Write(byte[] buffer, int offset, int count)
        { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value)
        { Check(1); base.WriteByte(value); }
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            table[i] = crc;
        }
        return table;
    }
}
