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
