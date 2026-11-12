using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GeoNex.Core.Symbology;
using SkiaSharp;

namespace GeoNex.Services
{
    public sealed record SvgMarkerCatalogItem(
        string Id,
        string Label,
        string Category,
        string RelativePath,
        IReadOnlyList<string> Tags,
        string DefaultFill,
        string DefaultStroke,
        float DefaultStrokeWidth);

    /// <summary>
    /// Motor de compilação, injeção paramétrica e cache em memória de marcadores SVG para SkiaSharp.
    /// </summary>
    public static class SvgCompilationService
    {
        private static readonly ConcurrentDictionary<string, SKPicture> _pictureCache = new(StringComparer.Ordinal);
        private static readonly List<SvgMarkerCatalogItem> _catalog = new();
        private static readonly ConcurrentDictionary<string, string> _rawXmlCache = new(StringComparer.OrdinalIgnoreCase);

        static SvgCompilationService()
        {
            // Conecta o provedor de imagens ao modelo de domínio
            SvgMarkerSymbolLayer.PictureProvider = GetOrCompilePicture;
            InitializeCatalog();
        }

        public static IReadOnlyList<SvgMarkerCatalogItem> GetCatalog() => _catalog;

        public static IEnumerable<SvgMarkerCatalogItem> SearchCatalog(string query, string? category = null)
        {
            query = query?.Trim() ?? string.Empty;
            foreach (var item in _catalog)
            {
                if (!string.IsNullOrEmpty(category) && !category.Equals("TODOS", StringComparison.OrdinalIgnoreCase)
                    && !item.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(query))
                {
                    yield return item;
                    continue;
                }

                if (item.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    yield return item;
                    continue;
                }

                bool tagMatched = false;
                for (int i = 0; i < item.Tags.Count; i++)
                {
                    if (item.Tags[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        tagMatched = true;
                        break;
                    }
                }

                if (tagMatched)
                {
                    yield return item;
                }
            }
        }

        public static SKPicture? GetOrCompilePicture(string path, string? embeddedXml, SKColor fill, SKColor fillSecondary, SKColor stroke, float strokeWidth)
        {
            string cacheKey = $"{path}|{embeddedXml?.GetHashCode()}|{(uint)fill}|{(uint)fillSecondary}|{(uint)stroke}|{strokeWidth:F2}";
            if (_pictureCache.TryGetValue(cacheKey, out var existing))
            {
                return existing;
            }

            string? xml = embeddedXml;
            if (string.IsNullOrEmpty(xml) && !string.IsNullOrEmpty(path))
            {
                xml = ResolveSvgXml(path);
            }

            if (string.IsNullOrWhiteSpace(xml))
            {
                return null;
            }

            string injectedXml = InjectParameters(xml, fill, fillSecondary, stroke, strokeWidth);
            SKPicture picture = CompileSvgToPicture(injectedXml);

            _pictureCache.TryAdd(cacheKey, picture);
            return picture;
        }

        private static string? ResolveSvgXml(string path)
        {
            if (_rawXmlCache.TryGetValue(path, out var cachedXml))
            {
                return cachedXml;
            }

            string resolvedPath = path;
            if (path.StartsWith("geonex://", StringComparison.OrdinalIgnoreCase))
            {
                string rel = path.Substring("geonex://".Length).TrimStart('/');
                resolvedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Markers", rel);
            }

            if (!File.Exists(resolvedPath))
            {
                // Tenta caminho relativo ao projeto
                string altPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, resolvedPath);
                if (File.Exists(altPath)) resolvedPath = altPath;
                else
                {
                    string rootPath = Path.Combine(Directory.GetCurrentDirectory(), "GeoNex", "Assets", "Markers", Path.GetFileName(resolvedPath));
                    if (File.Exists(rootPath)) resolvedPath = rootPath;
                }
            }

            if (File.Exists(resolvedPath))
            {
                try
                {
                    string xml = File.ReadAllText(resolvedPath);
                    _rawXmlCache.TryAdd(path, xml);
                    return xml;
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static readonly Regex ParamFillRegex = new(@"param\((?:fill|color)\)[^'""]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ParamFillSecondaryRegex = new(@"param\(fill-secondary\)[^'""]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ParamStrokeRegex = new(@"param\(stroke\)[^'""]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ParamStrokeWidthRegex = new(@"param\(stroke-width\)[^'""]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string InjectParameters(string svgXml, SKColor fill, SKColor fillSecondary, SKColor stroke, float strokeWidth)
        {
            string fillHex = $"#{fill.Red:X2}{fill.Green:X2}{fill.Blue:X2}";
            string fillSecHex = $"#{fillSecondary.Red:X2}{fillSecondary.Green:X2}{fillSecondary.Blue:X2}";
            string strokeHex = $"#{stroke.Red:X2}{stroke.Green:X2}{stroke.Blue:X2}";
            string swStr = strokeWidth.ToString("0.##", CultureInfo.InvariantCulture);

            string modified = ParamFillSecondaryRegex.Replace(svgXml, fillSecHex);
            modified = ParamFillRegex.Replace(modified, fillHex);
            modified = ParamStrokeRegex.Replace(modified, strokeHex);
            modified = ParamStrokeWidthRegex.Replace(modified, swStr);

            return modified;
        }

        public static SKPicture CompileSvgToPicture(string svgXml)
        {
            using var recorder = new SKPictureRecorder();
            SKCanvas canvas = recorder.BeginRecording(new SKRect(0, 0, 32, 32));

            try
            {
                var doc = XDocument.Parse(svgXml);
                var svgRoot = doc.Root;
                if (svgRoot != null)
                {
                    // Lê viewBox ou width/height
                    float vbWidth = 32f, vbHeight = 32f;
                    string? vb = svgRoot.Attribute("viewBox")?.Value;
                    if (!string.IsNullOrEmpty(vb))
                    {
                        var parts = vb.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 4 &&
                            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float w) &&
                            float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float h))
                        {
                            vbWidth = w;
                            vbHeight = h;
                        }
                    }

                    // Escala para caber em 32x32 se necessário
                    if (vbWidth != 32f || vbHeight != 32f)
                    {
                        canvas.Scale(32f / vbWidth, 32f / vbHeight);
                    }

                    foreach (var element in svgRoot.Descendants())
                    {
                        RenderSvgElement(canvas, element);
                    }
                }
            }
            catch
            {
                // Fallback em caso de XML inválido: desenha um marcador geométrico de aviso
                using var p = new SKPaint { Color = SKColors.OrangeRed, Style = SKPaintStyle.Fill, IsAntialias = true };
                canvas.DrawCircle(16, 16, 12, p);
            }

            return recorder.EndRecording();
        }

        private static void RenderSvgElement(SKCanvas canvas, XElement el)
        {
            string name = el.Name.LocalName.ToLowerInvariant();
            if (name == "svg" || name == "defs" || name == "g") return;

            string? fillAttr = el.Attribute("fill")?.Value;
            string? strokeAttr = el.Attribute("stroke")?.Value;
            string? swAttr = el.Attribute("stroke-width")?.Value;
            string? opAttr = el.Attribute("opacity")?.Value;

            float opacity = 1.0f;
            if (!string.IsNullOrEmpty(opAttr) && float.TryParse(opAttr, NumberStyles.Float, CultureInfo.InvariantCulture, out float opVal))
            {
                opacity = Math.Clamp(opVal, 0f, 1f);
            }

            SKColor fillColor = ParseSvgColor(fillAttr, SKColors.Black);
            SKColor strokeColor = ParseSvgColor(strokeAttr, SKColors.Empty);
            float strokeWidth = 1.0f;
            if (!string.IsNullOrEmpty(swAttr) && float.TryParse(swAttr, NumberStyles.Float, CultureInfo.InvariantCulture, out float swVal))
            {
                strokeWidth = swVal;
            }

            bool hasFill = fillAttr != "none" && fillColor.Alpha > 0;
            bool hasStroke = strokeAttr != null && strokeAttr != "none" && strokeColor.Alpha > 0 && strokeWidth > 0f;

            if (opacity < 1.0f)
            {
                if (hasFill) fillColor = fillColor.WithAlpha((byte)(fillColor.Alpha * opacity));
                if (hasStroke) strokeColor = strokeColor.WithAlpha((byte)(strokeColor.Alpha * opacity));
            }

            using var fillPaint = new SKPaint { Color = fillColor, Style = SKPaintStyle.Fill, IsAntialias = true };
            using var strokePaint = new SKPaint { Color = strokeColor, Style = SKPaintStyle.Stroke, StrokeWidth = strokeWidth, IsAntialias = true };

            switch (name)
            {
                case "circle":
                    float cx = ParseFloat(el.Attribute("cx")?.Value, 0);
                    float cy = ParseFloat(el.Attribute("cy")?.Value, 0);
                    float r = ParseFloat(el.Attribute("r")?.Value, 0);
                    if (hasFill) canvas.DrawCircle(cx, cy, r, fillPaint);
                    if (hasStroke) canvas.DrawCircle(cx, cy, r, strokePaint);
                    break;

                case "rect":
                    float rx = ParseFloat(el.Attribute("x")?.Value, 0);
                    float ry = ParseFloat(el.Attribute("y")?.Value, 0);
                    float rw = ParseFloat(el.Attribute("width")?.Value, 0);
                    float rh = ParseFloat(el.Attribute("height")?.Value, 0);
                    float corner = ParseFloat(el.Attribute("rx")?.Value, 0);
                    if (corner > 0)
                    {
                        if (hasFill) canvas.DrawRoundRect(new SKRect(rx, ry, rx + rw, ry + rh), corner, corner, fillPaint);
                        if (hasStroke) canvas.DrawRoundRect(new SKRect(rx, ry, rx + rw, ry + rh), corner, corner, strokePaint);
                    }
                    else
                    {
                        if (hasFill) canvas.DrawRect(new SKRect(rx, ry, rx + rw, ry + rh), fillPaint);
                        if (hasStroke) canvas.DrawRect(new SKRect(rx, ry, rx + rw, ry + rh), strokePaint);
                    }
                    break;

                case "ellipse":
                    float ecx = ParseFloat(el.Attribute("cx")?.Value, 0);
                    float ecy = ParseFloat(el.Attribute("cy")?.Value, 0);
                    float erx = ParseFloat(el.Attribute("rx")?.Value, 0);
                    float ery = ParseFloat(el.Attribute("ry")?.Value, 0);
                    if (hasFill) canvas.DrawOval(new SKRect(ecx - erx, ecy - ery, ecx + erx, ecy + ery), fillPaint);
                    if (hasStroke) canvas.DrawOval(new SKRect(ecx - erx, ecy - ery, ecx + erx, ecy + ery), strokePaint);
                    break;

                case "line":
                    float x1 = ParseFloat(el.Attribute("x1")?.Value, 0);
                    float y1 = ParseFloat(el.Attribute("y1")?.Value, 0);
                    float x2 = ParseFloat(el.Attribute("x2")?.Value, 0);
                    float y2 = ParseFloat(el.Attribute("y2")?.Value, 0);
                    if (hasStroke) canvas.DrawLine(x1, y1, x2, y2, strokePaint);
                    break;

                case "polygon":
                case "polyline":
                    string? points = el.Attribute("points")?.Value;
                    if (!string.IsNullOrEmpty(points))
                    {
                        using var polyPath = new SKPath();
                        var pts = points.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < pts.Length - 1; i += 2)
                        {
                            if (float.TryParse(pts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float px) &&
                                float.TryParse(pts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float py))
                            {
                                if (i == 0) polyPath.MoveTo(px, py);
                                else polyPath.LineTo(px, py);
                            }
                        }
                        if (name == "polygon") polyPath.Close();
                        if (hasFill && name == "polygon") canvas.DrawPath(polyPath, fillPaint);
                        if (hasStroke) canvas.DrawPath(polyPath, strokePaint);
                    }
                    break;

                case "path":
                    string? d = el.Attribute("d")?.Value;
                    if (!string.IsNullOrEmpty(d))
                    {
                        using var path = SKPath.ParseSvgPathData(d);
                        if (path != null)
                        {
                            if (hasFill) canvas.DrawPath(path, fillPaint);
                            if (hasStroke) canvas.DrawPath(path, strokePaint);
                        }
                    }
                    break;

                case "text":
                    float tx = ParseFloat(el.Attribute("x")?.Value, 0);
                    float ty = ParseFloat(el.Attribute("y")?.Value, 0);
                    float fsz = ParseFloat(el.Attribute("font-size")?.Value, 10);
                    string textContent = el.Value;
                    using (var tf = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold))
                    using (var font = new SKFont(tf, fsz))
                    {
                        font.MeasureText(textContent, out var textBounds);
                        string? anchor = el.Attribute("text-anchor")?.Value;
                        if (anchor == "middle") tx -= textBounds.MidX;
                        if (hasFill) canvas.DrawText(textContent, tx, ty, font, fillPaint);
                    }
                    break;
            }
        }

        private static float ParseFloat(string? val, float defVal)
        {
            if (string.IsNullOrEmpty(val)) return defVal;
            return float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float res) ? res : defVal;
        }

        private static SKColor ParseSvgColor(string? val, SKColor defColor)
        {
            if (string.IsNullOrEmpty(val) || val.Equals("none", StringComparison.OrdinalIgnoreCase))
                return SKColors.Empty;

            if (SKColor.TryParse(val, out var parsed))
                return parsed;

            return defColor;
        }

        private static void InitializeCatalog()
        {
            _catalog.Clear();

            // Adiciona os marcadores canônicos mais utilizados no QGIS e SIGEF
            AddCatalogItem("marco_incra_m", "Marco Tipo M (Concreto)", "Topografia", "Topografia/marco_incra_m.svg",
                new[] { "incra", "sigef", "marco", "concreto", "vertice", "limite", "georreferenciamento" }, "#ffffff", "#0284c7", 2.0f);

            AddCatalogItem("marco_incra_p", "Ponto Tipo P (Piquete / Ferro)", "Topografia", "Topografia/marco_incra_p.svg",
                new[] { "incra", "sigef", "piquete", "estaca", "ferro", "ponto", "campo" }, "#ffffff", "#d97706", 2.0f);

            AddCatalogItem("marco_incra_v", "Vértice Tipo V (Virtual)", "Topografia", "Topografia/marco_incra_v.svg",
                new[] { "incra", "sigef", "virtual", "calculado", "amarracao" }, "#94a3b8", "#64748b", 1.8f);

            AddCatalogItem("marco_geodesico_ibge", "Marco Geodésico Fundamental (IBGE)", "Topografia", "Topografia/marco_geodesico_ibge.svg",
                new[] { "ibge", "geodesia", "satelite", "datum", "sirgas" }, "#0f172a", "#38bdf8", 2.0f);

            AddCatalogItem("estacao_total", "Estação Total / Teodolito", "Topografia", "Topografia/estacao_total.svg",
                new[] { "estacao", "teodolito", "aparelho", "tripe", "medicao", "campo" }, "#f59e0b", "#d97706", 1.5f);

            // Infraestrutura
            AddCatalogItem("poste_concreto_simples", "Poste de Energia / Concreto", "Infraestrutura", "Infraestrutura/poste_concreto_simples.svg",
                new[] { "poste", "eletrica", "energia", "concreto", "distribuicao" }, "#cbd5e1", "#475569", 2.2f);

            AddCatalogItem("poste_transformador", "Poste com Transformador (Trafo)", "Infraestrutura", "Infraestrutura/poste_transformador.svg",
                new[] { "poste", "transformador", "trafo", "media tensao", "energia" }, "#cbd5e1", "#475569", 2.2f);

            AddCatalogItem("torre_alta_tensao", "Torre de Linha de Transmissão (LT)", "Infraestrutura", "Infraestrutura/torre_alta_tensao.svg",
                new[] { "torre", "transmissao", "alta tensao", "trelica", "subestacao" }, "#dc2626", "#334155", 2.0f);

            AddCatalogItem("hidrante_incendio", "Hidrante de Coluna de Incêndio", "Infraestrutura", "Infraestrutura/hidrante_incendio.svg",
                new[] { "hidrante", "bombeiros", "incendio", "agua", "seguranca" }, "#dc2626", "#991b1b", 1.8f);

            AddCatalogItem("valvula_registro_agua", "Registro de Gaveta / Válvula", "Infraestrutura", "Infraestrutura/valvula_registro_agua.svg",
                new[] { "valvula", "registro", "adutora", "agua", "saneamento" }, "#0284c7", "#0369a1", 1.5f);

            AddCatalogItem("poco_visita_esgoto_pv", "Poço de Visita de Esgoto (PV)", "Infraestrutura", "Infraestrutura/poco_visita_esgoto_pv.svg",
                new[] { "pv", "esgoto", "saneamento", "tampa", "bueiro" }, "#64748b", "#334155", 2.0f);

            // Ambiental
            AddCatalogItem("nascente_olho_dagua", "Nascente / Olho d'Água (APP)", "Ambiental", "Ambiental/nascente_olho_dagua.svg",
                new[] { "nascente", "app", "agua", "rio", "preservacao", "car" }, "#e0f2fe", "#0284c7", 2.0f);

            AddCatalogItem("poco_artesiano", "Poço Tubular Profundo (Artesiano)", "Ambiental", "Ambiental/poco_artesiano.svg",
                new[] { "poco", "artesiano", "outorga", "agua", "subterranea" }, "#94a3b8", "#334155", 2.0f);

            AddCatalogItem("arvore_nativa_notavel", "Espécime Arbóreo Nativo", "Ambiental", "Ambiental/arvore_nativa_notavel.svg",
                new[] { "arvore", "nativa", "compensacao", "vegetacao", "florestal" }, "#22c55e", "#15803d", 2.0f);

            AddCatalogItem("estacao_fluviometrica", "Estação Fluviométrica (ANA / CPRM)", "Ambiental", "Ambiental/estacao_fluviometrica.svg",
                new[] { "fluviometria", "vazao", "rio", "ana", "cprm", "hidrologia" }, "#0284c7", "#0369a1", 2.0f);

            AddCatalogItem("foco_calor_queimada", "Foco de Calor / Queimada (INPE)", "Ambiental", "Ambiental/foco_calor_queimada.svg",
                new[] { "queimada", "fogo", "calor", "inpe", "incendio", "satelite" }, "#ef4444", "#b91c1c", 1.5f);

            // Serviços
            AddCatalogItem("hospital_urgencia", "Hospital / Pronto Atendimento", "Servicos", "Servicos/hospital_urgencia.svg",
                new[] { "hospital", "saude", "medico", "cruz vermelha", "sus" }, "#ffffff", "#dc2626", 2.0f);

            AddCatalogItem("escola_educacao", "Escola / Unidade Escolar", "Servicos", "Servicos/escola_educacao.svg",
                new[] { "escola", "colegio", "educacao", "aluno", "ensino" }, "#0284c7", "#0369a1", 1.8f);

            AddCatalogItem("seguranca_policia", "Segurança Pública / Polícia", "Servicos", "Servicos/seguranca_policia.svg",
                new[] { "policia", "delegacia", "seguranca", "militar", "civil" }, "#1e293b", "#0ea5e9", 2.0f);

            AddCatalogItem("posto_combustivel", "Posto de Combustíveis", "Servicos", "Servicos/posto_combustivel.svg",
                new[] { "posto", "combustivel", "gasolina", "etanol", "diesel" }, "#0284c7", "#0369a1", 2.0f);

            // Transportes
            AddCatalogItem("ponto_onibus_parada", "Ponto de Ônibus / Parada", "Transportes", "Transportes/ponto_onibus_parada.svg",
                new[] { "onibus", "transporte", "parada", "urbano", "passageiro" }, "#0284c7", "#0369a1", 2.0f);

            AddCatalogItem("estacao_ferroviaria_trem", "Estação Ferroviária / Trem", "Transportes", "Transportes/estacao_ferroviaria_trem.svg",
                new[] { "trem", "trilho", "ferrovia", "estacao", "metro" }, "#f97316", "#ea580c", 2.0f);

            AddCatalogItem("aeroporto_pista", "Aeroporto / Aeródromo / Pouso", "Transportes", "Transportes/aeroporto_pista.svg",
                new[] { "aeroporto", "aviao", "pouso", "pista", "hangar" }, "#0284c7", "#0369a1", 2.0f);

            AddCatalogItem("semaforo_transito", "Semáforo / Sinal de Trânsito", "Transportes", "Transportes/semaforo_transito.svg",
                new[] { "semaforo", "sinal", "transito", "cruzamento", "sinaleira" }, "#1e293b", "#0f172a", 2.0f);

            // Agronegócio
            AddCatalogItem("silo_armazenamento_graos", "Silo de Grãos / Armazém", "Agronegocio", "Agronegocio/silo_armazenamento_graos.svg",
                new[] { "silo", "graos", "soja", "milho", "armazem", "agro" }, "#94a3b8", "#475569", 2.0f);

            AddCatalogItem("pivo_central_irrigacao", "Pivô Central de Irrigação", "Agronegocio", "Agronegocio/pivo_central_irrigacao.svg",
                new[] { "pivo", "irrigacao", "lavoura", "circulo", "aspersao" }, "#22c55e", "#15803d", 2.0f);

            AddCatalogItem("curral_pecuaria", "Curral / Mangueiro de Gado", "Agronegocio", "Agronegocio/curral_pecuaria.svg",
                new[] { "curral", "gado", "boi", "pecuaria", "retiro" }, "#78350f", "#451a03", 2.0f);

            AddCatalogItem("sede_fazenda", "Sede de Propriedade Rural", "Agronegocio", "Agronegocio/sede_fazenda.svg",
                new[] { "sede", "fazenda", "sitio", "casa", "rural", "car" }, "#ea580c", "#9a3412", 2.0f);

            // Geologia
            AddCatalogItem("mina_ouro_mineracao", "Mina / Lavra Mineral (ANM)", "Geologia", "Geologia/mina_ouro_mineracao.svg",
                new[] { "mina", "lavra", "anm", "minerio", "pedreira", "ouro" }, "#f59e0b", "#b45309", 2.0f);

            AddCatalogItem("afloramento_rochoso", "Afloramento Rochoso", "Geologia", "Geologia/afloramento_rochoso.svg",
                new[] { "rocha", "afloramento", "geologia", "litologia", "granito" }, "#78716c", "#44403c", 2.0f);

            AddCatalogItem("sondagem_geotecnica_spt", "Furo de Sondagem / SPT", "Geologia", "Geologia/sondagem_geotecnica_spt.svg",
                new[] { "sondagem", "spt", "solo", "geotecnia", "furo" }, "#0284c7", "#0369a1", 2.0f);

            // Defesa Civil
            AddCatalogItem("area_risco_deslizamento", "Área de Risco de Deslizamento (R1 a R4)", "DefesaCivil", "DefesaCivil/area_risco_deslizamento.svg",
                new[] { "risco", "deslizamento", "encosta", "defesa civil", "alerta" }, "#ef4444", "#b91c1c", 2.0f);

            AddCatalogItem("cota_inundacao_enchente", "Cota de Inundação / Alagamento", "DefesaCivil", "DefesaCivil/cota_inundacao_enchente.svg",
                new[] { "enchente", "inundacao", "alagamento", "chuva", "rio transbordado" }, "#0284c7", "#0369a1", 2.0f);

            AddCatalogItem("ponto_encontro_evacuacao", "Ponto de Encontro de Emergência", "DefesaCivil", "DefesaCivil/ponto_encontro_evacuacao.svg",
                new[] { "encontro", "seguro", "evacuacao", "defesa civil", "abrigo" }, "#16a34a", "#15803d", 2.0f);
        }

        private static void AddCatalogItem(string id, string label, string category, string relPath, string[] tags, string fill, string stroke, float sw)
        {
            _catalog.Add(new SvgMarkerCatalogItem(id, label, category, relPath, tags, fill, stroke, sw));
        }
    }
}
