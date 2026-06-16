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

            // Junta os limites absolutos de TODAS as geometrias
            foreach (var path in _mapService.VetoresPorCamada.Values) { if (primeiro) { limitesTotais = path.Bounds; primeiro = false; } else { limitesTotais.Union(path.Bounds); } }
            foreach (var path in _mapService.LinhasPorCamada.Values) { if (primeiro) { limitesTotais = path.Bounds; primeiro = false; } else { limitesTotais.Union(path.Bounds); } }
            foreach (var path in _mapService.PontosPorCamada.Values) { if (primeiro) { limitesTotais = path.Bounds; primeiro = false; } else { limitesTotais.Union(path.Bounds); } }

            if (!limitesTotais.IsEmpty)
            {
                float escalaX = width / limitesTotais.Width;
                float escalaY = height / limitesTotais.Height;
                float escalaAutoFit = Math.Min(escalaX, escalaY) * 0.8f;

                var matriz = SKMatrix.CreateTranslation(-limitesTotais.MidX, -limitesTotais.MidY);
                matriz = matriz.PostConcat(SKMatrix.CreateScale(escalaAutoFit * _mapService.CameraZoom, escalaAutoFit * _mapService.CameraZoom));
                matriz = matriz.PostConcat(SKMatrix.CreateTranslation(width / 2f + _mapService.CameraPanX, height / 2f + _mapService.CameraPanY));

                // === MOTOR HIERÁRQUICO DE Z-INDEX ===
                // O loop for opera de 0 (Fundo Absoluto) até Count-1 (Topo Visual)
                // Isto garante que se a Ortofoto estiver por cima, ela tapará os vetores.
                for (int i = 0; i < _mapService.OrdemCamadas.Count; i++)
                {
                    string camadaAtual = _mapService.OrdemCamadas[i];

                    lock (_mapService)
                    {
                        if (_mapService.TemRaster && camadaAtual == _mapService.NomeRasterAtivo && _mapService.DatasetRaster != null && matriz.TryInvert(out SKMatrix inverse))
                        {
                            // 1. BLINDAGEM DA MATRIZ: A ortofoto GDAL já ocupa o monitor inteiro.
                            // Resetamos a matriz gráfica para impedir que a redução de escala UTM 
                            // dos vetores contamine a fotografia.
                            canvas.ResetMatrix();

                            SKPoint topLeft = inverse.MapPoint(new SKPoint(0, 0));
                            SKPoint bottomRight = inverse.MapPoint(new SKPoint(width, height));

                            // 2. DECIFRADOR DA ÂNCORA (Memória Gráfica -> UTM Real)
                            double minLng = Math.Min(topLeft.X, bottomRight.X) + _mapService.OffsetMundoX;
                            double maxLng = Math.Max(topLeft.X, bottomRight.X) + _mapService.OffsetMundoX;
                            double minLat = Math.Min(-topLeft.Y, -bottomRight.Y) + _mapService.OffsetMundoY;
                            double maxLat = Math.Max(-topLeft.Y, -bottomRight.Y) + _mapService.OffsetMundoY;

                            string[] warpArgs = {
                                "-te", minLng.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                       minLat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                       maxLng.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                       maxLat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                // Sem conversão EPSG:4326 forçada para garantir alinhamento milimétrico
                                "-ts", width.ToString(), height.ToString(),
                                "-r", "bilinear",
                                "-dstalpha",
                                "-wm", "2048",
                                "-multi",
                                "-wo", "NUM_THREADS=ALL_CPUS",
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

                                        // Imprime a imagem do GDAL estritamente a 100% da escala da tela
                                        canvas.DrawBitmap(rasterBitmap, 0, 0);
                                    }
                                }
                            }
                        }
                    }

                    // 1. DESENHA POLÍGONOS (Com Preenchimento)
                    if (_mapService.VetoresPorCamada.TryGetValue(camadaAtual, out var polyPath))
                    {
                        canvas.SetMatrix(matriz);
                        float zoomReal = escalaAutoFit * _mapService.CameraZoom;
                        using var pincelBordaLOD = _mapService.PincelBorda.Clone();
                        pincelBordaLOD.StrokeWidth = 1f / zoomReal;

                        canvas.DrawPath(polyPath, _mapService.PincelFill);
                        if (zoomReal > 0.0005f) canvas.DrawPath(polyPath, pincelBordaLOD);
                    }

                    // 2. DESENHA LINHAS (Estritamente sem preenchimento)
                    if (_mapService.LinhasPorCamada.TryGetValue(camadaAtual, out var linePath))
                    {
                        canvas.SetMatrix(matriz);
                        float zoomReal = escalaAutoFit * _mapService.CameraZoom;
                        using var pincelBordaLOD = _mapService.PincelBorda.Clone();
                        pincelBordaLOD.StrokeWidth = 2f / zoomReal; // Linha de rua mais grossa para visibilidade

                        canvas.DrawPath(linePath, pincelBordaLOD);
                    }

                    // 3. DESENHA PONTOS
                    if (_mapService.PontosPorCamada.TryGetValue(camadaAtual, out var pointPath))
                    {
                        canvas.SetMatrix(matriz);
                        float zoomReal = escalaAutoFit * _mapService.CameraZoom;
                        using var pincelBordaLOD = _mapService.PincelBorda.Clone();
                        pincelBordaLOD.StrokeWidth = 1f / zoomReal;
                        using var pincelPonto = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Cyan, IsAntialias = true };

                        canvas.DrawPath(pointPath, pincelPonto);
                        canvas.DrawPath(pointPath, pincelBordaLOD);
                    }
                } // Fim do ciclo for das camadas

                // --- CAMADA DE SELEÇÃO E TOPOGRAFIA (Renderizada sempre no topo absoluto) ---
                if (_mapService.CaminhoDestaquePoligono != null)
                {
                    canvas.SetMatrix(matriz);
                    float zoomReal = escalaAutoFit * _mapService.CameraZoom;
                    using var pincelDestaqueBordaLOD = _mapService.PincelDestaqueBorda.Clone();
                    pincelDestaqueBordaLOD.StrokeWidth = 3f / zoomReal;

                    canvas.DrawPath(_mapService.CaminhoDestaquePoligono, _mapService.PincelDestaqueFill);
                    canvas.DrawPath(_mapService.CaminhoDestaquePoligono, pincelDestaqueBordaLOD);
                }

                if (_mapService.CaminhoDestaqueLinha != null)
                {
                    canvas.SetMatrix(matriz);
                    float zoomReal = escalaAutoFit * _mapService.CameraZoom;
                    using var pincelDestaqueBordaLOD = _mapService.PincelDestaqueBorda.Clone();
                    pincelDestaqueBordaLOD.StrokeWidth = 4f / zoomReal; // Neón de destaque mais largo

                    // A seleção de rua não preenche o centro, apenas brilha a borda!
                    canvas.DrawPath(_mapService.CaminhoDestaqueLinha, pincelDestaqueBordaLOD);
                }
            } // Fim do ciclo for das camadaslo for das camadas

                // --- CAMADA DE SELEÇÃO E TOPOGRAFIA (Renderizada sempre no topo absoluto) ---
                if (_mapService.CaminhoDestaque != null)
                {
                    canvas.SetMatrix(matriz);
                    float zoomReal = escalaAutoFit * _mapService.CameraZoom;

                    // 2. CLONAGEM DO PINCEL DE DESTAQUE (Previne distorção geométrica no Zoom)
                    using var pincelDestaqueBordaLOD = _mapService.PincelDestaqueBorda.Clone();
                    pincelDestaqueBordaLOD.StrokeWidth = 3f / zoomReal;

                    canvas.DrawPath(_mapService.CaminhoDestaque, _mapService.PincelDestaqueFill);
                    canvas.DrawPath(_mapService.CaminhoDestaque, pincelDestaqueBordaLOD);
                }
            } // Fim do if (!limitesTotais.IsEmpty)

            canvas.Restore();
            using var image = surface.Snapshot();

            // Qualidade 80: Reduz o tamanho do ficheiro em 50%, 
            // acelerando a entrega para o JS sem perda visual notável.
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);

            res.ContentType = "image/jpeg";
            res.ContentLength64 = data.Size;
            data.SaveTo(res.OutputStream);
            res.OutputStream.Close();
        }
        catch { try { context.Response.StatusCode = 500; context.Response.Close(); } catch { } }
    }
    public void Stop() { _isRunning = false; _listener?.Stop(); }
}