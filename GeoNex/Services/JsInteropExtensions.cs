using Microsoft.JSInterop;
using System;
using System.Threading.Tasks;

namespace GeoNex.Services // Ajuste o namespace se necessário
{
    public static class JsInteropExtensions
    {
        /// <summary>
        /// Executa uma chamada JSInterop em background (Fire-and-Forget) capturando e logando falhas,
        /// sem bloquear a Thread de UI do Blazor.
        /// </summary>
        public static void InvokeVoidAsyncSafe(this IJSRuntime jsRuntime, string identifier, params object[] args)
        {
            _ = jsRuntime.InvokeVoidAsync(identifier, args).AsTask().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    // Num ambiente de produção, isto seria encaminhado para um ILogger
                    Console.WriteLine($"[FALHA JSINTEROP] Ocorreu um erro ao invocar '{identifier}': {t.Exception?.Flatten().InnerException?.Message}");
                }
            });
        }
    }
}