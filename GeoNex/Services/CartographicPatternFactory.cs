using System;
using System.Collections.Concurrent;
using System.IO;
using SkiaSharp;

namespace GeoNex.Services
{
    /// <summary>
    /// Fábrica otimizada para geração e cache de texturas cartográficas (Hachuras) e Marcadores Geométricos (Pontos)
    /// com suporte a aceleração gráfica por hardware SkiaSharp (padrão QGIS / ArcGIS Pro / OGC SLD).
    /// </summary>
    public static class CartographicPatternFactory
    {
        private static readonly ConcurrentDictionary<string, SKShader> _shaderCache = new();

        private readonly record struct MarkerSpriteKey(
            string Forma,
            int DiameterSubPx,
            uint FillColor,
            uint StrokeColor,
            int StrokeSubPx,
            int RotationTenths);

        private sealed record MarkerSpriteEntry(SKImage Image, float HalfExtentPx);
        private static readonly ConcurrentDictionary<MarkerSpriteKey, MarkerSpriteEntry> _markerSpriteCache = new();

        // Paths canônicos normalizados (raio = 1.0f) centrados em (0, 0)
        private static readonly SKPath _pathQuadrado;
        private static readonly SKPath _pathLosango;
        private static readonly SKPath _pathTriangulo;
        private static readonly SKPath _pathTrianguloInvertido;
        private static readonly SKPath _pathCruz;
        private static readonly SKPath _pathX;
        private static readonly SKPath _pathEstrela;
        private static readonly SKPath _pathPentagono;
        private static readonly SKPath _pathHexagono;
        private static readonly SKPath _pathMarcoIncra;
        private static readonly SKPath _pathAlvoTarget;
        private static readonly SKPath _pathPontoCotado;
        private static readonly SKPath _pathPino;
        private static readonly SKPath _pathQuadradoCruz;
        private static readonly SKPath _pathMarcoGeodesicoQGIS;
        private static readonly SKPath _pathEstacaoTopograficaQGIS;
        private static readonly SKPath _pathTorreEnergiaQGIS;
        private static readonly SKPath _pathPosteEnergiaQGIS;
        private static readonly SKPath _pathPocoAguaQGIS;
        private static readonly SKPath _pathArvoreFlorestalQGIS;
        private static readonly SKPath _pathEdificacaoSedeQGIS;
        private static readonly SKPath _pathVerticeTriangulacaoQGIS;

        static CartographicPatternFactory()
        {
            // 1. Quadrado [-1, 1]
            _pathQuadrado = new SKPath();
            _pathQuadrado.AddRect(new SKRect(-0.85f, -0.85f, 0.85f, 0.85f));

            // 2. Losango / Diamante
            _pathLosango = new SKPath();
            _pathLosango.MoveTo(0f, -1.15f);
            _pathLosango.LineTo(1.15f, 0f);
            _pathLosango.LineTo(0f, 1.15f);
            _pathLosango.LineTo(-1.15f, 0f);
            _pathLosango.Close();

            // 3. Triângulo Equilátero
            _pathTriangulo = new SKPath();
            _pathTriangulo.MoveTo(0f, -1.15f);
            _pathTriangulo.LineTo(1.0f, 0.85f);
            _pathTriangulo.LineTo(-1.0f, 0.85f);
            _pathTriangulo.Close();

            // 3b. Triângulo Invertido (Piquete / Estaca)
            _pathTrianguloInvertido = new SKPath();
            _pathTrianguloInvertido.MoveTo(0f, 1.15f);
            _pathTrianguloInvertido.LineTo(1.0f, -0.85f);
            _pathTrianguloInvertido.LineTo(-1.0f, -0.85f);
            _pathTrianguloInvertido.Close();

            // 4. Cruz Cartográfica (+)
            _pathCruz = new SKPath();
            float w = 0.35f;
            _pathCruz.MoveTo(-w, -1.1f);
            _pathCruz.LineTo(w, -1.1f);
            _pathCruz.LineTo(w, -w);
            _pathCruz.LineTo(1.1f, -w);
            _pathCruz.LineTo(1.1f, w);
            _pathCruz.LineTo(w, w);
            _pathCruz.LineTo(w, 1.1f);
            _pathCruz.LineTo(-w, 1.1f);
            _pathCruz.LineTo(-w, w);
            _pathCruz.LineTo(-1.1f, w);
            _pathCruz.LineTo(-1.1f, -w);
            _pathCruz.LineTo(-w, -w);
            _pathCruz.Close();

            // 5. Marcador X
            _pathX = new SKPath();
            var mat45 = SKMatrix.CreateRotationDegrees(45f);
            _pathCruz.Transform(mat45, _pathX);

            // 6. Estrela de 5 Pontas Cartográfica
            _pathEstrela = new SKPath();
            float rExt = 1.15f;
            float rInt = 0.48f;
            for (int i = 0; i < 10; i++)
            {
                double angle = -Math.PI / 2.0 + (i * Math.PI / 5.0);
                float r = (i % 2 == 0) ? rExt : rInt;
                float x = (float)(r * Math.Cos(angle));
                float y = (float)(r * Math.Sin(angle));
                if (i == 0) _pathEstrela.MoveTo(x, y);
                else _pathEstrela.LineTo(x, y);
            }
            _pathEstrela.Close();

            // 7. Pentágono Regular
            _pathPentagono = new SKPath();
            for (int i = 0; i < 5; i++)
            {
                double angle = -Math.PI / 2.0 + (i * 2.0 * Math.PI / 5.0);
                float x = (float)(1.1f * Math.Cos(angle));
                float y = (float)(1.1f * Math.Sin(angle));
                if (i == 0) _pathPentagono.MoveTo(x, y);
                else _pathPentagono.LineTo(x, y);
            }
            _pathPentagono.Close();

            // 8. Hexágono Regular
            _pathHexagono = new SKPath();
            for (int i = 0; i < 6; i++)
            {
                double angle = -Math.PI / 2.0 + (i * 2.0 * Math.PI / 6.0);
                float x = (float)(1.1f * Math.Cos(angle));
                float y = (float)(1.1f * Math.Sin(angle));
                if (i == 0) _pathHexagono.MoveTo(x, y);
                else _pathHexagono.LineTo(x, y);
            }
            _pathHexagono.Close();

            // 9. Marco INCRA (SIGEF) - Círculo com Cruzeta
            _pathMarcoIncra = new SKPath();
            _pathMarcoIncra.AddCircle(0f, 0f, 0.95f, SKPathDirection.Clockwise);
            _pathMarcoIncra.MoveTo(-1.25f, 0f); _pathMarcoIncra.LineTo(1.25f, 0f);
            _pathMarcoIncra.MoveTo(0f, -1.25f); _pathMarcoIncra.LineTo(0f, 1.25f);

            // 10. Alvo / Target / GNSS - Anéis Concêntricos
            _pathAlvoTarget = new SKPath();
            _pathAlvoTarget.AddCircle(0f, 0f, 1.05f, SKPathDirection.Clockwise);
            _pathAlvoTarget.AddCircle(0f, 0f, 0.45f, SKPathDirection.Clockwise);

            // 11. Ponto Cotado - Centro com Mira
            _pathPontoCotado = new SKPath();
            _pathPontoCotado.AddCircle(0f, 0f, 0.35f, SKPathDirection.Clockwise);
            _pathPontoCotado.MoveTo(-1.25f, 0f); _pathPontoCotado.LineTo(1.25f, 0f);
            _pathPontoCotado.MoveTo(0f, -1.25f); _pathPontoCotado.LineTo(0f, 1.25f);

            // 12. Pino de Localização / POI
            _pathPino = new SKPath();
            _pathPino.MoveTo(0f, 1.15f);
            _pathPino.CubicTo(0.7f, 0.35f, 0.85f, -0.2f, 0.85f, -0.5f);
            _pathPino.ArcTo(new SKRect(-0.85f, -1.35f, 0.85f, 0.35f), 0f, -180f, false);
            _pathPino.CubicTo(-0.85f, -0.2f, -0.7f, 0.35f, 0f, 1.15f);
            _pathPino.Close();
            _pathPino.AddCircle(0f, -0.5f, 0.32f, SKPathDirection.CounterClockwise);

            // 13. Vértice Geodésico Quadrado com Cruz
            _pathQuadradoCruz = new SKPath();
            _pathQuadradoCruz.AddRect(new SKRect(-0.85f, -0.85f, 0.85f, 0.85f));
            _pathQuadradoCruz.MoveTo(-1.1f, 0f); _pathQuadradoCruz.LineTo(1.1f, 0f);
            _pathQuadradoCruz.MoveTo(0f, -1.1f); _pathQuadradoCruz.LineTo(0f, 1.1f);

            // 14. Marco Geodésico Bicolor QGIS (Círculo com Mira e 2 Quadrantes)
            _pathMarcoGeodesicoQGIS = new SKPath();
            _pathMarcoGeodesicoQGIS.AddCircle(0f, 0f, 1.0f, SKPathDirection.Clockwise);
            _pathMarcoGeodesicoQGIS.MoveTo(0f, 0f); _pathMarcoGeodesicoQGIS.ArcTo(new SKRect(-1f, -1f, 1f, 1f), 0f, 90f, false); _pathMarcoGeodesicoQGIS.Close();
            _pathMarcoGeodesicoQGIS.MoveTo(0f, 0f); _pathMarcoGeodesicoQGIS.ArcTo(new SKRect(-1f, -1f, 1f, 1f), 180f, 90f, false); _pathMarcoGeodesicoQGIS.Close();
            _pathMarcoGeodesicoQGIS.MoveTo(-1.3f, 0f); _pathMarcoGeodesicoQGIS.LineTo(1.3f, 0f);
            _pathMarcoGeodesicoQGIS.MoveTo(0f, -1.3f); _pathMarcoGeodesicoQGIS.LineTo(0f, 1.3f);

            // 15. Estação Total / Teodolito QGIS (Tripé de Campo + Instrumento)
            _pathEstacaoTopograficaQGIS = new SKPath();
            _pathEstacaoTopograficaQGIS.MoveTo(-0.1f, -0.2f); _pathEstacaoTopograficaQGIS.LineTo(-0.85f, 1.15f);
            _pathEstacaoTopograficaQGIS.MoveTo(0f, -0.2f); _pathEstacaoTopograficaQGIS.LineTo(0f, 1.15f);
            _pathEstacaoTopograficaQGIS.MoveTo(0.1f, -0.2f); _pathEstacaoTopograficaQGIS.LineTo(0.85f, 1.15f);
            _pathEstacaoTopograficaQGIS.AddRect(new SKRect(-0.35f, -0.65f, 0.35f, -0.2f));
            _pathEstacaoTopograficaQGIS.MoveTo(-0.75f, -0.85f); _pathEstacaoTopograficaQGIS.LineTo(0.75f, -0.85f);
            _pathEstacaoTopograficaQGIS.MoveTo(0f, -0.65f); _pathEstacaoTopograficaQGIS.LineTo(0f, -1.05f);

            // 16. Torre de Transmissão de Energia QGIS
            _pathTorreEnergiaQGIS = new SKPath();
            _pathTorreEnergiaQGIS.MoveTo(-0.75f, 1.15f); _pathTorreEnergiaQGIS.LineTo(-0.25f, -0.6f);
            _pathTorreEnergiaQGIS.LineTo(0f, -1.15f);
            _pathTorreEnergiaQGIS.LineTo(0.25f, -0.6f); _pathTorreEnergiaQGIS.LineTo(0.75f, 1.15f);
            _pathTorreEnergiaQGIS.MoveTo(-0.95f, -0.55f); _pathTorreEnergiaQGIS.LineTo(0.95f, -0.55f);
            _pathTorreEnergiaQGIS.MoveTo(-0.75f, -0.15f); _pathTorreEnergiaQGIS.LineTo(0.75f, -0.15f);
            _pathTorreEnergiaQGIS.MoveTo(-0.55f, 0.55f); _pathTorreEnergiaQGIS.LineTo(0.55f, 0.1f);
            _pathTorreEnergiaQGIS.MoveTo(0.55f, 0.55f); _pathTorreEnergiaQGIS.LineTo(-0.55f, 0.1f);

            // 17. Poste de Rede / Iluminação QGIS
            _pathPosteEnergiaQGIS = new SKPath();
            _pathPosteEnergiaQGIS.MoveTo(0f, 1.15f); _pathPosteEnergiaQGIS.LineTo(0f, -1.05f);
            _pathPosteEnergiaQGIS.MoveTo(-0.85f, -0.7f); _pathPosteEnergiaQGIS.LineTo(0.85f, -0.7f);
            _pathPosteEnergiaQGIS.AddCircle(-0.85f, -0.7f, 0.15f, SKPathDirection.Clockwise);
            _pathPosteEnergiaQGIS.AddCircle(0.85f, -0.7f, 0.15f, SKPathDirection.Clockwise);

            // 18. Poço Artesiano / Nascente QGIS
            _pathPocoAguaQGIS = new SKPath();
            _pathPocoAguaQGIS.AddCircle(0f, 0f, 1.1f, SKPathDirection.Clockwise);
            _pathPocoAguaQGIS.AddCircle(0f, 0f, 0.75f, SKPathDirection.Clockwise);
            _pathPocoAguaQGIS.MoveTo(0f, -0.55f);
            _pathPocoAguaQGIS.CubicTo(0.35f, -0.15f, 0.45f, 0.2f, 0.45f, 0.35f);
            _pathPocoAguaQGIS.ArcTo(new SKRect(-0.45f, -0.1f, 0.45f, 0.65f), 0f, 180f, false);
            _pathPocoAguaQGIS.CubicTo(-0.45f, 0.2f, -0.35f, -0.15f, 0f, -0.55f);
            _pathPocoAguaQGIS.Close();

            // 19. Árvore / Floresta / Vegetação QGIS
            _pathArvoreFlorestalQGIS = new SKPath();
            _pathArvoreFlorestalQGIS.AddRect(new SKRect(-0.15f, 0.4f, 0.15f, 1.15f));
            _pathArvoreFlorestalQGIS.AddCircle(0f, -0.4f, 0.55f, SKPathDirection.Clockwise);
            _pathArvoreFlorestalQGIS.AddCircle(-0.45f, 0.05f, 0.45f, SKPathDirection.Clockwise);
            _pathArvoreFlorestalQGIS.AddCircle(0.45f, 0.05f, 0.45f, SKPathDirection.Clockwise);

            // 20. Edificação / Sede da Fazenda QGIS
            _pathEdificacaoSedeQGIS = new SKPath();
            _pathEdificacaoSedeQGIS.MoveTo(-0.85f, 0.05f);
            _pathEdificacaoSedeQGIS.LineTo(0f, -0.95f);
            _pathEdificacaoSedeQGIS.LineTo(0.85f, 0.05f);
            _pathEdificacaoSedeQGIS.LineTo(0.7f, 0.05f);
            _pathEdificacaoSedeQGIS.LineTo(0.7f, 1.1f);
            _pathEdificacaoSedeQGIS.LineTo(-0.7f, 1.1f);
            _pathEdificacaoSedeQGIS.LineTo(-0.7f, 0.05f);
            _pathEdificacaoSedeQGIS.Close();
            _pathEdificacaoSedeQGIS.AddRect(new SKRect(-0.2f, 0.55f, 0.2f, 1.1f));

            // 21. Vértice de Triangulação Geodésica QGIS
            _pathVerticeTriangulacaoQGIS = new SKPath();
            _pathVerticeTriangulacaoQGIS.MoveTo(0f, -1.15f);
            _pathVerticeTriangulacaoQGIS.LineTo(1.05f, 0.85f);
            _pathVerticeTriangulacaoQGIS.LineTo(-1.05f, 0.85f);
            _pathVerticeTriangulacaoQGIS.Close();
            _pathVerticeTriangulacaoQGIS.AddCircle(0f, -0.1f, 0.3f, SKPathDirection.Clockwise);
        }

        /// <summary>
        /// Obtém ou cria no cache um SKShader tileado para hachuras cartográficas de polígonos.
        /// </summary>
        public static SKShader? ObterShaderHachura(string padrao, SKColor corFundo, SKColor corHachura, int espacamento)
        {
            if (string.IsNullOrEmpty(padrao) || padrao.Equals("Solido", StringComparison.OrdinalIgnoreCase))
                return null;

            int size = Math.Clamp(espacamento, 6, 48);
            string key = $"{padrao}_{corFundo.Alpha}_{corFundo.Red}_{corFundo.Green}_{corFundo.Blue}_{corHachura.Alpha}_{corHachura.Red}_{corHachura.Green}_{corHachura.Blue}_{size}";

            return _shaderCache.GetOrAdd(key, _ =>
            {
                int tileSize = (padrao.Contains("Diagonal", StringComparison.OrdinalIgnoreCase)) ? size * 2 : size;
                var info = new SKImageInfo(tileSize, tileSize, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info);
                if (surface == null) return null!;

                var c = surface.Canvas;
                c.Clear(corFundo);

                using var paintHachura = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = corHachura,
                    StrokeWidth = Math.Max(1.5f, tileSize / 10f),
                    IsAntialias = true,
                    StrokeCap = SKStrokeCap.Square
                };

                switch (padrao)
                {
                    case "LinhasHorizontais":
                        c.DrawLine(0, tileSize / 2f, tileSize, tileSize / 2f, paintHachura);
                        break;

                    case "LinhasVerticais":
                        c.DrawLine(tileSize / 2f, 0, tileSize / 2f, tileSize, paintHachura);
                        break;

                    case "DiagonalDireita":
                        c.DrawLine(-tileSize / 2f, tileSize / 2f, tileSize / 2f, -tileSize / 2f, paintHachura);
                        c.DrawLine(0, tileSize, tileSize, 0, paintHachura);
                        c.DrawLine(tileSize / 2f, tileSize * 1.5f, tileSize * 1.5f, tileSize / 2f, paintHachura);
                        break;

                    case "DiagonalEsquerda":
                        c.DrawLine(-tileSize / 2f, tileSize / 2f, tileSize / 2f, tileSize * 1.5f, paintHachura);
                        c.DrawLine(0, 0, tileSize, tileSize, paintHachura);
                        c.DrawLine(tileSize / 2f, -tileSize / 2f, tileSize * 1.5f, tileSize / 2f, paintHachura);
                        break;

                    case "Cruzada":
                        c.DrawLine(0, 0, tileSize, tileSize, paintHachura);
                        c.DrawLine(0, tileSize, tileSize, 0, paintHachura);
                        break;

                    case "Pontilhado":
                        paintHachura.Style = SKPaintStyle.Fill;
                        c.DrawCircle(tileSize / 2f, tileSize / 2f, Math.Max(1.5f, tileSize / 8f), paintHachura);
                        c.DrawCircle(0, 0, Math.Max(1.5f, tileSize / 8f), paintHachura);
                        c.DrawCircle(tileSize, 0, Math.Max(1.5f, tileSize / 8f), paintHachura);
                        c.DrawCircle(0, tileSize, Math.Max(1.5f, tileSize / 8f), paintHachura);
                        c.DrawCircle(tileSize, tileSize, Math.Max(1.5f, tileSize / 8f), paintHachura);
                        break;

                    default:
                        c.DrawLine(0, tileSize / 2f, tileSize, tileSize / 2f, paintHachura);
                        break;
                }

                using var img = surface.Snapshot();
                return img.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
            });
        }

        /// <summary>
        /// Obtém ou compila um Sprite SKImage pré-rasterizado em espaço de dispositivo (pixels físicos)
        /// para blitting ultra-rápido de milhares de marcadores sem Save/Translate/Scale/DrawPath por feição.
        /// </summary>
        internal static (SKImage? Image, float HalfExtentPx) ObterSpriteMarcadorFisico(
            string forma,
            float raioPx,
            float rotacaoGraus,
            SKColor fillColor,
            SKColor? strokeColor,
            float strokeWidthPx)
        {
            if (raioPx <= 0.25f || raioPx > 96f || !float.IsFinite(raioPx))
                return (null, 0f);

            string normForma = string.IsNullOrEmpty(forma) ? "Circulo" : forma;
            int diamSubPx = Math.Clamp((int)Math.Round(raioPx * 4f), 2, 384);
            int strokeSubPx = strokeColor.HasValue ? Math.Clamp((int)Math.Round(strokeWidthPx * 4f), 0, 128) : 0;
            int rotTenths = normForma.Equals("Circulo", StringComparison.OrdinalIgnoreCase)
                ? 0
                : ((int)Math.Round(rotacaoGraus * 10f) % 3600 + 3600) % 3600;

            var key = new MarkerSpriteKey(
                normForma,
                diamSubPx,
                (uint)fillColor,
                strokeColor.HasValue ? (uint)strokeColor.Value : 0u,
                strokeSubPx,
                rotTenths);

            if (_markerSpriteCache.Count > 256)
            {
                foreach (var old in _markerSpriteCache.Values)
                    old.Image.Dispose();
                _markerSpriteCache.Clear();
            }

            var entry = _markerSpriteCache.GetOrAdd(key, k =>
            {
                float r = k.DiameterSubPx * 0.25f;
                float sw = k.StrokeSubPx * 0.25f;
                float padding = Math.Max(3f, sw + 2.5f);
                int halfDim = (int)Math.Ceiling(r * 1.35f + padding);
                int dim = Math.Max(8, halfDim * 2);
                float center = dim * 0.5f;

                var info = new SKImageInfo(dim, dim, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info);
                var c = surface.Canvas;
                c.Clear(SKColors.Transparent);

                using var fp = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = new SKColor(k.FillColor),
                    IsAntialias = true
                };
                using var sp = k.StrokeSubPx > 0
                    ? new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = new SKColor(k.StrokeColor),
                        StrokeWidth = Math.Max(1f, sw),
                        IsAntialias = true,
                        StrokeJoin = SKStrokeJoin.Round,
                        StrokeCap = SKStrokeCap.Round
                    }
                    : null;

                if (k.Forma.Equals("Circulo", StringComparison.OrdinalIgnoreCase))
                {
                    c.DrawCircle(center, center, r, fp);
                    if (sp != null)
                    {
                        c.DrawCircle(center, center, r, sp);
                    }
                }
                else
                {
                    DesenharMarcador(c, new SKPoint(center, center), r, k.Forma, k.RotationTenths * 0.1f, fp, sp);
                }

                return new MarkerSpriteEntry(surface.Snapshot(), center);
            });

            return (entry.Image, entry.HalfExtentPx);
        }

        /// <summary>
        /// Desenha um marcador vetorial parametrizado com suporte a formas geométricas e rotação arbitrária.
        /// </summary>
        public static void DesenharMarcador(
            SKCanvas canvas,
            SKPoint centro,
            float raio,
            string forma,
            float rotacaoGraus,
            SKPaint fillPaint,
            SKPaint? strokePaint)
        {
            if (string.IsNullOrEmpty(forma) || forma.Equals("Circulo", StringComparison.OrdinalIgnoreCase))
            {
                canvas.DrawCircle(centro, raio, fillPaint);
                if (strokePaint != null)
                {
                    canvas.DrawCircle(centro, raio, strokePaint);
                }
                return;
            }

            if (forma.StartsWith("svg:", StringComparison.OrdinalIgnoreCase) ||
                forma.StartsWith("geonex://", StringComparison.OrdinalIgnoreCase) ||
                forma.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                string svgPath = forma.StartsWith("svg:", StringComparison.OrdinalIgnoreCase) ? forma.Substring(4) : forma;
                var pic = SvgCompilationService.GetOrCompilePicture(svgPath, null, fillPaint.Color, fillPaint.Color, strokePaint?.Color ?? SKColors.Transparent, strokePaint?.StrokeWidth ?? 1f);
                if (pic != null)
                {
                    int save = canvas.Save();
                    canvas.Translate(centro.X, centro.Y);
                    if (rotacaoGraus != 0f) canvas.RotateDegrees(rotacaoGraus);
                    float targetSize = raio * 2f;
                    float srcW = pic.CullRect.Width > 0 ? pic.CullRect.Width : 32f;
                    float srcH = pic.CullRect.Height > 0 ? pic.CullRect.Height : 32f;
                    canvas.Scale(targetSize / srcW, targetSize / srcH);
                    canvas.Translate(-srcW * 0.5f, -srcH * 0.5f);
                    canvas.DrawPicture(pic);
                    canvas.RestoreToCount(save);
                    return;
                }
            }

            SKPath? pathBase = forma switch
            {
                "Quadrado" => _pathQuadrado,
                "Losango" or "Diamante" => _pathLosango,
                "Triangulo" => _pathTriangulo,
                "TrianguloInvertido" => _pathTrianguloInvertido,
                "Cruz" => _pathCruz,
                "X" => _pathX,
                "Estrela" => _pathEstrela,
                "Pentagono" => _pathPentagono,
                "Hexagono" => _pathHexagono,
                "MarcoINCRA" => _pathMarcoIncra,
                "AlvoTarget" => _pathAlvoTarget,
                "PontoCotado" => _pathPontoCotado,
                "PinoLocalizacao" or "Pin" => _pathPino,
                "QuadradoCruz" => _pathQuadradoCruz,
                "MarcoGeodesicoQGIS" => _pathMarcoGeodesicoQGIS,
                "EstacaoTopograficaQGIS" => _pathEstacaoTopograficaQGIS,
                "TorreEnergiaQGIS" => _pathTorreEnergiaQGIS,
                "PosteEnergiaQGIS" => _pathPosteEnergiaQGIS,
                "PocoAguaQGIS" => _pathPocoAguaQGIS,
                "ArvoreFlorestalQGIS" => _pathArvoreFlorestalQGIS,
                "EdificacaoSedeQGIS" => _pathEdificacaoSedeQGIS,
                "VerticeTriangulacaoQGIS" => _pathVerticeTriangulacaoQGIS,
                _ => null
            };

            if (pathBase == null)
            {
                canvas.DrawCircle(centro, raio, fillPaint);
                if (strokePaint != null) canvas.DrawCircle(centro, raio, strokePaint);
                return;
            }

            canvas.Save();
            canvas.Translate(centro.X, centro.Y);
            if (rotacaoGraus != 0f)
            {
                canvas.RotateDegrees(rotacaoGraus);
            }
            canvas.Scale(raio, raio);

            canvas.DrawPath(pathBase, fillPaint);
            if (strokePaint != null)
            {
                float oldWidth = strokePaint.StrokeWidth;
                strokePaint.StrokeWidth = Math.Max(0.5f, oldWidth / Math.Max(0.1f, raio));
                canvas.DrawPath(pathBase, strokePaint);
                strokePaint.StrokeWidth = oldWidth;
            }

            canvas.Restore();
        }
    }
}
