using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetTopologySuite.Features;
using OSGeo.OSR;

namespace GeoNex.Services
{
    public sealed record BatchTaskItem(
        string Id,
        string Nome,
        string CaminhoEntrada,
        string CaminhoSaida,
        string AlgoritmoId,
        Dictionary<string, object?> Parametros
    );

    public sealed record BatchProgressNotification(
        int TarefaAtual,
        int TotalTarefas,
        string NomeTarefaAtual,
        double ProgressoGeral,
        double ProgressoTarefaAtual,
        string Mensagem,
        bool Sucesso = true
    );

    /// <summary>
    /// Motor de execução em lote (Batch Processing Queue) para o subsistema de geoprocessamento do GeoNex.
    /// Permite encadear dezenas de tarefas espaciais sequenciais com recuperação transacional de falhas.
    /// </summary>
    public sealed class BatchGeoprocessingEngine
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly ConcurrentQueue<BatchTaskItem> _fila = new();
        private readonly ProcessingAlgorithmRegistry _registry;

        public BatchGeoprocessingEngine(ProcessingAlgorithmRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public void EnfileirarTarefa(BatchTaskItem item)
        {
            if (item != null) _fila.Enqueue(item);
        }

        public void LimparFila()
        {
            while (_fila.TryDequeue(out _)) { }
        }

        public int QuantidadeNaFila => _fila.Count;

        /// <summary>
        /// Processa todas as tarefas enfileiradas sequencialmente em segundo plano.
        /// </summary>
        public async Task<List<GeoprocessingResult>> ExecutarFilaAsync(
            IProgress<BatchProgressNotification>? progresso = null,
            CancellationToken ct = default)
        {
            var resultados = new List<GeoprocessingResult>();
            var itens = _fila.ToArray();
            LimparFila();

            if (itens.Length == 0) return resultados;

            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            var swTotal = Stopwatch.StartNew();

            try
            {
                int total = itens.Length;
                for (int i = 0; i < total; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var task = itens[i];

                    progresso?.Report(new BatchProgressNotification(
                        i + 1,
                        total,
                        task.Nome,
                        i / (double)total,
                        0.0,
                        $"Iniciando tarefa {i + 1}/{total}: {task.Nome}"));

                    var algoritmo = _registry.ObterPorId(task.AlgoritmoId);
                    if (algoritmo == null)
                    {
                        resultados.Add(new GeoprocessingResult(false, 0, null, $"Algoritmo '{task.AlgoritmoId}' não encontrado no catálogo."));
                        continue;
                    }

                    if (!File.Exists(task.CaminhoEntrada))
                    {
                        resultados.Add(new GeoprocessingResult(false, 0, null, $"Arquivo de entrada '{task.CaminhoEntrada}' não existe."));
                        continue;
                    }

                    var taskSw = Stopwatch.StartNew();
                    try
                    {
                        // 1. Carrega camada de entrada
                        var (feicoesEntrada, srs) = GeoprocessingEngine.CarregarFeicoesDeShapefile(task.CaminhoEntrada);

                        // 2. Executa algoritmo
                        var progressoIndividual = new Progress<GeoprocessingProgress>(p =>
                        {
                            progresso?.Report(new BatchProgressNotification(
                                i + 1,
                                total,
                                task.Nome,
                                (i + p.Percentual) / total,
                                p.Percentual,
                                p.Mensagem));
                        });

                        var feicoesSaida = await algoritmo.ExecutarAsync(
                            feicoesEntrada,
                            null,
                            task.Parametros,
                            progressoIndividual,
                            ct).ConfigureAwait(false);

                        // 3. Persistência atômica
                        string dir = Path.GetDirectoryName(Path.GetFullPath(task.CaminhoSaida)) ?? AppDomain.CurrentDomain.BaseDirectory;
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                        GeoprocessingEngine.SalvarShapefile(task.CaminhoSaida, feicoesSaida, srs);

                        taskSw.Stop();
                        resultados.Add(new GeoprocessingResult(true, feicoesSaida.Count, task.CaminhoSaida, null, taskSw.Elapsed));

                        progresso?.Report(new BatchProgressNotification(
                            i + 1,
                            total,
                            task.Nome,
                            (i + 1) / (double)total,
                            1.0,
                            $"Tarefa {i + 1}/{total} concluída ({feicoesSaida.Count} feições)."));
                    }
                    catch (Exception ex)
                    {
                        taskSw.Stop();
                        resultados.Add(new GeoprocessingResult(false, 0, null, $"Falha na tarefa '{task.Nome}': {ex.Message}", taskSw.Elapsed));

                        progresso?.Report(new BatchProgressNotification(
                            i + 1,
                            total,
                            task.Nome,
                            (i + 1) / (double)total,
                            1.0,
                            $"Erro na tarefa {i + 1}: {ex.Message}",
                            false));
                    }
                }
            }
            finally
            {
                _semaphore.Release();
            }

            return resultados;
        }

        /// <summary>
        /// Utilitário para processamento em lote de todos os arquivos Shapefile (.shp) de uma pasta.
        /// </summary>
        public int EnfileirarPasta(
            string diretorioEntrada,
            string diretorioSaida,
            string algoritmoId,
            Dictionary<string, object?> parametros,
            string sufixoArquivo = "_proc")
        {
            if (!Directory.Exists(diretorioEntrada)) return 0;
            if (!Directory.Exists(diretorioSaida)) Directory.CreateDirectory(diretorioSaida);

            var shps = Directory.GetFiles(diretorioEntrada, "*.shp", SearchOption.TopDirectoryOnly);
            int enfileirados = 0;

            foreach (var shp in shps)
            {
                string nomeBase = Path.GetFileNameWithoutExtension(shp);
                string saidaShp = Path.Combine(diretorioSaida, $"{nomeBase}{sufixoArquivo}.shp");
                EnfileirarTarefa(new BatchTaskItem(
                    Guid.NewGuid().ToString("N")[..8],
                    $"Lote: {nomeBase}",
                    shp,
                    saidaShp,
                    algoritmoId,
                    parametros
                ));
                enfileirados++;
            }

            return enfileirados;
        }
    }
}
