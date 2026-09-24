using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GeoNex.Services;
using SkiaSharp;

/// <summary>Isolated Windows GPU experiment; no production renderer dependency.</summary>
internal static class OpenGlPolygonMetrics
{
    public static void Measure(SKPath path, int width, int height, float scale)
    {
        try
        {
            var timer = Stopwatch.StartNew();
            using var window = new GlWindow();
            using var api = GRGlInterface.Create();
            using var context = api == null ? null : GRContext.CreateGl(api);
            if (context == null) { Console.WriteLine("GPU unavailable: no Skia GL context"); return; }
            Console.WriteLine($"GPU renderer={Marshal.PtrToStringAnsi(glGetString(0x1F01))} version={Marshal.PtrToStringAnsi(glGetString(0x1F02))} init_ms={timer.Elapsed.TotalMilliseconds:F2}");
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var cpu = SKSurface.Create(info);
            using var gpu = SKSurface.Create(context, false, info);
            if (gpu == null) throw new IOException("Cannot create GPU surface");
            using var fill = new SKPaint { Color = SKColors.Cyan.WithAlpha(25), IsAntialias = true };
            using var stroke = new SKPaint { Color = SKColors.Cyan.WithAlpha(200), IsAntialias = true,
                Style = SKPaintStyle.Stroke, StrokeWidth = 1 / scale, StrokeJoin = SKStrokeJoin.Round };
            var matrix = MapCoordinateFrame.Create(MapViewportMetrics.Create(width, height, 1), SKPoint.Empty, scale).LocalToPhysicalMatrix;
            for (int sample = 0; sample < 3; sample++)
            {
                var camera = matrix; camera.TransX += sample;
                byte[]? reference = null;
                foreach (string mode in new[] { "cpu", "gpu", "gpu-fill" })
                {
                    bool hardware = mode != "cpu";
                    var surface = hardware ? gpu : cpu;
                    timer.Restart();
                    surface.Canvas.Clear(SKColors.Transparent);
                    surface.Canvas.SetMatrix(camera);
                    surface.Canvas.DrawPath(path, fill);
                    if (mode != "gpu-fill") surface.Canvas.DrawPath(path, stroke);
                    using var image = surface.Snapshot();
                    using var bitmap = new SKBitmap(info);
                    if (!image.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
                        throw new IOException("GPU readback failed");
                    if (mode == "gpu-fill")
                    {
                        using var borderCanvas = new SKCanvas(bitmap);
                        borderCanvas.SetMatrix(camera); borderCanvas.DrawPath(path, stroke); borderCanvas.Flush();
                    }
                    timer.Stop();
                    var bytes = bitmap.Bytes;
                    Console.WriteLine($"GPU sample={sample} mode={mode} draw_readback_ms={timer.Elapsed.TotalMilliseconds:F2}");
                    if (!hardware) reference = bytes;
                    else
                    {
                        int maximum = 0, changed = 0; double squared = 0;
                        for (int i = 0; i < bytes.Length; i++)
                        {
                            int difference = Math.Abs(reference![i] - bytes[i]);
                            maximum = Math.Max(maximum, difference); squared += difference * difference;
                            if (difference > 0) changed++;
                        }
                        Console.WriteLine($"GPU pixels max_delta={maximum} rms={Math.Sqrt(squared / bytes.Length):F4} changed_channels={changed}/{bytes.Length}");
                    }
                }
            }
        }
        catch (Exception error) { Console.WriteLine($"GPU experiment unavailable: {error.GetType().Name}: {error.Message}"); }
    }

    private sealed class GlWindow : IDisposable
    {
        private nint _window, _dc, _context;
        private readonly nint _previousDc = wglGetCurrentDC(), _previousContext = wglGetCurrentContext();
        public GlWindow()
        {
            try
            {
                _window = CreateWindowExW(0, "STATIC", "GeoNex offscreen benchmark", 0x80000000,
                    0, 0, 1, 1, 0, 0, 0, 0); // No WS_VISIBLE; never activates a window.
                if (_window == 0) throw new Win32Exception();
                _dc = GetDC(_window);
                var format = new PixelFormat { Size = (ushort)Marshal.SizeOf<PixelFormat>(), Version = 1,
                    Flags = 0x25, ColorBits = 32, AlphaBits = 8, StencilBits = 8 };
                int index = ChoosePixelFormat(_dc, ref format);
                if (index == 0 || !SetPixelFormat(_dc, index, ref format)) throw new Win32Exception();
                _context = wglCreateContext(_dc);
                if (_context == 0 || !wglMakeCurrent(_dc, _context)) throw new Win32Exception();
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (_context != 0) { wglMakeCurrent(_previousDc, _previousContext); wglDeleteContext(_context); _context = 0; }
            if (_dc != 0) { ReleaseDC(_window, _dc); _dc = 0; }
            if (_window != 0) { DestroyWindow(_window); _window = 0; }
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormat
    {
        public ushort Size, Version;
        public uint Flags;
        public byte PixelType, ColorBits, RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift,
            AlphaBits, AlphaShift, AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits,
            DepthBits, StencilBits, AuxBuffers, LayerType, Reserved;
        public uint LayerMask, VisibleMask, DamageMask;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int ChoosePixelFormat(nint dc, ref PixelFormat format);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool SetPixelFormat(nint dc, int index, ref PixelFormat format);
    [DllImport("opengl32.dll", SetLastError = true)] private static extern nint wglCreateContext(nint dc);
    [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(nint context);
    [DllImport("opengl32.dll", SetLastError = true)] private static extern bool wglMakeCurrent(nint dc, nint context);
    [DllImport("opengl32.dll")] private static extern nint wglGetCurrentDC();
    [DllImport("opengl32.dll")] private static extern nint wglGetCurrentContext();
    [DllImport("opengl32.dll")] private static extern nint glGetString(uint name);
}
