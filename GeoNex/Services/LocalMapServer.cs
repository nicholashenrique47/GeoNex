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

                                // A CHAVE BLINDADA: Agora o cache invalida sozinho se o tamanho do mapa (limitesTotais) ou a escala mudarem!
                                string targetCacheKey = $"{width}_{height}_{_mapService.CameraPanX}_{_mapService.CameraPanY}_{_mapService.CameraZoom}_{_mapService.NomeRasterAtivo}_{limitesTotais.MidX}_{limitesTotais.MidY}_{escalaAutoFit}";

                                // ESTÁGIO 1: CÂMARA PARADA NO SÍTIO CERTO -> Usa o Cache perfeitamente alinhado
                                if (_mapService.RasterCache != null && _mapService.CacheKey == targetCacheKey)
                                {
                                    canvas.DrawBitmap(_mapService.RasterCache, 0, 0);
                                }
                                // ESTÁGIO 2: ARRASTANDO O RATO (PAN) -> Move a foto que já está na memória sem usar CPU!
                                else if (_mapService.IsPanning && _mapService.RasterCache != null)
                                {
                                    float offsetX = (float)_mapService.CameraPanX - _mapService.CachePanX;
                                    float offsetY = (float)_mapService.CameraPanY - _mapService.CachePanY;

                                    // Desenha a ortofoto deslizando em perfeita sincronia com os vetores
                                    canvas.DrawBitmap(_mapService.RasterCache, offsetX, offsetY);
                                }
                                // ESTÁGIO 3: LARGOU O RATO OU DEU ZOOM -> Reprocessa via GDAL
                                else
                                {
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
                                        "-r", "bilinear", "-dstalpha", "-wm", "2048", "-multi", "-wo", "NUM_THREADS=ALL_CPUS", "-of", "MEM"
                                    };

                                    using GDALWarpAppOptions warpOptions = new GDALWarpAppOptions(warpArgs);
                                    using Dataset memDs = Gdal.Warp("", new[] { _mapService.DatasetRaster }, warpOptions, null, null);

                                    if (memDs != null && memDs.RasterCount > 0)
                                    {
                                        int numBandas = Math.Min(memDs.RasterCount, 4);
                                        int[] listaBandas = new int[numBandas];
                                        for (int b = 0; b < numBandas; b++) listaBandas[b] = b + 1;

                                        using var rasterBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                                        using (var tmpCanvas = new SKCanvas(rasterBitmap)) { tmpCanvas.Clear(new SKColor(0, 0, 0, 0)); }

                                        IntPtr ptr = rasterBitmap.GetPixels();
                                        if (ptr != IntPtr.Zero)
                                        {
                                            memDs.ReadRaster(0, 0, width, height, ptr, width, height, DataType.GDT_Byte, numBandas, listaBandas, 4, width * 4, 1);

                                            // Atualiza a memória VRAM e grava de onde a foto tirada
                                            _mapService.RasterCache?.Dispose();
                                            _mapService.RasterCache = rasterBitmap.Copy();
                                            _mapService.CacheKey = targetCacheKey;
                                            _mapService.CachePanX = (float)_mapService.CameraPanX;
                                            _mapService.CachePanY = (float)_mapService.CameraPanY;

                                            canvas.DrawBitmap(rasterBitmap, 0, 0);
                                        }
                                    }
                                }
                            }
                        }

                        // Determinar a caixa envolvente visível no espaço do mundo (Viewport real)
                        // Determinar a caixa envolvente visível no espaço do mundo (Viewport real)
                        if (matriz.TryInvert(out SKMatrix matrizInversaMundo))
                        {
                            SKPoint cantoSuperiorEsquerdo = matrizInversaMundo.MapPoint(new SKPoint(0, 0));
                            SKPoint cantoInferiorDireito = matrizInversaMundo.MapPoint(new SKPoint(width, height));

                            // Criar o retângulo de corte baseado na visualização atual do operador
                            SKRect viewportMundo = new SKRect(
                                Math.Min(cantoSuperiorEsquerdo.X, cantoInferiorDireito.X),
                                Math.Min(cantoSuperiorEsquerdo.Y, cantoInferiorDireito.Y),
                                Math.Max(cantoSuperiorEsquerdo.X, cantoInferiorDireito.X),
                                Math.Max(cantoSuperiorEsquerdo.Y, cantoInferiorDireito.Y)
                            );

                            float zoomReal = escalaAutoFit * _mapService.CameraZoom;

                            // === PASSO 2: OTIMIZAÇÃO DE MEMÓRIA (GARBAGE COLLECTION) ===
                            // Instanciamos os pincéis APENAS UMA VEZ aqui fora, e reutilizamo-los lá dentro!
                            using var pincelBordaLOD = _mapService.PincelBorda.Clone();
                            using var pincelPonto = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Cyan, IsAntialias = true };
                            // ==========================================================

                            // 3. DESENHA POLÍGONOS (Com Descarte Espacial Inteligente)
                            // Determinar a caixa envolvente visível no espaço do mundo (Viewport real)
                            if (matriz.TryInvert(out SKMatrix matrizInversaMundo))
                            {
                                SKPoint cantoSuperiorEsquerdo = matrizInversaMundo.MapPoint(new SKPoint(0, 0));
                                SKPoint cantoInferiorDireito = matrizInversaMundo.MapPoint(new SKPoint(width, height));

                                // Criar o retângulo de corte baseado na visualização atual do operador
                                SKRect viewportMundo = new SKRect(
                                    Math.Min(cantoSuperiorEsquerdo.X, cantoInferiorDireito.X),
                                    Math.Min(cantoSuperiorEsquerdo.Y, cantoInferiorDireito.Y),
                                    Math.Max(cantoSuperiorEsquerdo.X, cantoInferiorDireito.X),
                                    Math.Max(cantoSuperiorEsquerdo.Y, cantoInferiorDireito.Y)
                                );

                                float zoomReal = escalaAutoFit * _mapService.CameraZoom;

                                // === PASSO 2: OTIMIZAÇÃO DE MEMÓRIA (GARBAGE COLLECTION) ===
                                // Instanciamos os pincéis APENAS UMA VEZ aqui fora, e reutilizamo-los lá dentro!
                                using var pincelBordaLOD = _mapService.PincelBorda.Clone();
                                using var pincelPonto = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Cyan, IsAntialias = true };
                                // ==========================================================

                                // 3. DESENHA POLÍGONOS (Com Descarte Espacial Inteligente)
                                if (_mapService.VetoresPorCamada.TryGetValue(camadaAtual, out var polyPath))
                                {
                                    if (viewportMundo.IntersectsWith(polyPath.Bounds))
                                    {
                                        canvas.SetMatrix(matriz);
                                        pincelBordaLOD.StrokeWidth = 1f / zoomReal;
                                        canvas.DrawPath(polyPath, _mapService.PincelFill);
                                        if (zoomReal > 0.0005f) canvas.DrawPath(polyPath, pincelBordaLOD);
                                    }
                                }

                                // 4. DESENHA LINHAS (Com Descarte Espacial Inteligente)
                                if (_mapService.LinhasPorCamada.TryGetValue(camadaAtual, out var linePath))
                                {
                                    if (viewportMundo.IntersectsWith(linePath.Bounds))
                                    {
                                        canvas.SetMatrix(matriz);
                                        pincelBordaLOD.StrokeWidth = 2f / zoomReal;
                                        canvas.DrawPath(linePath, pincelBordaLOD);
                                    }
                                }

                                // 5. DESENHA PONTOS
                                if (_mapService.PontosPorCamada.TryGetValue(camadaAtual, out var pointPath))
                                {
                                    if (viewportMundo.IntersectsWith(pointPath.Bounds))
                                    {
                                        canvas.SetMatrix(matriz);
                                        pincelBordaLOD.StrokeWidth = 1f / zoomReal;
                                        canvas.DrawPath(pointPath, pincelPonto);
                                        canvas.DrawPath(pointPath, pincelBordaLOD);
                                    }
                                }
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
                    // === DESENHO DA FERRAMENTA DE MEDIÇÃO (LIMPA) ===

                    // === DESENHO DO INDICADOR DE SNAP (HOVER MAGNÉTICO TIPO ARCGIS) ===
                    // === DESENHO DA FERRAMENTA DE MEDIÇÃO (COM LINHA ELÁSTICA) ===
                    if (_mapService.PontosMedicao.Count > 0 || _mapService.PontoCursorMundo.HasValue)
                    {
                        canvas.SetMatrix(matriz);
                        float zoomReal = escalaAutoFit * _mapService.CameraZoom;

                        using var pincelLinha = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Cyan, StrokeWidth = 2.5f / zoomReal, IsAntialias = true };
                        using var pincelLinhaTracejada = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            Color = SKColors.White.WithAlpha(180),
                            StrokeWidth = 1.5f / zoomReal,
                            PathEffect = SKPathEffect.CreateDash(new float[] { 10f / zoomReal, 10f / zoomReal }, 0),
                            IsAntialias = true
                        };
                        using var pincelPonto = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
                        using var pincelBordaPonto = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Cyan, StrokeWidth = 1.5f / zoomReal, IsAntialias = true };
                        using var pincelArea = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Cyan.WithAlpha(40), IsAntialias = true };

                        var pathMedicao = new SKPath();
                        for (int i = 0; i < _mapService.PontosMedicao.Count; i++)
                        {
                            var pt = _mapService.PontosMedicao[i];
                            if (i == 0) pathMedicao.MoveTo(pt);
                            else pathMedicao.LineTo(pt);
                        }

                        // 1. DESENHA A ÁREA FECHADA E LINHA TRACEJADA DE FECHO
                        if (_mapService.MostrarAreaMedicao && _mapService.PontosMedicao.Count > 2)
                        {
                            var pathArea = new SKPath(pathMedicao);
                            pathArea.Close();
                            canvas.DrawPath(pathArea, pincelArea);
                            var pPrimeiro = _mapService.PontosMedicao[0];
                            var pUltimo = _mapService.PontosMedicao[_mapService.PontosMedicao.Count - 1];
                            canvas.DrawLine(pUltimo, pPrimeiro, pincelLinhaTracejada);
                        }

                        // 2. DESENHA A LINHA PRINCIPAL CONSOLIDADA
                        if (_mapService.PontosMedicao.Count > 0)
                        {
                            canvas.DrawPath(pathMedicao, pincelLinha);
                        }

                        // 3. A LINHA ELÁSTICA (RUBBERBAND) AO VIVO A SEGUIR O RATO!
                        // 3. A LINHA ELÁSTICA (RUBBERBAND) AO VIVO A SEGUIR O RATO!
                        if (_mapService.PontosMedicao.Count > 0 && _mapService.PontoCursorMundo.HasValue)
                        {
                            var pUltimo = _mapService.PontosMedicao[_mapService.PontosMedicao.Count - 1];
                            var pMouse = _mapService.PontoCursorMundo.Value;
                            canvas.DrawLine(pUltimo, pMouse, pincelLinhaTracejada);
                        }

                        // 4. DESENHA OS PINOS TOPOGRÁFICOS
                        for (int i = 0; i < _mapService.PontosMedicao.Count; i++)
                        {
                            var pt = _mapService.PontosMedicao[i];
                            canvas.DrawCircle(pt, 4.5f / zoomReal, pincelPonto);
                            canvas.DrawCircle(pt, 4.5f / zoomReal, pincelBordaPonto);
                        }
                    }

                    // === DESENHO DO INDICADOR DE SNAP (HOVER MAGNÉTICO TIPO ARCGIS) ===
                    if (_mapService.PontoCursorSnap.HasValue)
                    {
                        canvas.SetMatrix(matriz);
                        float zoomReal = escalaAutoFit * _mapService.CameraZoom;
                        var snapPt = _mapService.PontoCursorSnap.Value;

                        using var pincelSnap = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Yellow, StrokeWidth = 2.0f / zoomReal, IsAntialias = true };
                        using var pincelSnapFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Yellow.WithAlpha(80), IsAntialias = true };

                        float size = 14f / zoomReal;

                        // Caixa alvo do Snap e Mira
                        var rect = new SKRect(snapPt.X - size / 2, snapPt.Y - size / 2, snapPt.X + size / 2, snapPt.Y + size / 2);
                        canvas.DrawRect(rect, pincelSnapFill);
                        canvas.DrawRect(rect, pincelSnap);
                        canvas.DrawLine(snapPt.X - size, snapPt.Y, snapPt.X + size, snapPt.Y, pincelSnap);
                        canvas.DrawLine(snapPt.X, snapPt.Y - size, snapPt.X, snapPt.Y + size, pincelSnap);
                    }
                } // Fim do if !limitesTotais.IsEmpty

                canvas.Restore();
                using var image = surface.Snapshot();

                // O WebP em modo Lossy (Qualidade 75-80) entrega uma taxa de compressão
                // superior ao JPEG com metade do tempo de processamento de codificação.
                using var data = image.Encode(SKEncodedImageFormat.Webp, 80);

                res.ContentType = "image/webp";
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