using System;
using System.Collections.Generic;
using SkiaSharp;

namespace GeoNex.Core.Symbology
{
    /// <summary>
    /// Sub-camada de marcador vetorial SVG dinamicamente parametrizado (Padrão QGIS SvgMarker).
    /// </summary>
    public class SvgMarkerSymbolLayer : IMarkerSymbolLayer
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Name { get; set; } = "Marcador SVG";
        public MarkerLayerType LayerType => MarkerLayerType.Svg;
        public bool Enabled { get; set; } = true;
        public bool Locked { get; set; } = false;

        public float Size { get; set; } = 12.0f; // Tamanho em pixels/milímetros
        public RenderUnit SizeUnit { get; set; } = RenderUnit.Pixels;
        public float RotationAngle { get; set; } = 0.0f;
        public SKPoint Offset { get; set; } = SKPoint.Empty;
        public RenderUnit OffsetUnit { get; set; } = RenderUnit.Pixels;
        public MarkerAnchorPoint AnchorPoint { get; set; } = MarkerAnchorPoint.Center;
        public float Opacity { get; set; } = 1.0f;

        public string SvgSourcePath { get; set; } = string.Empty;
        public string? EmbeddedSvgContent { get; set; }
        public SKColor FillColor { get; set; } = SKColors.White;
        public SKColor FillSecondaryColor { get; set; } = new SKColor(14, 165, 233);
        public SKColor StrokeColor { get; set; } = new SKColor(2, 132, 199);
        public float StrokeWidth { get; set; } = 1.5f;
        public bool PreserveAspectRatio { get; set; } = true;
        public SKPoint FixedAspectScale { get; set; } = new SKPoint(1f, 1f);

        public DataDefinedPropertyCollection DataDefinedProperties { get; } = new();

        public delegate SKPicture? SvgPictureProvider(string path, string? embeddedXml, SKColor fill, SKColor fillSecondary, SKColor stroke, float strokeWidth);
        public static SvgPictureProvider? PictureProvider { get; set; }

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

            string sourcePath = SvgSourcePath;
            if (DataDefinedProperties.TryGet("SvgSource", out var propSvg) && propSvg?.Enabled == true)
            {
                sourcePath = propSvg.EvaluateString(featureAttributes, sourcePath) ?? sourcePath;
            }

            SKPicture? picture = PictureProvider?.Invoke(sourcePath, EmbeddedSvgContent, effectiveFill, FillSecondaryColor, effectiveStroke, StrokeWidth);
            if (picture == null) return;

            float x = screenPos.X + Offset.X * scaleFactor;
            float y = screenPos.Y + Offset.Y * scaleFactor;

            var cullRect = picture.CullRect;
            float srcW = cullRect.Width > 0 ? cullRect.Width : 32f;
            float srcH = cullRect.Height > 0 ? cullRect.Height : 32f;

            float scaleX = (effectiveSize * FixedAspectScale.X) / srcW;
            float scaleY = (effectiveSize * FixedAspectScale.Y) / srcH;

            int saveCount = canvas.Save();
            canvas.Translate(x, y);

            if (effectiveRotation != 0f)
            {
                canvas.RotateDegrees(effectiveRotation);
            }

            canvas.Scale(scaleX, scaleY);
            // Centraliza o SVG em relação à origem
            canvas.Translate(-srcW * 0.5f, -srcH * 0.5f);

            if (Opacity < 1.0f)
            {
                using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp(Opacity, 0f, 1f))) };
                canvas.SaveLayer(paint);
                canvas.DrawPicture(picture);
                canvas.Restore();
            }
            else
            {
                canvas.DrawPicture(picture);
            }

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
            return new SvgMarkerSymbolLayer
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
                SvgSourcePath = SvgSourcePath,
                EmbeddedSvgContent = EmbeddedSvgContent,
                FillColor = FillColor,
                FillSecondaryColor = FillSecondaryColor,
                StrokeColor = StrokeColor,
                StrokeWidth = StrokeWidth,
                PreserveAspectRatio = PreserveAspectRatio,
                FixedAspectScale = FixedAspectScale
            };
        }
    }
}
