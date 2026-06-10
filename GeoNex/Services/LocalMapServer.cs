using System;
using System.Net;
using System.Threading.Tasks;
using SkiaSharp;
using OSGeo.GDAL;

namespace GeoNex.Services;

public class LocalMapServer
{
    private HttpListener _listener;
    private readonly MapRenderingService _mapService;
    private bool _isRunning;
    private int _port;

    public string BaseUrl => $"http://localhost:{_port}/";

    public LocalMapServer(MapRenderingService mapService)
    {
        _mapService = mapService;
    }

    public void Start()
    {
        if (_isRunning) return;
        using (var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            tcp.Start();
            _port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
        }

        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUrl + "mapa/");
        _listener.Start();
        _isRunning = true;
        Task.Run(EscutarRequisicoes);
    }

    private async Task EscutarRequisicoes()
    {
        while (_isRunning)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => ProcessarRequisicao(context));
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

            // PERFORMANCE: Premul é nativo e mais rápido para operações em memória
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;

            canvas.Clear(new SKColor(22, 25, 33));
            canvas.Save();

            SKRect limitesTotais = new SKRect();
            bool primeiro = true;

            // Une as caixas delimitadoras da Ortofoto e dos Shapefiles (que agora partilham a mesma dimensão)
            if (_mapService.TemRaster) { limitesTotais = _mapService.LimitesRasterGlobal; primeiro = false; }
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

                // --- OTIMIZAÇÃO DMA MULTI-CORE: Recorte do TIF ---
                if (_mapService.TemRaster && _mapService.DatasetRaster != null && matriz.TryInvert(out SKMatrix inverse))
                {
                    SKPoint topLeft = inverse.MapPoint(new SKPoint(0, 0));
                    SKPoint bottomRight = inverse.MapPoint(new SKPoint(width, height));

                    double minLng = Math.Min(topLeft.X, bottomRight.X);
                    double maxLng = Math.Max(topLeft.X, bottomRight.X);
                    double minLat = Math.Min(-topLeft.Y, -bottomRight.Y);
                    double maxLat = Math.Max(-topLeft.Y, -bottomRight.Y);

                    // CONFIGURAÇÕES DE PERFORMANCE EXTREMA
                    string[] warpArgs = {
                        "-te", minLng.ToString(System.Globalization.CultureInfo.InvariantCulture),
                               minLat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                               maxLng.ToString(System.Globalization.CultureInfo.InvariantCulture),
                               maxLat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "-ts", width.ToString(), height.ToString(),
                        "-r", "bilinear",    // Mais rápido que o Cubic, qualidade quase idêntica
                        "-dstalpha",
                        "-wm", "2048",       // Permite até 2GB de RAM para acelerar a renderização
                        "-multi",            // LIGA O MULTITHREADING (Usa múltiplos núcleos)
                        "-wo", "NUM_THREADS=ALL_CPUS", // Obriga o GDAL a forçar todos os núcleos do seu PC
                        "-of", "MEM"
                    };

                    using GDALWarpAppOptions warpOptions = new GDALWarpAppOptions(warpArgs);
                    using Dataset memDs = Gdal.Warp("", new[] { _mapService.DatasetRaster }, warpOptions, null, null);

                    if (memDs != null)
                    {
                        using var rasterBitmap = new SKBitmap(info);
                        IntPtr ptr = rasterBitmap.GetPixels();

                        memDs.ReadRaster(0, 0, width, height, ptr, width, height, DataType.GDT_Byte, 4, new[] { 1, 2, 3, 4 }, 4, width * 4, 1);
                        canvas.DrawBitmap(rasterBitmap, 0, 0);
                    }
                }

                // --- Renderização dos Vetores ---
                canvas.SetMatrix(matriz);
                // Escala a espessura da linha dinamicamente para não desaparecer (Mantém 1 pixel de tamanho no monitor)
                _mapService.PincelBorda.StrokeWidth = 1f / (escalaAutoFit * _mapService.CameraZoom);
                foreach (var path in _mapService.PathsCompilados)
                {
                    canvas.DrawPath(path, _mapService.PincelFill);
                    canvas.DrawPath(path, _mapService.PincelBorda);
                }
            }

            canvas.Restore();

            // Codifica em JPEG com qualidade 85 (Perda de 5% de qualidade que reduz o peso na rede e I/O em quase 40%)
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);

            res.ContentType = "image/jpeg";
            res.ContentLength64 = data.Size;
            data.SaveTo(res.OutputStream);
            res.OutputStream.Close();
        }
        catch { try { context.Response.StatusCode = 500; context.Response.Close(); } catch { } }
    }

    public void Stop() { _isRunning = false; _listener?.Stop(); }
}