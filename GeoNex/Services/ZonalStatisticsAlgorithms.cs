using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;

namespace GeoNex.Services
{
    /// <summary>
    /// Algoritmos de estatísticas espaciais zonais e agregação multidimensional:
    /// Contagem e Soma de Pontos em Polígonos, Densidade Linear por Zona e Junção Espacial Avançada (Spatial Join).
    /// </summary>
    public static class ZonalStatisticsAlgorithms
    {
        public static List<IFeature> ExecutarContagemPontosPoligono(
            IReadOnlyList<IFeature> zonas,
            IReadOnlyList<IFeature> pontos,
            string? campoValorSomar = null,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (zonas == null || zonas.Count == 0) return resultado;

            var arvorePontos = new STRtree<IFeature>();
            if (pontos != null)
            {
                foreach (var p in pontos)
                {
                    if (p.Geometry != null && !p.Geometry.IsEmpty)
                        arvorePontos.Insert(p.Geometry.EnvelopeInternal, p);
                }
            }

            int total = zonas.Count;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var z = zonas[i];
                if (z.Geometry == null || z.Geometry.IsEmpty) continue;

                var prep = PreparedGeometryFactory.Prepare(z.Geometry);
                var candidatos = arvorePontos.Query(z.Geometry.EnvelopeInternal);

                int contagem = 0;
                double soma = 0.0;
                double minVal = double.MaxValue;
                double maxVal = double.MinValue;

                foreach (var cand in candidatos)
                {
                    if (prep.Contains(cand.Geometry))
                    {
                        contagem++;
                        if (!string.IsNullOrWhiteSpace(campoValorSomar) && cand.Attributes != null && cand.Attributes.Exists(campoValorSomar))
                        {
                            var valObj = cand.Attributes[campoValorSomar];
                            if (valObj != null && double.TryParse(valObj.ToString(), out double num))
                            {
                                soma += num;
                                if (num < minVal) minVal = num;
                                if (num > maxVal) maxVal = num;
                            }
                        }
                    }
                }

                var attrs = new AttributesTable();
                if (z.Attributes != null)
                {
                    foreach (var nome in z.Attributes.GetNames())
                        attrs.Add(nome, z.Attributes[nome]);
                }
                attrs.Add("PT_COUNT", contagem);
                if (!string.IsNullOrWhiteSpace(campoValorSomar))
                {
                    attrs.Add("PT_SUM", Math.Round(soma, 4));
                    attrs.Add("PT_MEAN", contagem > 0 ? Math.Round(soma / contagem, 4) : 0.0);
                    attrs.Add("PT_MIN", contagem > 0 && minVal != double.MaxValue ? Math.Round(minVal, 4) : 0.0);
                    attrs.Add("PT_MAX", contagem > 0 && maxVal != double.MinValue ? Math.Round(maxVal, 4) : 0.0);
                }

                resultado.Add(new Feature(z.Geometry, attrs));

                if (i % 200 == 0 || i == total - 1)
                {
                    double taxa = sw.Elapsed.TotalSeconds > 0 ? (i + 1) / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total,
                        $"Contagem Zonal: polígono {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            return resultado;
        }

        public static List<IFeature> ExecutarDensidadeLinearPoligonos(
            IReadOnlyList<IFeature> zonas,
            IReadOnlyList<IFeature> linhas,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (zonas == null || zonas.Count == 0) return resultado;

            var arvoreLinhas = new STRtree<IFeature>();
            if (linhas != null)
            {
                foreach (var l in linhas)
                {
                    if (l.Geometry != null && !l.Geometry.IsEmpty)
                        arvoreLinhas.Insert(l.Geometry.EnvelopeInternal, l);
                }
            }

            int total = zonas.Count;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var z = zonas[i];
                if (z.Geometry == null || z.Geometry.IsEmpty) continue;

                var prep = PreparedGeometryFactory.Prepare(z.Geometry);
                var candidatos = arvoreLinhas.Query(z.Geometry.EnvelopeInternal);
                double compTotalM = 0.0;

                foreach (var cand in candidatos)
                {
                    if (prep.Intersects(cand.Geometry))
                    {
                        try
                        {
                            var recorte = z.Geometry.Intersection(cand.Geometry);
                            if (recorte != null && !recorte.IsEmpty)
                                compTotalM += recorte.Length;
                        }
                        catch
                        {
                            try
                            {
                                var recorte = z.Geometry.Buffer(0).Intersection(cand.Geometry.Buffer(0));
                                if (recorte != null && !recorte.IsEmpty)
                                    compTotalM += recorte.Length;
                            }
                            catch { }
                        }
                    }
                }

                double areaKm2 = z.Geometry.Area / 1_000_000.0;
                double densidade = areaKm2 > 0.000001 ? (compTotalM / 1_000.0) / areaKm2 : 0.0;

                var attrs = new AttributesTable();
                if (z.Attributes != null)
                {
                    foreach (var nome in z.Attributes.GetNames())
                        attrs.Add(nome, z.Attributes[nome]);
                }
                attrs.Add("LINE_LEN_M", Math.Round(compTotalM, 2));
                attrs.Add("LINE_KM", Math.Round(compTotalM / 1000.0, 3));
                attrs.Add("AREA_KM2", Math.Round(areaKm2, 4));
                attrs.Add("DENS_KM_KM2", Math.Round(densidade, 4));

                resultado.Add(new Feature(z.Geometry, attrs));

                if (i % 200 == 0 || i == total - 1)
                {
                    double taxa = sw.Elapsed.TotalSeconds > 0 ? (i + 1) / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total,
                        $"Densidade Linear: polígono {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            return resultado;
        }

        public static List<IFeature> ExecutarSpatialJoinAvancado(
            IReadOnlyList<IFeature> camadaAlvo,
            IReadOnlyList<IFeature> camadaJuncao,
            string predicadoTopologico = "Intersects",
            double distanciaToleranciaMetros = 0.0,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (camadaAlvo == null || camadaAlvo.Count == 0) return resultado;

            if (camadaJuncao == null || camadaJuncao.Count == 0)
            {
                foreach (var f in camadaAlvo)
                    resultado.Add(new Feature(f.Geometry, f.Attributes));
                return resultado;
            }

            var arvore = new STRtree<IFeature>();
            foreach (var fj in camadaJuncao)
            {
                if (fj.Geometry != null && !fj.Geometry.IsEmpty)
                {
                    var env = fj.Geometry.EnvelopeInternal;
                    if (distanciaToleranciaMetros > 0.001)
                    {
                        var envExp = new Envelope(env);
                        envExp.ExpandBy(distanciaToleranciaMetros);
                        arvore.Insert(envExp, fj);
                    }
                    else
                    {
                        arvore.Insert(env, fj);
                    }
                }
            }

            int total = camadaAlvo.Count;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var alvo = camadaAlvo[i];
                if (alvo.Geometry == null || alvo.Geometry.IsEmpty) continue;

                var prep = PreparedGeometryFactory.Prepare(alvo.Geometry);
                var envBusca = alvo.Geometry.EnvelopeInternal;
                if (distanciaToleranciaMetros > 0.001)
                {
                    var envExp = new Envelope(envBusca);
                    envExp.ExpandBy(distanciaToleranciaMetros);
                    envBusca = envExp;
                }

                var candidatos = arvore.Query(envBusca);
                IFeature? correspondente = null;

                foreach (var c in candidatos)
                {
                    bool satisfez = predicadoTopologico switch
                    {
                        "Contains" => prep.Contains(c.Geometry),
                        "Within" => prep.Within(c.Geometry),
                        "Touches" => alvo.Geometry.Touches(c.Geometry),
                        "Crosses" => alvo.Geometry.Crosses(c.Geometry),
                        "WithinDistance" => alvo.Geometry.Distance(c.Geometry) <= distanciaToleranciaMetros,
                        _ => prep.Intersects(c.Geometry)
                    };

                    if (satisfez)
                    {
                        correspondente = c;
                        break; // 1-to-1 match
                    }
                }

                var attrs = VectorOverlayAlgorithms.FundirAtributos(alvo.Attributes, correspondente?.Attributes);
                resultado.Add(new Feature(alvo.Geometry, attrs));

                if (i % 200 == 0 || i == total - 1)
                {
                    double taxa = sw.Elapsed.TotalSeconds > 0 ? (i + 1) / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total,
                        $"Spatial Join: feição {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            return resultado;
        }
    }
}
