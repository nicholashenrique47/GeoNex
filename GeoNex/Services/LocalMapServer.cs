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

    public LocalMapServer(MapRenderingService mapService) { _mapService = mapService; }

    public void Start()
    {
        if (_isRunning) return;
        using (var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            tcp.Start(); _port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
        }
        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUrl + "mapa/");
        _listener.Start();
        _isRunning = true;
        Task.Run(EscutarRequisicoes);
    }

    private async Task EscutarRequisicoes()
    {
        while (_isRunning) { try { var context = await _listener.GetContextAsync(); _ = Task.Run(() => ProcessarRequisicao(context)); } catch { } }
    }

    private void ProcessarRequisicao(HttpListenerContext context)
    {
        try
        {
            var req = context.Request; var res = context.Response;
            res.AppendHeader("Access-Control-Allow-Origin", "*");
            res.AppendHeader("Cache-Control", "no-cache, no-store, must-revalidate");

            int width = int.Parse(req.QueryString["w"] ?? "1920");
            int height = int.Parse(req.QueryString["h"] ?? "1080");

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;

            canvas.Clear(new SKColor(22, 25, 33));
            canvas.Save();

            SKRect limitesTotais = new SKRect();
            bool primeiro = true;

            if (_mapService.TemRaster) { limitesTotais = _mapService.LimitesRasterGlobal; primeiro = false; }

            // Lê o Bounding Box de cada Mega-Path em vez de milhares individuais
            foreach (var path in _mapService.VetoresPorCamada.Values)
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

                for (int i = _mapService.OrdemCamadas.Count - 1; i >= 0; i--)
                {
                    string camadaAtual = _mapService.OrdemCamadas[i];

                    lock (_mapService)
                    {
                        if (_mapService.TemRaster && camadaAtual == _mapService.NomeRasterAtivo && _mapService.DatasetRaster != null && matriz.TryInvert(out SKMatrix inverse))
                        {
                            SKPoint topLeft = inverse.MapPoint(new SKPoint(0, 0));
                            SKPoint bottomRight = inverse.MapPoint(new SKPoint(width, height));

                            double minLng = Math.Min(topLeft.X, bottomRight.X);
                            double maxLng = Math.Max(topLeft.X, bottomRight.X);
                            double minLat = Math.Min(-topLeft.Y, -bottomRight.Y);
                            double maxLat = Math.Max(-topLeft.Y, -bottomRight.Y);

                            string[] warpArgs = {
                                "-te", minLng.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                       minLat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                       maxLng.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                       maxLat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                "-t_srs", "EPSG:4326",
                                "-ts", width.ToString(), height.ToString(),
                                "-r", "bilinear",
                                "-dstalpha",
                                "-wm", "2048",
                                "-multi",
                                "-wo", "NUM_THREADS=ALL_CPUS",
                                "-wo", "OPTIMIZE_SIZE=TRUE", // Otimiza a árvore de execução do C++
                                "-cwt", "Byte",              // Trava os cálculos de transformação em 8-bits, eliminando peso flutuante
                                "-of", "MEM"
                            };

                            using GDALWarpAppOptions warpOptions = new GDALWarpAppOptions(warpArgs);
                            using Dataset memDs = Gdal.Warp("", new[] { _mapService.DatasetRaster }, warpOptions, null, null);

                            if (memDs != null)
                            {
                                int qtBandas = memDs.RasterCount;
                                if (qtBandas > 0)
                                {
                                    int numBandas = Math.Min(qtBandas, 4);
                                    int[] listaBandas = new int[numBandas];
                                    for (int b = 0; b < numBandas; b++) listaBandas[b] = b + 1;

                                    using var rasterBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                                    using (var tmpCanvas = new SKCanvas(rasterBitmap)) { tmpCanvas.Clear(new SKColor(0, 0, 0, 0)); }

                                    IntPtr ptr = rasterBitmap.GetPixels();
                                    if (ptr != IntPtr.Zero)
                                    {
                                        memDs.ReadRaster(0, 0, width, height, ptr, width, height, DataType.GDT_Byte, numBandas, listaBandas, 4, width * 4, 1);
                                        canvas.DrawBitmap(rasterBitmap, 0, 0);
                                    }
                                }
                            }
                        }
                    }

                    // A EXECUÇÃO TURBO: Desenha toda a cidade/lotes de Guaratuba com um único comando GPU
                    // A EXECUÇÃO TURBO: Desenha toda a malha vetorial com um único comando GPU
                    if (_mapService.VetoresPorCamada.ContainsKey(camadaAtual))
                    {
                        canvas.SetMatrix(matriz);

                        // Calcula o zoom real da câmara em relação ao mundo
                        float zoomReal = escalaAutoFit * _mapService.CameraZoom;
                        _mapService.PincelBorda.StrokeWidth = 1f / zoomReal;

                        var superPath = _mapService.VetoresPorCamada[camadaAtual];

                        // Desenha sempre o preenchimento translúcido
                        canvas.DrawPath(superPath, _mapService.PincelFill);

                        // LOD (Level Of Detail): Só desenha as bordas se o utilizador estiver suficientemente próximo.
                        // Se o zoomReal for muito pequeno (visão de pássaro), omite as bordas para evitar que os lotes
                        // se aglomerem num borrão negro, mantendo o mapa limpo e profissional.
                        if (zoomReal > 0.0005f)
                        {
                            canvas.DrawPath(superPath, _mapService.PincelBorda);
                        }
                    }
                }
            }

            canvas.Restore();
            using var image = surface.Snapshot();

            // OTIMIZAÇÃO DE REDE: Redução microscópica na qualidade (impercetível) para acelerar a transferência IPC
            // Formato WebP: 30% mais leve que o JPEG e com decodificação direta via GPU no Chromium
            using var data = image.Encode(SKEncodedImageFormat.Webp, 95);
            res.ContentType = "image/webp";

            res.ContentType = "image/jpeg";
            res.ContentLength64 = data.Size;
            data.SaveTo(res.OutputStream);
            res.OutputStream.Close();
        }
        catch { try { context.Response.StatusCode = 500; context.Response.Close(); } catch { } }
    }
    public void Stop() { _isRunning = false; _listener?.Stop(); }
}