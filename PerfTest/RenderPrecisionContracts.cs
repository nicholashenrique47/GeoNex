using GeoNex.Services;
using SkiaSharp;

internal static class RenderPrecisionContracts
{
    public static void Run()
    {
        const double ox = -5400000, oy = -3000000;
        double[] x = { 0, .125, .125, 0, 0 };
        double[] y = { 0, 0, .125, .125, 0 };
        using var expected = new SKPath();
        using var actual = new SKPath();
        using var old = new SKPath();
        foreach (double shift in new[] { 0d, .140625 }) // Two parcels, 1/64 m gap.
        {
            var nearX = x.Select(n => n + shift).ToArray();
            var worldX = nearX.Select(n => n + ox).ToArray();
            var worldY = y.Select(n => n + oy).ToArray();
            TransformedRingWriter.Append(expected, nearX, y, 0, 5, 0, 0, true, 1000, false);
            TransformedRingWriter.Append(actual, worldX, worldY, 0, 5, ox, oy, true, 1000, false);
            TransformedRingWriter.Append(old, worldX, worldY, 0, 5, 0, 0, true, 1000, false);
        }
        Check(expected.Points.SequenceEqual(actual.Points), "Local-origin vertices retain the parcel gap");
        Check(old.Bounds.Height == 0, "Regression fixture reproduces world-float collapse");
        foreach (float dpi in new[] { 1f, 1.25f, 2f })
        foreach (float rotation in new[] { 0f, 17f, 90f })
        {
            var frame = MapCoordinateFrame.Create(MapViewportMetrics.Create(600, 600, dpi), SKPoint.Empty, 1000, rotation);
            Check(Pixels(expected, frame).SequenceEqual(Pixels(actual, frame)), "DPI/rotation pixels differ");
        }
        Check(RenderPrecisionPolicy.NeedsLocalOrigin(new SKPoint((float)ox, (float)oy), 10), "High-zoom rebasing selected");
        Check(!RenderPrecisionPolicy.NeedsLocalOrigin(new SKPoint(10, 10), 1), "Ordinary cache remains enabled");
        Console.WriteLine("Render precision: PASS (collapsed-parcel regression, 1/64m gap, 3 DPIs, 3 rotations)");
    }
    private static byte[] Pixels(SKPath path, MapCoordinateFrame frame)
    {
        using var bitmap = new SKBitmap(frame.Viewport.PhysicalWidth, frame.Viewport.PhysicalHeight);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(frame.LocalToPhysicalMatrix);
        using var paint = new SKPaint { Color = SKColors.Cyan, IsAntialias = true };
        canvas.DrawPath(path, paint);
        return bitmap.Bytes;
    }
    private static void Check(bool valid, string message)
    { if (!valid) throw new InvalidOperationException(message); }
}
