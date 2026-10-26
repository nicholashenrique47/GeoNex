using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace GeoNex.Services
{
    /// <summary>
    /// Serviço de alta confiabilidade para exportação e importação de estilos de camadas,
    /// compatível com o formato nativo GeoNex (.gnxstyle / JSON) e com o padrão aberto OGC SLD (Styled Layer Descriptor).
    /// </summary>
    public static class StyleSerializationService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Serializa o estilo da camada em JSON (.gnxstyle).
        /// </summary>
        public static string ExportarParaJson(EstiloCamada estilo)
        {
            return JsonSerializer.Serialize(estilo, JsonOptions);
        }

        /// <summary>
        /// Restaura o estilo da camada a partir de JSON (.gnxstyle).
        /// </summary>
        public static EstiloCamada? ImportarDeJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<EstiloCamada>(json, JsonOptions);
        }

        /// <summary>
        /// Exporta o estilo da camada no formato padrão aberto OGC SLD 1.0/1.1 (compatível com QGIS e GeoServer).
        /// </summary>
        public static string ExportarParaSLD(string nomeCamada, EstiloCamada estilo)
        {
            XNamespace sld = "http://www.opengis.net/sld";
            XNamespace ogc = "http://www.opengis.net/ogc";
            XNamespace se = "http://www.opengis.net/se";
            XNamespace xlink = "http://www.w3.org/1999/xlink";
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

            var rule = new XElement(sld + "Rule",
                new XElement(sld + "Name", "Regra_" + nomeCamada),
                new XElement(sld + "Title", "Estilo GeoNex - " + nomeCamada)
            );

            // Simbolizador de Polígono
            var polygonSymbolizer = new XElement(sld + "PolygonSymbolizer");
            if (!estilo.PreenchimentoTransparente)
            {
                var fill = new XElement(sld + "Fill",
                    new XElement(sld + "CssParameter", new XAttribute("name", "fill"), estilo.CorPreenchimento),
                    new XElement(sld + "CssParameter", new XAttribute("name", "fill-opacity"),
                        estilo.Opacidade.ToString("0.##", CultureInfo.InvariantCulture))
                );
                polygonSymbolizer.Add(fill);
            }

            if (!estilo.BordaTransparente)
            {
                var stroke = new XElement(sld + "Stroke",
                    new XElement(sld + "CssParameter", new XAttribute("name", "stroke"), estilo.CorBorda),
                    new XElement(sld + "CssParameter", new XAttribute("name", "stroke-width"),
                        estilo.EspessuraBorda.ToString("0.##", CultureInfo.InvariantCulture))
                );
                if (estilo.TipoLinha == "Dash")
                    stroke.Add(new XElement(sld + "CssParameter", new XAttribute("name", "stroke-dasharray"), "5 5"));
                else if (estilo.TipoLinha == "Dot")
                    stroke.Add(new XElement(sld + "CssParameter", new XAttribute("name", "stroke-dasharray"), "2 4"));

                polygonSymbolizer.Add(stroke);
            }
            rule.Add(polygonSymbolizer);

            // Simbolizador de Linha
            var lineSymbolizer = new XElement(sld + "LineSymbolizer",
                new XElement(sld + "Stroke",
                    new XElement(sld + "CssParameter", new XAttribute("name", "stroke"), estilo.CorPreenchimento),
                    new XElement(sld + "CssParameter", new XAttribute("name", "stroke-width"),
                        estilo.EspessuraLinha.ToString("0.##", CultureInfo.InvariantCulture)),
                    new XElement(sld + "CssParameter", new XAttribute("name", "stroke-opacity"),
                        estilo.Opacidade.ToString("0.##", CultureInfo.InvariantCulture))
                )
            );
            rule.Add(lineSymbolizer);

            // Simbolizador de Ponto (PointSymbolizer)
            string sldWellKnownName = estilo.FormaMarcador.ToLowerInvariant() switch
            {
                "quadrado" => "square",
                "triangulo" => "triangle",
                "estrela" => "star",
                "cruz" or "x" => "cross",
                _ => "circle"
            };

            var pointSymbolizer = new XElement(sld + "PointSymbolizer",
                new XElement(sld + "Graphic",
                    new XElement(sld + "Mark",
                        new XElement(sld + "WellKnownName", sldWellKnownName),
                        new XElement(sld + "Fill",
                            new XElement(sld + "CssParameter", new XAttribute("name", "fill"), estilo.CorPreenchimento)
                        ),
                        new XElement(sld + "Stroke",
                            new XElement(sld + "CssParameter", new XAttribute("name", "stroke"), estilo.CorBorda),
                            new XElement(sld + "CssParameter", new XAttribute("name", "stroke-width"),
                                estilo.EspessuraBorda.ToString("0.##", CultureInfo.InvariantCulture))
                        )
                    ),
                    new XElement(sld + "Size", (estilo.Tamanho * 2f).ToString("0.##", CultureInfo.InvariantCulture)),
                    new XElement(sld + "Rotation", estilo.RotacaoMarcador.ToString("0.##", CultureInfo.InvariantCulture))
                )
            );
            rule.Add(pointSymbolizer);

            var sldDoc = new XDocument(
                new XDeclaration("1.0", "UTF-8", "yes"),
                new XElement(sld + "StyledLayerDescriptor",
                    new XAttribute("version", "1.0.0"),
                    new XAttribute(XNamespace.Xmlns + "sld", sld.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "ogc", ogc.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "se", se.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "xlink", xlink.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
                    new XElement(sld + "NamedLayer",
                        new XElement(sld + "Name", nomeCamada),
                        new XElement(sld + "UserStyle",
                            new XElement(sld + "Title", "Estilo Cartográfico GeoNex"),
                            new XElement(sld + "FeatureTypeStyle", rule)
                        )
                    )
                )
            );

            return sldDoc.ToString();
        }

        /// <summary>
        /// Faz o parse de um XML SLD e extrai as propriedades principais para o EstiloCamada.
        /// </summary>
        public static EstiloCamada? ImportarDeSLD(string sldXml)
        {
            if (string.IsNullOrWhiteSpace(sldXml)) return null;

            try
            {
                var doc = XDocument.Parse(sldXml);
                var estilo = new EstiloCamada();

                foreach (var el in doc.Descendants())
                {
                    string localName = el.Name.LocalName;
                    if (localName.Equals("CssParameter", StringComparison.OrdinalIgnoreCase) ||
                        localName.Equals("SvgParameter", StringComparison.OrdinalIgnoreCase))
                    {
                        string? name = el.Attribute("name")?.Value;
                        string val = el.Value.Trim();

                        if (string.Equals(name, "fill", StringComparison.OrdinalIgnoreCase))
                            estilo.CorPreenchimento = val;
                        else if (string.Equals(name, "stroke", StringComparison.OrdinalIgnoreCase))
                            estilo.CorBorda = val;
                        else if (string.Equals(name, "stroke-width", StringComparison.OrdinalIgnoreCase) &&
                                 float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float sw))
                        {
                            estilo.EspessuraBorda = sw;
                            estilo.EspessuraLinha = sw;
                        }
                        else if (string.Equals(name, "fill-opacity", StringComparison.OrdinalIgnoreCase) &&
                                 float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float fo))
                        {
                            estilo.Opacidade = fo;
                        }
                    }
                    else if (localName.Equals("WellKnownName", StringComparison.OrdinalIgnoreCase))
                    {
                        string wkn = el.Value.Trim().ToLowerInvariant();
                        estilo.FormaMarcador = wkn switch
                        {
                            "square" => "Quadrado",
                            "triangle" => "Triangulo",
                            "star" => "Estrela",
                            "cross" => "Cruz",
                            _ => "Circulo"
                        };
                    }
                    else if (localName.Equals("Size", StringComparison.OrdinalIgnoreCase) &&
                             float.TryParse(el.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float sz))
                    {
                        estilo.Tamanho = sz / 2f;
                    }
                    else if (localName.Equals("Rotation", StringComparison.OrdinalIgnoreCase) &&
                             float.TryParse(el.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float rot))
                    {
                        estilo.RotacaoMarcador = rot;
                    }
                }

                return estilo;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Erro ao interpretar SLD: " + ex.Message);
                return null;
            }
        }
    }
}
