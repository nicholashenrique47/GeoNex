using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Operation.Union;
using Point = NetTopologySuite.Geometries.Point;

namespace GeoNex.Services
{
    /// <summary>
    /// Algoritmos de derivação geométrica: Zonas de amortecimento (Buffer),
    /// Envoltória Convexa (Convex Hull) e Extração de Centroides / Polos de Inacessibilidade.
    /// </summary>
    public static class VectorGeometryAlgorithms
    {
        /// <summary>
        /// Gera zonas de amortecimento (Buffer) para feições de pontos, linhas ou polígonos.
        /// Suporta distância fixa ou variável por atributo, estilos de ponta/junção e dissolução opcional.
        /// </summary>
        public static List<IFeature> ExecutarBuffer(
            IReadOnlyList<IFeature> feicoes,
            double distanciaPadrao,
            string? campoDistancia = null,
            int segmentosQuadrante = 16,
            BufferEndCapStyle endCap = BufferEndCapStyle.Round,
            BufferJoinStyle joinStyle = BufferJoinStyle.Round,
            bool dissolver = false,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var buffersIndividuais = new List<IFeature>();
            if (feicoes == null || feicoes.Count == 0)
                return buffersIndividuais;

            var endCapNts = endCap switch
            {
                BufferEndCapStyle.Flat => EndCapStyle.Flat,
                BufferEndCapStyle.Square => EndCapStyle.Square,
                _ => EndCapStyle.Round
            };

            var joinNts = joinStyle switch
            {
                BufferJoinStyle.Mitre => JoinStyle.Mitre,
                BufferJoinStyle.Bevel => JoinStyle.Bevel,
                _ => JoinStyle.Round
            };

            var bufferParams = new BufferParameters(Math.Clamp(segmentosQuadrante, 4, 64))
            {
                EndCapStyle = endCapNts,
                JoinStyle = joinNts
            };

            int total = feicoes.Count;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = feicoes[i];
                var geom = f.Geometry;

                if (geom == null || geom.IsEmpty) continue;

                double distFeicao = distanciaPadrao;
                if (!string.IsNullOrWhiteSpace(campoDistancia) && f.Attributes != null && f.Attributes.Exists(campoDistancia))
                {
                    var val = f.Attributes[campoDistancia];
                    if (val != null && double.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double dVal))
                    {
                        distFeicao = dVal;
                    }
                }

                try
                {
                    var bufGeom = BufferOp.Buffer(geom, distFeicao, bufferParams);
                    if (bufGeom != null && !bufGeom.IsEmpty)
                    {
                        var attrs = ClonarAtributos(f.Attributes);
                        attrs.Add("BUF_DIST", distFeicao);
                        buffersIndividuais.Add(new Feature(bufGeom, attrs));
                    }
                }
                catch
                {
                    // Tenta reparo prévio com Buffer(0)
                    try
                    {
                        var bufGeom = BufferOp.Buffer(geom.Buffer(0), distFeicao, bufferParams);
                        if (bufGeom != null && !bufGeom.IsEmpty)
                        {
                            var attrs = ClonarAtributos(f.Attributes);
                            attrs.Add("BUF_DIST", distFeicao);
                            buffersIndividuais.Add(new Feature(bufGeom, attrs));
                        }
                    }
                    catch { }
                }

                if (i % 200 == 0 || i == total - 1)
                {
                    double taxa = i > 0 && sw.Elapsed.TotalSeconds > 0 ? i / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total * (dissolver ? 0.7 : 1.0),
                        $"Gerando buffer da feição {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            if (!dissolver || buffersIndividuais.Count <= 1)
                return buffersIndividuais;

            // Dissolução dos buffers resultantes
            progresso?.Report(new GeoprocessingProgress(0.75, "Dissolvendo sobreposições de buffers...", buffersIndividuais.Count, total));
            ct.ThrowIfCancellationRequested();

            var geomsParaUnir = buffersIndividuais.Select(b => b.Geometry).ToList();
            Geometry uniaoBuffers;
            try
            {
                uniaoBuffers = CascadedPolygonUnion.Union(geomsParaUnir);
            }
            catch
            {
                uniaoBuffers = UnaryUnionOp.Union(geomsParaUnir);
            }

            var resultadoDissolvido = new List<IFeature>();
            if (uniaoBuffers != null && !uniaoBuffers.IsEmpty)
            {
                var attrs = new AttributesTable();
                attrs.Add("BUF_DIST", distanciaPadrao);
                attrs.Add("TOTAL_BUF", buffersIndividuais.Count);
                attrs.Add("DISSOLVIDO", "SIM");
                resultadoDissolvido.Add(new Feature(uniaoBuffers, attrs));
            }

            progresso?.Report(new GeoprocessingProgress(1.0, "Buffer com dissolução concluído.", total, total));
            return resultadoDissolvido;
        }

        /// <summary>
        /// Computa a Envoltória Convexa (Convex Hull) de forma global ou individual/agrupada.
        /// </summary>
        public static List<IFeature> ExecutarEnvoltoriaConvexa(
            IReadOnlyList<IFeature> feicoes,
            bool global = true,
            string? campoAgrupamento = null,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (feicoes == null || feicoes.Count == 0)
                return resultado;

            // 1. MODO GLOBAL: 1 Envoltória Convexa para toda a camada
            if (global && string.IsNullOrWhiteSpace(campoAgrupamento))
            {
                progresso?.Report(new GeoprocessingProgress(0.3, "Coletando vértices para envoltória convexa global...", 0, feicoes.Count));
                ct.ThrowIfCancellationRequested();

                var geoms = feicoes.Select(f => f.Geometry).Where(g => g != null && !g.IsEmpty).ToArray();
                if (geoms.Length == 0) return resultado;

                var factory = geoms[0].Factory;
                var colecao = factory.CreateGeometryCollection(geoms);
                var hull = colecao.ConvexHull();

                if (hull != null && !hull.IsEmpty)
                {
                    var attrs = new AttributesTable();
                    attrs.Add("TOTAL_FEIC", feicoes.Count);
                    attrs.Add("TIPO_HULL", "GLOBAL");
                    attrs.Add("AREA_M2", Math.Round(hull.Area, 2));
                    resultado.Add(new Feature(hull, attrs));
                }

                progresso?.Report(new GeoprocessingProgress(1.0, "Envoltória convexa global concluída.", feicoes.Count, feicoes.Count));
                return resultado;
            }

            // 2. MODO AGRUPADO POR ATRIBUTO
            if (!string.IsNullOrWhiteSpace(campoAgrupamento))
            {
                var grupos = feicoes
                    .Where(f => f.Geometry != null && !f.Geometry.IsEmpty)
                    .GroupBy(f => f.Attributes?.Exists(campoAgrupamento) == true ? f.Attributes[campoAgrupamento]?.ToString() ?? "" : "");

                int totalGrupos = grupos.Count();
                int idx = 0;

                foreach (var g in grupos)
                {
                    ct.ThrowIfCancellationRequested();
                    var geoms = g.Select(f => f.Geometry).ToArray();
                    if (geoms.Length > 0)
                    {
                        var factory = geoms[0].Factory;
                        var colecao = factory.CreateGeometryCollection(geoms);
                        var hull = colecao.ConvexHull();

                        if (hull != null && !hull.IsEmpty)
                        {
                            var attrs = new AttributesTable();
                            string campoTrunk = campoAgrupamento.Length > 10 ? campoAgrupamento.Substring(0, 10) : campoAgrupamento;
                            attrs.Add(campoTrunk, g.Key);
                            attrs.Add("TOTAL_FEIC", geoms.Length);
                            attrs.Add("AREA_M2", Math.Round(hull.Area, 2));
                            resultado.Add(new Feature(hull, attrs));
                        }
                    }

                    idx++;
                    progresso?.Report(new GeoprocessingProgress(idx / (double)totalGrupos, $"Calculando envoltória do grupo {idx}/{totalGrupos}", idx, totalGrupos));
                }

                return resultado;
            }

            // 3. MODO INDIVIDUAL POR FEIÇÃO
            int total = feicoes.Count;
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = feicoes[i];
                if (f.Geometry != null && !f.Geometry.IsEmpty)
                {
                    var hull = f.Geometry.ConvexHull();
                    if (hull != null && !hull.IsEmpty)
                    {
                        resultado.Add(new Feature(hull, ClonarAtributos(f.Attributes)));
                    }
                }

                if (i % 200 == 0 || i == total - 1)
                {
                    progresso?.Report(new GeoprocessingProgress((i + 1) / (double)total, $"Envoltória individual {i + 1}/{total}", i + 1, total));
                }
            }

            return resultado;
        }

        /// <summary>
        /// Extrai centroides ou Polos de Inacessibilidade (Polylabel) de geometrias poligonais ou lineares,
        /// preservando integralmente todos os atributos originais na camada pontual de saída.
        /// </summary>
        public static List<IFeature> ExecutarCentroides(
            IReadOnlyList<IFeature> feicoes,
            bool usarPolylabel = true,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (feicoes == null || feicoes.Count == 0)
                return resultado;

            int total = feicoes.Count;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = feicoes[i];
                var geom = f.Geometry;

                if (geom == null || geom.IsEmpty) continue;

                Point pontoPolo;

                if (usarPolylabel && (geom is Polygon or MultiPolygon))
                {
                    // Centroide geométrico de massa como base
                    var centroideMassa = geom.Centroid;

                    // Se o centroide geométrico já estiver no interior do polígono, aproveita-o imediatamente
                    if (geom.Contains(centroideMassa))
                    {
                        pontoPolo = centroideMassa;
                    }
                    else
                    {
                        // Para geometrias côncavas (formato C, ferradura), busca o ponto interior garantido
                        var interiorPoint = geom.InteriorPoint;
                        pontoPolo = interiorPoint is Point p ? p : centroideMassa;
                    }
                }
                else
                {
                    pontoPolo = geom.Centroid;
                }

                if (pontoPolo != null && !pontoPolo.IsEmpty)
                {
                    resultado.Add(new Feature(pontoPolo, ClonarAtributos(f.Attributes)));
                }

                if (i % 200 == 0 || i == total - 1)
                {
                    double taxa = i > 0 && sw.Elapsed.TotalSeconds > 0 ? i / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total,
                        $"Extraindo centroide {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            return resultado;
        }

        private static AttributesTable ClonarAtributos(IAttributesTable? origem)
        {
            var destino = new AttributesTable();
            if (origem != null)
            {
                foreach (var nome in origem.GetNames())
                {
                    string nomeTrunk = nome.Length > 10 ? nome.Substring(0, 10) : nome;
                    destino.Add(nomeTrunk, origem[nome]);
                }
            }
            return destino;
        }
    }
}
