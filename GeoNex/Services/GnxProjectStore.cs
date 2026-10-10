using GeoNex.Data;
using GeoNex.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
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
    public const string RecoveryPointSuffix = ".recovery.gnx";
    public const string AutosaveSuffix = ".autosave.gnx";

    public static string ObterCaminhoPontoRestauracao(string caminhoProjeto)
        => Path.GetFullPath(caminhoProjeto) + RecoveryPointSuffix;

    public static string ObterCaminhoAutosave(string caminhoProjeto)
        => Path.GetFullPath(caminhoProjeto) + AutosaveSuffix;

    public static string ObterCaminhoProjetoDoAutosave(string caminhoAutosave)
    {
        string caminho = Path.GetFullPath(caminhoAutosave);
        if (!EhAutosave(caminho))
            throw new ArgumentException("O arquivo informado não é um autosave GeoNex.", nameof(caminhoAutosave));
        return caminho[..^AutosaveSuffix.Length];
    }

    public static bool EhPontoRestauracao(string caminhoProjeto)
        => Path.GetFileName(caminhoProjeto).EndsWith(RecoveryPointSuffix, StringComparison.OrdinalIgnoreCase);

    public static bool EhAutosave(string caminhoProjeto)
        => Path.GetFileName(caminhoProjeto).EndsWith(AutosaveSuffix, StringComparison.OrdinalIgnoreCase);

    public static bool TemAutosaveMaisRecente(string caminhoProjeto)
    {
        try
        {
            string caminho = Path.GetFullPath(caminhoProjeto);
            string autosave = ObterCaminhoAutosave(caminho);
            if (!File.Exists(caminho) || !File.Exists(autosave)) return false;
            Projeto? salvo = LerProjetoSemMigrar(caminho);
            Projeto? snapshot = LerProjetoSemMigrar(autosave);
            return salvo is not null && snapshot is not null && salvo.Id == snapshot.Id &&
                !EstadosPersistidosEquivalentes(salvo, snapshot);
        }
        catch { return false; }
    }

    public static async Task<Projeto> CriarAsync(
        string caminhoCompleto,
        string nomeProjeto,
        Guid? idProjeto = null,
        DateTime? criadoEm = null)
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
                Id = idProjeto ?? Guid.NewGuid(),
                Nome = nomeProjeto.Trim(),
                CaminhoArquivo = caminhoCompleto,
                CriadoEm = criadoEm ?? DateTime.Now
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

    /// <summary>Creates an independent .gnx copy and rebases portable source paths.</summary>
    public static async Task<Projeto> SalvarComoAsync(
        string caminhoOrigem,
        string caminhoDestino,
        string? nomeProjeto = null)
    {
        Projeto origem = await AbrirAsync(caminhoOrigem);
        Projeto copia = await CriarAsync(caminhoDestino, nomeProjeto ?? origem.Nome);
        try
        {
            CamadaProjetoSnapshot[] camadas = origem.Camadas
                .Select(camada => new CamadaProjetoSnapshot(
                    camada.Nome,
                    camada.Tipo,
                    camada.Visivel,
                    camada.Ordem,
                    camada.CaminhoFonteOriginal ?? string.Empty,
                    camada.FonteJson,
                    camada.EstiloJson))
                .ToArray();

            await SalvarAsync(
                copia,
                camadas,
                origem.CRS,
                origem.CamadaBase,
                origem.OffsetMundoX,
                origem.OffsetMundoY,
                origem.OffsetMundoDefinido,
                origem.CameraPanX,
                origem.CameraPanY,
                origem.CameraZoom,
                origem.LayoutJson);
            return await AbrirAsync(copia.CaminhoArquivo);
        }
        catch
        {
            TryDelete(copia.CaminhoArquivo);
            TryDelete(copia.CaminhoArquivo + "-wal");
            TryDelete(copia.CaminhoArquivo + "-shm");
            throw;
        }
    }

    /// <summary>
    /// The map engine indexes resources by the stable project layer name. Source file
    /// names are independent of display names, so two datasets with the same basename
    /// can be restored safely when their project layer names differ.
    /// </summary>
    public static void ValidarNomesCamadasRuntime(IEnumerable<Camada> camadas)
    {
        ArgumentNullException.ThrowIfNull(camadas);
        var nomes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Camada camada in camadas)
        {
            string nomeRuntime = camada.Nome;
            if (string.IsNullOrWhiteSpace(nomeRuntime) || !nomes.Add(nomeRuntime))
                throw new InvalidDataException(
                    $"O projeto contém camadas que usam o mesmo nome no mapa ('{nomeRuntime}'). Renomeie as camadas ou remova a duplicata antes de abrir.");
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
        string? layoutJson,
        string? nomeProjeto = null,
        bool criarPontoRestauracao = false,
        string? caminhoDestino = null,
        bool substituirLayout = false)
    {
        ArgumentNullException.ThrowIfNull(projetoAtual);
        ArgumentNullException.ThrowIfNull(camadas);
        if (nomeProjeto is not null && string.IsNullOrWhiteSpace(nomeProjeto))
            throw new ArgumentException("Informe um nome para o projeto.", nameof(nomeProjeto));
        if (nomeProjeto?.Trim().Length > 120)
            throw new ArgumentException("O nome do projeto deve ter até 120 caracteres.", nameof(nomeProjeto));
        string caminho = Path.GetFullPath(caminhoDestino ?? projetoAtual.CaminhoArquivo);
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

        if (criarPontoRestauracao)
            await CriarPontoRestauracaoAsync(caminho);

        await using var transaction = await banco.Database.BeginTransactionAsync();
        Projeto? projeto = await banco.Projetos
            .Include(item => item.Camadas)
            .FirstOrDefaultAsync(item => item.Id == projetoAtual.Id);
        if (projeto is null)
            throw new InvalidDataException("O projeto ativo não foi encontrado dentro do arquivo .gnx.");

        projeto.CaminhoArquivo = caminho;
        if (nomeProjeto is not null) projeto.Nome = nomeProjeto.Trim();
        projeto.CRS = string.IsNullOrWhiteSpace(crsProjeto) ? "EPSG:4326" : crsProjeto;
        projeto.CamadaBase = camadaBase;
        projeto.OffsetMundoX = double.IsFinite(offsetMundoX) ? offsetMundoX : 0;
        projeto.OffsetMundoY = double.IsFinite(offsetMundoY) ? offsetMundoY : 0;
        projeto.OffsetMundoDefinido = offsetMundoDefinido;
        projeto.CameraPanX = double.IsFinite(cameraPanX) ? cameraPanX : 0;
        projeto.CameraPanY = double.IsFinite(cameraPanY) ? cameraPanY : 0;
        projeto.CameraZoom = double.IsFinite(cameraZoom) && cameraZoom > 0 ? cameraZoom : 1;
        if (substituirLayout || layoutJson is not null)
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

    /// <summary>
    /// Stores a recoverable session beside its source project without changing the manual-save file.
    /// A new snapshot is written under a temporary name and published only after validation.
    /// </summary>
    public static async Task<bool> SalvarAutosaveAsync(
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
        if (EhAutosave(projetoAtual.CaminhoArquivo) || EhPontoRestauracao(projetoAtual.CaminhoArquivo))
            throw new InvalidOperationException("Autosave só pode ser criado a partir do projeto manual original.");

        string caminhoOrigem = Path.GetFullPath(projetoAtual.CaminhoArquivo);
        if (!File.Exists(caminhoOrigem))
            throw new FileNotFoundException("O projeto manual não está disponível para criar o autosave.", caminhoOrigem);

        CamadaProjetoSnapshot[] desejadas = camadas.ToArray();
        Projeto salvo = await AbrirAsync(caminhoOrigem);
        if (salvo.Id != projetoAtual.Id)
            throw new InvalidDataException("A identidade do projeto mudou; o autosave foi cancelado para evitar misturar projetos.");
        if (EstadoEquivalente(salvo, projetoAtual.Nome, desejadas, crsProjeto, camadaBase, offsetMundoX, offsetMundoY,
                offsetMundoDefinido, cameraPanX, cameraPanY, cameraZoom, layoutJson))
        {
            DescartarAutosave(caminhoOrigem);
            return false;
        }

        string caminhoAutosave = ObterCaminhoAutosave(caminhoOrigem);
        if (File.Exists(caminhoAutosave))
        {
            Projeto snapshot = await AbrirAsync(caminhoAutosave);
            if (snapshot.Id != projetoAtual.Id)
                throw new InvalidDataException("O arquivo de autosave pertence a outro projeto e não será substituído.");

            await SalvarAsync(snapshot, desejadas, crsProjeto, camadaBase, offsetMundoX, offsetMundoY,
                offsetMundoDefinido, cameraPanX, cameraPanY, cameraZoom, layoutJson,
                projetoAtual.Nome, substituirLayout: true);
            return true;
        }

        string pasta = Path.GetDirectoryName(caminhoAutosave)
            ?? throw new IOException("Não foi possível localizar a pasta do projeto para criar o autosave.");
        string temporario = Path.Combine(pasta, $".{Path.GetFileName(caminhoAutosave)}.{Guid.NewGuid():N}.tmp.gnx");
        try
        {
            Projeto snapshot = await CriarAsync(temporario, projetoAtual.Nome, projetoAtual.Id, projetoAtual.CriadoEm);
            await SalvarAsync(snapshot, desejadas, crsProjeto, camadaBase, offsetMundoX, offsetMundoY,
                offsetMundoDefinido, cameraPanX, cameraPanY, cameraZoom, layoutJson,
                projetoAtual.Nome, substituirLayout: true);
            await ValidarIntegridadeSqliteAsync(temporario);
            LimparPoolSqlite(temporario);
            File.Move(temporario, caminhoAutosave, overwrite: false);
            return true;
        }
        catch
        {
            LimparPoolSqlite(temporario);
            TryDelete(temporario);
            TryDelete(temporario + "-wal");
            TryDelete(temporario + "-shm");
            throw;
        }
    }

    /// <summary>Promotes a newer automatic session to the original project after explicit user choice.</summary>
    public static async Task<Projeto> PromoverAutosaveAsync(string caminhoProjeto)
    {
        caminhoProjeto = Path.GetFullPath(caminhoProjeto);
        string caminhoAutosave = ObterCaminhoAutosave(caminhoProjeto);
        if (!TemAutosaveMaisRecente(caminhoProjeto))
            throw new InvalidDataException("Não há uma sessão automática mais recente para recuperar.");

        Projeto projetoSalvo = await AbrirAsync(caminhoProjeto);
        Projeto projetoAutosave = await AbrirAsync(caminhoAutosave);
        if (projetoSalvo.Id != projetoAutosave.Id)
            throw new InvalidDataException("A sessão automática não pertence ao projeto selecionado.");

        CamadaProjetoSnapshot[] camadas = projetoAutosave.Camadas
            .Select(camada => new CamadaProjetoSnapshot(
                camada.Nome,
                camada.Tipo,
                camada.Visivel,
                camada.Ordem,
                camada.CaminhoFonteOriginal ?? string.Empty,
                camada.FonteJson,
                camada.EstiloJson))
            .ToArray();

        await SalvarAsync(
            projetoAutosave,
            camadas,
            projetoAutosave.CRS,
            projetoAutosave.CamadaBase,
            projetoAutosave.OffsetMundoX,
            projetoAutosave.OffsetMundoY,
            projetoAutosave.OffsetMundoDefinido,
            projetoAutosave.CameraPanX,
            projetoAutosave.CameraPanY,
            projetoAutosave.CameraZoom,
            projetoAutosave.LayoutJson,
            projetoAutosave.Nome,
            criarPontoRestauracao: true,
            caminhoDestino: caminhoProjeto,
            substituirLayout: true);

        return await AbrirAsync(caminhoProjeto);
    }

    public static void DescartarAutosave(string caminhoProjeto)
    {
        if (EhAutosave(caminhoProjeto) || EhPontoRestauracao(caminhoProjeto)) return;
        string caminho = ObterCaminhoAutosave(caminhoProjeto);
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = caminho,
                ForeignKeys = true
            }.ToString();
            using var poolConnection = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(poolConnection);
            TryDelete(caminho);
            TryDelete(caminho + "-wal");
            TryDelete(caminho + "-shm");
        }
        catch { }
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
        if (string.IsNullOrWhiteSpace(caminhoFonte) || FonteEhRemota(caminhoFonte) || Path.IsPathRooted(caminhoFonte))
            return caminhoFonte ?? string.Empty;
        string pastaProjeto = Path.GetDirectoryName(Path.GetFullPath(caminhoProjeto)) ?? Environment.CurrentDirectory;
        return Path.GetFullPath(Path.Combine(pastaProjeto, caminhoFonte));
    }

    public static string PersistirCaminhoFonte(string caminhoProjeto, string caminhoFonte)
    {
        if (string.IsNullOrWhiteSpace(caminhoFonte) || FonteEhRemota(caminhoFonte) || !Path.IsPathRooted(caminhoFonte))
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

    public static bool FonteEhRemota(string caminho) =>
        caminho.Contains("://", StringComparison.Ordinal) ||
        caminho.StartsWith("/vsimem/", StringComparison.OrdinalIgnoreCase) ||
        caminho.StartsWith("/vsicurl/", StringComparison.OrdinalIgnoreCase);

    private static bool EstadoEquivalente(
        Projeto salvo,
        string nomeProjeto,
        IReadOnlyCollection<CamadaProjetoSnapshot> camadas,
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
        if (!string.Equals(salvo.Nome, nomeProjeto, StringComparison.Ordinal) ||
            !string.Equals(salvo.CRS, string.IsNullOrWhiteSpace(crsProjeto) ? "EPSG:4326" : crsProjeto, StringComparison.Ordinal) ||
            !string.Equals(salvo.CamadaBase, camadaBase, StringComparison.Ordinal) ||
            salvo.OffsetMundoX != (double.IsFinite(offsetMundoX) ? offsetMundoX : 0) ||
            salvo.OffsetMundoY != (double.IsFinite(offsetMundoY) ? offsetMundoY : 0) ||
            salvo.OffsetMundoDefinido != offsetMundoDefinido ||
            salvo.CameraPanX != (double.IsFinite(cameraPanX) ? cameraPanX : 0) ||
            salvo.CameraPanY != (double.IsFinite(cameraPanY) ? cameraPanY : 0) ||
            salvo.CameraZoom != (double.IsFinite(cameraZoom) && cameraZoom > 0 ? cameraZoom : 1) ||
            !string.Equals(salvo.LayoutJson, layoutJson, StringComparison.Ordinal) ||
            salvo.Camadas.Count != camadas.Count)
            return false;

        var salvasPorNome = salvo.Camadas.ToDictionary(camada => camada.Nome, StringComparer.OrdinalIgnoreCase);
        foreach (CamadaProjetoSnapshot desejada in camadas)
        {
            if (!salvasPorNome.TryGetValue(desejada.Nome, out Camada? existente) ||
                !string.Equals(existente.Tipo, desejada.Tipo, StringComparison.Ordinal) ||
                existente.Visivel != desejada.Visivel ||
                existente.Ordem != desejada.Ordem ||
                !string.Equals(existente.FonteJson, desejada.FonteJson, StringComparison.Ordinal) ||
                !string.Equals(existente.EstiloJson, desejada.EstiloJson, StringComparison.Ordinal))
                return false;

            string caminhoDesejadoPersistido = PersistirCaminhoFonte(salvo.CaminhoArquivo, desejada.CaminhoFonte);
            string caminhoDesejado = ResolverCaminhoFonte(salvo.CaminhoArquivo, caminhoDesejadoPersistido);
            string caminhoSalvo = existente.CaminhoFonteOriginal ?? string.Empty;
            StringComparison comparacaoCaminho = FonteEhRemota(caminhoDesejado)
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            if (!string.Equals(caminhoSalvo, caminhoDesejado, comparacaoCaminho))
                return false;
        }

        return true;
    }

    private static Projeto? LerProjetoSemMigrar(string caminho)
    {
        using var banco = new GeoNexContext(caminho);
        if (!banco.Database.CanConnect()) return null;
        Projeto? projeto = banco.Projetos
            .AsNoTracking()
            .Include(item => item.Camadas)
            .OrderBy(item => item.CriadoEm)
            .FirstOrDefault();
        if (projeto is not null) projeto.CaminhoArquivo = Path.GetFullPath(caminho);
        return projeto;
    }

    private static bool EstadosPersistidosEquivalentes(Projeto salvo, Projeto snapshot)
    {
        if (!string.Equals(salvo.Nome, snapshot.Nome, StringComparison.Ordinal) ||
            !string.Equals(salvo.CRS, snapshot.CRS, StringComparison.Ordinal) ||
            !string.Equals(salvo.CamadaBase, snapshot.CamadaBase, StringComparison.Ordinal) ||
            salvo.OffsetMundoX != snapshot.OffsetMundoX ||
            salvo.OffsetMundoY != snapshot.OffsetMundoY ||
            salvo.OffsetMundoDefinido != snapshot.OffsetMundoDefinido ||
            salvo.CameraPanX != snapshot.CameraPanX ||
            salvo.CameraPanY != snapshot.CameraPanY ||
            salvo.CameraZoom != snapshot.CameraZoom ||
            !string.Equals(salvo.LayoutJson, snapshot.LayoutJson, StringComparison.Ordinal) ||
            salvo.Camadas.Count != snapshot.Camadas.Count)
            return false;

        var camadasSalvas = salvo.Camadas.ToDictionary(camada => camada.Nome, StringComparer.OrdinalIgnoreCase);
        foreach (Camada camadaSnapshot in snapshot.Camadas)
        {
            if (!camadasSalvas.TryGetValue(camadaSnapshot.Nome, out Camada? camadaSalva) ||
                !string.Equals(camadaSalva.Tipo, camadaSnapshot.Tipo, StringComparison.Ordinal) ||
                camadaSalva.Visivel != camadaSnapshot.Visivel ||
                camadaSalva.Ordem != camadaSnapshot.Ordem ||
                !string.Equals(camadaSalva.FonteJson, camadaSnapshot.FonteJson, StringComparison.Ordinal) ||
                !string.Equals(camadaSalva.EstiloJson, camadaSnapshot.EstiloJson, StringComparison.Ordinal))
                return false;

            string caminhoSalvo = ResolverCaminhoFonte(salvo.CaminhoArquivo, camadaSalva.CaminhoFonteOriginal);
            string caminhoSnapshot = ResolverCaminhoFonte(snapshot.CaminhoArquivo, camadaSnapshot.CaminhoFonteOriginal);
            StringComparison comparacaoCaminho = FonteEhRemota(caminhoSalvo) || FonteEhRemota(caminhoSnapshot)
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            if (!string.Equals(caminhoSalvo, caminhoSnapshot, comparacaoCaminho))
                return false;
        }

        return true;
    }

    private static Task ValidarIntegridadeSqliteAsync(string caminho)
    {
        return Task.Run(() =>
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = caminho,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check";
            string? resultado = Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(resultado, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SQLite não validou o arquivo '{caminho}': {resultado ?? "sem resultado"}.");
        });
    }

    private static void LimparPoolSqlite(string caminho)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = caminho,
            ForeignKeys = true
        };
        using var connection = new SqliteConnection(builder.ToString());
        SqliteConnection.ClearPool(connection);
    }

    /// <summary>
    /// Creates a consistent SQLite snapshot beside the project before an explicit save.
    /// SQLite's online backup API includes committed WAL content without copying live sidecars.
    /// The temporary file is published only after SQLite verifies it.
    /// </summary>
    private static async Task CriarPontoRestauracaoAsync(string caminhoProjeto)
    {
        string caminhoRecuperacao = ObterCaminhoPontoRestauracao(caminhoProjeto);
        string pasta = Path.GetDirectoryName(caminhoProjeto)
            ?? throw new IOException("Não foi possível localizar a pasta do projeto para criar o ponto de restauração.");
        string caminhoTemporario = Path.Combine(pasta, $".{Path.GetFileName(caminhoProjeto)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await Task.Run(() =>
            {
                var origemBuilder = new SqliteConnectionStringBuilder
                {
                    DataSource = caminhoProjeto,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                };
                var destinoBuilder = new SqliteConnectionStringBuilder
                {
                    DataSource = caminhoTemporario,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                };

                using var origem = new SqliteConnection(origemBuilder.ToString());
                using var destino = new SqliteConnection(destinoBuilder.ToString());
                origem.Open();
                destino.Open();
                origem.BackupDatabase(destino);

                using var verificar = destino.CreateCommand();
                verificar.CommandText = "PRAGMA quick_check";
                string? resultado = Convert.ToString(verificar.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(resultado, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SQLite não validou o ponto de restauração: {resultado ?? "sem resultado"}.");
            });

            File.Move(caminhoTemporario, caminhoRecuperacao, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(caminhoTemporario);
            throw new IOException(
                $"Não foi possível criar o ponto de restauração '{caminhoRecuperacao}'. O salvamento foi cancelado para preservar a versão atual do projeto.",
                ex);
        }
    }

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
