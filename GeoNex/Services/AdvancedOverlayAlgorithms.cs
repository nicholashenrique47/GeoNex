using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Operation.Union;

namespace GeoNex.Services
{
    /// <summary>
    /// Operações de superposição topológica avançada:
    /// Diferença Simétrica (XOR Espacial), União Topológica Plena (Full Topology Union) e Atualização de Camadas (Spatial Update / Patch).
    /// </summary>
    public static class AdvancedOverlayAlgorithms
    {
        public static List<IFeature> ExecutarDiferencaSimetrica(
            IReadOnlyList<IFeature> camadaA,
            IReadOnlyList<IFeature> camadaB,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (camadaA == null || camadaA.Count == 0)
            {
                if (camadaB != null)
                {
                    foreach (var f in camadaB)
                        resultado.Add(new Feature(f.Geometry, f.Attributes));
                }
                return resultado;
            }

            if (camadaB == null || camadaB.Count == 0)
            {
                foreach (var f in camadaA)
                    resultado.Add(new Feature(f.Geometry, f.Attributes));
                return resultado;
            }

            // 1. Unifica geometrias de B para subtrair de A
            var geomsB = camadaB.Where(f => f.Geometry != null && !f.Geometry.IsEmpty).Select(f => f.Geometry).ToList();
            var uniaoB = CascadedPolygonUnion.Union(geomsB);

            // 2. Unifica geometrias de A para subtrair de B
            var geomsA = camadaA.Where(f => f.Geometry != null && !f.Geometry.IsEmpty).Select(f => f.Geometry).ToList();
            var uniaoA = CascadedPolygonUnion.Union(geomsA);

            progresso?.Report(new GeoprocessingProgress(0.2, "Diferença Simétrica: processando parte A \\ B", 0, 100));
            var aMenosB = VectorOverlayAlgorithms.ExecutarDiferenca(camadaA, uniaoB, null, ct);

            progresso?.Report(new GeoprocessingProgress(0.6, "Diferença Simétrica: processando parte B \\ A", 50, 100));
            var bMenosA = VectorOverlayAlgorithms.ExecutarDiferenca(camadaB, uniaoA, null, ct);

            resultado.AddRange(aMenosB);
            resultado.AddRange(bMenosA);

            progresso?.Report(new GeoprocessingProgress(1.0, "Diferença Simétrica concluída com sucesso", 100, 100));
            return resultado;
        }

        public static List<IFeature> ExecutarUniaoPlena(
            IReadOnlyList<IFeature> camadaA,
            IReadOnlyList<IFeature> camadaB,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if ((camadaA == null || camadaA.Count == 0) && (camadaB == null || camadaB.Count == 0))
                return resultado;

            var sw = Stopwatch.StartNew();
            progresso?.Report(new GeoprocessingProgress(0.1, "União Plena: extraindo contornos e gerando grafo planar", 0, 100));

            var todasLinhas = new List<Geometry>();
            var arvoreA = new STRtree<IFeature>();
            var arvoreB = new STRtree<IFeature>();

            if (camadaA != null)
            {
                foreach (var fa in camadaA)
                {
                    if (fa.Geometry != null && !fa.Geometry.IsEmpty)
                    {
                        arvoreA.Insert(fa.Geometry.EnvelopeInternal, fa);
                        todasLinhas.Add(fa.Geometry.Boundary);
                    }
                }
            }

            if (camadaB != null)
            {
                foreach (var fb in camadaB)
                {
                    if (fb.Geometry != null && !fb.Geometry.IsEmpty)
                    {
                        arvoreB.Insert(fb.Geometry.EnvelopeInternal, fb);
                        todasLinhas.Add(fb.Geometry.Boundary);
                    }
                }
            }

            if (todasLinhas.Count == 0) return resultado;

            ct.ThrowIfCancellationRequested();
            var uniaoLinhas = UnaryUnionOp.Union(todasLinhas);
            var polygonizer = new Polygonizer();
            polygonizer.Add(uniaoLinhas);
            var faces = polygonizer.GetPolygons();

            int totalFaces = faces.Count;
            progresso?.Report(new GeoprocessingProgress(0.4, $"União Plena: associando atributos a {totalFaces} faces poligonais", 0, totalFaces));

            int count = 0;
            foreach (Polygon face in faces)
            {
                ct.ThrowIfCancellationRequested();
                count++;
                var pInt = face.InteriorPoint;

                var candidatosA = arvoreA.Query(face.EnvelopeInternal);
                var fa = candidatosA.FirstOrDefault(f => f.Geometry.Contains(pInt));

                var candidatosB = arvoreB.Query(face.EnvelopeInternal);
                var fb = candidatosB.FirstOrDefault(f => f.Geometry.Contains(pInt));

                // Se a face não pertence nem a A nem a B (ex: buraco entre feições), descarta
                if (fa == null && fb == null) continue;

                var attrs = VectorOverlayAlgorithms.FundirAtributos(fa?.Attributes, fb?.Attributes);
                resultado.Add(new Feature(face, attrs));

                if (count % 200 == 0 || count == totalFaces)
                {
                    double taxa = sw.Elapsed.TotalSeconds > 0 ? count / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        0.4 + 0.6 * (count / (double)totalFaces),
                        $"União Plena: face {count}/{totalFaces}",
                        count,
                        totalFaces,
                        taxa));
                }
            }

            return resultado;
        }

        public static List<IFeature> ExecutarAtualizacaoCamadas(
            IReadOnlyList<IFeature> camadaBaseA,
            IReadOnlyList<IFeature> camadaAtualizacaoB,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (camadaBaseA == null || camadaBaseA.Count == 0)
            {
                if (camadaAtualizacaoB != null)
                {
                    foreach (var f in camadaAtualizacaoB)
                        resultado.Add(new Feature(f.Geometry, f.Attributes));
                }
                return resultado;
            }

            if (camadaAtualizacaoB == null || camadaAtualizacaoB.Count == 0)
            {
                foreach (var f in camadaBaseA)
                    resultado.Add(new Feature(f.Geometry, f.Attributes));
                return resultado;
            }

            progresso?.Report(new GeoprocessingProgress(0.1, "Atualização: unificando camada prioritária B", 0, 100));
            var geomsB = camadaAtualizacaoB.Where(f => f.Geometry != null && !f.Geometry.IsEmpty).Select(f => f.Geometry).ToList();
            var uniaoB = CascadedPolygonUnion.Union(geomsB);

            progresso?.Report(new GeoprocessingProgress(0.3, "Atualização: recortando base A", 30, 100));
            var baseRecortada = VectorOverlayAlgorithms.ExecutarDiferenca(camadaBaseA, uniaoB, null, ct);

            resultado.AddRange(baseRecortada);
            foreach (var f in camadaAtualizacaoB)
            {
                resultado.Add(new Feature(f.Geometry, f.Attributes));
            }

            progresso?.Report(new GeoprocessingProgress(1.0, "Atualização concluída com sucesso", 100, 100));
            return resultado;
        }
    }
}
