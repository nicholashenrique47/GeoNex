using GeoNex.Data;
using GeoNex.Models;
using Microsoft.Data.Sqlite;
using OSGeo.OGR;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using NetTopologySuite.Geometries;

namespace GeoNex.Services;

public class ProjetoService
{
    // Guarda o projeto que está aberto na tela no momento
    public Projeto? ProjetoAtual { get; private set; }

    private GeoNexContext? _bancoDeDados;

    private static void ReprojetarMetadadosEmLotes(
        List<CompiledFeature> features,
        string sourceWkt, string targetCrs,
        double offsetX,
        double offsetY)
    {
        // Cinco pontos por feição: quatro cantos do envelope + centroide.
        // Um lote pequeno mantém o working set no cache e evita milhões de
        // double[3] de vida curta no GC durante a abertura da camada.
        const int pointsPerFeature = 5;
        int workers = ProjectionBatchRunner.WorkerCount(features.Count, GeoNexHardware.WorkersFor(features.Count));
        var projectionTimer = System.Diagnostics.Stopwatch.StartNew();
        ProjectionBatchRunner.Run(features.Count, workers, sourceWkt, targetCrs,
            (first, count, x, y, z, transform) =>
            {
                int pointCount = count * pointsPerFeature;
                for (int local = 0; local < count; ++local)
                {
                    var feature = features[first + local];
                    var envelope = feature.EnvelopeWorld;
                    int cursor = local * pointsPerFeature;
                    x[cursor] = envelope.MinX; y[cursor] = envelope.MinY;
                    x[cursor + 1] = envelope.MinX; y[cursor + 1] = envelope.MaxY;
                    x[cursor + 2] = envelope.MaxX; y[cursor + 2] = envelope.MinY;
                    x[cursor + 3] = envelope.MaxX; y[cursor + 3] = envelope.MaxY;
                    x[cursor + 4] = feature.CentroidLocal.X + offsetX;
                    y[cursor + 4] = -feature.CentroidLocal.Y + offsetY;
                }

                Array.Clear(z, 0, pointCount);
                transform.TransformPoints(pointCount, x, y, z);

                // Reject failed transformations before publishing any metadata from this batch.
                for (int point = 0; point < pointCount; point++)
                    if (!double.IsFinite(x[point]) || !double.IsFinite(y[point]))
                        throw new InvalidDataException($"Falha na reprojeção da feição {first + point / pointsPerFeature}.");

                for (int local = 0; local < count; ++local)
                {
                    int cursor = local * pointsPerFeature;
                    double minX = Math.Min(Math.Min(x[cursor], x[cursor + 1]), Math.Min(x[cursor + 2], x[cursor + 3]));
                    double maxX = Math.Max(Math.Max(x[cursor], x[cursor + 1]), Math.Max(x[cursor + 2], x[cursor + 3]));
                    double minY = Math.Min(Math.Min(y[cursor], y[cursor + 1]), Math.Min(y[cursor + 2], y[cursor + 3]));
                    double maxY = Math.Max(Math.Max(y[cursor], y[cursor + 1]), Math.Max(y[cursor + 2], y[cursor + 3]));
                    var feature = features[first + local];
                    feature.EnvelopeWorld.Init(minX, maxX, minY, maxY);
                    feature.CentroidLocal = new SkiaSharp.SKPoint(
                        (float)(x[cursor + 4] - offsetX),
                        -(float)(y[cursor + 4] - offsetY));
                }
            });
        Console.WriteLine($"[GEONEX PERF] Metadados reprojetados: features={features.Count}, workers={workers}, elapsed_ms={projectionTimer.Elapsed.TotalMilliseconds:F2}");
    }

    /// <summary>
    /// Cria um novo arquivo .gnx físico no computador e injeta as tabelas básicas.
    /// </summary>
    public async Task<bool> CriarNovoProjetoAsync(string caminhoCompleto, string nomeProjeto)
    {
        try
        {
            // 1. Aponta o motor para o caminho que o usuário escolheu (Ex: C:\Mapas\Guaratuba.gnx)
            _bancoDeDados = new GeoNexContext(caminhoCompleto);

            // 2. A mágica acontece aqui: Cria o arquivo físico e todas as tabelas!
            await _bancoDeDados.Database.EnsureCreatedAsync();

            // 3. Cria o registro inicial do projeto
            var novoProjeto = new Projeto
            {
                Nome = nomeProjeto,
                CaminhoArquivo = caminhoCompleto
            };

            _bancoDeDados.Projetos.Add(novoProjeto);
            await _bancoDeDados.SaveChangesAsync();

            // 4. Define este como o projeto ativo do sistema
            ProjetoAtual = novoProjeto;

            return true;
        }
        catch (Exception ex)
        {
            // Em um software real, aqui gravaríamos um log de erro
            Console.WriteLine($"Erro ao criar projeto: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Retorna o nome do projeto atual para exibir na barra de status inferior
    /// </summary>
    public string ObterNomeProjetoAtivo()
    {
        return ProjetoAtual != null ? ProjetoAtual.Nome : "Nenhum projeto aberto";
    }
    public async Task<bool> ImportarCamadaVetorParaGnxAsync(string caminhoGnx, string caminhoShp)
    {
        try
        {
            using OSGeo.OGR.DataSource dsOrigem = Ogr.Open(caminhoShp, 0);
            using Layer layerOrigem = dsOrigem.GetLayerByIndex(0);
            string nomeTabela = Path.GetFileNameWithoutExtension(caminhoShp);

            using var connection = new SqliteConnection($"Data Source={caminhoGnx}");
            await connection.OpenAsync();

            // 1. Criar a Tabela Dinamicamente baseada nas colunas do DBF
            var cmdCreate = connection.CreateCommand();
            string colunasSql = "ID INTEGER PRIMARY KEY, Geometria TEXT, ";

            using FeatureDefn defn = layerOrigem.GetLayerDefn();
            for (int i = 0; i < defn.GetFieldCount(); i++)
            {
                using FieldDefn field = defn.GetFieldDefn(i);
                colunasSql += $"[{field.GetName()}] TEXT" + (i < defn.GetFieldCount() - 1 ? ", " : "");
            }

            cmdCreate.CommandText = $"CREATE TABLE IF NOT EXISTS [{nomeTabela}] ({colunasSql})";
            await cmdCreate.ExecuteNonQueryAsync();

            // 2. Loop de Inserção (Sincronia entre o motor GIS e o SQL)
            layerOrigem.ResetReading();
            OSGeo.OGR.Feature feat;
            while ((feat = layerOrigem.GetNextFeature()) != null)
            {
                var cmdInsert = connection.CreateCommand();
                // Lógica de INSERT parametrizado aqui...
                // (Para cada feição, extraímos o WKT da geometria e os atributos)
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro na materialização: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// JIT Compiler: Se o ficheiro for GeoJSON/KML, compila instantaneamente para um Shapefile binário em cache
    /// para alimentar o motor Zero-Allocation do GeoNex sem perder performance!
    /// </summary>
    public string CompilarParaShapefileNativo(string caminhoOriginal)
    {
        string extensao = System.IO.Path.GetExtension(caminhoOriginal).ToLower();
        if (extensao == ".shp") return caminhoOriginal;

        string cacheDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GeoNexCache");
        if (!System.IO.Directory.Exists(cacheDir)) System.IO.Directory.CreateDirectory(cacheDir);

        string nomeLimpo = System.IO.Path.GetFileNameWithoutExtension(caminhoOriginal);
        string caminhoShpDestino = System.IO.Path.Combine(cacheDir, $"{nomeLimpo}_{Guid.NewGuid().ToString().Substring(0,8)}.shp");

        Console.WriteLine($"[GEONEX JIT] A compilar vetor genérico ({extensao}) para Shapefile Binário: {caminhoShpDestino}");

        using var dsOrigem = OSGeo.OGR.Ogr.Open(caminhoOriginal, 0);
        if (dsOrigem == null) throw new Exception("Falha ao abrir o ficheiro vetorial original via OGR.");

        var driverShp = OSGeo.OGR.Ogr.GetDriverByName("ESRI Shapefile");
        if (driverShp == null) throw new Exception("Driver ESRI Shapefile não encontrado no GDAL.");

        using var dsDestino = driverShp.CopyDataSource(dsOrigem, caminhoShpDestino, null);
        if (dsDestino == null) throw new Exception("Falha na compilação do Shapefile JIT.");

        return caminhoShpDestino;
    }
    /// <summary>
    /// Lê o Shapefile e carrega-o para a Memória RAM, alimentando a GPU (Visão) e a Árvore (Cérebro).
    /// </summary>
    public void CarregarShapefileParaMotorMapas(string caminhoShp, string nomeCamada, MapRenderingService mapService)
    {
        try
        {
            var feicoesCompletas = new List<CompiledFeature>();

            // Verifica se a camada tem estilo de categorização para extrairmos APENAS essa coluna
            string? colunaCategoria = null;
            if (mapService.EstilosPorCamada.TryGetValue(nomeCamada, out var estilo) && estilo.TipoSimbologia == "CATEGORIZADA")
            {
                colunaCategoria = estilo.ColunaSimbologia;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 1. OTIMIZAÇÃO: Pega o Extent Global do cabeçalho binário em menos de 1ms
            var ext = FastShapeReader.GetExtent(caminhoShp);
            
            // Leitura de Sistema de Referência de Coordenadas (CRS / SRC) via ficheiro .prj
            string caminhoPrj = System.IO.Path.ChangeExtension(caminhoShp, ".prj");
            string srsOrigem = "";
            if (System.IO.File.Exists(caminhoPrj))
            {
                srsOrigem = System.IO.File.ReadAllText(caminhoPrj);
            }

            if (!mapService.OffsetMundoDefinido)
            {
                mapService.DefinirOffset(ext.MinX, ext.MaxX, ext.MinY, ext.MaxY);
                // O primeiro layer define o Mestre de Projeção (Project CRS)
                if (!string.IsNullOrEmpty(srsOrigem))
                {
                    mapService.ProjetoSRS = srsOrigem;
                }
            }
            
            var swReader = System.Diagnostics.Stopwatch.StartNew();
            // 2. PIPELINE BINÁRIO NATIVO (Zero-Allocation & Multi-Core)
            var (features, shpData) = FastShapeReader.ReadAllFeatures(caminhoShp, nomeCamada, mapService.OffsetMundoX, mapService.OffsetMundoY, colunaCategoria);
            
            // Armazena a string SRS da camada (mesmo que não seja a do projeto, para futura reprojeção)
            shpData.LayerSRS = srsOrigem;
            
            // Se a projeção da camada for diferente do projeto mestre, criamos o transformador On-The-Fly!
            if (!string.IsNullOrEmpty(srsOrigem) && !string.IsNullOrEmpty(mapService.ProjetoSRS) && !SrsFactory.IsSame(srsOrigem, mapService.ProjetoSRS))
            {
                // Como o Skia renderiza em várias threads no Pan/Zoom, usamos ThreadLocal para evitar locks de contenção (Thread-Safe GDAL)
                shpData.TransformLocal = SrsFactory.CreateThreadLocalTransform(srsOrigem, mapService.ProjetoSRS);
                
                // Reprojetar também os Bounding Boxes (EnvelopeWorld) e Centroids para garantir que a R-Tree funcione perfeitamente
                try
                {
                    ReprojetarMetadadosEmLotes(
                        features, srsOrigem, mapService.ProjetoSRS, mapService.OffsetMundoX, mapService.OffsetMundoY);
                }
                catch { shpData.Dispose(); throw; }
            }
            
            feicoesCompletas = features;
            mapService.PublishShapefile(nomeCamada, shpData);
            swReader.Stop();
            Console.WriteLine($"[GEONEX PERF] FastShapeReader levou {swReader.ElapsedMilliseconds} ms");

            // PreCompilarPoligonos já constrói e publica o índice. A versão anterior
            // executava ConstruirIndiceEspacial também em paralelo, criando duas
            // STRtrees gigantes para a mesma camada e disputando CPU/memória.
            var swIndice = System.Diagnostics.Stopwatch.StartNew();
            mapService.PreCompilarPoligonos(nomeCamada, feicoesCompletas);
            swIndice.Stop();
            Console.WriteLine($"[GEONEX PERF] Publicar camada + índice levou {swIndice.ElapsedMilliseconds} ms");

            if (!mapService.OrdemCamadas.Contains(nomeCamada))
            {
                mapService.OrdemCamadas.Add(nomeCamada);
            }
            
            sw.Stop();
            Console.WriteLine($"[GEONEX PERF] TOTAL CarregarShapefileParaMotorMapas levou {sw.ElapsedMilliseconds} ms. Feições: {feicoesCompletas.Count}");
            
            try
            {
                string logStr = $"FastShapeReader levou {swReader.ElapsedMilliseconds} ms\n" +
                                $"Publicar camada + índice levou {swIndice.ElapsedMilliseconds} ms\n" +
                                $"TOTAL: {sw.ElapsedMilliseconds} ms\n";
                System.IO.File.WriteAllText("log_perf.txt", logStr);
            }
            catch {}
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Erro ao carregar Shapefile {caminhoShp}: {ex}");
            // The caller must not announce success or fit an unpublished layer after import failure.
            throw;
        }
    }
}
