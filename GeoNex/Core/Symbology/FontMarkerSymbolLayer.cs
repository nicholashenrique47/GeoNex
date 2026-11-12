using System;
using System.Collections.Generic;
using SkiaSharp;

namespace GeoNex.Core.Symbology
{
    /// <summary>
    /// Sub-camada de marcador tipográfico baseado em glifos TrueType / OpenType (Padrão QGIS FontMarker).
    /// </summary>
    public class FontMarkerSymbolLayer : IMarkerSymbolLayer
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Name { get; set; } = "Marcador de Fonte";
        public MarkerLayerType LayerType => MarkerLayerType.Font;
        public bool Enabled { get; set; } = true;
        public bool Locked { get; set; } = false;

        public float Size { get; set; } = 12.0f;
        public RenderUnit SizeUnit { get; set; } = RenderUnit.Points;
        public float RotationAngle { get; set; } = 0.0f;
        public SKPoint Offset { get; set; } = SKPoint.Empty;
        public RenderUnit OffsetUnit { get; set; } = RenderUnit.Pixels;
        public MarkerAnchorPoint AnchorPoint { get; set; } = MarkerAnchorPoint.Center;
        public float Opacity { get; set; } = 1.0f;

        public string FontFamily { get; set; } = "Segoe UI Symbol";
        public string CharacterCode { get; set; } = "★"; // Caractere ou código unicode
        public SKColor TextColor { get; set; } = SKColors.Black;
        public SKColor StrokeColor { get; set; } = SKColors.Transparent;
        public float StrokeWidth { get; set; } = 0.0f;

        public DataDefinedPropertyCollection DataDefinedProperties { get; } = new();

        [ThreadStatic]
        private static SKPaint? t_fontFillPaint;
        [ThreadStatic]
        private static SKPaint? t_fontStrokePaint;

        private static SKPaint GetFontFillPaint() => t_fontFillPaint ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        private static SKPaint GetFontStrokePaint() => t_fontStrokePaint ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };

        public void Render(SKCanvas canvas, SKPoint screenPos, float scaleFactor, float dynamicRotation, IReadOnlyDictionary<string, object?>? featureAttributes = null)
        {
            if (!Enabled || string.IsNullOrEmpty(CharacterCode)) return;

            float effectiveSize = Size * scaleFactor;
            if (DataDefinedProperties.TryGet("Size", out var propSize) && propSize?.Enabled == true)
            {
                effectiveSize = propSize.EvaluateFloat(featureAttributes, effectiveSize) * scaleFactor;
            }

            if (effectiveSize <= 1.0f) return;

            float effectiveRotation = RotationAngle + dynamicRotation;
            if (DataDefinedProperties.TryGet("Rotation", out var propRot) && propRot?.Enabled == true)
            {
                effectiveRotation += propRot.EvaluateFloat(featureAttributes, 0f);
            }

            SKColor effectiveColor = TextColor;
            if (DataDefinedProperties.TryGet("FillColor", out var propColor) && propColor?.Enabled == true)
            {
                effectiveColor = propColor.EvaluateColor(featureAttributes, effectiveColor);
            }

            string character = CharacterCode;
            if (DataDefinedProperties.TryGet("Character", out var propChar) && propChar?.Enabled == true)
            {
                character = propChar.EvaluateString(featureAttributes, character) ?? character;
            }

            float x = screenPos.X + Offset.X * scaleFactor;
            float y = screenPos.Y + Offset.Y * scaleFactor;

            using var typeface = SKTypeface.FromFamilyName(FontFamily, SKFontStyle.Normal) ?? SKTypeface.Default;
            using var font = new SKFont(typeface, effectiveSize);

            font.MeasureText(character, out SKRect bounds);

            int saveCount = canvas.Save();
            canvas.Translate(x, y);

            if (effectiveRotation != 0f)
            {
                canvas.RotateDegrees(effectiveRotation);
            }

            // Centraliza o glifo
            float textX = -bounds.MidX;
            float textY = -bounds.MidY;

            var fillPaint = GetFontFillPaint();
            fillPaint.Color = Opacity < 1.0f ? effectiveColor.WithAlpha((byte)(effectiveColor.Alpha * Math.Clamp(Opacity, 0f, 1f))) : effectiveColor;

            if (StrokeColor.Alpha > 0 && StrokeWidth > 0f)
            {
                var strokePaint = GetFontStrokePaint();
                strokePaint.Color = StrokeColor;
                strokePaint.StrokeWidth = StrokeWidth * scaleFactor;
                canvas.DrawText(character, textX, textY, font, strokePaint);
            }

            canvas.DrawText(character, textX, textY, font, fillPaint);
            canvas.RestoreToCount(saveCount);
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
            return new FontMarkerSymbolLayer
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
                FontFamily = FontFamily,
                CharacterCode = CharacterCode,
                TextColor = TextColor,
                StrokeColor = StrokeColor,
                StrokeWidth = StrokeWidth
            };
        }
    }
}
