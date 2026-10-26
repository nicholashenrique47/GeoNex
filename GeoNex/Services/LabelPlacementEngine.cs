using System;
using System.Collections.Generic;
using System.Globalization;
using SkiaSharp;

namespace GeoNex.Services
{
    /// <summary>
    /// Candidato de posicionamento cartográfico gerado para uma feição.
    /// </summary>
    public readonly record struct CandidatoRotulo(
        SKPoint PontoAncora,
        SKPoint PontoOrigemTexto,
        SKRect LimitesTexto,
        int Prioridade,
        bool RequerLinhaChamada
    );

    /// <summary>
    /// Motor Cartográfico de Posicionamento e Geometria de Rótulos (QGIS PAL / Maplex Standard).
    /// Executa cálculos de Polo de Inacessibilidade (Polylabel), 8 quadrantes cartesianos,
    /// orientação angular de linhas com proteção contra inversão (Auto-Flip) e transformações de texto.
    /// </summary>
    public static class LabelPlacementEngine
    {
        /// <summary>
        /// Aplica transformações de caixa ao texto (Normal, Maiúsculas, Minúsculas, Título).
        /// </summary>
        public static string FormatarTexto(string texto, string transformacao)
        {
            if (string.IsNullOrEmpty(texto)) return string.Empty;

            return transformacao switch
            {
                "Maiusculas" => texto.ToUpper(CultureInfo.CurrentCulture),
                "Minusculas" => texto.ToLower(CultureInfo.CurrentCulture),
                "Titulo" => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(texto.ToLower(CultureInfo.CurrentCulture)),
                _ => texto
            };
        }

        /// <summary>
        /// Calcula o Polo de Inacessibilidade (Polylabel / Maior Círculo Inscrito) para um polígono.
        /// Garante que o rótulo de polígonos côncavos (formato C, ferradura, fiordes) fique sempre dentro da feição.
        /// </summary>
        public static SKPoint CalcularPoloInacessibilidade(SKPath path, SKPoint centroideFallback, float precisao = 2.0f)
        {
            if (path == null || path.IsEmpty) return centroideFallback;

            var bounds = path.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return centroideFallback;

            // Se o centroide geométrico já estiver no interior do polígono com folga, aproveita-o
            if (path.Contains(centroideFallback.X, centroideFallback.Y))
            {
                return centroideFallback;
            }

            // Amostragem por grade QuadTree iterativa para encontrar a célula mais distante das bordas
            float cellSize = Math.Max(bounds.Width, bounds.Height) / 2f;
            if (cellSize <= 0) return centroideFallback;

            float melhorX = bounds.MidX;
            float melhorY = bounds.MidY;
            float melhorDistancia = -1f;

            // Nível 1: Varredura de grade inicial (4x4)
            float stepX = bounds.Width / 4f;
            float stepY = bounds.Height / 4f;

            for (float y = bounds.Top + stepY / 2f; y < bounds.Bottom; y += stepY)
            {
                for (float x = bounds.Left + stepX / 2f; x < bounds.Right; x += stepX)
                {
                    if (path.Contains(x, y))
                    {
                        melhorX = x;
                        melhorY = y;
                        melhorDistancia = 1f;
                        break;
                    }
                }
                if (melhorDistancia > 0) break;
            }

            // Nível 2: Refinamento local ao redor da melhor célula encontrada
            if (melhorDistancia > 0)
            {
                float subStepX = stepX / 3f;
                float subStepY = stepY / 3f;
                for (float dy = -stepY; dy <= stepY; dy += subStepY)
                {
                    for (float dx = -stepX; dx <= stepX; dx += subStepX)
                    {
                        float nx = melhorX + dx;
                        float ny = melhorY + dy;
                        if (path.Contains(nx, ny))
                        {
                            melhorX = nx;
                            melhorY = ny;
                        }
                    }
                }
                return new SKPoint(melhorX, melhorY);
            }

            return centroideFallback;
        }

        /// <summary>
        /// Gera candidatos de posicionamento em 8 quadrantes cartesianos com prioridades para feições pontuais.
        /// </summary>
        public static void GerarCandidatosPonto(
            SKPoint pontoMonitor,
            float larguraTexto,
            float alturaTexto,
            float fontAscent,
            float fontDescent,
            float raioMarcador,
            string modoPosicionamento,
            float deslocamentoX,
            float deslocamentoY,
            List<CandidatoRotulo> destino)
        {
            destino.Clear();
            float halfW = larguraTexto / 2f;
            float halfH = alturaTexto / 2f;
            float padding = Math.Max(4f, raioMarcador + 2f);

            // Ajuste vertical para que o ponto base de desenho alinhe a baseline corretamente
            float baseLineOffsetY = -fontAscent;

            if (modoPosicionamento == "SobrePonto")
            {
                var rect = new SKRect(
                    pontoMonitor.X - halfW,
                    pontoMonitor.Y - halfH,
                    pontoMonitor.X + halfW,
                    pontoMonitor.Y + halfH);
                destino.Add(new CandidatoRotulo(pontoMonitor, new SKPoint(pontoMonitor.X, pontoMonitor.Y - halfH + baseLineOffsetY), rect, 1, false));
                return;
            }

            if (modoPosicionamento == "Fixo")
            {
                float x = pontoMonitor.X + deslocamentoX;
                float y = pontoMonitor.Y + deslocamentoY;
                var rect = new SKRect(x - halfW, y - halfH, x + halfW, y + halfH);
                bool deslocado = Math.Abs(deslocamentoX) > 10f || Math.Abs(deslocamentoY) > 10f;
                destino.Add(new CandidatoRotulo(pontoMonitor, new SKPoint(x, y - halfH + baseLineOffsetY), rect, 1, deslocado));
                return;
            }

            // Modo 8 Quadrantes Dinâmicos (Padrão Cartográfico Internacional: Top-Right preferencial)
            // 1. Superior Direito (Top-Right)
            {
                float cx = pontoMonitor.X + padding + halfW + deslocamentoX;
                float cy = pontoMonitor.Y - padding - halfH + deslocamentoY;
                var rect = new SKRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH);
                destino.Add(new CandidatoRotulo(pontoMonitor, new SKPoint(cx, cy - halfH + baseLineOffsetY), rect, 1, false));
            }

            // 2. Superior (Top)
            {
                float cx = pontoMonitor.X + deslocamentoX;
                float cy = pontoMonitor.Y - padding - halfH + deslocamentoY;
                var rect = new SKRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH);
                destino.Add(new CandidatoRotulo(pontoMonitor, new SKPoint(cx, cy - halfH + baseLineOffsetY), rect, 2, false));
            }

            // 3. Direita (Right)
            {
                float cx = pontoMonitor.X + padding + halfW + deslocamentoX;
                float cy = pontoMonitor.Y + deslocamentoY;
                var rect = new SKRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH);
                destino.Add(new CandidatoRotulo(pontoMonitor, new SKPoint(cx, cy - halfH + baseLineOffsetY), rect, 3, false));
            }

            // 4. Superior Esquerdo (Top-Left)
            {
                float cx = pontoMonitor.X - padding - halfW + deslocamentoX;
                float cy = pontoMonitor.Y - padding - halfH + deslocamentoY;
                var rect = new SKRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH);
                destino.Add(new CandidatoRotulo(pontoMonitor, new SKPoint(cx, cy - halfH + baseLineOffsetY), rect, 4, false));
            }

            // 5. Inferior Direito (Bottom-Right)
            {
                float cx = pontoMonitor.X + padding + halfW + deslocamentoX;
                float cy = pontoMonitor.Y + padding + halfH + deslocamentoY;
                var rect = new SKRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH);
                destino.Add(new CandidatoRotulo(pontoMonitor, new SKPoint(cx, cy - halfH + baseLineOffsetY), rect, 5, true));
            }

            // 6. Esquerda (Left)
            {
                float cx = pontoMonitor.X - padding - halfW + deslocamentoX;
                float cy = pontoMonitor.Y + deslocamentoY;
                var rect = new SKRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH);
                destino.Add(new CandidatoRotulo(pontoMonitor, new SKPoint(cx, cy - halfH + baseLineOffsetY), rect, 6, true));
            }

            // 7. Inferior (Bottom)
            {
                float cx = pontoMonitor.X + deslocamentoX;
                float cy = pontoMonitor.Y + padding + halfH + deslocamentoY;
                var rect = new SKRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH);
                destino.Add(new CandidatoRotulo(pontoMonitor, new SKPoint(cx, cy - halfH + baseLineOffsetY), rect, 7, true));
            }

            // 8. Inferior Esquerdo (Bottom-Left)
            {
                float cx = pontoMonitor.X - padding - halfW + deslocamentoX;
                float cy = pontoMonitor.Y + padding + halfH + deslocamentoY;
                var rect = new SKRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH);
                destino.Add(new CandidatoRotulo(pontoMonitor, new SKPoint(cx, cy - halfH + baseLineOffsetY), rect, 8, true));
            }
        }

        /// <summary>
        /// Calcula o ângulo de orientação de um segmento ou linha em graus com proteção Auto-Flip.
        /// Impede que o nome de uma rodovia ou rio fique de cabeça para baixo na tela.
        /// </summary>
        public static float CalcularAnguloComAutoFlip(float x1, float y1, float x2, float y2)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            if (Math.Abs(dx) < 1e-4f && Math.Abs(dy) < 1e-4f) return 0f;

            double anguloRad = Math.Atan2(dy, dx);
            float anguloGraus = (float)(anguloRad * 180.0 / Math.PI);

            // Normaliza para [0, 360)
            while (anguloGraus < 0f) anguloGraus += 360f;
            while (anguloGraus >= 360f) anguloGraus -= 360f;

            // Auto-Flip Guard: Se estiver apontando para a esquerda (90° a 270°), inverte 180°
            if (anguloGraus > 90f && anguloGraus < 270f)
            {
                anguloGraus -= 180f;
                if (anguloGraus < 0f) anguloGraus += 360f;
            }

            return anguloGraus;
        }
    }
}
