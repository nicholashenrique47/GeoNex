using SkiaSharp;

// Cross-renderer evidence only: different symbol composition is not an
// acceptable reference for the separate 1/255 GeoNex regression contract.
internal static class RendererFrameComparison
{
    public static void Run(string first, string second)
    {
        using var a = SKBitmap.Decode(first) ?? throw new InvalidDataException(first);
        using var b = SKBitmap.Decode(second) ?? throw new InvalidDataException(second);
        if (a.Width != b.Width || a.Height != b.Height) throw new InvalidDataException("Frame dimensions differ");
        using var aa = a.Copy(SKColorType.Rgba8888);
        using var bb = b.Copy(SKColorType.Rgba8888);
        var x = aa.GetPixelSpan(); var y = bb.GetPixelSpan();
        int maximum = 0, changed = 0, overOne = 0, alphaMaximum = 0;
        long squared = 0;
        for (int i = 0; i < x.Length; i += 4)
        {
            int pixelMaximum = 0;
            for (int c = 0; c < 4; c++)
            {
                int difference = Math.Abs(x[i + c] - y[i + c]);
                pixelMaximum = Math.Max(pixelMaximum, difference);
                squared += difference * difference;
                if (c == 3) alphaMaximum = Math.Max(alphaMaximum, difference);
            }
            maximum = Math.Max(maximum, pixelMaximum);
            if (pixelMaximum > 0) changed++;
            if (pixelMaximum > 1) overOne++;
        }
        Console.WriteLine(FormattableString.Invariant($"RENDERERS pixels={a.Width * a.Height} changed_pixels={changed} over_one_pixels={overOne} max_delta={maximum} alpha_max_delta={alphaMaximum} rgba_rms={Math.Sqrt(squared / (double)x.Length):F5}"));
    }
}
