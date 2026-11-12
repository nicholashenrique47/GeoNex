using System;
using System.Collections.Generic;
using SkiaSharp;

namespace GeoNex.Core.Symbology
{
    /// <summary>
    /// Sub-camada de marcador geométrico simples (Círculo, Quadrado, Losango, etc.) estilo QGIS.
    /// </summary>
    public class SimpleMarkerSymbolLayer : IMarkerSymbolLayer
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Name { get; set; } = "Marcador Simples";
        public MarkerLayerType LayerType => MarkerLayerType.Simple;
        public bool Enabled { get; set; } = true;
        public bool Locked { get; set; } = false;

        public float Size { get; set; } = 6.0f; // em pixels/milímetros
        public RenderUnit SizeUnit { get; set; } = RenderUnit.Pixels;
        public float RotationAngle { get; set; } = 0.0f; // 0 a 360 graus
        public SKPoint Offset { get; set; } = SKPoint.Empty;
        public RenderUnit OffsetUnit { get; set; } = RenderUnit.Pixels;
        public MarkerAnchorPoint AnchorPoint { get; set; } = MarkerAnchorPoint.Center;
        public float Opacity { get; set; } = 1.0f;

        public MarkerShapeType Shape { get; set; } = MarkerShapeType.Circle;
        public SKColor FillColor { get; set; } = SKColors.SkyBlue;
        public SKColor StrokeColor { get; set; } = SKColors.DeepSkyBlue;
        public float StrokeWidth { get; set; } = 1.0f;
        public SKStrokeCap StrokeCap { get; set; } = SKStrokeCap.Round;
        public SKStrokeJoin StrokeJoin { get; set; } = SKStrokeJoin.Round;

        public DataDefinedPropertyCollection DataDefinedProperties { get; } = new();

        [ThreadStatic]
        private static SKPaint? t_fillPaint;
        [ThreadStatic]
        private static SKPaint? t_strokePaint;
        [ThreadStatic]
        private static SKPath? t_renderPath;

        private static SKPaint GetFillPaint() => t_fillPaint ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        private static SKPaint GetStrokePaint() => t_strokePaint ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        private static SKPath GetRenderPath()
        {
            var p = t_renderPath ??= new SKPath();
            p.Reset();
            return p;
        }

        public void Render(SKCanvas canvas, SKPoint screenPos, float scaleFactor, float dynamicRotation, IReadOnlyDictionary<string, object?>? featureAttributes = null)
        {
            if (!Enabled) return;

            float effectiveSize = Size * scaleFactor;
            if (DataDefinedProperties.TryGet("Size", out var propSize) && propSize?.Enabled == true)
            {
                effectiveSize = propSize.EvaluateFloat(featureAttributes, effectiveSize) * scaleFactor;
            }

            if (effectiveSize <= 0.5f) return;

            float effectiveRotation = RotationAngle + dynamicRotation;
            if (DataDefinedProperties.TryGet("Rotation", out var propRot) && propRot?.Enabled == true)
            {
                effectiveRotation += propRot.EvaluateFloat(featureAttributes, 0f);
            }

            SKColor effectiveFill = FillColor;
            if (DataDefinedProperties.TryGet("FillColor", out var propFill) && propFill?.Enabled == true)
            {
                effectiveFill = propFill.EvaluateColor(featureAttributes, effectiveFill);
            }

            SKColor effectiveStroke = StrokeColor;
            if (DataDefinedProperties.TryGet("StrokeColor", out var propStroke) && propStroke?.Enabled == true)
            {
                effectiveStroke = propStroke.EvaluateColor(featureAttributes, effectiveStroke);
            }

            if (Opacity < 1.0f)
            {
                byte a = (byte)(effectiveFill.Alpha * Math.Clamp(Opacity, 0f, 1f));
                effectiveFill = effectiveFill.WithAlpha(a);

                byte sa = (byte)(effectiveStroke.Alpha * Math.Clamp(Opacity, 0f, 1f));
                effectiveStroke = effectiveStroke.WithAlpha(sa);
            }

            float effectiveStrokeWidth = StrokeWidth * scaleFactor;

            float x = screenPos.X + Offset.X * scaleFactor;
            float y = screenPos.Y + Offset.Y * scaleFactor;
            float radius = effectiveSize * 0.5f;

            int saveCount = canvas.Save();
            canvas.Translate(x, y);
            if (effectiveRotation != 0f)
            {
                canvas.RotateDegrees(effectiveRotation);
            }

            var fillPaint = GetFillPaint();
            fillPaint.Color = effectiveFill;

            var strokePaint = GetStrokePaint();
            strokePaint.Color = effectiveStroke;
            strokePaint.StrokeWidth = effectiveStrokeWidth;
            strokePaint.StrokeCap = StrokeCap;
            strokePaint.StrokeJoin = StrokeJoin;

            bool hasFill = effectiveFill.Alpha > 0;
            bool hasStroke = effectiveStroke.Alpha > 0 && effectiveStrokeWidth > 0f;

            switch (Shape)
            {
                case MarkerShapeType.Circle:
                    if (hasFill) canvas.DrawCircle(0, 0, radius, fillPaint);
                    if (hasStroke) canvas.DrawCircle(0, 0, radius, strokePaint);
                    break;

                case MarkerShapeType.Square:
                    var rect = new SKRect(-radius, -radius, radius, radius);
                    if (hasFill) canvas.DrawRect(rect, fillPaint);
                    if (hasStroke) canvas.DrawRect(rect, strokePaint);
                    break;

                case MarkerShapeType.Diamond:
                case MarkerShapeType.Triangle:
                case MarkerShapeType.TriangleInverted:
                case MarkerShapeType.Cross:
                case MarkerShapeType.CrossRotatedX:
                case MarkerShapeType.Star:
                case MarkerShapeType.Pentagon:
                case MarkerShapeType.Hexagon:
                case MarkerShapeType.Octagon:
                case MarkerShapeType.ArrowUp:
                case MarkerShapeType.ArrowDown:
                    var path = BuildShapePath(Shape, radius);
                    if (hasFill) canvas.DrawPath(path, fillPaint);
                    if (hasStroke) canvas.DrawPath(path, strokePaint);
                    break;

                case MarkerShapeType.LineHorizontal:
                    if (hasStroke) canvas.DrawLine(-radius, 0, radius, 0, strokePaint);
                    break;

                case MarkerShapeType.LineVertical:
                    if (hasStroke) canvas.DrawLine(0, -radius, 0, radius, strokePaint);
                    break;

                default:
                    if (hasFill) canvas.DrawCircle(0, 0, radius, fillPaint);
                    if (hasStroke) canvas.DrawCircle(0, 0, radius, strokePaint);
                    break;
            }

            canvas.RestoreToCount(saveCount);
        }

        private static SKPath BuildShapePath(MarkerShapeType shape, float r)
        {
            var p = GetRenderPath();
            switch (shape)
            {
                case MarkerShapeType.Diamond:
                    p.MoveTo(0, -r);
                    p.LineTo(r, 0);
                    p.LineTo(0, r);
                    p.LineTo(-r, 0);
                    p.Close();
                    break;

                case MarkerShapeType.Triangle:
                    p.MoveTo(0, -r);
                    p.LineTo(r * 0.866f, r * 0.5f);
                    p.LineTo(-r * 0.866f, r * 0.5f);
                    p.Close();
                    break;

                case MarkerShapeType.TriangleInverted:
                    p.MoveTo(0, r);
                    p.LineTo(r * 0.866f, -r * 0.5f);
                    p.LineTo(-r * 0.866f, -r * 0.5f);
                    p.Close();
                    break;

                case MarkerShapeType.Cross:
                    float arm = r * 0.35f;
                    p.MoveTo(-arm, -r); p.LineTo(arm, -r); p.LineTo(arm, -arm);
                    p.LineTo(r, -arm); p.LineTo(r, arm); p.LineTo(arm, arm);
                    p.LineTo(arm, r); p.LineTo(-arm, r); p.LineTo(-arm, arm);
                    p.LineTo(-r, arm); p.LineTo(-r, -arm); p.LineTo(-arm, -arm);
                    p.Close();
                    break;

                case MarkerShapeType.CrossRotatedX:
                    p.MoveTo(-r, -r); p.LineTo(r, r);
                    p.MoveTo(r, -r); p.LineTo(-r, r);
                    break;

                case MarkerShapeType.Star:
                    for (int i = 0; i < 10; i++)
                    {
                        float currentRadius = (i % 2 == 0) ? r : r * 0.45f;
                        float angle = (float)(i * Math.PI / 5.0 - Math.PI / 2.0);
                        float px = (float)(currentRadius * Math.Cos(angle));
                        float py = (float)(currentRadius * Math.Sin(angle));
                        if (i == 0) p.MoveTo(px, py);
                        else p.LineTo(px, py);
                    }
                    p.Close();
                    break;

                case MarkerShapeType.Pentagon:
                    for (int i = 0; i < 5; i++)
                    {
                        float angle = (float)(i * 2.0 * Math.PI / 5.0 - Math.PI / 2.0);
                        float px = (float)(r * Math.Cos(angle));
                        float py = (float)(r * Math.Sin(angle));
                        if (i == 0) p.MoveTo(px, py);
                        else p.LineTo(px, py);
                    }
                    p.Close();
                    break;

                case MarkerShapeType.Hexagon:
                    for (int i = 0; i < 6; i++)
                    {
                        float angle = (float)(i * 2.0 * Math.PI / 6.0);
                        float px = (float)(r * Math.Cos(angle));
                        float py = (float)(r * Math.Sin(angle));
                        if (i == 0) p.MoveTo(px, py);
                        else p.LineTo(px, py);
                    }
                    p.Close();
                    break;

                case MarkerShapeType.Octagon:
                    for (int i = 0; i < 8; i++)
                    {
                        float angle = (float)(i * 2.0 * Math.PI / 8.0 + Math.PI / 8.0);
                        float px = (float)(r * Math.Cos(angle));
                        float py = (float)(r * Math.Sin(angle));
                        if (i == 0) p.MoveTo(px, py);
                        else p.LineTo(px, py);
                    }
                    p.Close();
                    break;

                case MarkerShapeType.ArrowUp:
                    p.MoveTo(0, -r);
                    p.LineTo(r * 0.8f, r * 0.2f);
                    p.LineTo(r * 0.35f, r * 0.2f);
                    p.LineTo(r * 0.35f, r);
                    p.LineTo(-r * 0.35f, r);
                    p.LineTo(-r * 0.35f, r * 0.2f);
                    p.LineTo(-r * 0.8f, r * 0.2f);
                    p.Close();
                    break;

                case MarkerShapeType.ArrowDown:
                    p.MoveTo(0, r);
                    p.LineTo(r * 0.8f, -r * 0.2f);
                    p.LineTo(r * 0.35f, -r * 0.2f);
                    p.LineTo(r * 0.35f, -r);
                    p.LineTo(-r * 0.35f, -r);
                    p.LineTo(-r * 0.35f, -r * 0.2f);
                    p.LineTo(-r * 0.8f, -r * 0.2f);
                    p.Close();
                    break;

                default:
                    p.AddCircle(0, 0, r);
                    break;
            }
            return p;
        }

        public SKRect CalculateBounds(SKPoint screenPos, float scaleFactor)
        {
            float half = Size * scaleFactor * 0.5f;
            return new SKRect(
                screenPos.X + Offset.X * scaleFactor - half,
                screenPos.Y + Offset.Y * scaleFactor - half,
                screenPos.X + Offset.X * scaleFactor + half,
                screenPos.Y + Offset.Y * scaleFactor + half
            );
        }

        public IMarkerSymbolLayer Clone()
        {
            return new SimpleMarkerSymbolLayer
            {
                Id = Guid.NewGuid(),
                Name = Name,
                Enabled = Enabled,
                Locked = Locked,
                Size = Size,
                SizeUnit = SizeUnit,
                RotationAngle = RotationAngle,
                Offset = Offset,
                OffsetUnit = OffsetUnit,
                AnchorPoint = AnchorPoint,
                Opacity = Opacity,
                Shape = Shape,
                FillColor = FillColor,
                StrokeColor = StrokeColor,
                StrokeWidth = StrokeWidth,
                StrokeCap = StrokeCap,
                StrokeJoin = StrokeJoin
            };
        }
    }
}
