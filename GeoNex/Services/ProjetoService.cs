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
            var feicoesCompletas = new NetTopologySuite.Features.FeatureCollection();
            var wkbReader = new NetTopologySuite.IO.WKBReader();

            Ogr.RegisterAll();
            using var dsOrigem = Ogr.Open(caminhoShp, 0);
            if (dsOrigem == null) throw new Exception("Falha ao abrir a fonte via OGR.");

            using var layer = dsOrigem.GetLayerByIndex(0);
            using var defn = layer.GetLayerDefn();
            int fieldCount = defn.GetFieldCount();
            var fieldNames = new string[fieldCount];
            for (int i = 0; i < fieldCount; i++)
            {
                using var fDefn = defn.GetFieldDefn(i);
                fieldNames[i] = fDefn.GetName();
            }

            layer.ResetReading();
            OSGeo.OGR.Feature feat;

            while ((feat = layer.GetNextFeature()) != null)
            {
                var atributos = new NetTopologySuite.Features.AttributesTable();
                for (int i = 0; i < fieldCount; i++)
                {
                    // Ler como string é mais seguro e rápido para o UI
                    atributos.Add(fieldNames[i], feat.GetFieldAsString(i));
                }

                using var geom = feat.GetGeometryRef();
                if (geom != null)
                {
                    int size = (int)geom.WkbSize();
                    byte[] wkb = new byte[size];
                    geom.ExportToWkb(wkb, wkbByteOrder.wkbNDR);
                    
                    var ntsGeom = wkbReader.Read(wkb);
                    var feature = new NetTopologySuite.Features.Feature(ntsGeom, atributos);
                    feicoesCompletas.Add(feature);
                }
                
                feat.Dispose();
            }

            // A INJEÇÃO DUPLA ESTRUTURADA
            // 1. Alimenta o cérebro matemático de colisão (Raycast)
            mapService.ConstruirIndiceEspacial(nomeCamada, feicoesCompletas);

            // 2. Entrega a geometria pura à Placa Gráfica para desenhar com a Âncora de Precisão
            mapService.PreCompilarPoligonos(nomeCamada, feicoesCompletas);

            if (!mapService.OrdemCamadas.Contains(nomeCamada))
            {
                mapService.OrdemCamadas.Add(nomeCamada);
            }

            Console.WriteLine($"[GEONEX] Camada {nomeCamada} carregada via OGR. Feições: {feicoesCompletas.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GEONEX] Erro crítico ao carregar via OGR: {ex.Message}");
        }
    }
}