using Microsoft.Extensions.Logging;
using SkiaSharp.Views.Maui.Controls.Hosting;
using GeoNex.Services;
using GeoNex.Services.Topografia;
using GeoNex.Services.Topografia.Export;
using GeoNex.Services.Topografia.Validation;
using GeoNex.Services.Licensing;
using CommunityToolkit.Maui;

#if WINDOWS
using GeoNex.Platforms.Windows;
#endif

namespace GeoNex;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        System.IO.File.AppendAllText("debug_boot.txt", "1. CreateMauiApp Started\n");
        GdalRuntimeBootstrap.Configure();
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .UseSkiaSharp();

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton<MapRenderingService>();
        builder.Services.AddSingleton<ProjetoService>();
        builder.Services.AddSingleton<WindowCloseCoordinator>();
        builder.Services.AddSingleton<RasterService>();
        builder.Services.AddSingleton<ExportService>();

        // Serviços de Topografia, Certificação SIGEF/INCRA e Licenciamento SaaS
        builder.Services.AddSingleton<TopografiaProcessingService>();
        builder.Services.AddSingleton<SigefOdsExportService>();
        builder.Services.AddSingleton<MemorialDescritivoExportService>();
        builder.Services.AddSingleton<SpatialConfrontationService>();
        builder.Services.AddSingleton<TopografiaForensicValidator>();
        builder.Services.AddSingleton<LicenseValidationService>();

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
