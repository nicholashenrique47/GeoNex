using SkiaSharp;

namespace GeoNex.Services;

public partial class LocalMapServer
{
    // Base layers alone enter the scene cache. Paint tools on every response.
    private void DrawInteractionOverlays(SKCanvas canvas, SKMatrix matriz, SKRect viewportMundo,
        float zoomReal, List<SKPoint> ptsMedicao, List<SKPoint> ptsAquisicao)
    {
        using var restore = new SKAutoCanvasRestore(canvas);
        bool clientRenderedDigitizingPreview = _mapService.ClientRenderedDigitizingPreview;
        // The browser canvas owns this marker while client preview is active.
        // Drawing it here too used an older camera matrix and produced a second,
        // offset crosshair over the correctly anchored coordinate.
        if (!clientRenderedDigitizingPreview && _mapService.PontoRestricaoAbsoluta is { } lockedPoint)
        {
            canvas.SetMatrix(matriz);
            float screenUnit = 1f / Math.Max(zoomReal, 0.0001f);
            using var guides = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(89, 197, 210, 82),
                StrokeWidth = screenUnit,
                IsAntialias = true
            };
            using var cross = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(34, 200, 173),
                StrokeWidth = 1.5f * screenUnit,
                IsAntialias = true
            };
            using var centerFill = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = new SKColor(9, 20, 28, 235),
                IsAntialias = true
            };
            using var centerBorder = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = SKColors.White,
                StrokeWidth = 1.4f * screenUnit,
                IsAntialias = true
            };

            canvas.DrawLine(viewportMundo.Left, lockedPoint.Y, viewportMundo.Right, lockedPoint.Y, guides);
            canvas.DrawLine(lockedPoint.X, viewportMundo.Top, lockedPoint.X, viewportMundo.Bottom, guides);
            float arm = 6f * screenUnit;
            canvas.DrawLine(lockedPoint.X - arm, lockedPoint.Y, lockedPoint.X + arm, lockedPoint.Y, cross);
            canvas.DrawLine(lockedPoint.X, lockedPoint.Y - arm, lockedPoint.X, lockedPoint.Y + arm, cross);
            canvas.DrawCircle(lockedPoint, 4f * screenUnit, centerFill);
            canvas.DrawCircle(lockedPoint, 4f * screenUnit, centerBorder);
            using var center = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(34, 200, 173), IsAntialias = true };
            canvas.DrawCircle(lockedPoint, 1.35f * screenUnit, center);
        }

        // 8. FERRAMENTA DE MEDIÇÃO
        if (!clientRenderedDigitizingPreview && !_mapService.ClientRenderedMeasurementPreview &&
            (_mapService.PontosMedicao.Count > 0 || _mapService.PontoCursorMundo.HasValue))
        {
            canvas.SetMatrix(matriz);
            using var pincelLinha = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.DodgerBlue, StrokeWidth = 2.5f / zoomReal, IsAntialias = true };
            using var pincelLinhaTracejada = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White.WithAlpha(180), StrokeWidth = 1.5f / zoomReal, PathEffect = SKPathEffect.CreateDash(new float[] { 10f / zoomReal, 10f / zoomReal }, 0), IsAntialias = true };
            using var pincelPontoMed = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.DarkOrange, IsAntialias = true };
            using var pincelBordaPontoMed = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.FloralWhite, StrokeWidth = 1.5f / zoomReal, IsAntialias = true };
            using var pincelArea = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(239, 68, 68, 97), IsAntialias = true };

            using var pathMedicao = new SKPath();
            for (int i = 0; i < ptsMedicao.Count; i++)
            {
                if (i == 0) pathMedicao.MoveTo(ptsMedicao[i]);
                else pathMedicao.LineTo(ptsMedicao[i]);
            }

            if (_mapService.MostrarAreaMedicao && ptsMedicao.Count > 2)
            {
                using var pathArea = new SKPath(pathMedicao);
                pathArea.Close();
                canvas.DrawPath(pathArea, pincelArea);
                canvas.DrawLine(ptsMedicao[^1], ptsMedicao[0], pincelLinhaTracejada);
            }

            if (ptsMedicao.Count > 0) canvas.DrawPath(pathMedicao, pincelLinha);
            if (ptsMedicao.Count > 0 && _mapService.PontoCursorMundo.HasValue)
            {
                canvas.DrawLine(ptsMedicao[^1], _mapService.PontoCursorMundo.Value, pincelLinhaTracejada);
            }

            foreach (var pt in ptsMedicao)
            {
                canvas.DrawCircle(pt, 4.5f / zoomReal, pincelPontoMed);
                canvas.DrawCircle(pt, 4.5f / zoomReal, pincelBordaPontoMed);
            }
        }
        // 8.1. FERRAMENTA DE AQUISIÇÃO (DESENHO DE LOTE)
        if (!clientRenderedDigitizingPreview && ptsAquisicao.Count > 0 &&
            _mapService.ConstrucaoAtiva != ConstructionMode.Vertices)
            DrawConstructionOverlay(canvas, matriz, zoomReal, ptsAquisicao);
        if (!clientRenderedDigitizingPreview && ptsAquisicao.Count > 0 &&
            _mapService.ConstrucaoAtiva == ConstructionMode.Vertices)
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
                var ultimoPonto = ptsAquisicao[^1];

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

            // Traço azul com halo branco para manter contraste sobre o mapa.
            using var pincelHaloLinhaAq = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White.WithAlpha(235), StrokeWidth = 3.2f / zoomReal, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
            using var pincelLinhaAq = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(37, 99, 235), StrokeWidth = 1.8f / zoomReal, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
            using var pincelHaloPreviaAq = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White.WithAlpha(235), StrokeWidth = 3.2f / zoomReal, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
            using var pincelPreviaAq = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(37, 99, 235), StrokeWidth = 1.8f / zoomReal, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
            using var pincelHaloPontoAq = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(9, 18, 28, 235), IsAntialias = true };
            using var pincelPontoAq = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(230, 79, 93), IsAntialias = true };
            using var pincelPontoInicialAq = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(245, 158, 11), IsAntialias = true };
            using var pincelBordaPontoAq = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White, StrokeWidth = 1.4f / zoomReal, IsAntialias = true };
            using var pincelCentroPontoAq = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
            using var pincelAreaAq = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(168, 85, 247, 46), IsAntialias = true };

            using var pathAq = new SKPath();
            for (int i = 0; i < ptsAquisicao.Count; i++)
            {
                if (i == 0) pathAq.MoveTo(ptsAquisicao[i]);
                else pathAq.LineTo(ptsAquisicao[i]);
            }

            // ==========================================================
            // >>> UPGRADE PROFISSIONAL: CROSSHAIR E HUD DINÂMICO <<<
            // ==========================================================
            if (_mapService.PontoCursorMundo.HasValue && !_mapService.ClientRenderedMeasurementPreview)
            {
                var cursorPts = _mapService.PontoCursorMundo.Value;

                if (!_mapService.PontoRestricaoAbsoluta.HasValue)
                {
                    // 1. MIRA ORTOGONAL (CROSSHAIR ESTILO AUTOCAD)
                    using var paintMira = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White.WithAlpha(100), StrokeWidth = 1f / zoomReal, IsAntialias = false };
                    canvas.DrawLine(viewportMundo.Left, cursorPts.Y, viewportMundo.Right, cursorPts.Y, paintMira);
                    canvas.DrawLine(cursorPts.X, viewportMundo.Top, cursorPts.X, viewportMundo.Bottom, paintMira);
                }

                // 2. HUD DINÂMICO NO CURSOR (LIVE TOOLTIP)
                if (ptsAquisicao.Count > 0 && !_mapService.PontoRestricaoAbsoluta.HasValue)
                {
                    var ultimoPt = ptsAquisicao.Last();

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

            if (_mapService.TipoGeometriaAtiva == "POLIGONO" && ptsAquisicao.Count >= 3)
            {
                using var pathAreaAq = new SKPath(pathAq);
                pathAreaAq.Close();
                canvas.DrawPath(pathAreaAq, pincelAreaAq);
            }

            canvas.DrawPath(pathAq, pincelHaloLinhaAq);
            canvas.DrawPath(pathAq, pincelLinhaAq);
            if (_mapService.PontoCursorMundo is { } cursor)
            {
                canvas.DrawLine(ptsAquisicao[^1], cursor, pincelHaloPreviaAq);
                canvas.DrawLine(ptsAquisicao[^1], cursor, pincelPreviaAq);
            }

            for (int i = 0; i < ptsAquisicao.Count; i++)
            {
                var pt = ptsAquisicao[i];
                bool pontoInicial = i == 0 && _mapService.TipoGeometriaAtiva != "PONTO";
                canvas.DrawCircle(pt, 6.25f / zoomReal, pincelHaloPontoAq);
                canvas.DrawCircle(pt, 5f / zoomReal, pontoInicial ? pincelPontoInicialAq : pincelPontoAq);
                canvas.DrawCircle(pt, 5f / zoomReal, pincelBordaPontoAq);
                canvas.DrawCircle(pt, 1.45f / zoomReal, pincelCentroPontoAq);
            }
        } // Fim do bloco if (ptsAquisicao.Count > 0)

        // 9. SNAP HOVER MAGNÉTICO
        if (!clientRenderedDigitizingPreview && !_mapService.ClientRenderedMeasurementPreview && _mapService.PontoCursorSnap.HasValue)
        {
            canvas.SetMatrix(matriz);
            var snapPt = _mapService.PontoCursorSnap.Value;
            SKColor snapColor = _mapService.PontoCursorSnapTipo switch
            {
                SnapKind.Vertex => SKColors.Yellow,
                SnapKind.Midpoint => SKColors.Cyan,
                SnapKind.Edge => SKColors.Orange,
                SnapKind.Intersection => SKColors.Magenta,
                _ => SKColors.Yellow
            };
            using var pincelSnap = new SKPaint { Style = SKPaintStyle.Stroke, Color = snapColor, StrokeWidth = 2.0f / zoomReal, IsAntialias = true };
            using var pincelSnapFill = new SKPaint { Style = SKPaintStyle.Fill, Color = snapColor.WithAlpha(80), IsAntialias = true };
            float size = 14f / zoomReal;
            var rect = new SKRect(snapPt.X - size / 2, snapPt.Y - size / 2, snapPt.X + size / 2, snapPt.Y + size / 2);
            switch (_mapService.PontoCursorSnapTipo)
            {
                case SnapKind.Midpoint:
                    canvas.DrawCircle(snapPt, size * 0.45f, pincelSnapFill);
                    canvas.DrawCircle(snapPt, size * 0.45f, pincelSnap);
                    break;
                case SnapKind.Edge:
                    using (var diamond = new SKPath())
                    {
                        diamond.MoveTo(snapPt.X, snapPt.Y - size * 0.55f);
                        diamond.LineTo(snapPt.X + size * 0.55f, snapPt.Y);
                        diamond.LineTo(snapPt.X, snapPt.Y + size * 0.55f);
                        diamond.LineTo(snapPt.X - size * 0.55f, snapPt.Y);
                        diamond.Close();
                        canvas.DrawPath(diamond, pincelSnapFill);
                        canvas.DrawPath(diamond, pincelSnap);
                    }
                    break;
                case SnapKind.Intersection:
                    canvas.DrawCircle(snapPt, size * 0.45f, pincelSnapFill);
                    canvas.DrawCircle(snapPt, size * 0.45f, pincelSnap);
                    canvas.DrawLine(snapPt.X - size, snapPt.Y - size, snapPt.X + size, snapPt.Y + size, pincelSnap);
                    canvas.DrawLine(snapPt.X + size, snapPt.Y - size, snapPt.X - size, snapPt.Y + size, pincelSnap);
                    break;
                default:
                    canvas.DrawRect(rect, pincelSnapFill);
                    canvas.DrawRect(rect, pincelSnap);
                    canvas.DrawLine(snapPt.X - size, snapPt.Y, snapPt.X + size, snapPt.Y, pincelSnap);
                    canvas.DrawLine(snapPt.X, snapPt.Y - size, snapPt.X, snapPt.Y + size, pincelSnap);
                    break;
            }
        }
        // =========================================================================
        // 10. CONTORNO ANIMADO DA FEIÇÃO SELECIONADA (MARCHING ANTS)
        // =========================================================================
        if (_mapService.CaminhoDestaquePoligono != null)
        {
            canvas.SetMatrix(matriz);
            using var paintM = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = SKColors.Cyan,
                StrokeWidth = 3f / zoomReal,
                PathEffect = SKPathEffect.CreateDash(new float[] { 10f / zoomReal, 10f / zoomReal }, (DateTime.Now.Millisecond % 1000) / 1000f * 20f / zoomReal),
                IsAntialias = true
            };
            using var paintFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Cyan.WithAlpha(50) };
            canvas.DrawPath(_mapService.CaminhoDestaquePoligono, paintFill);
            canvas.DrawPath(_mapService.CaminhoDestaquePoligono, paintM);
        }
        if (_mapService.CaminhoDestaqueLinha != null)
        {
            canvas.SetMatrix(matriz);
            using var paintM = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = SKColors.Cyan,
                StrokeWidth = 4f / zoomReal,
                PathEffect = SKPathEffect.CreateDash(new float[] { 10f / zoomReal, 10f / zoomReal }, (DateTime.Now.Millisecond % 1000) / 1000f * 20f / zoomReal),
                IsAntialias = true
            };
            canvas.DrawPath(_mapService.CaminhoDestaqueLinha, paintM);
        }

    }

    private void DrawConstructionOverlay(SKCanvas canvas, SKMatrix matrix, float zoom, List<SKPoint> controls)
    {
        var preview = SketchConstruction.Preview(_mapService.ConstrucaoAtiva, controls, _mapService.PontoCursorMundo, _mapService.ConstrucaoLados);
        canvas.SetMatrix(matrix);
        using var path = new SKPath();
        for (int i = 0; i < preview.Points.Length; i++)
        {
            if (i == 0) path.MoveTo(preview.Points[i]);
            else path.LineTo(preview.Points[i]);
        }
        if (preview.Closed) path.Close();
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(59, 130, 246),
            StrokeWidth = 2.5f / zoom, IsAntialias = true };
        using var fill = new SKPaint { Color = new SKColor(168, 85, 247, 46), IsAntialias = true };
        if (preview.Closed) canvas.DrawPath(path, fill);
        canvas.DrawPath(path, stroke);
        using var guide = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White.WithAlpha(150),
            StrokeWidth = 1f / zoom, IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([6f / zoom, 6f / zoom], 0) };
        if (_mapService.ConstrucaoAtiva == ConstructionMode.Circle && preview.Points.Length > 0)
            canvas.DrawLine(controls[0], preview.Closed ? preview.Points[0] : preview.Points[^1], guide);
        if (_mapService.ConstrucaoAtiva == ConstructionMode.Ellipse && controls.Count > 0)
        {
            canvas.DrawLine(controls[0], controls.Count > 1 ? controls[1] :
                (preview.Points.Length > 0 ? preview.Points[^1] : controls[0]), guide);
            if (controls.Count > 2)
                canvas.DrawLine(controls[0], controls[2], guide);
        }
        if (_mapService.ConstrucaoAtiva == ConstructionMode.RegularPolygon && controls.Count > 0)
            canvas.DrawLine(controls[0], controls.Count > 1 ? controls[1] :
                (preview.Points.Length > 0 ? preview.Points[0] : controls[0]), guide);
        using var handle = new SKPaint { Color = SKColors.White, IsAntialias = true };
        for (int i = 0; i < controls.Count; i++)
        {
            handle.Color = i == 0 ? SKColors.Orange : SKColors.White;
            canvas.DrawCircle(controls[i], 4.5f / zoom, handle);
            canvas.DrawCircle(controls[i], 5.5f / zoom, stroke);
        }
    }
}
