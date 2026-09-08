#if WINDOWS
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;

namespace GeoNex.Platforms.Windows;

public static class WebView2Transparency
{
    public static void Aplicar()
    {
        BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping(
            "GeoNexTransparency",
            (handler, view) =>
            {
                if (handler.PlatformView is not WebView2 wv2) return;

                // O nome correto da API no MAUI (WinUI 3) é este:
                wv2.CoreWebView2Initialized += (s, args) =>
                {
                    // Força a transparência no renderizador nativo da Microsoft
                    wv2.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
                };
            });
    }
}
#endif