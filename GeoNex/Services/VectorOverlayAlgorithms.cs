using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Index.Strtree;
using Point = NetTopologySuite.Geometries.Point;

namespace GeoNex.Services
{
    /// <summary>
    /// Algoritmos matemáticos de sobreposição vetorial (Overlay): Recorte (Clip),
    /// Intersecção (Intersection) e Diferença (Difference / Erase).
    /// </summary>
    public static class VectorOverlayAlgorithms
    {
        /// <summary>
        /// Recorta a camada de entrada pelo polígono de máscara unificado.
        /// Preserva integralmente os atributos das feições de entrada.
        /// </summary>
        public static List<IFeature> ExecutarRecorte(
            IReadOnlyList<IFeature> feicoesEntrada,
            Geometry mascaraUnificada,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (feicoesEntrada == null || feicoesEntrada.Count == 0 || mascaraUnificada == null || mascaraUnificada.IsEmpty)
                return resultado;

            var envMascara = mascaraUnificada.EnvelopeInternal;
            int total = feicoesEntrada.Count;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var feicao = feicoesEntrada[i];
                var geom = feicao.Geometry;

                if (geom == null || geom.IsEmpty) continue;

                var envGeom = geom.EnvelopeInternal;

                // 1. Fast-path: Bounding Box exterior rejeitado em O(1)
                if (!envMascara.Intersects(envGeom))
                    continue;

                // 2. Fast-path: Totalmente contido (sem necessidade de corte)
                if (envMascara.Contains(envGeom) && mascaraUnificada.Contains(geom))
                {
                    resultado.Add(ClonarFeicao(feicao, geom));
                    continue;
                }

                // 3. Corte topológico exato
                if (mascaraUnificada.Intersects(geom))
                {
                    try
                    {
                        var recorte = geom.Intersection(mascaraUnificada);
                        if (recorte != null && !recorte.IsEmpty)
                        {
                            var filtrado = FiltrarDimensao(recorte, geom.Dimension);
                            if (filtrado != null && !filtrado.IsEmpty)
                            {
                                resultado.Add(ClonarFeicao(feicao, filtrado));
                            }
                        }
                    }
                    catch
                    {
                        // Se falhar por precisão de ponto flutuante, tenta com Buffer(0)
                        try
                        {
                            var geomReparada = geom.Buffer(0);
                            var mascaraReparada = mascaraUnificada.Buffer(0);
                            var recorte = geomReparada.Intersection(mascaraReparada);
                            if (recorte != null && !recorte.IsEmpty)
                            {
                                var filtrado = FiltrarDimensao(recorte, geom.Dimension);
                                if (filtrado != null && !filtrado.IsEmpty)
                                {
                                    resultado.Add(ClonarFeicao(feicao, filtrado));
                                }
                            }
                        }
                        catch
                        {
                            // Ignora feição degenerada irrecuperável
                        }
                    }
                }

                if (i % 200 == 0 || i == total - 1)
                {
                    double taxa = i > 0 && sw.Elapsed.TotalSeconds > 0 ? i / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total,
                        $"Recortando feição {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            return resultado;
        }

        /// <summary>
        /// Realiza o cruzamento pleno entre Camada A e Camada B com fusão de esquemas de atributos
        /// e resolução de colisões de nomes para o formato DBF.
        /// </summary>
        public static List<IFeature> ExecutarInterseccao(
            IReadOnlyList<IFeature> feicoesA,
            IReadOnlyList<IFeature> feicoesB,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (feicoesA == null || feicoesA.Count == 0 || feicoesB == null || feicoesB.Count == 0)
                return resultado;

            // 1. Indexar Camada B em STRtree
            var treeB = new STRtree<IFeature>();
            for (int b = 0; b < feicoesB.Count; b++)
            {
                var fb = feicoesB[b];
                if (fb.Geometry != null && !fb.Geometry.IsEmpty)
                {
                    treeB.Insert(fb.Geometry.EnvelopeInternal, fb);
                }
            }
            treeB.Build();

            int totalA = feicoesA.Count;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < totalA; i++)
            {
                ct.ThrowIfCancellationRequested();
                var fa = feicoesA[i];
                var geomA = fa.Geometry;

                if (geomA == null || geomA.IsEmpty) continue;

                var candidatosB = treeB.Query(geomA.EnvelopeInternal);
                if (candidatosB == null || candidatosB.Count == 0) continue;

                for (int j = 0; j < candidatosB.Count; j++)
                {
                    var fb = candidatosB[j];
                    var geomB = fb.Geometry;

                    if (geomB == null || geomB.IsEmpty) continue;

                    if (geomA.Intersects(geomB))
                    {
                        try
                        {
                            var inter = geomA.Intersection(geomB);
                            if (inter != null && !inter.IsEmpty)
                            {
                                var dimEsperada = (Dimension)Math.Min((int)geomA.Dimension, (int)geomB.Dimension);
                                var filtrado = FiltrarDimensao(inter, dimEsperada);
                                if (filtrado != null && !filtrado.IsEmpty)
                                {
                                    var attrsFundidos = FundirAtributos(fa.Attributes, fb.Attributes);
                                    resultado.Add(new Feature(filtrado, attrsFundidos));
                                }
                            }
                        }
                        catch
                        {
                            // Tenta auto-reparo com Buffer(0)
                            try
                            {
                                var inter = geomA.Buffer(0).Intersection(geomB.Buffer(0));
                                if (inter != null && !inter.IsEmpty)
                                {
                                    var dimEsperada = (Dimension)Math.Min((int)geomA.Dimension, (int)geomB.Dimension);
                                    var filtrado = FiltrarDimensao(inter, dimEsperada);
                                    if (filtrado != null && !filtrado.IsEmpty)
                                    {
                                        var attrsFundidos = FundirAtributos(fa.Attributes, fb.Attributes);
                                        resultado.Add(new Feature(filtrado, attrsFundidos));
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }

                if (i % 200 == 0 || i == totalA - 1)
                {
                    double taxa = i > 0 && sw.Elapsed.TotalSeconds > 0 ? i / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)totalA,
                        $"Intersecção: processando feição {i + 1}/{totalA}",
                        i + 1,
                        totalA,
                        taxa));
                }
            }

            return resultado;
        }

        /// <summary>
        /// Subtrai as áreas da máscara da camada de entrada (Diferença / Erase).
        /// Preserva os atributos da camada de entrada.
        /// </summary>
        public static List<IFeature> ExecutarDiferenca(
            IReadOnlyList<IFeature> feicoesEntrada,
            Geometry mascaraSubtracao,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (feicoesEntrada == null || feicoesEntrada.Count == 0)
                return resultado;

            if (mascaraSubtracao == null || mascaraSubtracao.IsEmpty)
            {
                // Nada a subtrair: retorna todas as feições de entrada
                for (int i = 0; i < feicoesEntrada.Count; i++)
                    resultado.Add(ClonarFeicao(feicoesEntrada[i], feicoesEntrada[i].Geometry));
                return resultado;
            }

            var envMascara = mascaraSubtracao.EnvelopeInternal;
            int total = feicoesEntrada.Count;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var feicao = feicoesEntrada[i];
                var geom = feicao.Geometry;

                if (geom == null || geom.IsEmpty) continue;

                // 1. Fast-path: Se a máscara não intersecta o envelope, mantém feição 100%
                if (!envMascara.Intersects(geom.EnvelopeInternal))
                {
                    resultado.Add(ClonarFeicao(feicao, geom));
                    continue;
                }

                // 2. Se a máscara contém totalmente a feição, ela é 100% apagada
                if (mascaraSubtracao.Contains(geom))
                {
                    continue;
                }

                // 3. Subtração booleana
                try
                {
                    var dif = geom.Difference(mascaraSubtracao);
                    if (dif != null && !dif.IsEmpty)
                    {
                        var filtrado = FiltrarDimensao(dif, geom.Dimension);
                        if (filtrado != null && !filtrado.IsEmpty)
                        {
                            resultado.Add(ClonarFeicao(feicao, filtrado));
                        }
                    }
                }
                catch
                {
                    try
                    {
                        var dif = geom.Buffer(0).Difference(mascaraSubtracao.Buffer(0));
                        if (dif != null && !dif.IsEmpty)
                        {
                            var filtrado = FiltrarDimensao(dif, geom.Dimension);
                            if (filtrado != null && !filtrado.IsEmpty)
                            {
                                resultado.Add(ClonarFeicao(feicao, filtrado));
                            }
                        }
                    }
                    catch { }
                }

                if (i % 200 == 0 || i == total - 1)
                {
                    double taxa = i > 0 && sw.Elapsed.TotalSeconds > 0 ? i / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total,
                        $"Diferença: processando feição {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            return resultado;
        }

        private static IFeature ClonarFeicao(IFeature origem, Geometry novaGeometria)
        {
            var attrs = new AttributesTable();
            if (origem.Attributes != null)
            {
                foreach (var nome in origem.Attributes.GetNames())
                {
                    attrs.Add(nome, origem.Attributes[nome]);
                }
            }
            return new Feature(novaGeometria, attrs);
        }

        public static AttributesTable FundirAtributos(IAttributesTable? attrsA, IAttributesTable? attrsB)
        {
            var resultado = new AttributesTable();
            var nomesUsados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (attrsA != null)
            {
                foreach (var nome in attrsA.GetNames())
                {
                    string nomeSanitizado = TruncarNomeDbf(nome);
                    resultado.Add(nomeSanitizado, attrsA[nome]);
                    nomesUsados.Add(nomeSanitizado);
                }
            }

            if (attrsB != null)
            {
                foreach (var nome in attrsB.GetNames())
                {
                    string nomeFinal = TruncarNomeDbf(nome);
                    if (nomesUsados.Contains(nomeFinal))
                    {
                        // Conflito de nomes: adiciona sufixo _1, _2
                        int sufixo = 1;
                        string raiz = nomeFinal.Length > 8 ? nomeFinal.Substring(0, 8) : nomeFinal;
                        string candidato = $"{raiz}_{sufixo}";
                        while (nomesUsados.Contains(candidato))
                        {
                            sufixo++;
                            candidato = $"{raiz}_{sufixo}";
                        }
                        nomeFinal = candidato;
                    }
                    resultado.Add(nomeFinal, attrsB[nome]);
                    nomesUsados.Add(nomeFinal);
                }
            }

            return resultado;
        }

        private static string TruncarNomeDbf(string nome)
        {
            if (string.IsNullOrEmpty(nome)) return "CAMPO";
            string limpo = nome.Trim().Replace(" ", "_");
            return limpo.Length > 10 ? limpo.Substring(0, 10) : limpo;
        }

        private static Geometry? FiltrarDimensao(Geometry geom, Dimension dimEsperada)
        {
            if (geom == null || geom.IsEmpty) return null;

            if (geom.Dimension == dimEsperada)
                return geom;

            if (dimEsperada == Dimension.Surface)
            {
                var poligonos = PolygonExtracter.GetPolygons(geom).Cast<Polygon>().ToArray();
                if (poligonos.Length == 1) return poligonos[0];
                if (poligonos.Length > 1) return geom.Factory.CreateMultiPolygon(poligonos);
                return null;
            }

            if (dimEsperada == Dimension.Curve)
            {
                var linhas = LineStringExtracter.GetLines(geom).Cast<LineString>().ToArray();
                if (linhas.Length == 1) return linhas[0];
                if (linhas.Length > 1) return geom.Factory.CreateMultiLineString(linhas);
                return null;
            }

            if (dimEsperada == Dimension.Point)
            {
                var pontos = PointExtracter.GetPoints(geom).Cast<Point>().ToArray();
                if (pontos.Length == 1) return pontos[0];
                if (pontos.Length > 1) return geom.Factory.CreateMultiPoint(pontos);
                return null;
            }

            return geom;
        }
    }
}
