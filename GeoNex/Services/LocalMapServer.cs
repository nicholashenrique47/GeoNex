using System;
using System.Net;
using System.Threading.Tasks;
using SkiaSharp;
using OSGeo.GDAL;

namespace GeoNex.Services
{
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
                res.AppendHeader("Access-Control-Allow-Origin", "*");
                res.AppendHeader("Cache-Control", "no-cache, no-store, must-revalidate");

                int width = int.Parse(req.QueryString["w"] ?? "1920");
                int height = int.Parse(req.QueryString["h"] ?? "1080");

                var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info);
                var canvas = surface.Canvas;

                canvas.Clear(new SKColor(22, 25, 33));
                canvas.Save();

                // 1. LIMITES TOTAIS COM TODAS AS GEOMETRIAS
                SKRect limitesTotais = new SKRect();
                bool primeiro = true;

                if (_mapService.TemRaster) { limitesTotais = _mapService.LimitesRasterGlobal; primeiro = false; }

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

                    // 2. MOTOR HIERÁRQUICO DE Z-INDEX (Desenha do fundo para o topo)
                    for (int i = 0; i < _mapService.OrdemCamadas.Count; i++)
                    {
                        string camadaAtual = _mapService.OrdemCamadas[i];

                        lock (_mapService)
                        {
                            if (_mapService.TemRaster && camadaAtual == _mapService.NomeRasterAtivo && _mapService.DatasetRaster != null && matriz.TryInvert(out SKMatrix inverse))
                            {
                                canvas.ResetMatrix(); // Blindagem da matriz para Ortofoto

                                SKPoint topLeft = inverse.MapPoint(new SKPoint(0, 0));
                                SKPoint bottomRight = inverse.MapPoint(new SKPoint(width, height));

                                double minLng = Math.Min(topLeft.X, bottomRight.X) + _mapService.OffsetMundoX;
                                double maxLng = Math.Max(topLeft.X, bottomRight.X) + _mapService.OffsetMundoX;
                                double minLat = Math.Min(-topLeft.Y, -bottomRight.Y) + _mapService.OffsetMundoY;
                                double maxLat = Math.Max(-topLeft.Y, -bottomRight.Y) + _mapService.OffsetMundoY;

                                string[] warpArgs = {
                                    "-te", minLng.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                           minLat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                           maxLng.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                           maxLat.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
                                            canvas.DrawBitmap(rasterBitmap, 0, 0);
                                        }
                                    }
                                }
                            }
                        }

                        // 3. DESENHA POLÍGONOS (Com Preenchimento)
                        if (_mapService.VetoresPorCamada.TryGetValue(camadaAtual, out var polyPath))
                        {
                            canvas.SetMatrix(matriz);
                            float zoomReal = escalaAutoFit * _mapService.CameraZoom;
                            using var pincelBordaLOD = _mapService.PincelBorda.Clone();
                            pincelBordaLOD.StrokeWidth = 1f / zoomReal;

                            canvas.DrawPath(polyPath, _mapService.PincelFill);
                            if (zoomReal > 0.0005f) canvas.DrawPath(polyPath, pincelBordaLOD);
                        }

                        // 4. DESENHA LINHAS (Estritamente sem preenchimento)
                        if (_mapService.LinhasPorCamada.TryGetValue(camadaAtual, out var linePath))
                        {
                            canvas.SetMatrix(matriz);
                            float zoomReal = escalaAutoFit * _mapService.CameraZoom;
                            using var pincelBordaLOD = _mapService.PincelBorda.Clone();
                            pincelBordaLOD.StrokeWidth = 2f / zoomReal;

                            canvas.DrawPath(linePath, pincelBordaLOD);
                        }

                        // 5. DESENHA PONTOS
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
                    } // Fim do loop de camadas

                    // 6. CAMADA DE DESTAQUE TOPOGRÁFICA (SELEÇÃO NO TOPO ABSOLUTO)
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
                        pincelDestaqueBordaLOD.StrokeWidth = 4f / zoomReal;

                        canvas.DrawPath(_mapService.CaminhoDestaqueLinha, pincelDestaqueBordaLOD);
                    }
                    // 7. FERRAMENTAS DE MEDIÇÃO TOPOGRÁFICA
                    if (_mapService.PontosMedicao.Count > 0)
                    {
                        canvas.SetMatrix(matriz);
                        float zoomReal = escalaAutoFit * _mapService.CameraZoom;

                        using var pincelMedicaoLinha = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Cyan, StrokeWidth = 3f / zoomReal, IsAntialias = true };
                        using var pincelMedicaoPonto = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Red, IsAntialias = true };

                        var medicaoPath = new SKPath();
                        for (int j = 0; j < _mapService.PontosMedicao.Count; j++)
                        {
                            var pt = _mapService.PontosMedicao[j];
                            if (j == 0) medicaoPath.MoveTo(pt);
                            else medicaoPath.LineTo(pt);

                            // Desenha o pino de marcação vermelho
                            canvas.DrawCircle(pt, 5f / zoomReal, pincelMedicaoPonto);
                        }
                        canvas.DrawPath(medicaoPath, pincelMedicaoLinha);
                    }
                } // Fim do if !limitesTotais.IsEmpty

                canvas.Restore();
                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);

                res.ContentType = "image/jpeg";
                res.ContentLength64 = data.Size;
                data.SaveTo(res.OutputStream);
                res.OutputStream.Close();
            }
            catch
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
}