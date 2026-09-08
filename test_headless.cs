using System;
using OpenTK.Windowing.Desktop;
using SkiaSharp;

class Program
{
    static void Main()
    {
        Console.WriteLine("Testing Headless GPU Context...");
        try 
        {
            var windowSettings = new NativeWindowSettings()
            {
                Size = new OpenTK.Mathematics.Vector2i(1, 1),
                Title = "Headless",
                StartVisible = false
            };

            var window = new NativeWindow(GameWindowSettings.Default, windowSettings);
            window.Context.MakeCurrent();

            var glInterface = GRGlInterface.Create();
            Console.WriteLine($"GL Interface Created: {glInterface != null}");

            var grContext = GRContext.CreateGl(glInterface);
            Console.WriteLine($"GRContext Created: {grContext != null}");
        }
        catch(Exception e)
        {
            Console.WriteLine($"Error: {e}");
        }
    }
}
