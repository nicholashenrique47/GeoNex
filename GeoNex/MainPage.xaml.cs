using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using GeoNex.Services;
using System;

#if WINDOWS
using Microsoft.UI.Xaml.Controls;
#endif

namespace GeoNex;

public partial class MainPage : ContentPage
{
    private readonly MapRenderingService _mapService;

    private readonly SKPaint _pincelBorda = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Cyan.WithAlpha(200), StrokeWidth = 0, IsAntialias = true };
    private readonly SKPaint _pincelFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Cyan.WithAlpha(25), IsAntialias = true };
    private readonly SKPaint _pincelRadar = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill, IsAntialias = true };

    public MainPage(MapRenderingService mapService)
    {
#if WINDOWS
        // Define a transparência do WebView2 antes de qualquer inicialização de interface
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00000000");
#endif

        InitializeComponent();
        _mapService = mapService;

        _mapService.OnMapInvalidated += () =>
            MainThread.BeginInvokeOnMainThread(() => mapCanvas.InvalidateSurface());

#if WINDOWS
        // Vincula a transparência do controle WinUI assim que o ciclo do Handler for concluído
        blazorWebView.HandlerChanged += (s, e) =>
        {
            if (blazorWebView.Handler?.PlatformView is WebView2 wv2)
            {
                wv2.CoreWebView2Initialized += (sender, args) =>
                {
                    wv2.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
                };
            }
        };
#endif

        // Temporizador de segurança para invalidação de superfície
        Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            mapCanvas.InvalidateSurface();
            return true;
        });
    }

    private void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info = e.Info;

        // 1. PINTA O FUNDO DE ROSA CHOQUE
        // Se a tela ficar rosa, significa que o vidro Blazor ESTÁ transparente 
        // e estamos a ver a placa gráfica nativa!
        canvas.Clear(SKColors.HotPink);

        // 2. DESENHA A BOLA EM COORDENADAS FIXAS (Ignorando matemática de ecrã)
        canvas.DrawCircle(200, 200, 150, _pincelRadar);

        // ... (o resto do código do OnPaintSurface continua igual abaixo)
        canvas.Save();

        if (_mapService.PathsCompilados.Count > 0)
        {
            SKRect limitesTotais = new SKRect();
            bool primeiro = true;
            foreach (var path in _mapService.PathsCompilados)
            {
                if (primeiro) { limitesTotais = path.Bounds; primeiro = false; }
                else { limitesTotais.Union(path.Bounds); }
            }

            if (!limitesTotais.IsEmpty)
            {
                float escalaX = info.Width / limitesTotais.Width;
                float escalaY = info.Height / limitesTotais.Height;
                float escalaAutoFit = Math.Min(escalaX, escalaY) * 0.8f;

                canvas.Translate(info.Width / 2f + _mapService.CameraPanX, info.Height / 2f + _mapService.CameraPanY);
                canvas.Scale(escalaAutoFit * _mapService.CameraZoom);
                canvas.Translate(-limitesTotais.MidX, -limitesTotais.MidY);

                _pincelBorda.StrokeWidth = 1f / (escalaAutoFit * _mapService.CameraZoom);

                foreach (var path in _mapService.PathsCompilados)
                {
                    canvas.DrawPath(path, _pincelFill);
                    canvas.DrawPath(path, _pincelBorda);
                }
            }
        }

        canvas.Restore();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _pincelBorda.Dispose();
        _pincelFill.Dispose();
        _pincelRadar.Dispose();
    }
}