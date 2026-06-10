using System;
using System.Net;
using System.Threading.Tasks;
using System.IO;
using SkiaSharp;

namespace GeoNex.Services;

public class LocalMapServer
{
    private HttpListener _listener;
    private readonly MapRenderingService _mapService;
    private readonly RasterService _rasterService;
    private bool _isRunning;
    private int _port;

    public string BaseUrl => $"http://localhost:{_port}/";

    public LocalMapServer(MapRenderingService mapService, RasterService rasterService)
    {
        _mapService = mapService;
        _rasterService = rasterService;
    }

    public void Start()
    {
        if (_isRunning) return;

        try
        {
            // Deteta uma porta TCP livre no sistema automaticamente
            using (var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
            {
                tcp.Start();
                _port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                tcp.Stop();
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/mapa/");
            _listener.Start();
            _isRunning = true;

            Task.Run(EscutarRequisicoes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro ao iniciar o servidor de mapas: {ex.Message}");
        }
    }

    private async Task EscutarRequisicoes()
    {
        while (_isRunning)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => ProcessarRequisicao(context)); // Multi-threading nativo
            }
            catch { }
        }
    }

    private void ProcessarRequisicao(HttpListenerContext context)
    {
        try
        {
            var req = context.Request;
            var res = context.Response;

            int width = int.Parse(req.QueryString["w"] ?? "1920");
            int height = int.Parse(req.QueryString["h"] ?? "1080");

            // 1. Aloca a tela gráfica diretamente na memória RAM limpa
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;

            canvas.Clear(new SKColor(22, 25, 33)); // Cor base da interface profissional
            canvas.Save();

            SKRect limitesTotais = new SKRect();
            bool primeiro = true;

            if (_mapService.TemRaster)
            {
                limitesTotais = _mapService.LimitesRasterGlobal;
                primeiro = false;
            }

            foreach (var path in _mapService.PathsCompilados)
            {
                if (primeiro) { limitesTotais = path.Bounds; primeiro = false; }
                else { limitesTotais.Union(path.Bounds); }
            }

            if (!limitesTotais.IsEmpty)
            {
                float escalaX = width / limitesTotais.Width;
                float escalaY = height / limitesTotais.Height;
                float escalaAutoFit = Math.Min(escalaX, escalaY) * 0.8f;

                var matriz = SKMatrix.CreateTranslation(-limitesTotais.MidX, -limitesTotais.MidY);
                matriz = matriz.PostConcat(SKMatrix.CreateScale(escalaAutoFit * _mapService.CameraZoom, escalaAutoFit * _mapService.CameraZoom));
                matriz = matriz.PostConcat(SKMatrix.CreateTranslation(width / 2f + _mapService.CameraPanX, height / 2f + _mapService.CameraPanY));

                canvas.SetMatrix(matriz);

                // --- Renderização da Ortofoto ---
                if (_mapService.TemRaster)
                {
                    using var paintRaster = new SKPaint { FilterQuality = SKFilterQuality.High };
                    canvas.DrawBitmap(_mapService.RasterAtivo, _mapService.LimitesRasterDesenho, paintRaster);
                }

                // --- Renderização dos Vetores ---
                _mapService.PincelBorda.StrokeWidth = 1f / (escalaAutoFit * _mapService.CameraZoom);

                foreach (var path in _mapService.PathsCompilados)
                {
                    canvas.DrawPath(path, _mapService.PincelFill);
                    canvas.DrawPath(path, _mapService.PincelBorda);
                }
            }

            canvas.Restore();

            // 2. Serialização binária direta para o Stream de rede (Sem strings ou Base64)
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);

            res.ContentType = "image/jpeg";
            res.ContentLength64 = data.Size;

            data.SaveTo(res.OutputStream);
            res.OutputStream.Close();
        }
        catch (Exception ex)
        {
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
        }
    }

    public void Stop()
    {
        _isRunning = false;
        _listener?.Stop();
    }
}