using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NetTopologySuite;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Triangulate;
using Point = NetTopologySuite.Geometries.Point;

namespace GeoNex.Services
{
    /// <summary>
    /// Algoritmos de proximidade e tesselação espacial contínua:
    /// Voronoi (Thiessen), Triangulação de Delaunay (TIN) e Matriz de Distâncias / Vizinho Mais Próximo (Clark-Evans).
    /// </summary>
    public static class ProximityTessellationAlgorithms
    {
        public static List<IFeature> ExecutarVoronoi(
            IReadOnlyList<IFeature> pontos,
            Geometry? mascaraLimite = null,
            double margemPercentual = 0.2,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (pontos == null || pontos.Count == 0) return resultado;

            var coordsValidas = new List<Coordinate>();
            var arvorePontos = new STRtree<IFeature>();

            foreach (var f in pontos)
            {
                if (f.Geometry != null && !f.Geometry.IsEmpty)
                {
                    coordsValidas.Add(f.Geometry.Coordinate);
                    arvorePontos.Insert(f.Geometry.EnvelopeInternal, f);
                }
            }

            if (coordsValidas.Count == 0) return resultado;

            var builder = new VoronoiDiagramBuilder();
            builder.SetSites(coordsValidas);

            var env = new Envelope();
            foreach (var c in coordsValidas) env.ExpandToInclude(c);
            if (mascaraLimite != null && !mascaraLimite.IsEmpty)
            {
                env.ExpandToInclude(mascaraLimite.EnvelopeInternal);
            }
            double delta = Math.Max(env.Width, env.Height) * Math.Max(0.01, margemPercentual);
            env.ExpandBy(delta);
            builder.ClipEnvelope = env;

            var geomFactory = NtsGeometryServices.Instance.CreateGeometryFactory();
            var diagrama = builder.GetDiagram(geomFactory);
            if (diagrama == null || diagrama.IsEmpty) return resultado;

            int total = diagrama.NumGeometries;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var celula = diagrama.GetGeometryN(i);
                if (celula == null || celula.IsEmpty) continue;

                if (mascaraLimite != null && !mascaraLimite.IsEmpty)
                {
                    try
                    {
                        celula = celula.Intersection(mascaraLimite);
                    }
                    catch
                    {
                        try { celula = celula.Buffer(0).Intersection(mascaraLimite.Buffer(0)); } catch { celula = null; }
                    }
                    if (celula == null || celula.IsEmpty || !(celula is Polygon || celula is MultiPolygon))
                        continue;
                }

                // Identifica o ponto gerador original associado à célula
                var candidatos = arvorePontos.Query(celula.EnvelopeInternal);
                IFeature? gerador = null;
                foreach (var cand in candidatos)
                {
                    if (celula.Contains(cand.Geometry) || celula.Intersects(cand.Geometry))
                    {
                        gerador = cand;
                        break;
                    }
                }

                var attrs = new AttributesTable();
                if (gerador?.Attributes != null)
                {
                    foreach (var nome in gerador.Attributes.GetNames())
                    {
                        attrs.Add(nome, gerador.Attributes[nome]);
                    }
                }
                attrs.Add("VOR_ID", i + 1);
                attrs.Add("VOR_AREA", Math.Round(celula.Area, 4));

                resultado.Add(new Feature(celula, attrs));

                if (i % 100 == 0 || i == total - 1)
                {
                    double taxa = sw.Elapsed.TotalSeconds > 0 ? (i + 1) / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total,
                        $"Voronoi: célula {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            return resultado;
        }

        public static List<IFeature> ExecutarTriangulacaoDelaunay(
            IReadOnlyList<IFeature> pontos,
            double tolerancia = 0.0,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (pontos == null || pontos.Count == 0) return resultado;

            var coords = new List<Coordinate>();
            foreach (var f in pontos)
            {
                if (f.Geometry != null && !f.Geometry.IsEmpty)
                {
                    coords.Add(f.Geometry.Coordinate);
                }
            }

            if (coords.Count < 3) return resultado;

            var builder = new DelaunayTriangulationBuilder { Tolerance = tolerancia };
            builder.SetSites(coords);

            var geomFactory = NtsGeometryServices.Instance.CreateGeometryFactory();
            var triangulos = builder.GetTriangles(geomFactory);
            if (triangulos == null || triangulos.IsEmpty) return resultado;

            int total = triangulos.NumGeometries;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var tri = triangulos.GetGeometryN(i) as Polygon;
                if (tri == null || tri.IsEmpty) continue;

                var c = tri.ExteriorRing.Coordinates;
                double l1 = c[0].Distance(c[1]);
                double l2 = c[1].Distance(c[2]);
                double l3 = c[2].Distance(c[0]);
                double perim = l1 + l2 + l3;
                double maxEdge = Math.Max(l1, Math.Max(l2, l3));

                var attrs = new AttributesTable
                {
                    { "TRI_ID", i + 1 },
                    { "AREA_M2", Math.Round(tri.Area, 4) },
                    { "PERIM_M", Math.Round(perim, 4) },
                    { "MAX_EDGE", Math.Round(maxEdge, 4) }
                };

                resultado.Add(new Feature(tri, attrs));

                if (i % 200 == 0 || i == total - 1)
                {
                    double taxa = sw.Elapsed.TotalSeconds > 0 ? (i + 1) / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total,
                        $"Delaunay: triângulo {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            return resultado;
        }

        public static (List<IFeature> Linhas, string RelatorioEstatistico) ExecutarVizinhoMaisProximo(
            IReadOnlyList<IFeature> pontos,
            double? areaEstudoM2 = null,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var linhasResultado = new List<IFeature>();
            if (pontos == null || pontos.Count < 2)
                return (linhasResultado, "Pontos insuficientes para análise de vizinhança.");

            var arvore = new STRtree<IFeature>();
            var envelopeTotal = new Envelope();
            var pontosValidos = new List<IFeature>();

            foreach (var p in pontos)
            {
                if (p.Geometry != null && !p.Geometry.IsEmpty)
                {
                    pontosValidos.Add(p);
                    arvore.Insert(p.Geometry.EnvelopeInternal, p);
                    envelopeTotal.ExpandToInclude(p.Geometry.EnvelopeInternal);
                }
            }

            int n = pontosValidos.Count;
            if (n < 2) return (linhasResultado, "Pontos válidos insuficientes.");

            var geomFactory = NtsGeometryServices.Instance.CreateGeometryFactory();
            double somaDistancias = 0.0;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < n; i++)
            {
                ct.ThrowIfCancellationRequested();
                var pAtual = pontosValidos[i];
                var cAtual = pAtual.Geometry.Coordinate;

                double raioBusca = Math.Max(envelopeTotal.Width, envelopeTotal.Height) * 0.05;
                if (raioBusca <= 0) raioBusca = 100.0;
                IFeature? vizinho = null;
                double distMin = double.MaxValue;

                while (vizinho == null && raioBusca < Math.Max(envelopeTotal.Width, envelopeTotal.Height) * 3)
                {
                    var envBusca = new Envelope(cAtual.X - raioBusca, cAtual.X + raioBusca, cAtual.Y - raioBusca, cAtual.Y + raioBusca);
                    var candidatos = arvore.Query(envBusca);
                    foreach (var cand in candidatos)
                    {
                        if (ReferenceEquals(cand, pAtual)) continue;
                        double d = cAtual.Distance(cand.Geometry.Coordinate);
                        if (d > 0.000001 && d < distMin)
                        {
                            distMin = d;
                            vizinho = cand;
                        }
                    }
                    raioBusca *= 2;
                }

                if (vizinho != null)
                {
                    somaDistancias += distMin;
                    var linha = geomFactory.CreateLineString(new[] { cAtual, vizinho.Geometry.Coordinate });
                    var attrs = new AttributesTable
                    {
                        { "ORIG_FID", i + 1 },
                        { "DIST_M", Math.Round(distMin, 4) }
                    };
                    linhasResultado.Add(new Feature(linha, attrs));
                }

                if (i % 200 == 0 || i == n - 1)
                {
                    double taxa = sw.Elapsed.TotalSeconds > 0 ? (i + 1) / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)n,
                        $"Vizinho Mais Próximo: feição {i + 1}/{n}",
                        i + 1,
                        n,
                        taxa));
                }
            }

            double rA = somaDistancias / Math.Max(1, n);
            double area = areaEstudoM2 ?? Math.Max(1.0, envelopeTotal.Width * envelopeTotal.Height);
            double densidade = n / area;
            double rE = densidade > 0 ? 0.5 / Math.Sqrt(densidade) : 0;
            double r = rE > 0 ? rA / rE : 1.0;
            double se = densidade > 0 ? 0.26136 / Math.Sqrt(n * densidade) : 1.0;
            double z = se > 0 ? (rA - rE) / se : 0.0;

            string padrao = r < 0.9 ? "Agrupado (Clustered)" : (r > 1.1 ? "Disperso (Regular)" : "Aleatório (Random)");
            string relatorio = $"Clark-Evans R: {r:F4} ({padrao}) | Z-Score: {z:F2} | Dist. Média Observada: {rA:F2}m | Dist. Média Esperada: {rE:F2}m | N: {n}";

            return (linhasResultado, relatorio);
        }
    }
}
