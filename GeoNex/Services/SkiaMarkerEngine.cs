using System;
using System.Collections.Generic;
using GeoNex.Core.Symbology;
using SkiaSharp;

namespace GeoNex.Services
{
    /// <summary>
    /// Fachada central de renderização acelerada de marcadores e pontos para SkiaSharp (padrão QGIS Composite Engine).
    /// </summary>
    public static class SkiaMarkerEngine
    {
        public static void RenderMarker(
            SKCanvas canvas,
            SKPoint screenPos,
            EstiloCamada estilo,
            float scaleFactor,
            float rotation,
            float proportionalSizeFactor = 1.0f,
            IReadOnlyDictionary<string, object?>? attributes = null)
        {
            var symbol = estilo.ObterSimboloComposto();
            if (symbol == null || symbol.Layers.Count == 0) return;

            float effectiveScale = scaleFactor * proportionalSizeFactor;
            symbol.Render(canvas, screenPos, effectiveScale, rotation, attributes);
        }

        public static void DrawSimpleMarker(
            SKCanvas canvas,
            SimpleMarkerSymbolLayer layer,
            SKPoint screenPos,
            float scaleFactor,
            float dynamicRotation,
            IReadOnlyDictionary<string, object?>? attributes = null)
        {
            layer.Render(canvas, screenPos, scaleFactor, dynamicRotation, attributes);
        }

        public static void DrawSvgMarker(
            SKCanvas canvas,
            SvgMarkerSymbolLayer layer,
            SKPoint screenPos,
            float scaleFactor,
            float dynamicRotation,
            IReadOnlyDictionary<string, object?>? attributes = null)
        {
            layer.Render(canvas, screenPos, scaleFactor, dynamicRotation, attributes);
        }

        public static void DrawFontMarker(
            SKCanvas canvas,
            FontMarkerSymbolLayer layer,
            SKPoint screenPos,
            float scaleFactor,
            float dynamicRotation,
            IReadOnlyDictionary<string, object?>? attributes = null)
        {
            layer.Render(canvas, screenPos, scaleFactor, dynamicRotation, attributes);
        }
    }
}
