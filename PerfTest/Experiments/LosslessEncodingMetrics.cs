using System.Diagnostics;
using System.Runtime.InteropServices;
using GeoNex.Services;
using SkiaSharp;

internal static class LosslessEncodingMetrics
{
    public static void Run(string file)
    {
        using var bitmap = SKBitmap.Decode(file) ?? throw new InvalidDataException("Invalid frame");
        using var pixels = new SKPixmap(new SKImageInfo(bitmap.Width, bitmap.Height, bitmap.ColorType, bitmap.AlphaType),
            bitmap.GetPixels(), bitmap.RowBytes);
        using var image = SKImage.FromPixels(pixels);
        var colors = new HashSet<uint>();
        uint? previous = null;
        foreach (uint color in MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan()))
        {
            if (color != previous) colors.Add(color);
            previous = color;
        }
        Console.WriteLine($"LOSSLESS size={bitmap.Width}x{bitmap.Height} premultiplied_colors={colors.Count}");
        string[] modes = { "png", "palette" };
        var samples = modes.ToDictionary(m => m, _ => new List<(double Encode, double Total, long Size, int Delta)>());
        byte[] expected = bitmap.Bytes;
        for (int round = 0; round < 5; round++)
        foreach (string mode in round % 2 == 0 ? modes : modes.Reverse())
        {
            long start = Stopwatch.GetTimestamp();
            using var encoded = mode == "palette" ? ExactPalettePngEncoder.TryEncode(pixels, 16L * 1024 * 1024, default) : MapFrameEncoding.EncodePng(image, 1);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (encoded == null)
            {
                if (round > 0) samples[mode].Add((ms, ms, 0, 0));
                continue;
            }
            using var decoded = SKBitmap.Decode(encoded) ?? throw new InvalidDataException("Invalid encoded image");
            double total = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            using var comparable = decoded.Copy(bitmap.ColorType);
            int delta = 0;
            var actual = comparable.GetPixelSpan();
            for (int i = 0; i < expected.Length; i++) delta = Math.Max(delta, Math.Abs(expected[i] - actual[i]));
            if (round > 0) samples[mode].Add((ms, total, encoded.Size, delta));
        }
        foreach (string mode in modes)
        {
            var s = samples[mode];
            double Median(Func<(double Encode, double Total, long Size, int Delta), double> select)
            { var v = s.Select(select).Order().ToArray(); return (v[1] + v[2]) / 2; }
            Console.WriteLine(FormattableString.Invariant($"LOSSLESS mode={mode} encode_ms={Median(x => x.Encode):F3} encode_decode_ms={Median(x => x.Total):F3} bytes={Median(x => x.Size):F0} max_delta={s.Max(x => x.Delta)}"));
        }
    }
}
