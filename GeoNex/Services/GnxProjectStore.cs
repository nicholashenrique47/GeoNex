using GeoNex.Data;
using GeoNex.Models;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;

namespace GeoNex.Services;

public sealed record CamadaProjetoSnapshot(
    string Nome,
    string Tipo,
    bool Visivel,
    int Ordem,
    string CaminhoFonte,
    string? FonteJson,
    string? EstiloJson);

/// <summary>Persistence and versioned SQLite schema for the single-file .gnx project document.</summary>
public static class GnxProjectStore
{
    public const int CurrentSchemaVersion = 2;

    public static async Task<Projeto> CriarAsync(string caminhoCompleto, string nomeProjeto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nomeProjeto);
        ArgumentException.ThrowIfNullOrWhiteSpace(caminhoCompleto);

        caminhoCompleto = Path.GetFullPath(caminhoCompleto);
        ValidarExtensaoProjeto(caminhoCompleto);
        string pasta = Path.GetDirectoryName(caminhoCompleto)
            ?? throw new ArgumentException("O caminho da pasta do projeto é inválido.", nameof(caminhoCompleto));
        Directory.CreateDirectory(pasta);

        bool arquivoCriado = false;
        try
        {
            using (new FileStream(caminhoCompleto, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) { }
            arquivoCriado = true;

            await using var banco = new GeoNexContext(caminhoCompleto);
            await banco.Database.EnsureCreatedAsync();
            await MigrarEsquemaAsync(banco);

            var projeto = new Projeto
            {
                Nome = nomeProjeto.Trim(),
                CaminhoArquivo = caminhoCompleto
            };
            banco.Projetos.Add(projeto);
            await banco.SaveChangesAsync();
            return projeto;
        }
        catch
        {
            if (arquivoCriado)
            {
                TryDelete(caminhoCompleto);
                TryDelete(caminhoCompleto + "-wal");
                TryDelete(caminhoCompleto + "-shm");
            }
            throw;
        }
    }

    public static async Task<Projeto> AbrirAsync(string caminhoCompleto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caminhoCompleto);
        caminhoCompleto = Path.GetFullPath(caminhoCompleto);
        ValidarExtensaoProjeto(caminhoCompleto);
        if (!File.Exists(caminhoCompleto))
            throw new FileNotFoundException("O arquivo do projeto não foi encontrado.", caminhoCompleto);

        await using var banco = new GeoNexContext(caminhoCompleto);
        if (!await banco.Database.CanConnectAsync())
            throw new InvalidDataException("O arquivo selecionado não é um banco SQLite válido.");

        await ValidarTabelasProjetoAsync(banco);
        await ValidarColunasObrigatoriasAsync(banco);
        await MigrarEsquemaAsync(banco);
        Projeto? projeto = await banco.Projetos
            .AsNoTracking()
            .Include(item => item.Camadas)
            .OrderBy(item => item.CriadoEm)
            .FirstOrDefaultAsync();
        if (projeto is null)
            throw new InvalidDataException("O arquivo SQLite não contém um projeto GeoNex.");

        var nomesVazios = projeto.Camadas.Where(camada => string.IsNullOrWhiteSpace(camada.Nome)).ToArray();
        if (nomesVazios.Length > 0)
            throw new InvalidDataException("O projeto contém camadas sem nome e não pode ser aberto com segurança.");
        var nomesDuplicados = projeto.Camadas
            .GroupBy(camada => camada.Nome, StringComparer.OrdinalIgnoreCase)
            .Where(grupo => grupo.Count() > 1)
            .Select(grupo => grupo.Key)
            .ToArray();
        if (nomesDuplicados.Length > 0)
            throw new InvalidDataException($"O projeto contém nomes de camadas repetidos: {string.Join(", ", nomesDuplicados)}.");

        projeto.CaminhoArquivo = caminhoCompleto;
        foreach (Camada camada in projeto.Camadas)
            camada.CaminhoFonteOriginal = ResolverCaminhoFonte(caminhoCompleto, camada.CaminhoFonteOriginal);
        ValidarNomesCamadasRuntime(projeto.Camadas);
        return projeto;
    }

    /// <summary>
    /// The map engine indexes resources by the visible runtime layer name. Local layers
    /// are currently named from their source filename, so distinct project records
    /// that resolve to the same filename cannot both be restored safely.
    /// </summary>
    public static void ValidarNomesCamadasRuntime(IEnumerable<Camada> camadas)
    {
        ArgumentNullException.ThrowIfNull(camadas);
        var nomes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Camada camada in camadas)
        {
            string nomeRuntime = camada.Nome;
            ProjetoFonteCamada? fonte = null;
            if (!string.IsNullOrWhiteSpace(camada.FonteJson))
            {
                try
                {
                    fonte = System.Text.Json.JsonSerializer.Deserialize<ProjetoFonteCamada>(
                        camada.FonteJson,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (System.Text.Json.JsonException) { }
            }

            bool arquivoLocal = fonte is null || string.Equals(fonte.Tipo, "Arquivo", StringComparison.OrdinalIgnoreCase);
            if (arquivoLocal && !string.IsNullOrWhiteSpace(camada.CaminhoFonteOriginal))
                nomeRuntime = Path.GetFileName(camada.CaminhoFonteOriginal);

            if (!nomes.Add(nomeRuntime))
                throw new InvalidDataException(
                    $"O projeto contém camadas que usam o mesmo nome no mapa ('{nomeRuntime}'). Renomeie as fontes ou remova a duplicata antes de abrir.");
        }
    }

    public static async Task SalvarAsync(
        Projeto projetoAtual,
        IEnumerable<CamadaProjetoSnapshot> camadas,
        string crsProjeto,
        string? camadaBase,
        double offsetMundoX,
        double offsetMundoY,
        bool offsetMundoDefinido,
        double cameraPanX,
        double cameraPanY,
        double cameraZoom,
        string? layoutJson)
    {
        ArgumentNullException.ThrowIfNull(projetoAtual);
        ArgumentNullException.ThrowIfNull(camadas);
        string caminho = Path.GetFullPath(projetoAtual.CaminhoArquivo);
        if (!File.Exists(caminho))
            throw new FileNotFoundException("O arquivo do projeto ativo não existe mais.", caminho);

        CamadaProjetoSnapshot[] desejadas = camadas.ToArray();
        if (desejadas.Any(camada => camada is null || string.IsNullOrWhiteSpace(camada.Nome)))
            throw new InvalidDataException("Não é possível salvar o projeto com camadas sem nome.");
        if (desejadas.Any(camada => string.IsNullOrWhiteSpace(camada.Tipo)))
            throw new InvalidDataException("Não é possível salvar o projeto com camadas sem tipo.");

        string[] nomesDuplicados = desejadas
            .GroupBy(camada => camada.Nome.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(grupo => grupo.Count() > 1)
            .Select(grupo => grupo.Key)
            .ToArray();
        if (nomesDuplicados.Length > 0)
            throw new InvalidDataException($"Não é possível salvar camadas com nomes repetidos: {string.Join(", ", nomesDuplicados)}.");
        await using var banco = new GeoNexContext(caminho);
        if (!await banco.Database.CanConnectAsync())
            throw new InvalidDataException("O arquivo do projeto ativo não pode ser aberto como SQLite.");
        await ValidarTabelasProjetoAsync(banco);
        await ValidarColunasObrigatoriasAsync(banco);
        await MigrarEsquemaAsync(banco);

        await using var transaction = await banco.Database.BeginTransactionAsync();
        Projeto? projeto = await banco.Projetos
            .Include(item => item.Camadas)
            .FirstOrDefaultAsync(item => item.Id == projetoAtual.Id);
        if (projeto is null)
            throw new InvalidDataException("O projeto ativo não foi encontrado dentro do arquivo .gnx.");

        projeto.CaminhoArquivo = caminho;
        projeto.CRS = string.IsNullOrWhiteSpace(crsProjeto) ? "EPSG:4326" : crsProjeto;
        projeto.CamadaBase = camadaBase;
        projeto.OffsetMundoX = double.IsFinite(offsetMundoX) ? offsetMundoX : 0;
        projeto.OffsetMundoY = double.IsFinite(offsetMundoY) ? offsetMundoY : 0;
        projeto.OffsetMundoDefinido = offsetMundoDefinido;
        projeto.CameraPanX = double.IsFinite(cameraPanX) ? cameraPanX : 0;
        projeto.CameraPanY = double.IsFinite(cameraPanY) ? cameraPanY : 0;
        projeto.CameraZoom = double.IsFinite(cameraZoom) && cameraZoom > 0 ? cameraZoom : 1;
        if (layoutJson is not null)
        {
            ValidarTamanhoLayout(layoutJson);
            projeto.LayoutJson = layoutJson;
        }

        var existentes = projeto.Camadas.ToDictionary(camada => camada.Nome, StringComparer.OrdinalIgnoreCase);
        var nomesDesejados = new HashSet<string>(desejadas.Select(camada => camada.Nome), StringComparer.OrdinalIgnoreCase);
        foreach (Camada removida in projeto.Camadas.Where(camada => !nomesDesejados.Contains(camada.Nome)).ToArray())
            banco.Camadas.Remove(removida);

        foreach (CamadaProjetoSnapshot snapshot in desejadas)
        {
            if (!existentes.TryGetValue(snapshot.Nome, out Camada? camada))
            {
                camada = new Camada { Nome = snapshot.Nome, ProjetoId = projeto.Id };
                banco.Camadas.Add(camada);
            }

            camada.Nome = snapshot.Nome;
            camada.Tipo = snapshot.Tipo;
            camada.Visivel = snapshot.Visivel;
            camada.Ordem = snapshot.Ordem;
            camada.CaminhoFonteOriginal = PersistirCaminhoFonte(caminho, snapshot.CaminhoFonte);
            camada.FonteJson = snapshot.FonteJson;
            camada.EstiloJson = snapshot.EstiloJson;
        }

        await banco.SaveChangesAsync();
        await transaction.CommitAsync();
        CopiarEstado(projeto, projetoAtual);
    }

    public static async Task SalvarLayoutAsync(Projeto projetoAtual, string layoutJson)
    {
        ArgumentNullException.ThrowIfNull(projetoAtual);
        ValidarTamanhoLayout(layoutJson);
        string caminho = Path.GetFullPath(projetoAtual.CaminhoArquivo);
        if (!File.Exists(caminho))
            throw new FileNotFoundException("O arquivo do projeto ativo não existe mais.", caminho);

        await using var banco = new GeoNexContext(caminho);
        if (!await banco.Database.CanConnectAsync())
            throw new InvalidDataException("O arquivo do projeto ativo não pode ser aberto como SQLite.");
        await ValidarTabelasProjetoAsync(banco);
        await ValidarColunasObrigatoriasAsync(banco);
        await MigrarEsquemaAsync(banco);
        await using var transaction = await banco.Database.BeginTransactionAsync();
        Projeto? projeto = await banco.Projetos.FirstOrDefaultAsync(item => item.Id == projetoAtual.Id);
        if (projeto is null)
            throw new InvalidDataException("O projeto ativo não foi encontrado dentro do arquivo .gnx.");
        projeto.LayoutJson = layoutJson;
        await banco.SaveChangesAsync();
        await transaction.CommitAsync();
        projetoAtual.LayoutJson = layoutJson;
    }

    public static string ResolverCaminhoFonte(string caminhoProjeto, string? caminhoFonte)
    {
        if (string.IsNullOrWhiteSpace(caminhoFonte) || PareceFonteRemota(caminhoFonte) || Path.IsPathRooted(caminhoFonte))
            return caminhoFonte ?? string.Empty;
        string pastaProjeto = Path.GetDirectoryName(Path.GetFullPath(caminhoProjeto)) ?? Environment.CurrentDirectory;
        return Path.GetFullPath(Path.Combine(pastaProjeto, caminhoFonte));
    }

    public static string PersistirCaminhoFonte(string caminhoProjeto, string caminhoFonte)
    {
        if (string.IsNullOrWhiteSpace(caminhoFonte) || PareceFonteRemota(caminhoFonte) || !Path.IsPathRooted(caminhoFonte))
            return caminhoFonte;

        string caminhoAbsoluto = Path.GetFullPath(caminhoFonte);
        string pastaProjeto = Path.GetDirectoryName(Path.GetFullPath(caminhoProjeto)) ?? Environment.CurrentDirectory;
        try
        {
            string relativo = Path.GetRelativePath(pastaProjeto, caminhoAbsoluto);
            bool foraDaPasta = Path.IsPathRooted(relativo) || relativo == ".." ||
                relativo.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relativo.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
            return foraDaPasta ? caminhoAbsoluto : relativo;
        }
        catch (ArgumentException)
        {
            return caminhoAbsoluto;
        }
    }

    public static string ChaveSeguraPostgis(Guid projetoId, string nomeCamada)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(nomeCamada));
        return $"GeoNex.PostGIS.{projetoId:N}.{Convert.ToHexString(hash)}";
    }

    private static void ValidarExtensaoProjeto(string caminho)
    {
        if (!string.Equals(Path.GetExtension(caminho), ".gnx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O arquivo de projeto GeoNex precisa usar a extensão .gnx.");
    }

    private static void ValidarTamanhoLayout(string? layoutJson)
    {
        if (layoutJson is not null && System.Text.Encoding.UTF8.GetByteCount(layoutJson) > 20_000_000)
            throw new InvalidDataException("O layout excede o limite de 20 MB.");
    }

    private static async Task ValidarTabelasProjetoAsync(GeoNexContext banco)
    {
        var connection = banco.Database.GetDbConnection();
        await banco.Database.OpenConnectionAsync();
        try
        {
            var tabelas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('Projetos','Camadas')";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tabelas.Add(reader.GetString(0));
            if (!tabelas.Contains("Projetos") || !tabelas.Contains("Camadas"))
                throw new InvalidDataException("O banco não contém as tabelas obrigatórias de um projeto GeoNex.");
        }
        finally
        {
            await banco.Database.CloseConnectionAsync();
        }
    }

    private static async Task ValidarColunasObrigatoriasAsync(GeoNexContext banco)
    {
        var connection = banco.Database.GetDbConnection();
        var obrigatorias = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Projetos"] = ["Id", "Nome", "CaminhoArquivo", "CRS", "CriadoEm"],
            ["Camadas"] = ["Id", "Nome", "Tipo", "Opacidade", "Visivel", "Ordem", "CaminhoFonteOriginal", "ProjetoId"]
        };
        await banco.Database.OpenConnectionAsync();
        try
        {
            foreach (var (tabela, esperadas) in obrigatorias)
            {
                var existentes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA table_info(\"{tabela}\")";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) existentes.Add(reader.GetString(1));
                string[] ausentes = esperadas.Where(coluna => !existentes.Contains(coluna)).ToArray();
                if (ausentes.Length > 0)
                    throw new InvalidDataException($"A tabela {tabela} não possui as colunas GeoNex obrigatórias: {string.Join(", ", ausentes)}.");
            }
        }
        finally
        {
            await banco.Database.CloseConnectionAsync();
        }
    }

    private static async Task MigrarEsquemaAsync(GeoNexContext banco)
    {
        await using var transaction = await banco.Database.BeginTransactionAsync();
        DbConnection connection = banco.Database.GetDbConnection();
        int version;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version";
            version = Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (version > CurrentSchemaVersion)
            throw new InvalidDataException($"Este projeto usa uma versão de esquema mais nova ({version}) que o GeoNex suporta ({CurrentSchemaVersion}).");

        await GarantirColunaAsync(banco, "Projetos", "CamadaBase", "TEXT NULL");
        await GarantirColunaAsync(banco, "Projetos", "OffsetMundoX", "REAL NOT NULL DEFAULT 0");
        await GarantirColunaAsync(banco, "Projetos", "OffsetMundoY", "REAL NOT NULL DEFAULT 0");
        await GarantirColunaAsync(banco, "Projetos", "OffsetMundoDefinido", "INTEGER NOT NULL DEFAULT 0");
        await GarantirColunaAsync(banco, "Projetos", "CameraPanX", "REAL NOT NULL DEFAULT 0");
        await GarantirColunaAsync(banco, "Projetos", "CameraPanY", "REAL NOT NULL DEFAULT 0");
        await GarantirColunaAsync(banco, "Projetos", "CameraZoom", "REAL NOT NULL DEFAULT 1");
        await GarantirColunaAsync(banco, "Projetos", "LayoutJson", "TEXT NULL");
        await GarantirColunaAsync(banco, "Camadas", "FonteJson", "TEXT NULL");
        await GarantirColunaAsync(banco, "Camadas", "EstiloJson", "TEXT NULL");

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion}";
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static async Task GarantirColunaAsync(
        GeoNexContext banco,
        string tabela,
        string coluna,
        string definicao)
    {
        if (!((tabela == "Projetos" &&
               (coluna == "CamadaBase" || coluna == "OffsetMundoX" || coluna == "OffsetMundoY" ||
                coluna == "OffsetMundoDefinido" || coluna == "CameraPanX" || coluna == "CameraPanY" ||
                coluna == "CameraZoom" || coluna == "LayoutJson")) ||
              (tabela == "Camadas" && (coluna == "FonteJson" || coluna == "EstiloJson"))))
            throw new InvalidOperationException("Tentativa de atualizar uma coluna de projeto não reconhecida.");

        var connection = banco.Database.GetDbConnection();
        var colunas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info(\"{tabela}\")";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) colunas.Add(reader.GetString(1));
        }

        if (!colunas.Contains(coluna))
        {
            string sql = "ALTER TABLE \"" + tabela + "\" ADD COLUMN \"" + coluna + "\" " + definicao;
            await banco.Database.ExecuteSqlRawAsync(sql);
        }
    }

    private static bool PareceFonteRemota(string caminho) =>
        caminho.Contains("://", StringComparison.Ordinal) ||
        caminho.StartsWith("/vsimem/", StringComparison.OrdinalIgnoreCase) ||
        caminho.StartsWith("/vsicurl/", StringComparison.OrdinalIgnoreCase);

    private static void CopiarEstado(Projeto origem, Projeto destino)
    {
        destino.Nome = origem.Nome;
        destino.CaminhoArquivo = origem.CaminhoArquivo;
        destino.CRS = origem.CRS;
        destino.CamadaBase = origem.CamadaBase;
        destino.OffsetMundoX = origem.OffsetMundoX;
        destino.OffsetMundoY = origem.OffsetMundoY;
        destino.OffsetMundoDefinido = origem.OffsetMundoDefinido;
        destino.CameraPanX = origem.CameraPanX;
        destino.CameraPanY = origem.CameraPanY;
        destino.CameraZoom = origem.CameraZoom;
        destino.LayoutJson = origem.LayoutJson;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
