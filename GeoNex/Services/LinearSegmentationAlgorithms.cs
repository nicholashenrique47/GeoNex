using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NetTopologySuite;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.LinearReferencing;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Operation.Union;

namespace GeoNex.Services
{
    /// <summary>
    /// Algoritmos de segmentação linear e partição topológica:
    /// Quebra de Linhas em Intersecções, Estaqueamento Fixo (Line Chaining) e Partição de Polígonos por Linhas.
    /// </summary>
    public static class LinearSegmentationAlgorithms
    {
        public static List<IFeature> ExecutarQuebraLinhasInterseccoes(
            IReadOnlyList<IFeature> linhas,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (linhas == null || linhas.Count == 0) return resultado;

            progresso?.Report(new GeoprocessingProgress(0.1, "Quebra em Intersecções: gerando grafo linear com nós", 0, 100));

            var geoms = linhas.Where(l => l.Geometry != null && !l.Geometry.IsEmpty).Select(l => l.Geometry).ToList();
            if (geoms.Count == 0) return resultado;

            ct.ThrowIfCancellationRequested();
            var uniaoComNos = UnaryUnionOp.Union(geoms);
            var linhasNodadas = LinearComponentExtracter.GetLines(uniaoComNos);

            int total = linhasNodadas.Count;
            progresso?.Report(new GeoprocessingProgress(0.5, $"Quebra em Intersecções: gerados {total} segmentos", 0, total));

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var l = (LineString)linhasNodadas[i];
                var attrs = new AttributesTable
                {
                    { "SEG_ID", i + 1 },
                    { "LENGTH_M", Math.Round(l.Length, 4) }
                };
                resultado.Add(new Feature(l, attrs));
            }

            progresso?.Report(new GeoprocessingProgress(1.0, "Quebra em Intersecções concluída com sucesso", total, total));
            return resultado;
        }

        public static List<IFeature> ExecutarEstaqueamentoLinhas(
            IReadOnlyList<IFeature> linhas,
            double distanciaEstacaM,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (linhas == null || linhas.Count == 0 || distanciaEstacaM <= 0.001) return resultado;

            int total = linhas.Count;
            int segGlobal = 1;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = linhas[i];
                if (f.Geometry == null || f.Geometry.IsEmpty) continue;

                void SubdividirLinha(LineString line)
                {
                    var indexador = new LengthIndexedLine(line);
                    double len = line.Length;
                    double start = 0.0;
                    int subIdx = 1;

                    while (start < len)
                    {
                        double end = Math.Min(start + distanciaEstacaM, len);
                        var subGeom = indexador.ExtractLine(start, end);
                        if (!subGeom.IsEmpty && subGeom.Length > 0.0001)
                        {
                            var attrs = new AttributesTable();
                            if (f.Attributes != null)
                            {
                                foreach (var n in f.Attributes.GetNames())
                                    attrs.Add(n, f.Attributes[n]);
                            }
                            attrs.Add("ORIG_FID", i + 1);
                            attrs.Add("STA_ID", segGlobal++);
                            attrs.Add("STA_NUM", subIdx++);
                            attrs.Add("STA_START", Math.Round(start, 2));
                            attrs.Add("STA_END", Math.Round(end, 2));
                            attrs.Add("SEG_LEN", Math.Round(subGeom.Length, 4));

                            resultado.Add(new Feature(subGeom, attrs));
                        }
                        start = end;
                    }
                }

                if (f.Geometry is LineString ls)
                {
                    SubdividirLinha(ls);
                }
                else if (f.Geometry is MultiLineString mls)
                {
                    for (int m = 0; m < mls.NumGeometries; m++)
                    {
                        if (mls.GetGeometryN(m) is LineString subLs)
                            SubdividirLinha(subLs);
                    }
                }

                if (i % 100 == 0 || i == total - 1)
                {
                    double taxa = sw.Elapsed.TotalSeconds > 0 ? (i + 1) / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total,
                        $"Estaqueamento: feição {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            return resultado;
        }

        public static List<IFeature> ExecutarParticaoPoligonosPorLinhas(
            IReadOnlyList<IFeature> poligonos,
            IReadOnlyList<IFeature> linhasCorte,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (poligonos == null || poligonos.Count == 0) return resultado;

            if (linhasCorte == null || linhasCorte.Count == 0)
            {
                foreach (var p in poligonos)
                    resultado.Add(new Feature(p.Geometry, p.Attributes));
                return resultado;
            }

            progresso?.Report(new GeoprocessingProgress(0.1, "Partição: unindo lâminas de corte", 0, 100));
            var geomsCorte = linhasCorte.Where(l => l.Geometry != null && !l.Geometry.IsEmpty).Select(l => l.Geometry).ToList();
            var uniaoCorte = UnaryUnionOp.Union(geomsCorte);

            int total = poligonos.Count;
            int fragGlobal = 1;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = poligonos[i];
                if (f.Geometry == null || f.Geometry.IsEmpty) continue;

                if (!f.Geometry.Intersects(uniaoCorte))
                {
                    resultado.Add(new Feature(f.Geometry, f.Attributes));
                    continue;
                }

                try
                {
                    var contorno = f.Geometry.Boundary;
                    var redeLinhas = contorno.Union(uniaoCorte);
                    var polygonizer = new Polygonizer();
                    polygonizer.Add(redeLinhas);
                    var fragmentos = polygonizer.GetPolygons();

                    int subFrag = 1;
                    foreach (Polygon frag in fragmentos)
                    {
                        if (f.Geometry.Contains(frag.InteriorPoint))
                        {
                            var attrs = new AttributesTable();
                            if (f.Attributes != null)
                            {
                                foreach (var n in f.Attributes.GetNames())
                                    attrs.Add(n, f.Attributes[n]);
                            }
                            attrs.Add("ORIG_FID", i + 1);
                            attrs.Add("FRAG_ID", fragGlobal++);
                            attrs.Add("FRAG_SUB", subFrag++);
                            attrs.Add("FRAG_AREA", Math.Round(frag.Area, 4));

                            resultado.Add(new Feature(frag, attrs));
                        }
                    }
                }
                catch
                {
                    // Fallback caso a união topológica falhe: preserva original
                    resultado.Add(new Feature(f.Geometry, f.Attributes));
                }

                if (i % 50 == 0 || i == total - 1)
                {
                    double taxa = sw.Elapsed.TotalSeconds > 0 ? (i + 1) / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total,
                        $"Partição de polígonos: {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            return resultado;
        }
    }
}
