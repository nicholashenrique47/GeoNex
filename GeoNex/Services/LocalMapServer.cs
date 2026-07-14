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
        
        // === MEMÓRIA ANTI-EXPLOSÃO DE CÂMARA ===
        private static float _ultimoMidX = 0f;
        private static float _ultimoMidY = 0f;
        private static float _ultimaEscalaAutoFit = 1f;
        
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

                // --- 1. APLICANDO DPI PARA ALTA RESOLUÇÃO ---
                int cssWidth = int.Parse(req.QueryString["w"] ?? "1920");
                int cssHeight = int.Parse(req.QueryString["h"] ?? "1080");
                float dpi = float.Parse(req.QueryString["dpi"] ?? "1.0", System.Globalization.CultureInfo.InvariantCulture);
                float faseSelecao = float.Parse(req.QueryString["phase"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                int physicalWidth = (int)(cssWidth * dpi);
                int physicalHeight = (int)(cssHeight * dpi);

                var info = new SKImageInfo(physicalWidth, physicalHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info);
                var canvas = surface.Canvas;

                canvas.Scale(dpi);

                int width = cssWidth;
                int height = cssHeight;

                canvas.Clear(new SKColor(22, 25, 33));
                canvas.Save();

                // 2. LIMITES TOTAIS
                SKRect limitesTotais = new SKRect();
                bool primeiro = true;

                if (_mapService.TemRaster) { limitesTotais = _mapService.LimitesRasterGlobal; primeiro = false; }

                foreach (var path in _mapService.VetoresPorCamada.Values) { if (primeiro) { limitesTotais = path.Bounds; primeiro = false; } else { limitesTotais.Union(path.Bounds); } }
                foreach (var path in _mapService.LinhasPorCamada.Values) { if (primeiro) { limitesTotais = path.Bounds; primeiro = false; } else { limitesTotais.Union(path.Bounds); } }
                foreach (var path in _mapService.PontosPorCamada.Values) { if (primeiro) { limitesTotais = path.Bounds; primeiro = false; } else { limitesTotais.Union(path.Bounds); } }

                float escalaAutoFit; float midX, midY;

                // === CORREÇÃO BUG 3: FALLBACK ANTI-EXPLOSÃO ===
                // Removemos o "if (!limitesTotais.IsEmpty) {" que trancava o mapa inteiro
                if (limitesTotais.IsEmpty || limitesTotais.Width < 0.001f || limitesTotais.Height < 0.001f)
                {
                    escalaAutoFit = _ultimaEscalaAutoFit > 0 ? _ultimaEscalaAutoFit : 1f;
                    midX = _ultimoMidX; midY = _ultimoMidY;
                }
                else
                {
                    float escalaX = width / limitesTotais.Width;
                    float escalaY = height / limitesTotais.Height;
                    escalaAutoFit = Math.Min(escalaX, escalaY) * 0.8f;
                    midX = limitesTotais.MidX; midY = limitesTotais.MidY;

                    _ultimaEscalaAutoFit = escalaAutoFit;
                    _ultimoMidX = midX; _ultimoMidY = midY;
                }

                float zoomReal = escalaAutoFit * _mapService.CameraZoom;

                var matriz = SKMatrix.CreateTranslation(-midX, -midY);
                matriz = matriz.PostConcat(SKMatrix.CreateScale(zoomReal, zoomReal));
                matriz = matriz.PostConcat(SKMatrix.CreateTranslation(width / 2f + _mapService.CameraPanX, height / 2f + _mapService.CameraPanY));

                // === CORREÇÃO BUG 1 (Parte A): ESCALA DE DPI NA MATRIZ GLOBAL ===
                matriz = matriz.PostConcat(SKMatrix.CreateScale(dpi, dpi));

                // CALCULA O VIEWPORT UMA ÚNICA VEZ
                SKRect viewportMundo = new SKRect();
                if (matriz.TryInvert(out SKMatrix matrizInversaMundo))
                {
                    SKPoint c1 = matrizInversaMundo.MapPoint(new SKPoint(0, 0));
                    SKPoint c2 = matrizInversaMundo.MapPoint(new SKPoint(physicalWidth, physicalHeight)); // Usa pixeis físicos por causa do DPI

                    viewportMundo = new SKRect(
                        Math.Min(c1.X, c2.X), Math.Min(c1.Y, c2.Y),
                        Math.Max(c1.X, c2.X), Math.Max(c1.Y, c2.Y)
                    );
                }

                // CRIA OS PINCÉIS UMA ÚNICA VEZ ANTES DO LOOP
                using var pincelBordaLOD = _mapService.PincelBorda.Clone();
                using var pincelPonto = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Cyan, IsAntialias = true };
                using var pincelDinamicoFill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
                using var pincelDinamicoBorda = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round };
                using var pincelDinamicoPonto = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
                
                // 3. MOTOR HIERÁRQUICO DE Z-INDEX
                for (int i = 0; i < _mapService.OrdemCamadas.Count; i++)
                {
                    string camadaAtual = _mapService.OrdemCamadas[i];

                    // RASTER
                    lock (_mapService)
                    {
                        if (_mapService.TemRaster && camadaAtual == _mapService.NomeRasterAtivo && _mapService.DatasetRaster != null && matriz.TryInvert(out SKMatrix inverse))
                        {
                            canvas.ResetMatrix();
                            // === CORREÇÃO BUG 1 (Parte B): ALINHAR O DPI DO RASTER ===
                            canvas.Scale(dpi);

                            string targetCacheKey = $"{width}_{height}_{_mapService.CameraPanX}_{_mapService.CameraPanY}_{_mapService.CameraZoom}_{_mapService.NomeRasterAtivo}_{midX}_{midY}_{escalaAutoFit}";

                            if (_mapService.RasterCache != null && _mapService.CacheKey == targetCacheKey)
                            {
                                canvas.DrawBitmap(_mapService.RasterCache, 0, 0);
                            }
                            else if (_mapService.IsPanning && _mapService.RasterCache != null)
                            {
                                float offsetX = (float)_mapService.CameraPanX - _mapService.CachePanX;
                                float offsetY = (float)_mapService.CameraPanY - _mapService.CachePanY;
                                canvas.DrawBitmap(_mapService.RasterCache, offsetX, offsetY);
                            }
                            else
                            {
                                SKPoint topLeft = inverse.MapPoint(new SKPoint(0, 0));
                                SKPoint bottomRight = inverse.MapPoint(new SKPoint(physicalWidth, physicalHeight));

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

                    // --- VETORES (Simbologia Dinâmica e Categorizada) ---
                    if (!viewportMundo.IsEmpty)
                    {
                        if (!_mapService.EstilosPorCamada.TryGetValue(camadaAtual, out var estiloCamada))
                        {
                            estiloCamada = new GeoNex.Services.EstiloCamada();
                        }

                        byte alphaCalculado = (byte)(estiloCamada.Opacidade * 255);

                        SKColor corBorda = estiloCamada.CorBorda == "transparent" ? SKColors.Transparent : SKColor.Parse(estiloCamada.CorBorda);
                        SKColor corFill = estiloCamada.CorPreenchimento == "transparent" ? SKColors.Transparent : SKColor.Parse(estiloCamada.CorPreenchimento);

                        pincelDinamicoFill.Color = corFill == SKColors.Transparent ? SKColors.Transparent : corFill.WithAlpha(alphaCalculado);
                        pincelDinamicoBorda.Color = corBorda;
                        pincelDinamicoBorda.StrokeWidth = estiloCamada.EspessuraBorda / zoomReal;
                        pincelDinamicoPonto.Color = corFill == SKColors.Transparent ? SKColors.Transparent : corFill;

                        // 4. DESENHA POLÍGONOS
                        if (estiloCamada.TipoSimbologia == "UNICA")
                        {
                            if (_mapService.VetoresPorCamada.TryGetValue(camadaAtual, out var polyPath))
                            {
                                if (viewportMundo.IntersectsWith(polyPath.Bounds))
                                {
                                    canvas.SetMatrix(matriz);

                                    if (!estiloCamada.PreenchimentoTransparente)
                                        canvas.DrawPath(polyPath, pincelDinamicoFill);

                                    if (!estiloCamada.BordaTransparente && zoomReal > 0.0005f)
                                        canvas.DrawPath(polyPath, pincelDinamicoBorda);
                                }
                            }
                        }
                        else if (estiloCamada.TipoSimbologia == "CATEGORIZADA")
                        {
                            if (_mapService.VetoresCategorizados.TryGetValue(camadaAtual, out var fragmentosDaCamada))
                            {
                                foreach (var categoria in fragmentosDaCamada)
                                {
                                    string nomeCategoria = categoria.Key;
                                    var polyPathFragmento = categoria.Value;

                                    if (viewportMundo.IntersectsWith(polyPathFragmento.Bounds))
                                    {
                                        string corHex = estiloCamada.CoresCategorizadas.ContainsKey(nomeCategoria)
                                            ? estiloCamada.CoresCategorizadas[nomeCategoria]
                                            : "#808080";

                                        SKColor corBase = SKColor.Parse(corHex);
                                        pincelDinamicoFill.Color = corBase.WithAlpha(alphaCalculado);

                                        canvas.SetMatrix(matriz);

                                        if (!estiloCamada.PreenchimentoTransparente)
                                            canvas.DrawPath(polyPathFragmento, pincelDinamicoFill);

                                        if (!estiloCamada.BordaTransparente && zoomReal > 0.0005f)
                                            canvas.DrawPath(polyPathFragmento, pincelDinamicoBorda);
                                    }
                                }
                            }
                        }

                        // 5. DESENHA LINHAS (Redes, arruamentos)
                        if (_mapService.LinhasPorCamada.TryGetValue(camadaAtual, out var linePath))
                        {
                            if (viewportMundo.IntersectsWith(linePath.Bounds))
                            {
                                canvas.SetMatrix(matriz);
                                if (!estiloCamada.BordaTransparente)
                                    canvas.DrawPath(linePath, pincelDinamicoBorda);
                            }
                        }

                        // 6. DESENHA PONTOS (Postes, árvores)
                        if (_mapService.PontosPorCamada.TryGetValue(camadaAtual, out var pointPath))
                        {
                            if (viewportMundo.IntersectsWith(pointPath.Bounds))
                            {
                                canvas.SetMatrix(matriz);
                                if (!estiloCamada.PreenchimentoTransparente)
                                    canvas.DrawPath(pointPath, pincelDinamicoPonto);
                                if (!estiloCamada.BordaTransparente)
                                    canvas.DrawPath(pointPath, pincelDinamicoBorda);
                            }
                        }
                        
                        // 7. MOTOR DE RÓTULOS DINÂMICOS
                        if (estiloCamada.ExibirRotulos && !string.IsNullOrEmpty(estiloCamada.ColunaRotulo))
                        {
                            if (!_mapService.PontosAncoragemRotulo.TryGetValue(camadaAtual, out var ancoras) || ancoras.Count == 0)
                            {
                                ancoras = new();
                                if (_mapService.FeicoesOriginais.TryGetValue(camadaAtual, out var feicoes))
                                {
                                    foreach (var feicao in feicoes)
                                    {
                                        if (feicao.Geometry != null && !feicao.Geometry.IsEmpty)
                                        {
                                            double px = 0, py = 0;
                                            try
                                            {
                                                var ptInt = feicao.Geometry.InteriorPoint;
                                                if (ptInt != null && !ptInt.IsEmpty) { px = ptInt.X; py = ptInt.Y; }
                                                else { px = feicao.Geometry.EnvelopeInternal.Centre.X; py = feicao.Geometry.EnvelopeInternal.Centre.Y; }
                                            }
                                            catch { px = feicao.Geometry.EnvelopeInternal.Centre.X; py = feicao.Geometry.EnvelopeInternal.Centre.Y; }

                                            float cx = (float)(px - _mapService.OffsetMundoX);
                                            float cy = -(float)(py - _mapService.OffsetMundoY);

                                            float larguraMundo = (float)feicao.Geometry.EnvelopeInternal.Width;
                                            if (larguraMundo == 0) larguraMundo = 999999f;

                                            ancoras.Add((new SKPoint(cx, cy), feicao.Attributes, larguraMundo));
                                        }
                                    }
                                }
                                _mapService.PontosAncoragemRotulo[camadaAtual] = ancoras;
                            }

                            if (ancoras.Count > 0)
                            {
                                using var fonteTypeface = estiloCamada.RotuloNegrito
                                    ? SKTypeface.FromFamilyName(null, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                                    : SKTypeface.Default;

                                using var pincelTexto = new SKPaint
                                {
                                    Typeface = fonteTypeface,
                                    TextSize = estiloCamada.TamanhoTextoRotulo,
                                    IsAntialias = true,
                                    Color = SKColor.Parse(estiloCamada.CorTextoRotulo),
                                    TextAlign = SKTextAlign.Center
                                };

                                using var pincelHalo = new SKPaint
                                {
                                    Typeface = fonteTypeface,
                                    TextSize = estiloCamada.TamanhoTextoRotulo,
                                    IsAntialias = true,
                                    Color = SKColor.Parse(estiloCamada.CorHaloRotulo),
                                    TextAlign = SKTextAlign.Center,
                                    Style = SKPaintStyle.Stroke,
                                    StrokeWidth = estiloCamada.TamanhoHaloRotulo,
                                    StrokeJoin = SKStrokeJoin.Round
                                };

                                float ajusteY = (pincelTexto.FontMetrics.Descent - pincelTexto.FontMetrics.Ascent) / 2f - pincelTexto.FontMetrics.Descent;

                                canvas.SetMatrix(SKMatrix.CreateScale(dpi, dpi));

                                foreach (var ancora in ancoras)
                                {
                                    float larguraNoEcra = ancora.LarguraMundo * zoomReal;

                                    if (larguraNoEcra < 35f) continue;

                                    if (viewportMundo.Contains(ancora.Ponto))
                                    {
                                        if (ancora.Atributos != null && ancora.Atributos.Exists(estiloCamada.ColunaRotulo))
                                        {
                                            var valorObjeto = ancora.Atributos[estiloCamada.ColunaRotulo];
                                            if (valorObjeto != null)
                                            {
                                                string texto = valorObjeto.ToString();
                                                SKPoint pontoMonitor = matriz.MapPoint(ancora.Ponto);

                                                if (estiloCamada.TamanhoHaloRotulo > 0)
                                                {
                                                    canvas.DrawText(texto, pontoMonitor.X, pontoMonitor.Y + ajusteY, pincelHalo);
                                                }
                                                canvas.DrawText(texto, pontoMonitor.X, pontoMonitor.Y + ajusteY, pincelTexto);
                                            }
                                        }
                                    }
                                }
                                canvas.SetMatrix(matriz);
                            }
                        }
                    }
                } // Fim do loop de camadas

                // 8. FERRAMENTA DE MEDIÇÃO
                if (_mapService.PontosMedicao.Count > 0 || _mapService.PontoCursorMundo.HasValue)
                {
                    canvas.SetMatrix(matriz);
                    using var pincelLinha = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Cyan, StrokeWidth = 2.5f / zoomReal, IsAntialias = true };
                    using var pincelLinhaTracejada = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White.WithAlpha(180), StrokeWidth = 1.5f / zoomReal, PathEffect = SKPathEffect.CreateDash(new float[] { 10f / zoomReal, 10f / zoomReal }, 0), IsAntialias = true };
                    using var pincelPontoMed = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
                    using var pincelBordaPontoMed = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Cyan, StrokeWidth = 1.5f / zoomReal, IsAntialias = true };
                    using var pincelArea = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Cyan.WithAlpha(40), IsAntialias = true };

                    var pathMedicao = new SKPath();
                    for (int i = 0; i < _mapService.PontosMedicao.Count; i++)
                    {
                        if (i == 0) pathMedicao.MoveTo(_mapService.PontosMedicao[i]);
                        else pathMedicao.LineTo(_mapService.PontosMedicao[i]);
                    }

                    if (_mapService.MostrarAreaMedicao && _mapService.PontosMedicao.Count > 2)
                    {
                        var pathArea = new SKPath(pathMedicao);
                        pathArea.Close();
                        canvas.DrawPath(pathArea, pincelArea);
                        canvas.DrawLine(_mapService.PontosMedicao[^1], _mapService.PontosMedicao[0], pincelLinhaTracejada);
                    }

                    if (_mapService.PontosMedicao.Count > 0) canvas.DrawPath(pathMedicao, pincelLinha);
                    if (_mapService.PontosMedicao.Count > 0 && _mapService.PontoCursorMundo.HasValue)
                    {
                        canvas.DrawLine(_mapService.PontosMedicao[^1], _mapService.PontoCursorMundo.Value, pincelLinhaTracejada);
                    }

                    foreach (var pt in _mapService.PontosMedicao)
                    {
                        canvas.DrawCircle(pt, 4.5f / zoomReal, pincelPontoMed);
                        canvas.DrawCircle(pt, 4.5f / zoomReal, pincelBordaPontoMed);
                    }
                }
                // 8.1. FERRAMENTA DE AQUISIÇÃO (DESENHO DE LOTE)
                if (_mapService.PontosAquisicao.Count > 0)
                {
                    canvas.SetMatrix(matriz);

                    // ==========================================================
                    // >>> ANEL VISUAL DE RESTRIÇÃO (COMPASSO AZUL) <<<
                    // ==========================================================
                    // ==========================================================
                    // >>> ANEL VISUAL DE RESTRIÇÃO (COMPASSO AZUL) <<<
                    // ==========================================================
                    // ==========================================================
                    // >>> ANEL VISUAL DE RESTRIÇÃO (COMPASSO DE ALTO CONTRASTE) <<<
                    // ==========================================================
                    if (_mapService.TravaDistanciaAtiva && _mapService.TravaDistanciaValor > 0)
                    {
                        var ultimoPonto = _mapService.PontosAquisicao[^1];

                        // 1. O HALO PRETO (Fundo para garantir contraste em telhados brancos/ortofotos claras)
                        using var paintAnelFundo = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            Color = SKColors.Black.WithAlpha(180), // Preto meio transparente
                            StrokeWidth = 4.0f / zoomReal,         // Mais grosso que a linha cyan
                            IsAntialias = true
                        };

                        // 2. A LINHA CYAN PRINCIPAL (Agora 100% sólida e ligeiramente mais grossa)
                        using var paintAnelGuia = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            Color = SKColors.Cyan,                 // Removido o alpha, agora brilha a 100%
                            StrokeWidth = 2.0f / zoomReal,         // Aumentado de 1.5 para 2.0
                            IsAntialias = true,
                            PathEffect = SKPathEffect.CreateDash(new float[] { 8f / zoomReal, 8f / zoomReal }, 0) // Traços maiores
                        };

                        // Desenha primeiro o fundo preto e depois o tracejado cyan por cima
                        canvas.DrawCircle(ultimoPonto.X, ultimoPonto.Y, (float)_mapService.TravaDistanciaValor, paintAnelFundo);
                        canvas.DrawCircle(ultimoPonto.X, ultimoPonto.Y, (float)_mapService.TravaDistanciaValor, paintAnelGuia);
                    }
                    // ==========================================================
                    // ==========================================================
                    // ==========================================================
                    // ==========================================================

                    // Estética Profissional para o modo de Desenho (Verde Primavera)
                    using var pincelLinhaAq = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.SpringGreen, StrokeWidth = 2.5f / zoomReal, IsAntialias = true };
                    using var pincelTracejadoAq = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.SpringGreen.WithAlpha(180), StrokeWidth = 1.5f / zoomReal, PathEffect = SKPathEffect.CreateDash(new float[] { 10f / zoomReal, 10f / zoomReal }, 0), IsAntialias = true };
                    using var pincelPontoAq = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
                    using var pincelBordaPontoAq = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.SpringGreen, StrokeWidth = 1.5f / zoomReal, IsAntialias = true };
                    using var pincelAreaAq = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.SpringGreen.WithAlpha(60), IsAntialias = true };

                    var pathAq = new SKPath();
                    for (int i = 0; i < _mapService.PontosAquisicao.Count; i++)
                    {
                        if (i == 0) pathAq.MoveTo(_mapService.PontosAquisicao[i]);
                        else pathAq.LineTo(_mapService.PontosAquisicao[i]);
                    }

                    // ==========================================================
                    // >>> UPGRADE PROFISSIONAL: CROSSHAIR E HUD DINÂMICO <<<
                    // ==========================================================
                    if (_mapService.PontoCursorMundo.HasValue)
                    {
                        var cursorPts = _mapService.PontoCursorMundo.Value;

                        // 1. MIRA ORTOGONAL (CROSSHAIR ESTILO AUTOCAD)
                        using var paintMira = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White.WithAlpha(100), StrokeWidth = 1f / zoomReal, IsAntialias = false };
                        // Linha Horizontal infinita
                        canvas.DrawLine(viewportMundo.Left, cursorPts.Y, viewportMundo.Right, cursorPts.Y, paintMira);
                        // Linha Vertical infinita
                        canvas.DrawLine(cursorPts.X, viewportMundo.Top, cursorPts.X, viewportMundo.Bottom, paintMira);

                        // 2. HUD DINÂMICO NO CURSOR (LIVE TOOLTIP)
                        if (_mapService.PontosAquisicao.Count > 0)
                        {
                            var ultimoPt = _mapService.PontosAquisicao.Last();

                            // Calcula Distância e Azimute Real
                            double dx = cursorPts.X - ultimoPt.X;
                            double dy = cursorPts.Y - ultimoPt.Y;
                            double distanciaReal = Math.Sqrt(dx * dx + dy * dy);

                            // Calcula o Azimute Geográfico (Norte = 0º, sentido horário)
                            double azimuteRad = Math.Atan2(dx, dy);
                            double azimuteDeg = azimuteRad * (180.0 / Math.PI);
                            if (azimuteDeg < 0) azimuteDeg += 360;

                            string textoHud = $"D: {distanciaReal:F2}m  |  Az: {azimuteDeg:F1}°";

                            // Estilo do Texto
                            using var paintTextoHud = new SKPaint { Typeface = SKTypeface.Default, TextSize = 12f / zoomReal, Color = SKColors.White, IsAntialias = true };

                            // Mede o tamanho do texto para criar a caixa de fundo
                            var rectTexto = new SKRect();
                            paintTextoHud.MeasureText(textoHud, ref rectTexto);

                            // Define a posição da caixa flutuante (25px para a direita e para baixo do rato)
                            float offsetCaixa = 25f / zoomReal;
                            float padding = 6f / zoomReal;
                            var caixaFundo = new SKRect(
                                cursorPts.X + offsetCaixa,
                                cursorPts.Y + offsetCaixa,
                                cursorPts.X + offsetCaixa + rectTexto.Width + (padding * 2),
                                cursorPts.Y + offsetCaixa + rectTexto.Height + (padding * 2)
                            );

                            // Desenha o Fundo Translúcido (Glassmorphism)
                            using var paintFundoHud = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Black.WithAlpha(180), IsAntialias = true };
                            using var paintBordaHud = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Cyan.WithAlpha(150), StrokeWidth = 1f / zoomReal, IsAntialias = true };

                            canvas.DrawRoundRect(caixaFundo, 4f / zoomReal, 4f / zoomReal, paintFundoHud);
                            canvas.DrawRoundRect(caixaFundo, 4f / zoomReal, 4f / zoomReal, paintBordaHud);

                            // Escreve o texto dentro da caixa
                            canvas.DrawText(textoHud, caixaFundo.Left + padding, caixaFundo.Bottom - padding, paintTextoHud);
                        }
                    }
                    // ==========================================================

                    // Desenha os vértices (bolinhas brancas com borda verde) por cima de tudo
                    // ==========================================================
                    // >>> RENDERIZAÇÃO FINAL (Área, Esqueleto e Vértices) <<<
                    // ==========================================================

                    if (_mapService.PontosAquisicao.Count >= 2 && _mapService.PontoCursorMundo.HasValue)
                    {
                        var pathAreaAq = new SKPath(pathAq);
                        pathAreaAq.Close();
                        canvas.DrawPath(pathAreaAq, pincelAreaAq);
                    }

                    canvas.DrawPath(pathAq, pincelLinhaAq);

                    for (int i = 0; i < _mapService.PontosAquisicao.Count; i++)
                    {
                        var pt = _mapService.PontosAquisicao[i];

                        if (i == 0) // PONTO DE ORIGEM LARANJA LIMPO
                        {
                            using var pincelOrigemFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Orange, IsAntialias = true };
                            using var pincelOrigemBorda = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White, StrokeWidth = 2f / zoomReal, IsAntialias = true };

                            canvas.DrawCircle(pt, 5.5f / zoomReal, pincelOrigemFill);
                            canvas.DrawCircle(pt, 5.5f / zoomReal, pincelOrigemBorda);
                        }
                        else // RESTANTES VÉRTICES
                        {
                            canvas.DrawCircle(pt, 4.5f / zoomReal, pincelPontoAq);
                            canvas.DrawCircle(pt, 4.5f / zoomReal, pincelBordaPontoAq);
                        }
                    }
                } // Fim do bloco if (_mapService.PontosAquisicao.Count > 0)

                // 9. SNAP HOVER MAGNÉTICO
                if (_mapService.PontoCursorSnap.HasValue)
                {
                    canvas.SetMatrix(matriz);
                    var snapPt = _mapService.PontoCursorSnap.Value;
                    using var pincelSnap = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Yellow, StrokeWidth = 2.0f / zoomReal, IsAntialias = true };
                    using var pincelSnapFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Yellow.WithAlpha(80), IsAntialias = true };
                    float size = 14f / zoomReal;
                    var rect = new SKRect(snapPt.X - size / 2, snapPt.Y - size / 2, snapPt.X + size / 2, snapPt.Y + size / 2);
                    canvas.DrawRect(rect, pincelSnapFill);
                    canvas.DrawRect(rect, pincelSnap);
                    canvas.DrawLine(snapPt.X - size, snapPt.Y, snapPt.X + size, snapPt.Y, pincelSnap);
                    canvas.DrawLine(snapPt.X, snapPt.Y - size, snapPt.X, snapPt.Y + size, pincelSnap);
                }
                // =========================================================================
                // 10. CONTORNO ANIMADO DA FEIÇÃO SELECIONADA (MARCHING ANTS)
                // =========================================================================
                // Nota: Substitua '_mapService.CaminhoFeicaoDestacada' pela propriedade correspondente 
                // do seu MapService que armazena o SKPath do lote selecionado atualmente.
                
                // === NOTA: APAGADA A CHAVE DE FECHO DO IF ANTIGO ===

                canvas.Restore();
                using var image = surface.Snapshot();

                using var data = image.Encode(SKEncodedImageFormat.Webp, 85);
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