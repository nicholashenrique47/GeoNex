using System;
using System.Collections.Generic;
using SkiaSharp;

namespace GeoNex.Core.Symbology
{
    public enum MarkerLayerType
    {
        Simple,
        Svg,
        Font
    }

    public enum RenderUnit
    {
        Pixels,
        Millimeters,
        Points,
        MapUnits
    }

    public enum MarkerAnchorPoint
    {
        Center,
        Top,
        Bottom,
        Left,
        Right,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    public enum MarkerShapeType
    {
        Circle,
        Square,
        Diamond,
        Triangle,
        TriangleInverted,
        Cross,
        CrossRotatedX,
        Star,
        Pentagon,
        Hexagon,
        Octagon,
        SemiCircle,
        ThirdArc,
        QuarterArc,
        LineHorizontal,
        LineVertical,
        ArrowUp,
        ArrowDown
    }

    /// <summary>
    /// Contrato universal para sub-camadas de marcadores cartográficos de ponto (Padrão Composite QGIS).
    /// </summary>
    public interface IMarkerSymbolLayer
    {
        Guid Id { get; init; }
        string Name { get; set; }
        MarkerLayerType LayerType { get; }
        bool Enabled { get; set; }
        bool Locked { get; set; }
        float Size { get; set; }
        RenderUnit SizeUnit { get; set; }
        float RotationAngle { get; set; }
        SKPoint Offset { get; set; }
        RenderUnit OffsetUnit { get; set; }
        MarkerAnchorPoint AnchorPoint { get; set; }
        float Opacity { get; set; }
        DataDefinedPropertyCollection DataDefinedProperties { get; }

        void Render(SKCanvas canvas, SKPoint screenPos, float scaleFactor, float dynamicRotation, IReadOnlyDictionary<string, object?>? featureAttributes = null);
        SKRect CalculateBounds(SKPoint screenPos, float scaleFactor);
        IMarkerSymbolLayer Clone();
    }
}
