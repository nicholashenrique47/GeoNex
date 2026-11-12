using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using GeoNex.Core.Symbology;
using SkiaSharp;

namespace GeoNex.Services
{
    /// <summary>
    /// Serviço de interoperabilidade bidirecional para arquivos de estilo QGIS 3.x (.qml).
    /// </summary>
    public static class QmlStyleInteropService
    {
        public static string ExportarParaQml(string nomeCamada, EstiloCamada estilo)
        {
            var symbol = estilo.ObterSimboloComposto();

            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-8", "yes"),
                new XElement("qgis",
                    new XAttribute("version", "3.34.0-Prizren"),
                    new XAttribute("styleCategories", "AllStyleCategories"),
                    new XElement("renderer-v2",
                        new XAttribute("type", "singleSymbol"),
                        new XAttribute("forceraster", "0"),
                        new XAttribute("symbollevels", "0"),
                        new XElement("symbols",
                            new XElement("symbol",
                                new XAttribute("type", "marker"),
                                new XAttribute("name", "0"),
                                new XAttribute("alpha", estilo.Opacidade.ToString("0.##", CultureInfo.InvariantCulture)),
                                new XAttribute("clip_to_extent", "1"),
                                new XAttribute("force_rhr", "0"),
                                GerarXmlCamadasMarcador(symbol, estilo)
                            )
                        )
                    )
                )
            );

            return doc.ToString();
        }

        private static object[] GerarXmlCamadasMarcador(CompositePointSymbol symbol, EstiloCamada fallbackEstilo)
        {
            var layers = symbol.Layers;
            if (layers.Count == 0)
            {
                return new object[]
                {
                    CriarNoSimpleMarker(fallbackEstilo.FormaMarcador, fallbackEstilo.CorPreenchimento, fallbackEstilo.CorBorda, fallbackEstilo.Tamanho, fallbackEstilo.EspessuraBorda, fallbackEstilo.RotacaoMarcador)
                };
            }

            var elements = new XElement[layers.Count];
            for (int i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                if (l is SvgMarkerSymbolLayer svgLayer)
                {
                    elements[i] = new XElement("layer",
                        new XAttribute("class", "SvgMarker"),
                        new XAttribute("enabled", svgLayer.Enabled ? "1" : "0"),
                        new XAttribute("locked", svgLayer.Locked ? "1" : "0"),
                        new XAttribute("pass", i.ToString()),
                        new XElement("prop", new XAttribute("k", "name"), new XAttribute("v", svgLayer.SvgSourcePath)),
                        new XElement("prop", new XAttribute("k", "size"), new XAttribute("v", svgLayer.Size.ToString("0.##", CultureInfo.InvariantCulture))),
                        new XElement("prop", new XAttribute("k", "size_unit"), new XAttribute("v", "MM")),
                        new XElement("prop", new XAttribute("k", "angle"), new XAttribute("v", svgLayer.RotationAngle.ToString("0.##", CultureInfo.InvariantCulture))),
                        new XElement("prop", new XAttribute("k", "color"), new XAttribute("v", $"{svgLayer.FillColor.Red},{svgLayer.FillColor.Green},{svgLayer.FillColor.Blue},255")),
                        new XElement("prop", new XAttribute("k", "outline_color"), new XAttribute("v", $"{svgLayer.StrokeColor.Red},{svgLayer.StrokeColor.Green},{svgLayer.StrokeColor.Blue},255")),
                        new XElement("prop", new XAttribute("k", "outline_width"), new XAttribute("v", svgLayer.StrokeWidth.ToString("0.##", CultureInfo.InvariantCulture))),
                        new XElement("prop", new XAttribute("k", "offset"), new XAttribute("v", $"{svgLayer.Offset.X.ToString("0.##", CultureInfo.InvariantCulture)},{svgLayer.Offset.Y.ToString("0.##", CultureInfo.InvariantCulture)}"))
                    );
                }
                else if (l is SimpleMarkerSymbolLayer simpleLayer)
                {
                    elements[i] = CriarNoSimpleMarker(simpleLayer.Shape.ToString(),
                        $"#{simpleLayer.FillColor.Red:X2}{simpleLayer.FillColor.Green:X2}{simpleLayer.FillColor.Blue:X2}",
                        $"#{simpleLayer.StrokeColor.Red:X2}{simpleLayer.StrokeColor.Green:X2}{simpleLayer.StrokeColor.Blue:X2}",
                        simpleLayer.Size, simpleLayer.StrokeWidth, simpleLayer.RotationAngle, i);
                }
                else
                {
                    elements[i] = CriarNoSimpleMarker("Circle", fallbackEstilo.CorPreenchimento, fallbackEstilo.CorBorda, fallbackEstilo.Tamanho, fallbackEstilo.EspessuraBorda, fallbackEstilo.RotacaoMarcador, i);
                }
            }
            return elements;
        }

        private static XElement CriarNoSimpleMarker(string shape, string fillHex, string strokeHex, float size, float strokeWidth, float angle, int pass = 0)
        {
            SKColor fc = EstiloCamada.ParseColorFast(fillHex, SKColors.SkyBlue);
            SKColor sc = EstiloCamada.ParseColorFast(strokeHex, SKColors.DeepSkyBlue);

            string qgisShape = shape.ToLowerInvariant() switch
            {
                "square" or "quadrado" => "square",
                "diamond" or "losango" => "diamond",
                "triangle" or "triangulo" => "triangle",
                "cross" or "cruz" => "cross",
                "crossrotatedx" or "x" => "cross2",
                "star" or "estrela" => "star",
                "hexagon" or "hexagono" => "hexagon",
                "pentagon" or "pentagono" => "pentagon",
                _ => "circle"
            };

            return new XElement("layer",
                new XAttribute("class", "SimpleMarker"),
                new XAttribute("enabled", "1"),
                new XAttribute("locked", "0"),
                new XAttribute("pass", pass.ToString()),
                new XElement("prop", new XAttribute("k", "name"), new XAttribute("v", qgisShape)),
                new XElement("prop", new XAttribute("k", "size"), new XAttribute("v", size.ToString("0.##", CultureInfo.InvariantCulture))),
                new XElement("prop", new XAttribute("k", "size_unit"), new XAttribute("v", "MM")),
                new XElement("prop", new XAttribute("k", "angle"), new XAttribute("v", angle.ToString("0.##", CultureInfo.InvariantCulture))),
                new XElement("prop", new XAttribute("k", "color"), new XAttribute("v", $"{fc.Red},{fc.Green},{fc.Blue},255")),
                new XElement("prop", new XAttribute("k", "outline_color"), new XAttribute("v", $"{sc.Red},{sc.Green},{sc.Blue},255")),
                new XElement("prop", new XAttribute("k", "outline_width"), new XAttribute("v", strokeWidth.ToString("0.##", CultureInfo.InvariantCulture)))
            );
        }

        public static EstiloCamada? ImportarDeQml(string qmlXml)
        {
            if (string.IsNullOrWhiteSpace(qmlXml)) return null;

            try
            {
                var doc = XDocument.Parse(qmlXml);
                var renderer = doc.Root?.Element("renderer-v2");
                var symbolEl = renderer?.Element("symbols")?.Element("symbol");
                if (symbolEl == null) return null;

                var estilo = new EstiloCamada();
                var composite = new CompositePointSymbol { Name = "Importado do QGIS" };

                var layers = symbolEl.Elements("layer");
                foreach (var layerEl in layers)
                {
                    string className = layerEl.Attribute("class")?.Value ?? "";
                    if (className.Equals("SvgMarker", StringComparison.OrdinalIgnoreCase))
                    {
                        var props = layerEl.Elements("prop");
                        string svgPath = GetPropVal(props, "name") ?? "";
                        float size = ParseFloat(GetPropVal(props, "size"), 12f);
                        float angle = ParseFloat(GetPropVal(props, "angle"), 0f);
                        SKColor fill = ParseQgisRgba(GetPropVal(props, "color"), SKColors.White);
                        SKColor stroke = ParseQgisRgba(GetPropVal(props, "outline_color"), new SKColor(2, 132, 199));
                        float strokeWidth = ParseFloat(GetPropVal(props, "outline_width"), 1.0f);

                        var svgLayer = new SvgMarkerSymbolLayer
                        {
                            Name = Path.GetFileNameWithoutExtension(svgPath),
                            SvgSourcePath = svgPath,
                            Size = size,
                            RotationAngle = angle,
                            FillColor = fill,
                            StrokeColor = stroke,
                            StrokeWidth = strokeWidth
                        };
                        composite.AddLayer(svgLayer);

                        estilo.SvgMarcadorCaminho = svgPath;
                        estilo.CorPreenchimento = $"#{fill.Red:X2}{fill.Green:X2}{fill.Blue:X2}";
                        estilo.CorBorda = $"#{stroke.Red:X2}{stroke.Green:X2}{stroke.Blue:X2}";
                        estilo.Tamanho = size;
                        estilo.EspessuraBorda = strokeWidth;
                        estilo.RotacaoMarcador = angle;
                    }
                    else if (className.Equals("SimpleMarker", StringComparison.OrdinalIgnoreCase))
                    {
                        var props = layerEl.Elements("prop");
                        string shape = GetPropVal(props, "name") ?? "circle";
                        float size = ParseFloat(GetPropVal(props, "size"), 6f);
                        float angle = ParseFloat(GetPropVal(props, "angle"), 0f);
                        SKColor fill = ParseQgisRgba(GetPropVal(props, "color"), SKColors.SkyBlue);
                        SKColor stroke = ParseQgisRgba(GetPropVal(props, "outline_color"), new SKColor(3, 105, 161));
                        float strokeWidth = ParseFloat(GetPropVal(props, "outline_width"), 1.0f);

                        MarkerShapeType parsedShape = shape.ToLowerInvariant() switch
                        {
                            "square" => MarkerShapeType.Square,
                            "diamond" => MarkerShapeType.Diamond,
                            "triangle" => MarkerShapeType.Triangle,
                            "cross" => MarkerShapeType.Cross,
                            "cross2" => MarkerShapeType.CrossRotatedX,
                            "star" => MarkerShapeType.Star,
                            "hexagon" => MarkerShapeType.Hexagon,
                            "pentagon" => MarkerShapeType.Pentagon,
                            _ => MarkerShapeType.Circle
                        };

                        composite.AddLayer(new SimpleMarkerSymbolLayer
                        {
                            Name = "Marcador Simples QGIS",
                            Shape = parsedShape,
                            Size = size,
                            RotationAngle = angle,
                            FillColor = fill,
                            StrokeColor = stroke,
                            StrokeWidth = strokeWidth
                        });

                        estilo.FormaMarcador = parsedShape.ToString();
                        estilo.CorPreenchimento = $"#{fill.Red:X2}{fill.Green:X2}{fill.Blue:X2}";
                        estilo.CorBorda = $"#{stroke.Red:X2}{stroke.Green:X2}{stroke.Blue:X2}";
                        estilo.Tamanho = size;
                        estilo.EspessuraBorda = strokeWidth;
                        estilo.RotacaoMarcador = angle;
                    }
                }

                if (composite.Layers.Count > 0)
                {
                    estilo.SimboloComposto = composite;
                }

                return estilo;
            }
            catch
            {
                return null;
            }
        }

        private static string? GetPropVal(System.Collections.Generic.IEnumerable<XElement> props, string key)
        {
            foreach (var p in props)
            {
                if (p.Attribute("k")?.Value == key)
                    return p.Attribute("v")?.Value;
            }
            return null;
        }

        private static float ParseFloat(string? s, float def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float r) ? r : def;
        }

        private static SKColor ParseQgisRgba(string? s, SKColor def)
        {
            if (string.IsNullOrWhiteSpace(s)) return def;
            var parts = s.Split(',');
            if (parts.Length >= 3 &&
                byte.TryParse(parts[0], out byte r) &&
                byte.TryParse(parts[1], out byte g) &&
                byte.TryParse(parts[2], out byte b))
            {
                byte a = 255;
                if (parts.Length >= 4 && byte.TryParse(parts[3], out byte parsedA)) a = parsedA;
                return new SKColor(r, g, b, a);
            }
            return def;
        }
    }
}
