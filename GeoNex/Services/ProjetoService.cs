using GeoNex.Data;
using GeoNex.Models;
using Microsoft.Data.Sqlite;
using OSGeo.OGR;

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
            Feature feat;
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
}