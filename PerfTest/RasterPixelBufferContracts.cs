using GeoNex.Services;
using SkiaSharp;

internal static class RasterPixelBufferContracts
{
    public static void Run()
    {
        var info = new SKImageInfo(257, 193, SKColorType.Rgba8888, SKAlphaType.Premul);
        long cost = (long)info.Width * info.Height * 4;
        using var pool = new RasterPixelBuffer();
        Require(pool.TryRent(info) == null, "Zero budget allocated pixels");
        pool.Maintain(cost);
        var first = pool.TryRent(info) ?? throw new InvalidOperationException("Rent failed");
        first.Bitmap.Erase(SKColors.Cyan);
        IntPtr address = first.Bitmap.GetPixels();
        using var image = first.CreateImage(); // Native image owns rental from here.
        using var child = image.Subset(new SKRectI(0, 0, 17, 13));
        using var shader = image.ToShader();
        Require(pool.TryRent(info) == null, "Live image buffer reused or budget exceeded");
        image.Dispose();
        Require(pool.TryRent(info) == null, "Native shader lost its buffer ownership");
        using (var target = new SKBitmap(17, 13, SKColorType.Rgba8888, SKAlphaType.Premul))
        using (var canvas = new SKCanvas(target))
        using (var paint = new SKPaint { Shader = shader })
        {
            canvas.Clear(SKColors.Transparent); canvas.DrawPaint(paint);
            Require(target.GetPixel(0, 0) == SKColors.Cyan, "Retained shader pixels changed");
        }
        shader.Dispose();
        using (var childPixels = child.PeekPixels())
        {
            using var whileChildLives = pool.TryRent(info);
            if (childPixels.GetPixels() == address)
                Require(whileChildLives == null, "Shared native subset lost its buffer ownership");
            else whileChildLives?.Bitmap.Erase(SKColors.Yellow); // Copied subsets are independent.
            Require(childPixels.GetPixelColor(0, 0) == SKColors.Cyan, "Child pixels changed");
        }
        child.Dispose();
        var second = pool.TryRent(info) ?? throw new InvalidOperationException("Released allocation was lost");
        Require(second.Bitmap.GetPixels() == address, "Returned buffer was not reused");
        second.Dispose(); second.Dispose();
        Require(pool.AllocatedBytes == cost, "Duplicate return corrupted accounting");
        pool.Maintain(cost - 1);
        Require(pool.AllocatedBytes == 0 && pool.TryRent(info) == null, "Pressure did not evict idle buffer");
        pool.Maintain(cost);
        var active = pool.TryRent(info)!;
        active.Bitmap.Erase(SKColors.Magenta);
        using var pending = active.CreateImage();
        pool.Dispose(); pool.Dispose();
        Require(pool.AllocatedBytes == cost && pool.TryRent(info) == null, "Shutdown freed active pixels");
        using (var pixels = pending.PeekPixels())
            Require(pixels.GetPixelColor(0, 0) == SKColors.Magenta, "Pending encoder lost pixels during shutdown");
        pending.Dispose();
        Require(pool.AllocatedBytes == 0, "Native owner did not release shutdown allocation");

        using var varying = new RasterPixelBuffer();
        varying.Maintain(long.MaxValue);
        Require(varying.TryRent(new SKImageInfo(int.MaxValue, int.MaxValue, SKColorType.Rgba8888, SKAlphaType.Premul)) == null,
            "Overflow or oversized frame accepted");
        Require(varying.TryRent(new SKImageInfo(17, 13, SKColorType.Bgra8888, SKAlphaType.Premul)) == null,
            "Unsupported color format accepted");
        using (var small = varying.TryRent(info)) Require(small != null, "Small rent failed");
        var resizedInfo = new SKImageInfo(401, 307, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var resized = varying.TryRent(resizedInfo))
            Require(resized != null && varying.AllocatedBytes == (long)401 * 307 * 4, "Resize retained old allocation");
        Require(varying.TryRent(new SKImageInfo(8192, 8192, SKColorType.Rgba8888, SKAlphaType.Premul)) == null &&
            varying.AllocatedBytes == 0, "Oversized resize retained an unusable idle buffer");
        varying.Maintain(0);
        Require(varying.AllocatedBytes == 0, "Zero budget retained allocation");
        varying.Maintain(cost);
        var pressureRental = varying.TryRent(info)!;
        pressureRental.Bitmap.Erase(SKColors.Cyan);
        using var underPressure = pressureRental.CreateImage();
        varying.Maintain(0);
        Require(varying.TryRent(info) == null && varying.AllocatedBytes == cost,
            "Pressure freed live pixels or allowed a new allocation");
        using (var pixels = underPressure.PeekPixels())
            Require(pixels.GetPixelColor(0, 0) == SKColors.Cyan, "Pressure changed a live image");
        underPressure.Dispose();
        Require(varying.AllocatedBytes == 0, "Pressure retained returned allocation");

        // Concurrent native releases race with memory pressure and shutdown.
        using var concurrent = new RasterPixelBuffer();
        concurrent.Maintain(cost * 8);
        var images = Enumerable.Range(0, 8).Select(i =>
        {
            var rental = concurrent.TryRent(info)!;
            rental.Bitmap.Erase(new SKColor((byte)i, 19, 31));
            return rental.CreateImage();
        }).ToArray();
        Require(concurrent.AllocatedBytes <= cost * 8, "Concurrent budget exceeded");
        Parallel.Invoke(() => { concurrent.Maintain(0); concurrent.Dispose(); },
            () => Parallel.For(0, images.Length, i =>
            {
                using var retained = images[i];
                using var pixels = retained.PeekPixels();
                Require(pixels.GetPixelColor(0, 0) == new SKColor((byte)i, 19, 31), "Concurrent reader saw another frame");
            }));
        Require(concurrent.AllocatedBytes == 0, "Concurrent shutdown leaked pixels");
        Console.WriteLine("Preview pixel buffer: PASS (reuse, exact native ownership/subsets, in-flight budget, resize, pressure, overflow, duplicate return, concurrent shutdown)");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
