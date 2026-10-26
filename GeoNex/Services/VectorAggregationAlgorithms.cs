using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;

namespace GeoNex.Services
{
    /// <summary>
    /// Algoritmos de agregação vetorial: Dissolução de geometrias e fusão de fronteiras
    /// com agregação estatística de atributos.
    /// </summary>
    public static class VectorAggregationAlgorithms
    {
        /// <summary>
        /// Dissolve feições eliminando fronteiras internas comuns, unificando polígonos
        /// ou linhas de forma global ou agrupada por atributo.
        /// </summary>
        public static List<IFeature> ExecutarDissolucao(
            IReadOnlyList<IFeature> feicoes,
            string? campoAgrupamento,
            ModoAgregacaoDissolve modoAgregacao = ModoAgregacaoDissolve.PrimeiroValor,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (feicoes == null || feicoes.Count == 0)
                return resultado;

            var sw = Stopwatch.StartNew();

            // 1. MODO DISSOLVER TUDO (Dissolve All)
            if (string.IsNullOrWhiteSpace(campoAgrupamento))
            {
                progresso?.Report(new GeoprocessingProgress(0.1, "Coletando geometrias para dissolução global...", 0, feicoes.Count));
                ct.ThrowIfCancellationRequested();

                var geoms = new List<Geometry>(feicoes.Count);
                for (int i = 0; i < feicoes.Count; i++)
                {
                    var g = feicoes[i].Geometry;
                    if (g != null && !g.IsEmpty) geoms.Add(g);
                }

                progresso?.Report(new GeoprocessingProgress(0.4, $"Unificando {geoms.Count} geometrias em cascata...", geoms.Count, feicoes.Count));
                ct.ThrowIfCancellationRequested();

                Geometry uniao = UnificarGeometrias(geoms);
                if (uniao != null && !uniao.IsEmpty)
                {
                    var attrs = new AttributesTable();
                    attrs.Add("TOTAL_FEIC", feicoes.Count);
                    attrs.Add("METODO", "DISSOLVE_ALL");
                    resultado.Add(new Feature(uniao, attrs));
                }

                progresso?.Report(new GeoprocessingProgress(1.0, "Dissolução global concluída.", feicoes.Count, feicoes.Count));
                return resultado;
            }

            // 2. MODO DISSOLVER POR ATRIBUTO (Dissolve by Field)
            progresso?.Report(new GeoprocessingProgress(0.1, $"Agrupando feições pelo campo '{campoAgrupamento}'...", 0, feicoes.Count));
            var grupos = new Dictionary<string, List<IFeature>>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < feicoes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = feicoes[i];
                if (f.Geometry == null || f.Geometry.IsEmpty) continue;

                string chave = "<NULO>";
                if (f.Attributes != null && f.Attributes.Exists(campoAgrupamento))
                {
                    var val = f.Attributes[campoAgrupamento];
                    chave = val?.ToString() ?? "<NULO>";
                }

                if (!grupos.TryGetValue(chave, out var lista))
                {
                    lista = new List<IFeature>();
                    grupos[chave] = lista;
                }
                lista.Add(f);
            }

            int totalGrupos = grupos.Count;
            int grupoIdx = 0;

            foreach (var kvp in grupos)
            {
                ct.ThrowIfCancellationRequested();
                string chave = kvp.Key;
                var feicoesGrupo = kvp.Value;

                var geomsGrupo = new List<Geometry>(feicoesGrupo.Count);
                for (int g = 0; g < feicoesGrupo.Count; g++)
                {
                    geomsGrupo.Add(feicoesGrupo[g].Geometry);
                }

                var uniao = UnificarGeometrias(geomsGrupo);
                if (uniao != null && !uniao.IsEmpty)
                {
                    var attrs = AgregarAtributos(feicoesGrupo, campoAgrupamento, chave, modoAgregacao);
                    resultado.Add(new Feature(uniao, attrs));
                }

                grupoIdx++;
                double taxa = sw.Elapsed.TotalSeconds > 0 ? grupoIdx / sw.Elapsed.TotalSeconds : 0;
                progresso?.Report(new GeoprocessingProgress(
                    grupoIdx / (double)totalGrupos,
                    $"Dissolvendo grupo {grupoIdx}/{totalGrupos} ('{chave}')",
                    grupoIdx,
                    totalGrupos,
                    taxa));
            }

            return resultado;
        }

        /// <summary>
        /// Mescla listas de feições compatíveis em um único conjunto.
        /// </summary>
        public static List<IFeature> ExecutarMesclagemCamadas(
            IReadOnlyList<IReadOnlyList<IFeature>> camadasFeicoes,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (camadasFeicoes == null || camadasFeicoes.Count == 0)
                return resultado;

            int total = camadasFeicoes.Sum(c => c.Count);
            int processadas = 0;

            for (int c = 0; c < camadasFeicoes.Count; c++)
            {
                var camada = camadasFeicoes[c];
                for (int i = 0; i < camada.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    resultado.Add(camada[i]);
                    processadas++;
                }

                progresso?.Report(new GeoprocessingProgress(
                    processadas / (double)total,
                    $"Mesclando camada {c + 1}/{camadasFeicoes.Count}",
                    processadas,
                    total));
            }

            return resultado;
        }

        private static Geometry UnificarGeometrias(IReadOnlyList<Geometry> geoms)
        {
            if (geoms == null || geoms.Count == 0)
                return GeometryFactory.Default.CreatePolygon();

            if (geoms.Count == 1)
                return geoms[0];

            var lista = geoms.ToList();

            try
            {
                return CascadedPolygonUnion.Union(lista);
            }
            catch
            {
                try
                {
                    return UnaryUnionOp.Union(lista);
                }
                catch
                {
                    // Fallback com reparo
                    var reparadas = new List<Geometry>(geoms.Count);
                    for (int i = 0; i < geoms.Count; i++)
                    {
                        reparadas.Add(geoms[i].Buffer(0));
                    }
                    return UnaryUnionOp.Union(reparadas);
                }
            }
        }

        private static AttributesTable AgregarAtributos(
            IReadOnlyList<IFeature> feicoes,
            string campoAgrupamento,
            string valorChave,
            ModoAgregacaoDissolve modo)
        {
            var attrs = new AttributesTable();
            string nomeCampoTruncado = campoAgrupamento.Length > 10 ? campoAgrupamento.Substring(0, 10) : campoAgrupamento;
            attrs.Add(nomeCampoTruncado, valorChave);
            attrs.Add("CONTAGEM", feicoes.Count);

            if (feicoes.Count == 0 || feicoes[0].Attributes == null)
                return attrs;

            var nomesCampos = feicoes[0].Attributes.GetNames();
            foreach (var nome in nomesCampos)
            {
                if (string.Equals(nome, campoAgrupamento, StringComparison.OrdinalIgnoreCase))
                    continue;

                string nomeTrunk = nome.Length > 10 ? nome.Substring(0, 10) : nome;

                if (modo == ModoAgregacaoDissolve.PrimeiroValor)
                {
                    attrs.Add(nomeTrunk, feicoes[0].Attributes[nome]);
                }
                else if (modo == ModoAgregacaoDissolve.Soma || modo == ModoAgregacaoDissolve.Media)
                {
                    // Tenta somar valores numéricos
                    double soma = 0;
                    int countValidos = 0;
                    bool ehNumerico = false;

                    for (int i = 0; i < feicoes.Count; i++)
                    {
                        var val = feicoes[i].Attributes?[nome];
                        if (val != null && double.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                        {
                            soma += d;
                            countValidos++;
                            ehNumerico = true;
                        }
                    }

                    if (ehNumerico && countValidos > 0)
                    {
                        if (modo == ModoAgregacaoDissolve.Soma)
                            attrs.Add(nomeTrunk, Math.Round(soma, 4));
                        else
                            attrs.Add(nomeTrunk, Math.Round(soma / countValidos, 4));
                    }
                    else
                    {
                        attrs.Add(nomeTrunk, feicoes[0].Attributes[nome]);
                    }
                }
            }

            return attrs;
        }
    }
}
