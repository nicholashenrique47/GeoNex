using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using CommunityToolkit.Maui;
using GeoNex.Services;
using GeoNex.Services.Topografia;
using GeoNex.Services.Topografia.Export;
using GeoNex.Services.Topografia.Import;
using GeoNex.Services.Topografia.Validation;
using GeoNex.Services.Licensing;

namespace GeoNex;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        System.IO.File.AppendAllText("debug_boot.txt", "1. CreateMauiApp Start\n");
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton<ProjetoService>();
        builder.Services.AddSingleton<RasterService>();
        builder.Services.AddSingleton<MapRenderingService>();
        builder.Services.AddSingleton<ExportService>();
        builder.Services.AddSingleton<WindowCloseCoordinator>();

        // Serviços de Topografia, Geodésia e Certificação SIGEF/INCRA
        builder.Services.AddSingleton<TopografiaProcessingService>();
        builder.Services.AddSingleton<SigefOdsExportService>();
        builder.Services.AddSingleton<MemorialDescritivoExportService>();
        builder.Services.AddSingleton<SpatialConfrontationService>();
        builder.Services.AddSingleton<TopografiaForensicValidator>();
        builder.Services.AddSingleton<LicenseValidationService>();

        // Módulos Avançados de Topografia: DXF, Prancha PDF, Importador de Campo e WFS INCRA
        builder.Services.AddSingleton<TopografiaDxfExportService>();
        builder.Services.AddSingleton<TopografiaPranchaPdfService>();
        builder.Services.AddSingleton<TopografiaFieldDataImporter>();
        builder.Services.AddSingleton<IncraSobreposicaoAuditorService>(sp =>
        {
            var client = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            return new IncraSobreposicaoAuditorService(client);
        });

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
