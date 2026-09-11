using SkiaSharp;

namespace GeoNex.Services;

public static class MapFrameEncoding
{
    // Localhost frames favor latency over minimum file size. Still lossless RGBA.
    public static SKData EncodePng(SKImage image)
    {
        using SKPixmap? pixels = image.PeekPixels();
        return (pixels?.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub, 1))
            ?? image.Encode(SKEncodedImageFormat.Png, 100))
            ?? throw new InvalidOperationException("Não foi possível codificar o frame PNG.");
    }
}
