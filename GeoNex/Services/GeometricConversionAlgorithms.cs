using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NetTopologySuite;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Operation.Union;
using Point = NetTopologySuite.Geometries.Point;

namespace GeoNex.Services
{
    /// <summary>
    /// Algoritmos de conversão, decomposição e reestruturação de tipos primitivos:
    /// Explodir Multipartes, Agrupar por Atributo, Polígonos para Linhas, Poligonização e Extração de Vértices com métricas.
    /// </summary>
    public static class GeometricConversionAlgorithms
    {
        public static List<IFeature> ExecutarExplodirMultipartes(
            IReadOnlyList<IFeature> feicoes,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (feicoes == null || feicoes.Count == 0) return resultado;

            int total = feicoes.Count;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = feicoes[i];
                if (f.Geometry == null || f.Geometry.IsEmpty) continue;

                int numPartes = f.Geometry.NumGeometries;
                for (int p = 0; p < numPartes; p++)
                {
                    var subGeom = f.Geometry.GetGeometryN(p);
                    var attrs = new AttributesTable();
                    if (f.Attributes != null)
                    {
                        foreach (var nome in f.Attributes.GetNames())
                            attrs.Add(nome, f.Attributes[nome]);
                    }
                    attrs.Add("ORIG_FID", i + 1);
                    attrs.Add("PART_IDX", p + 1);
                    attrs.Add("PART_TOTAL", numPartes);

                    resultado.Add(new Feature(subGeom, attrs));
                }

                if (i % 200 == 0 || i == total - 1)
                {
                    double taxa = sw.Elapsed.TotalSeconds > 0 ? (i + 1) / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total,
                        $"Explodir: feição {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            return resultado;
        }

        public static List<IFeature> ExecutarAgruparPorAtributo(
            IReadOnlyList<IFeature> feicoes,
            string campoChave,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (feicoes == null || feicoes.Count == 0) return resultado;

            var geomFactory = NtsGeometryServices.Instance.CreateGeometryFactory();
            var grupos = feicoes.GroupBy(f => f.Attributes?.Exists(campoChave) == true ? f.Attributes[campoChave]?.ToString() ?? "" : "");
            int totalGrupos = grupos.Count();
            int gIdx = 0;
            var sw = Stopwatch.StartNew();

            foreach (var grupo in grupos)
            {
                ct.ThrowIfCancellationRequested();
                gIdx++;
                var geomsValidas = grupo.Where(f => f.Geometry != null && !f.Geometry.IsEmpty).Select(f => f.Geometry).ToArray();
                if (geomsValidas.Length == 0) continue;

                var unificada = geomFactory.BuildGeometry(geomsValidas);
                var primeiro = grupo.First();
                var attrs = new AttributesTable();
                if (primeiro.Attributes != null)
                {
                    foreach (var nome in primeiro.Attributes.GetNames())
                        attrs.Add(nome, primeiro.Attributes[nome]);
                }
                attrs.Add("PART_COUNT", geomsValidas.Length);

                resultado.Add(new Feature(unificada, attrs));

                if (gIdx % 100 == 0 || gIdx == totalGrupos)
                {
                    double taxa = sw.Elapsed.TotalSeconds > 0 ? gIdx / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        gIdx / (double)totalGrupos,
                        $"Agrupar: grupo {gIdx}/{totalGrupos}",
                        gIdx,
                        totalGrupos,
                        taxa));
                }
            }

            return resultado;
        }

        public static List<IFeature> ExecutarPoligonosParaLinhas(
            IReadOnlyList<IFeature> feicoes,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (feicoes == null || feicoes.Count == 0) return resultado;

            int total = feicoes.Count;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = feicoes[i];
                if (f.Geometry == null || f.Geometry.IsEmpty) continue;

                void ExtrairDePoligono(Polygon poly)
                {
                    var attrsExt = new AttributesTable();
                    if (f.Attributes != null)
                    {
                        foreach (var n in f.Attributes.GetNames()) attrsExt.Add(n, f.Attributes[n]);
                    }
                    attrsExt.Add("ORIG_FID", i + 1);
                    attrsExt.Add("RING_TYPE", "EXTERIOR");
                    attrsExt.Add("RING_IDX", 0);
                    resultado.Add(new Feature(poly.ExteriorRing, attrsExt));

                    for (int r = 0; r < poly.NumInteriorRings; r++)
                    {
                        var attrsInt = new AttributesTable();
                        if (f.Attributes != null)
                        {
                            foreach (var n in f.Attributes.GetNames()) attrsInt.Add(n, f.Attributes[n]);
                        }
                        attrsInt.Add("ORIG_FID", i + 1);
                        attrsInt.Add("RING_TYPE", "INTERIOR");
                        attrsInt.Add("RING_IDX", r + 1);
                        resultado.Add(new Feature(poly.GetInteriorRingN(r), attrsInt));
                    }
                }

                if (f.Geometry is Polygon p)
                {
                    ExtrairDePoligono(p);
                }
                else if (f.Geometry is MultiPolygon mp)
                {
                    for (int k = 0; k < mp.NumGeometries; k++)
                    {
                        if (mp.GetGeometryN(k) is Polygon subPoly)
                            ExtrairDePoligono(subPoly);
                    }
                }

                if (i % 200 == 0 || i == total - 1)
                {
                    double taxa = sw.Elapsed.TotalSeconds > 0 ? (i + 1) / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total,
                        $"Polígonos para Linhas: {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            return resultado;
        }

        public static List<IFeature> ExecutarPoligonizacaoLinhas(
            IReadOnlyList<IFeature> linhas,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (linhas == null || linhas.Count == 0) return resultado;

            progresso?.Report(new GeoprocessingProgress(0.1, "Poligonização: unindo e nodando grafo linear", 0, 100));

            var geoms = linhas.Where(l => l.Geometry != null && !l.Geometry.IsEmpty).Select(l => l.Geometry).ToList();
            if (geoms.Count == 0) return resultado;

            ct.ThrowIfCancellationRequested();
            var uniaoLinhas = UnaryUnionOp.Union(geoms);

            var polygonizer = new Polygonizer();
            polygonizer.Add(uniaoLinhas);
            var poligonos = polygonizer.GetPolygons().OfType<Polygon>().ToList();

            int total = poligonos.Count;
            progresso?.Report(new GeoprocessingProgress(0.5, $"Poligonização: geradas {total} faces poligonais", 0, total));

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var p = poligonos[i];
                var attrs = new AttributesTable
                {
                    { "POLY_ID", i + 1 },
                    { "AREA_M2", Math.Round(p.Area, 4) },
                    { "PERIM_M", Math.Round(p.Length, 4) }
                };
                resultado.Add(new Feature(p, attrs));
            }

            progresso?.Report(new GeoprocessingProgress(1.0, "Poligonização concluída com sucesso", total, total));
            return resultado;
        }

        public static List<IFeature> ExecutarExtrairVertices(
            IReadOnlyList<IFeature> feicoes,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (feicoes == null || feicoes.Count == 0) return resultado;

            var geomFactory = NtsGeometryServices.Instance.CreateGeometryFactory();
            int total = feicoes.Count;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = feicoes[i];
                if (f.Geometry == null || f.Geometry.IsEmpty) continue;

                var coords = f.Geometry.Coordinates;
                double distAcumulada = 0.0;

                for (int v = 0; v < coords.Length; v++)
                {
                    if (v > 0)
                    {
                        distAcumulada += coords[v].Distance(coords[v - 1]);
                    }

                    double azimute = 0.0;
                    if (v < coords.Length - 1)
                    {
                        double dx = coords[v + 1].X - coords[v].X;
                        double dy = coords[v + 1].Y - coords[v].Y;
                        azimute = (Math.Atan2(dx, dy) * 180.0 / Math.PI + 360.0) % 360.0;
                    }
                    else if (coords.Length > 1)
                    {
                        // No último vértice, herda o azimute do segmento que chega nele
                        double dx = coords[v].X - coords[v - 1].X;
                        double dy = coords[v].Y - coords[v - 1].Y;
                        azimute = (Math.Atan2(dx, dy) * 180.0 / Math.PI + 360.0) % 360.0;
                    }

                    var pt = geomFactory.CreatePoint(coords[v]);
                    var attrs = new AttributesTable();
                    if (f.Attributes != null)
                    {
                        foreach (var nome in f.Attributes.GetNames())
                            attrs.Add(nome, f.Attributes[nome]);
                    }
                    attrs.Add("ORIG_FID", i + 1);
                    attrs.Add("V_IDX", v);
                    attrs.Add("DIST_M", Math.Round(distAcumulada, 2));
                    attrs.Add("AZIM_DEG", Math.Round(azimute, 2));
                    attrs.Add("IS_ENDPT", v == 0 || v == coords.Length - 1 ? 1 : 0);

                    resultado.Add(new Feature(pt, attrs));
                }

                if (i % 200 == 0 || i == total - 1)
                {
                    double taxa = sw.Elapsed.TotalSeconds > 0 ? (i + 1) / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total,
                        $"Extrair Vértices: feição {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            return resultado;
        }
    }
}
