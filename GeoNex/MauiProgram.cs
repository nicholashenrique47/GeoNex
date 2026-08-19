using Microsoft.Extensions.Logging;
using SkiaSharp.Views.Maui.Controls.Hosting;
using GeoNex.Services;
using CommunityToolkit.Maui; // 1. OBRIGATÓRIO: Importação do Toolkit

#if WINDOWS
using GeoNex.Platforms.Windows;
#endif

namespace GeoNex;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        System.IO.File.AppendAllText("debug_boot.txt", "1. CreateMauiApp Started\n");
        var builder = MauiApp.CreateBuilder();

        // A CORRENTE DE EXECUÇÃO
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit() // 2. OBRIGATÓRIO: Injeta as ferramentas de ficheiros/pastas logo a seguir
            .UseSkiaSharp();           // 3. OBRIGATÓRIO: Liga o motor Skia à placa de vídeo!

#if WINDOWS
        // Aplica a transparência no arranque
        // WebView2Transparency.Aplicar();
#endif

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton<MapRenderingService>();
        builder.Services.AddSingleton<ProjetoService>();
        builder.Services.AddSingleton<RasterService>();
        builder.Services.AddSingleton<ExportService>();
        builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif
        
        System.IO.File.AppendAllText("debug_boot.txt", "2. CreateMauiApp Build\n");
        var app = builder.Build();
        System.IO.File.AppendAllText("debug_boot.txt", "3. CreateMauiApp Finished\n");
        return app;
    }
}