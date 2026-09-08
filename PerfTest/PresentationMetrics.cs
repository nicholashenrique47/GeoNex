using System.Diagnostics;
using System.Globalization;
using SkiaSharp;

internal static class PresentationMetrics
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Warmup = 2;
    private const int Samples = 10;

    public static void Run()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.WriteLine($"Presentation samples={Samples} warmup={Warmup} size={Width}x{Height} seed=20260905");
        Console.WriteLine("scenario,format,encode_p50_ms,encode_p95_ms,decode_p50_ms,total_p50_ms,bytes");
        using SKBitmap vector = CreateVectorLike();
        using SKBitmap continuous = CreateContinuousRasterLike();
        Measure("vector_alpha", vector);
        Measure("continuous_raster", continuous);
    }

    private static void Measure(string scenario, SKBitmap bitmap)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        foreach ((string name, SKEncodedImageFormat format, int quality) in new[]
        {
            ("png100", SKEncodedImageFormat.Png, 100),
            ("webp70", SKEncodedImageFormat.Webp, 70),
            ("webp95", SKEncodedImageFormat.Webp, 95)
        })
        {
            var encode = new List<double>(Samples);
            var decode = new List<double>(Samples);
            var total = new List<double>(Samples);
            long payloadBytes = 0;
            for (int pass = -Warmup; pass < Samples; pass++)
            {
                long totalStart = Stopwatch.GetTimestamp();
                long encodeStart = Stopwatch.GetTimestamp();
                using SKData? data = image.Encode(format, quality);
                double encodeMs = Stopwatch.GetElapsedTime(encodeStart).TotalMilliseconds;
                if (data == null) throw new InvalidOperationException($"Encoding failed for {name}.");

                using var stream = new MemoryStream(checked((int)data.Size));
                data.SaveTo(stream);
                stream.Position = 0;
                long decodeStart = Stopwatch.GetTimestamp();
                using SKBitmap? decoded = SKBitmap.Decode(stream);
                double decodeMs = Stopwatch.GetElapsedTime(decodeStart).TotalMilliseconds;
                if (decoded == null) throw new InvalidOperationException($"Decoding failed for {name}.");
                double totalMs = Stopwatch.GetElapsedTime(totalStart).TotalMilliseconds;

                if (pass >= 0)
                {
                    encode.Add(encodeMs);
                    decode.Add(decodeMs);
                    total.Add(totalMs);
                    payloadBytes = data.Size;
                }
            }

            encode.Sort();
            decode.Sort();
            total.Sort();
            Console.WriteLine($"{scenario},{name},{Percentile(encode, .50):F3},{Percentile(encode, .95):F3},{Percentile(decode, .50):F3},{Percentile(total, .50):F3},{payloadBytes}");
        }
    }

    private static double Percentile(List<double> sorted, double percentile)
    {
        int index = Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static SKBitmap CreateVectorLike()
    {
        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var fill = new SKPaint { Color = new SKColor(40, 140, 220, 90), IsAntialias = true };
        using var stroke = new SKPaint { Color = SKColors.DarkBlue, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        for (int y = 0; y < Height; y += 36)
        for (int x = 0; x < Width; x += 36)
        {
            var rect = new SKRect(x + 2, y + 2, x + 33, y + 33);
            canvas.DrawRect(rect, fill);
            canvas.DrawRect(rect, stroke);
        }
        return bitmap;
    }

    private static unsafe SKBitmap CreateContinuousRasterLike()
    {
        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        byte* basePointer = (byte*)bitmap.GetPixels();
        for (int y = 0; y < Height; y++)
        {
            uint* row = (uint*)(basePointer + y * bitmap.RowBytes);
            for (int x = 0; x < Width; x++)
            {
                uint hash = unchecked((uint)(x * 73856093) ^ (uint)(y * 19349663) ^ 20260905u);
                byte noise = (byte)((hash ^ (hash >> 13) ^ (hash >> 21)) & 31);
                byte red = (byte)Math.Min(255, x * 220 / Width + noise);
                byte green = (byte)Math.Min(255, y * 220 / Height + noise);
                byte blue = (byte)Math.Min(255, (x + y) * 100 / (Width + Height) + noise);
                row[x] = (uint)(red | (green << 8) | (blue << 16) | (255u << 24));
            }
        }
        return bitmap;
    }
}
