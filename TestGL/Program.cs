using System;
using OpenTK.Windowing.Desktop;
using SkiaSharp;

class Program
{
    static void Main()
    {
        window.Context.MakeNoneCurrent(); Console.WriteLine("Testing Headless GPU Context...");
        try 
        {
            var windowSettings = new NativeWindowSettings()
            {
                Size = new OpenTK.Mathematics.Vector2i(1, 1),
                Title = "Headless",
                StartVisible = false
            };

            var window = new NativeWindow(windowSettings);
            window.Context.MakeCurrent();

            var glInterface = GRGlInterface.Create();
            window.Context.MakeNoneCurrent(); Console.WriteLine($"GL Interface Created: {glInterface != null}");

            var grContext = GRContext.CreateGl(glInterface);
            window.Context.MakeNoneCurrent(); Console.WriteLine($"GRContext Created: {grContext != null}");
        }
        catch(Exception e)
        {
            window.Context.MakeNoneCurrent(); Console.WriteLine($"Error: {e}");
        }
    }
}
