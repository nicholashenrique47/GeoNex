using System;
using System.Buffers;
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
        bool RequerLinhaChamada,
        float RotacaoGraus = 0f
    );

    /// <summary>
    /// Motor Cartográfico de Posicionamento e Geometria de Rótulos (QGIS PAL / Maplex Standard).
    /// Executa cálculos de Polo de Inacessibilidade (Polylabel com distância assinada real),
    /// 8 quadrantes cartesianos, posicionamento curvo/angular ao longo de linhas com Auto-Flip
    /// e transformações tipográficas com cache.
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
        /// Calcula o Polo de Inacessibilidade (Polylabel / Maior Círculo Inscrito) para um polígono SKPath.
        /// Avalia a distância real até as bordas do polígono para garantir que o rótulo de polígonos côncavos
        /// (formato C, L, ferradura, lotes irregulares) fique no ponto mais profundo do interior da feição.
        /// </summary>
        public static SKPoint CalcularPoloInacessibilidade(SKPath path, SKPoint centroideFallback, float precisao = 2.0f)
        {
            if (path == null || path.IsEmpty) return centroideFallback;

            var bounds = path.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return centroideFallback;

            int pointCount = path.PointCount;
            if (pointCount < 3)
            {
                return path.Contains(centroideFallback.X, centroideFallback.Y)
                    ? centroideFallback
                    : new SKPoint(bounds.MidX, bounds.MidY);
            }

            SKPoint[] points = ArrayPool<SKPoint>.Shared.Rent(pointCount);
            try
            {
                path.GetPoints(points, pointCount);
                float melhorX = centroideFallback.X;
                float melhorY = centroideFallback.Y;
                float melhorDistSq = path.Contains(centroideFallback.X, centroideFallback.Y)
                    ? MinDistanceToSegmentsSquared(points, pointCount, centroideFallback.X, centroideFallback.Y)
                    : -1f;

                // Se o centroide já possui ampla folga interna (> 22% da menor dimensão), retorna imediatamente
                float minDim = Math.Min(bounds.Width, bounds.Height);
                float folgaIdealSq = (minDim * 0.22f) * (minDim * 0.22f);
                if (melhorDistSq >= folgaIdealSq && melhorDistSq > 0f)
                {
                    return centroideFallback;
                }

                // Nível 1: Amostragem 5x5 para encontrar a célula de maior distância até a borda
                float stepX = bounds.Width / 5f;
                float stepY = bounds.Height / 5f;

                for (int iy = 0; iy < 5; iy++)
                {
                    float y = bounds.Top + (iy + 0.5f) * stepY;
                    for (int ix = 0; ix < 5; ix++)
                    {
                        float x = bounds.Left + (ix + 0.5f) * stepX;
                        if (!path.Contains(x, y)) continue;

                        float distSq = MinDistanceToSegmentsSquared(points, pointCount, x, y);
                        if (distSq > melhorDistSq)
                        {
                            melhorDistSq = distSq;
                            melhorX = x;
                            melhorY = y;
                        }
                    }
                }

                // Nível 2: Refinamento hierárquico ao redor do melhor candidato
                if (melhorDistSq >= 0f)
                {
                    float radiusX = stepX * 0.75f;
                    float radiusY = stepY * 0.75f;
                    for (int pass = 0; pass < 2; pass++)
                    {
                        float baseBestX = melhorX;
                        float baseBestY = melhorY;
                        float subX = radiusX * 0.5f;
                        float subY = radiusY * 0.5f;

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                float nx = baseBestX + dx * subX;
                                float ny = baseBestY + dy * subY;
                                if (!path.Contains(nx, ny)) continue;

                                float distSq = MinDistanceToSegmentsSquared(points, pointCount, nx, ny);
                                if (distSq > melhorDistSq)
                                {
                                    melhorDistSq = distSq;
                                    melhorX = nx;
                                    melhorY = ny;
                                }
                            }
                        }

                        radiusX = subX;
                        radiusY = subY;
                        if (Math.Max(radiusX, radiusY) <= precisao) break;
                    }

                    return new SKPoint(melhorX, melhorY);
                }

                return centroideFallback;
            }
            finally
            {
                ArrayPool<SKPoint>.Shared.Return(points);
            }
        }

        private static float MinDistanceToSegmentsSquared(SKPoint[] points, int n, float px, float py)
        {
            float minDistSq = float.MaxValue;
            int stride = Math.Max(1, n / 96);

            SKPoint prev = points[0];
            for (int i = stride; i < n; i += stride)
            {
                SKPoint curr = points[i];
                float dx = curr.X - prev.X;
                float dy = curr.Y - prev.Y;
                float lenSq = dx * dx + dy * dy;
                if (lenSq > 1e-6f)
                {
                    float t = ((px - prev.X) * dx + (py - prev.Y) * dy) / lenSq;
                    if (t < 0f) t = 0f;
                    else if (t > 1f) t = 1f;
                    float projX = prev.X + t * dx;
                    float projY = prev.Y + t * dy;
                    float distX = px - projX;
                    float distY = py - projY;
                    float dSq = distX * distX + distY * distY;
                    if (dSq < minDistSq) minDistSq = dSq;
                }
                prev = curr;
            }

            return minDistSq;
        }

        /// <summary>
        /// Gera candidatos de posicionamento para polígonos (centrado no Polo de Inacessibilidade
        /// com micro-ajustes internos e fallback opcional com linha de chamada).
        /// </summary>
        public static void GerarCandidatosPoligono(
            SKPoint pontoMonitor,
            float larguraTexto,
            float alturaTexto,
            float fontAscent,
            float larguraPoligonoPx,
            float alturaPoligonoPx,
            bool permitirLinhaChamada,
            List<CandidatoRotulo> destino)
        {
            destino.Clear();
            float halfW = larguraTexto * 0.5f;
            float halfH = alturaTexto * 0.5f;
            float baseLineOffsetY = -fontAscent;

            // 1. Candidato Central (Polo de Inacessibilidade exato)
            var rectCentro = new SKRect(
                pontoMonitor.X - halfW,
                pontoMonitor.Y - halfH,
                pontoMonitor.X + halfW,
                pontoMonitor.Y + halfH);
            destino.Add(new CandidatoRotulo(
                pontoMonitor,
                new SKPoint(pontoMonitor.X, pontoMonitor.Y - halfH + baseLineOffsetY),
                rectCentro,
                1,
                false));

            // 2..5. Micro-ajustes internos suaves se o polígono tiver espaço folgado
            if (larguraPoligonoPx > larguraTexto * 1.15f && alturaPoligonoPx > alturaTexto * 1.35f)
            {
                float shiftX = Math.Min(halfW * 0.35f, (larguraPoligonoPx - larguraTexto) * 0.35f);
                float shiftY = Math.Min(halfH * 0.45f, (alturaPoligonoPx - alturaTexto) * 0.35f);

                ReadOnlySpan<(float dx, float dy, int prio)> offsets = stackalloc (float, float, int)[]
                {
                    (0f, -shiftY, 2),
                    (0f, shiftY, 3),
                    (shiftX, 0f, 4),
                    (-shiftX, 0f, 5)
                };

                foreach (var (dx, dy, prio) in offsets)
                {
                    float cx = pontoMonitor.X + dx;
                    float cy = pontoMonitor.Y + dy;
                    var rect = new SKRect(cx - halfW, cy - halfH, cx + halfW, cy + halfH);
                    destino.Add(new CandidatoRotulo(
                        pontoMonitor,
                        new SKPoint(cx, cy - halfH + baseLineOffsetY),
                        rect,
                        prio,
                        false));
                }
            }

            // 6..7. Candidatos externos com Linha de Chamada (Callout) quando habilitado
            if (permitirLinhaChamada)
            {
                float calloutOffset = Math.Max(18f, Math.Min(larguraPoligonoPx, alturaPoligonoPx) * 0.4f);
                float cx1 = pontoMonitor.X + calloutOffset + halfW;
                float cy1 = pontoMonitor.Y - calloutOffset - halfH;
                destino.Add(new CandidatoRotulo(
                    pontoMonitor,
                    new SKPoint(cx1, cy1 - halfH + baseLineOffsetY),
                    new SKRect(cx1 - halfW, cy1 - halfH, cx1 + halfW, cy1 + halfH),
                    6,
                    true));

                float cx2 = pontoMonitor.X - calloutOffset - halfW;
                float cy2 = pontoMonitor.Y - calloutOffset - halfH;
                destino.Add(new CandidatoRotulo(
                    pontoMonitor,
                    new SKPoint(cx2, cy2 - halfH + baseLineOffsetY),
                    new SKRect(cx2 - halfW, cy2 - halfH, cx2 + halfW, cy2 + halfH),
                    7,
                    true));
            }
        }

        /// <summary>
        /// Gera candidatos de posicionamento alinhados à tangente de uma linha (com proteção Auto-Flip).
        /// </summary>
        public static void GerarCandidatosLinha(
            SKPoint pontoMonitor,
            float larguraTexto,
            float alturaTexto,
            float fontAscent,
            float anguloGraus,
            float espessuraLinhaPx,
            List<CandidatoRotulo> destino)
        {
            destino.Clear();
            float halfW = larguraTexto * 0.5f;
            float halfH = alturaTexto * 0.5f;
            float baseLineOffsetY = -fontAscent;

            float rad = anguloGraus * (MathF.PI / 180f);
            float cos = MathF.Cos(rad);
            float sin = MathF.Sin(rad);

            // Vetor normal apontando "para cima" da linha
            float nx = sin;
            float ny = -cos;
            float distNormal = Math.Max(3f, espessuraLinhaPx * 0.5f + halfH * 0.65f);

            // Envoltória rotacionada aproximada (OBB proyectado em AABB na tela)
            float aabbHalfW = Math.Abs(halfW * cos) + Math.Abs(halfH * sin) + 2f;
            float aabbHalfH = Math.Abs(halfW * sin) + Math.Abs(halfH * cos) + 2f;

            // 1. Acima da linha no ponto médio (Padrão Cartográfico de Rodovias/Hidrografia)
            {
                float cx = pontoMonitor.X + nx * distNormal;
                float cy = pontoMonitor.Y + ny * distNormal;
                var rect = new SKRect(cx - aabbHalfW, cy - aabbHalfH, cx + aabbHalfW, cy + aabbHalfH);
                destino.Add(new CandidatoRotulo(
                    pontoMonitor,
                    new SKPoint(cx, cy - halfH + baseLineOffsetY),
                    rect,
                    1,
                    false,
                    anguloGraus));
            }

            // 2. Sobre o eixo da linha (com halo)
            {
                var rect = new SKRect(
                    pontoMonitor.X - aabbHalfW,
                    pontoMonitor.Y - aabbHalfH,
                    pontoMonitor.X + aabbHalfW,
                    pontoMonitor.Y + aabbHalfH);
                destino.Add(new CandidatoRotulo(
                    pontoMonitor,
                    new SKPoint(pontoMonitor.X, pontoMonitor.Y - halfH + baseLineOffsetY),
                    rect,
                    2,
                    false,
                    anguloGraus));
            }

            // 3. Abaixo da linha
            {
                float cx = pontoMonitor.X - nx * distNormal;
                float cy = pontoMonitor.Y - ny * distNormal;
                var rect = new SKRect(cx - aabbHalfW, cy - aabbHalfH, cx + aabbHalfW, cy + aabbHalfH);
                destino.Add(new CandidatoRotulo(
                    pontoMonitor,
                    new SKPoint(cx, cy - halfH + baseLineOffsetY),
                    rect,
                    3,
                    false,
                    anguloGraus));
            }

            // 4 & 5. Deslizamento tangencial ao longo da linha (+40% / -40% do comprimento do texto)
            float slide = larguraTexto * 0.42f;
            {
                float cx = pontoMonitor.X + cos * slide + nx * distNormal;
                float cy = pontoMonitor.Y + sin * slide + ny * distNormal;
                var rect = new SKRect(cx - aabbHalfW, cy - aabbHalfH, cx + aabbHalfW, cy + aabbHalfH);
                destino.Add(new CandidatoRotulo(
                    pontoMonitor,
                    new SKPoint(cx, cy - halfH + baseLineOffsetY),
                    rect,
                    4,
                    false,
                    anguloGraus));
            }
            {
                float cx = pontoMonitor.X - cos * slide + nx * distNormal;
                float cy = pontoMonitor.Y - sin * slide + ny * distNormal;
                var rect = new SKRect(cx - aabbHalfW, cy - aabbHalfH, cx + aabbHalfW, cy + aabbHalfH);
                destino.Add(new CandidatoRotulo(
                    pontoMonitor,
                    new SKPoint(cx, cy - halfH + baseLineOffsetY),
                    rect,
                    5,
                    false,
                    anguloGraus));
            }
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

            if (modoPosicionamento == "Fixo" || modoPosicionamento == "TopRight")
            {
                float x = modoPosicionamento == "TopRight"
                    ? pontoMonitor.X + padding + halfW + deslocamentoX
                    : pontoMonitor.X + deslocamentoX;
                float y = modoPosicionamento == "TopRight"
                    ? pontoMonitor.Y - padding - halfH + deslocamentoY
                    : pontoMonitor.Y + deslocamentoY;
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
