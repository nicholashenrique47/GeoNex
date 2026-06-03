using Microsoft.Extensions.Logging;
using SkiaSharp.Views.Maui.Controls.Hosting;
using GeoNex.Services;
#if WINDOWS
using GeoNex.Platforms.Windows;
#endif

namespace GeoNex;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .UseSkiaSharp(); // OBRIGATÓRIO: Liga o motor Skia à placa de vídeo!

#if WINDOWS
        // Aplica a transparência no arranque
        WebView2Transparency.Aplicar();
#endif

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton<MapRenderingService>();
        builder.Services.AddSingleton<ProjetoService>();
        builder.Services.AddSingleton<RasterService>();
        builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}