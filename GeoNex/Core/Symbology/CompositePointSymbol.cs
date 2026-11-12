using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SkiaSharp;

namespace GeoNex.Core.Symbology
{
    /// <summary>
    /// Container agregador multi-camadas de um ponto geográfico completo (Padrão Composite QGIS).
    /// </summary>
    public class CompositePointSymbol
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Name { get; set; } = "Símbolo Composto";
        public ObservableCollection<IMarkerSymbolLayer> Layers { get; } = new();
        public float Opacity { get; set; } = 1.0f;
        public bool EnableClustering { get; set; } = false;

        public CompositePointSymbol()
        {
        }

        public void AddLayer(IMarkerSymbolLayer layer)
        {
            Layers.Add(layer);
        }

        public bool RemoveLayer(Guid layerId)
        {
            var layer = Layers.FirstOrDefault(l => l.Id == layerId);
            if (layer != null)
            {
                return Layers.Remove(layer);
            }
            return false;
        }

        public void MoveLayerUp(Guid layerId)
        {
            int index = -1;
            for (int i = 0; i < Layers.Count; i++)
            {
                if (Layers[i].Id == layerId)
                {
                    index = i;
                    break;
                }
            }

            if (index > 0)
            {
                var item = Layers[index];
                Layers.RemoveAt(index);
                Layers.Insert(index - 1, item);
            }
        }

        public void MoveLayerDown(Guid layerId)
        {
            int index = -1;
            for (int i = 0; i < Layers.Count; i++)
            {
                if (Layers[i].Id == layerId)
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0 && index < Layers.Count - 1)
            {
                var item = Layers[index];
                Layers.RemoveAt(index);
                Layers.Insert(index + 1, item);
            }
        }

        public IMarkerSymbolLayer? DuplicateLayer(Guid layerId)
        {
            var layer = Layers.FirstOrDefault(l => l.Id == layerId);
            if (layer != null)
            {
                var clone = layer.Clone();
                clone.Name += " (Cópia)";
                int index = Layers.IndexOf(layer);
                Layers.Insert(index + 1, clone);
                return clone;
            }
            return null;
        }

        public void Render(SKCanvas canvas, SKPoint screenPos, float scaleFactor, float dynamicRotation, IReadOnlyDictionary<string, object?>? featureAttributes = null)
        {
            if (Layers.Count == 0) return;

            int saveCount = 0;
            bool applyGlobalOpacity = Opacity < 1.0f;
            if (applyGlobalOpacity)
            {
                saveCount = canvas.Save();
                using var p = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp(Opacity, 0f, 1f))) };
                canvas.SaveLayer(p);
            }

            for (int i = 0; i < Layers.Count; i++)
            {
                var layer = Layers[i];
                if (layer.Enabled)
                {
                    layer.Render(canvas, screenPos, scaleFactor, dynamicRotation, featureAttributes);
                }
            }

            if (applyGlobalOpacity)
            {
                canvas.RestoreToCount(saveCount);
            }
        }

        public SKRect CalculateBounds(SKPoint screenPos, float scaleFactor)
        {
            if (Layers.Count == 0)
                return new SKRect(screenPos.X, screenPos.Y, screenPos.X, screenPos.Y);

            SKRect bounds = SKRect.Empty;
            bool first = true;
            for (int i = 0; i < Layers.Count; i++)
            {
                if (!Layers[i].Enabled) continue;
                var b = Layers[i].CalculateBounds(screenPos, scaleFactor);
                if (first)
                {
                    bounds = b;
                    first = false;
                }
                else
                {
                    bounds.Union(b);
                }
            }
            return bounds;
        }

        public CompositePointSymbol Clone()
        {
            var clone = new CompositePointSymbol
            {
                Id = Guid.NewGuid(),
                Name = Name,
                Opacity = Opacity,
                EnableClustering = EnableClustering
            };
            foreach (var layer in Layers)
            {
                clone.Layers.Add(layer.Clone());
            }
            return clone;
        }

        public static CompositePointSymbol CreateDefaultSimple(string shape = "Circle", string fillHex = "#0284c7", string strokeHex = "#0369a1", float size = 6.0f, float strokeWidth = 1.0f)
        {
            var symbol = new CompositePointSymbol { Name = "Ponto Padrão" };
            MarkerShapeType parsedShape = MarkerShapeType.Circle;
            if (Enum.TryParse<MarkerShapeType>(shape, true, out var s)) parsedShape = s;

            SKColor fill = SKColor.TryParse(fillHex, out var fc) ? fc : new SKColor(2, 132, 199);
            SKColor stroke = SKColor.TryParse(strokeHex, out var sc) ? sc : new SKColor(3, 105, 161);

            symbol.AddLayer(new SimpleMarkerSymbolLayer
            {
                Name = "Base Geométrica",
                Shape = parsedShape,
                FillColor = fill,
                StrokeColor = stroke,
                Size = size,
                StrokeWidth = strokeWidth
            });
            return symbol;
        }

        public static CompositePointSymbol CreateDefaultSvg(string svgPath, string fillHex = "#ffffff", string strokeHex = "#0284c7", float size = 12.0f)
        {
            var symbol = new CompositePointSymbol { Name = "Marcador SVG" };
            SKColor fill = SKColor.TryParse(fillHex, out var fc) ? fc : SKColors.White;
            SKColor stroke = SKColor.TryParse(strokeHex, out var sc) ? sc : new SKColor(2, 132, 199);

            symbol.AddLayer(new SvgMarkerSymbolLayer
            {
                Name = "Ícone Vetorial",
                SvgSourcePath = svgPath,
                FillColor = fill,
                StrokeColor = stroke,
                Size = size,
                StrokeWidth = 1.5f
            });
            return symbol;
        }
    }
}
