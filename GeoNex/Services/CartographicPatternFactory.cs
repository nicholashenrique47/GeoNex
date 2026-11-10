using System;
using System.Collections.Concurrent;
using SkiaSharp;

namespace GeoNex.Services
{
    /// <summary>
    /// Fábrica de alta performance para Marcadores Vetoriais e Padrões de Hachura Cartográfica.
    /// Projetada com disciplina zero-allocation para garantir 60 FPS contínuos no motor de mapa.
    /// </summary>
    public static class CartographicPatternFactory
    {
        // Cache estático thread-safe para Shaders de Hachura
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
        private static readonly SKPath _pathCruz;
        private static readonly SKPath _pathX;
        private static readonly SKPath _pathEstrela;
        private static readonly SKPath _pathPentagono;

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
                        // Linha diagonal principal de (0, size) até (size, 0) e cantos para emenda contínua
                        c.DrawLine(-tileSize / 2f, tileSize / 2f, tileSize / 2f, -tileSize / 2f, paintHachura);
                        c.DrawLine(0, tileSize, tileSize, 0, paintHachura);
                        c.DrawLine(tileSize / 2f, tileSize * 1.5f, tileSize * 1.5f, tileSize / 2f, paintHachura);
                        break;

                    case "DiagonalEsquerda":
                        // Linha diagonal invertida de (0, 0) até (size, size)
                        c.DrawLine(-tileSize / 2f, tileSize / 2f, tileSize / 2f, tileSize * 1.5f, paintHachura);
                        c.DrawLine(0, 0, tileSize, tileSize, paintHachura);
                        c.DrawLine(tileSize / 2f, -tileSize / 2f, tileSize * 1.5f, tileSize / 2f, paintHachura);
                        break;

                    case "DiagonalCruzada":
                        // Cruzamento das duas diagonais (Crosshatch clássico)
                        c.DrawLine(-tileSize / 2f, tileSize / 2f, tileSize / 2f, -tileSize / 2f, paintHachura);
                        c.DrawLine(0, tileSize, tileSize, 0, paintHachura);
                        c.DrawLine(tileSize / 2f, tileSize * 1.5f, tileSize * 1.5f, tileSize / 2f, paintHachura);

                        c.DrawLine(-tileSize / 2f, tileSize / 2f, tileSize / 2f, tileSize * 1.5f, paintHachura);
                        c.DrawLine(0, 0, tileSize, tileSize, paintHachura);
                        c.DrawLine(tileSize / 2f, -tileSize / 2f, tileSize * 1.5f, tileSize / 2f, paintHachura);
                        break;

                    case "Pontilhado":
                        using (var dotPaint = new SKPaint
                        {
                            Style = SKPaintStyle.Fill,
                            Color = corHachura,
                            IsAntialias = true
                        })
                        {
                            float dotRadius = Math.Max(1.5f, tileSize / 6f);
                            c.DrawCircle(tileSize / 2f, tileSize / 2f, dotRadius, dotPaint);
                            c.DrawCircle(0, 0, dotRadius, dotPaint);
                            c.DrawCircle(tileSize, 0, dotRadius, dotPaint);
                            c.DrawCircle(0, tileSize, dotRadius, dotPaint);
                            c.DrawCircle(tileSize, tileSize, dotRadius, dotPaint);
                        }
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
                int halfDim = (int)Math.Ceiling(r * 1.25f + padding);
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
                        StrokeWidth = Math.Max(1f, sw) / r,
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
                        sp.StrokeWidth = Math.Max(1f, sw);
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

            SKPath? pathBase = forma switch
            {
                "Quadrado" => _pathQuadrado,
                "Losango" or "Diamante" => _pathLosango,
                "Triangulo" => _pathTriangulo,
                "Cruz" => _pathCruz,
                "X" => _pathX,
                "Estrela" => _pathEstrela,
                "Pentagono" => _pathPentagono,
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
                canvas.DrawPath(pathBase, strokePaint);
            }

            canvas.Restore();
        }
    }
}
