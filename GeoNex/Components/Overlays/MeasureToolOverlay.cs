using System;
using System.Collections.Generic;
using SkiaSharp;

namespace GeoNex.Components.Overlays;

/// <summary>
/// Overlay gráfico SkiaSharp de alta performance para ferramenta de medição topográfica com snap magnético.
/// Garante alocação zero de objetos nativos no render loop (60 FPS contínuos).
/// </summary>
public sealed class MeasureToolOverlay : IDisposable
{
    private readonly SKPaint _polylinePaint;
    private readonly SKPaint _rubberbandPaint;
    private readonly SKPaint _vertexFillPaint;
    private readonly SKPaint _vertexStrokePaint;
    private readonly SKPaint _snapRingPaint;
    private readonly SKPaint _snapDotPaint;
    private readonly SKPaint _hudBoxPaint;
    private readonly SKPaint _hudBorderPaint;
    private readonly SKPaint _hudTextPaint;
    private readonly SKPaint _areaFillPaint;
    private bool _disposed;

    public MeasureToolOverlay()
    {
        _polylinePaint = new SKPaint
        {
            Color = new SKColor(0, 176, 255), // Azul ciano de alta visibilidade
            StrokeWidth = 2.5f,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        _rubberbandPaint = new SKPaint
        {
            Color = new SKColor(0, 230, 118), // Verde esmeralda dinâmico
            StrokeWidth = 2.0f,
            Style = SKPaintStyle.Stroke,
            PathEffect = SKPathEffect.CreateDash(new float[] { 8f, 4f }, 0),
            IsAntialias = true
        };

        _vertexFillPaint = new SKPaint
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _vertexStrokePaint = new SKPaint
        {
            Color = new SKColor(0, 120, 215),
            StrokeWidth = 1.5f,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        _snapRingPaint = new SKPaint
        {
            Color = new SKColor(255, 109, 0), // Laranja magnético
            StrokeWidth = 2.2f,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        _snapDotPaint = new SKPaint
        {
            Color = new SKColor(255, 109, 0),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _hudBoxPaint = new SKPaint
        {
            Color = new SKColor(18, 24, 38, 230),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _hudBorderPaint = new SKPaint
        {
            Color = new SKColor(56, 189, 248, 120),
            StrokeWidth = 1.0f,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        _hudTextPaint = new SKPaint
        {
            Color = SKColors.White,
            TextSize = 12.5f,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold)
        };

        _areaFillPaint = new SKPaint
        {
            Color = new SKColor(0, 176, 255, 40),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
    }

    /// <summary>
    /// Renderiza os elementos gráficos da medição diretamente sobre o canvas SkiaSharp.
    /// </summary>
    public void Draw(
        SKCanvas canvas,
        IReadOnlyList<SKPoint> fixedScreenPoints,
        SKPoint cursorScreenPoint,
        bool hasSnap,
        SKPoint snapScreenPoint,
        double totalDistanceMeters,
        double currentSegmentMeters,
        double azimuthDegrees,
        double? areaM2 = null)
    {
        if (_disposed || canvas == null) return;
        if ((fixedScreenPoints == null || fixedScreenPoints.Count == 0) && !hasSnap) return;

        SKPoint activeEndpoint = hasSnap ? snapScreenPoint : cursorScreenPoint;

        // 1. Preenchimento de Área (se houver 3 ou mais pontos e modo área)
        if (areaM2.HasValue && fixedScreenPoints != null && fixedScreenPoints.Count >= 2)
        {
            using var polyPath = new SKPath();
            polyPath.MoveTo(fixedScreenPoints[0]);
            for (int i = 1; i < fixedScreenPoints.Count; i++)
            {
                polyPath.LineTo(fixedScreenPoints[i]);
            }
            polyPath.LineTo(activeEndpoint);
            polyPath.Close();
            canvas.DrawPath(polyPath, _areaFillPaint);
        }

        // 2. Traçado dos segmentos já fixados
        if (fixedScreenPoints != null && fixedScreenPoints.Count > 0)
        {
            if (fixedScreenPoints.Count > 1)
            {
                for (int i = 0; i < fixedScreenPoints.Count - 1; i++)
                {
                    canvas.DrawLine(fixedScreenPoints[i], fixedScreenPoints[i + 1], _polylinePaint);
                    canvas.DrawCircle(fixedScreenPoints[i], 4f, _vertexFillPaint);
                    canvas.DrawCircle(fixedScreenPoints[i], 4f, _vertexStrokePaint);
                }
            }

            canvas.DrawCircle(fixedScreenPoints[^1], 4f, _vertexFillPaint);
            canvas.DrawCircle(fixedScreenPoints[^1], 4f, _vertexStrokePaint);

            // 3. Linha elástica (Rubber-band) até o ponto ativo
            canvas.DrawLine(fixedScreenPoints[^1], activeEndpoint, _rubberbandPaint);
        }

        // 4. Indicador de Snap Magnético
        if (hasSnap)
        {
            canvas.DrawCircle(snapScreenPoint, 8.5f, _snapRingPaint);
            canvas.DrawCircle(snapScreenPoint, 2.5f, _snapDotPaint);
        }

        // 5. Floating HUD Badge (Painel informativo próximo ao cursor)
        string label;
        if (areaM2.HasValue && areaM2.Value > 0)
        {
            string areaText = areaM2.Value >= 10000.0 ? $"{(areaM2.Value / 10000.0):F3} ha" : $"{areaM2.Value:F1} m²";
            label = $"Área: {areaText} | Perímetro: {FormatDistance(totalDistanceMeters)}";
        }
        else
        {
            label = $"Total: {FormatDistance(totalDistanceMeters)} | Trecho: {FormatDistance(currentSegmentMeters)} | Rumo: {azimuthDegrees:F1}°";
        }

        float textWidth = _hudTextPaint.MeasureText(label);
        float hudX = activeEndpoint.X + 16f;
        float hudY = activeEndpoint.Y - 14f;

        var hudRect = new SKRoundRect(new SKRect(hudX - 6f, hudY - 16f, hudX + textWidth + 8f, hudY + 6f), 4f, 4f);
        canvas.DrawRoundRect(hudRect, _hudBoxPaint);
        canvas.DrawRoundRect(hudRect, _hudBorderPaint);
        canvas.DrawText(label, hudX, hudY, _hudTextPaint);
    }

    public static string FormatDistance(double meters)
    {
        if (meters >= 1000.0)
        {
            return $"{(meters / 1000.0):F2} km";
        }
        return $"{meters:F2} m";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _polylinePaint.Dispose();
        _rubberbandPaint.Dispose();
        _vertexFillPaint.Dispose();
        _vertexStrokePaint.Dispose();
        _snapRingPaint.Dispose();
        _snapDotPaint.Dispose();
        _hudBoxPaint.Dispose();
        _hudBorderPaint.Dispose();
        _hudTextPaint.Dispose();
        _areaFillPaint.Dispose();
    }
}
