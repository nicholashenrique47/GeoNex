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
    /// Lê o Shapefile e carrega-o para a Memória RAM, alimentando a GPU (Visão) e a Árvore (Cérebro).
    /// </summary>
    public void CarregarShapefileParaMotorMapas(string caminhoShp, string nomeCamada, MapRenderingService mapService)
    {
        try
        {
            var factory = new GeometryFactory();
            // Utiliza o leitor nativo do NetTopologySuite para máxima compatibilidade com a Árvore
            using var reader = new ShapefileDataReader(caminhoShp, factory);

            var feicoesParaArvore = new FeatureCollection();
            var poligonosParaDesenho = new List<float[]>();

            while (reader.Read())
            {
                // 1. Extrair os Dados Cadastrais (Proprietário, Área, Inscrição) do DBF
                var atributos = new AttributesTable();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var nomeCampo = reader.GetName(i);
                    var valorCampo = reader.GetValue(i);
                    atributos.Add(nomeCampo, valorCampo);
                }

                // 2. Criar a Entidade Espacial
                var feature = new NetTopologySuite.Features.Feature(reader.Geometry, atributos);
                feicoesParaArvore.Add(feature);

                // 3. Converter coordenadas para a Placa Gráfica (SkiaSharp)
                if (reader.Geometry != null)
                {
                    var coords = reader.Geometry.Coordinates;
                    var floats = new float[coords.Length * 2];
                    for (int i = 0; i < coords.Length; i++)
                    {
                        floats[i * 2] = (float)coords[i].X;
                        floats[i * 2 + 1] = (float)coords[i].Y; // Invertido posteriormente no PreCompilar
                    }
                    poligonosParaDesenho.Add(floats);
                }
            }

            // --- A INJEÇÃO DUPLA DE ALTA PERFORMANCE ---

            // A) Alimenta o Cérebro Analítico (Cria a grelha invisível de pesquisa em 0.1ms)
            mapService.ConstruirIndiceEspacial(nomeCamada, feicoesParaArvore);

            // B) Alimenta o Motor Gráfico (Funde as linhas para desenhar no mapa)
            mapService.PreCompilarPoligonos(nomeCamada, poligonosParaDesenho);

            // Regista a camada no topo da pilha para saber quem recebe o clique primeiro
            if (!mapService.OrdemCamadas.Contains(nomeCamada))
            {
                mapService.OrdemCamadas.Add(nomeCamada);
            }

            Console.WriteLine($"[GEONEX] Camada {nomeCamada} carregada com sucesso. Feições ativas: {feicoesParaArvore.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GEONEX] Erro crítico ao carregar Shapefile: {ex.Message}");
        }
    }
}