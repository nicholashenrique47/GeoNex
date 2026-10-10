using GeoNex.Models;
using Microsoft.JSInterop;
using Microsoft.Maui.Storage;
using System.Text.Json;

namespace GeoNex.Components.Pages;

public partial class Home
{
    private const string RecentProjectsPreferenceKey = "GeoNex.RecentProjects.v1";
    private const int MaximumRecentProjects = 8;
    private List<string> _projetosRecentes = new();
    private bool _mostrarPropriedadesProjeto;
    private string _nomeProjetoConfiguracao = string.Empty;
    private string _crsProjetoConfiguracao = string.Empty;
    private string? _camadaBaseConfiguracao;

    private void CarregarProjetosRecentes()
    {
        try
        {
            string json = Preferences.Default.Get(RecentProjectsPreferenceKey, "[]");
            List<string>? paths = JsonSerializer.Deserialize<List<string>>(json);
            _projetosRecentes = (paths ?? new List<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path) &&
                    string.Equals(Path.GetExtension(path), ".gnx", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumRecentProjects)
                .ToList();
            PersistirProjetosRecentes();
        }
        catch
        {
            _projetosRecentes = new List<string>();
        }
    }

    private void RegistrarProjetoRecente(string caminho)
    {
        try
        {
            string caminhoCompleto = Path.GetFullPath(caminho);
            _projetosRecentes = new[] { caminhoCompleto }
                .Concat(_projetosRecentes.Where(item => !string.Equals(item, caminhoCompleto, StringComparison.OrdinalIgnoreCase)))
                .Take(MaximumRecentProjects)
                .ToList();
            PersistirProjetosRecentes();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Não foi possível atualizar a lista de projetos recentes: {ex.Message}");
        }
    }

    private void PersistirProjetosRecentes()
        => Preferences.Default.Set(RecentProjectsPreferenceKey, JsonSerializer.Serialize(_projetosRecentes));

    private async Task AbrirProjetoRecenteAsync(string caminho)
    {
        FecharMenusSuperiores();
        if (!File.Exists(caminho))
        {
            _projetosRecentes.RemoveAll(item => string.Equals(item, caminho, StringComparison.OrdinalIgnoreCase));
            try { PersistirProjetosRecentes(); } catch { }
            await JSRuntime.InvokeVoidAsync("alert", "Esse arquivo recente não está mais disponível. Ele foi removido da lista.");
            return;
        }

        await AbrirProjetoPorCaminhoAsync(caminho);
    }

    private void AbrirPropriedadesProjeto()
    {
        FecharMenusSuperiores();
        Projeto? projeto = ProjetoService.ProjetoAtual;
        if (projeto is null) return;
        _nomeProjetoConfiguracao = projeto.Nome;
        _crsProjetoConfiguracao = MapService.ProjetoSRS;
        _camadaBaseConfiguracao = _camadaBaseProjeto;
        _mostrarPropriedadesProjeto = true;
    }

    private void FecharPropriedadesProjeto() => _mostrarPropriedadesProjeto = false;

    private async Task SalvarPropriedadesProjetoAsync()
    {
        Projeto? projeto = ProjetoService.ProjetoAtual;
        if (projeto is null) { _mostrarPropriedadesProjeto = false; return; }
        if (!await PodeTrocarProjetoAsync()) return;

        string nome = _nomeProjetoConfiguracao.Trim();
        if (string.IsNullOrWhiteSpace(nome) || nome.Length > 120)
        {
            await JSRuntime.InvokeVoidAsync("alert", "O nome exibido precisa ter entre 1 e 120 caracteres.");
            return;
        }

        string? camadaBase = string.IsNullOrWhiteSpace(_camadaBaseConfiguracao)
            ? null
            : _camadaBaseConfiguracao;
        if (camadaBase is not null && !CamadasAtivas.Any(camada =>
                !camada.PendenteReconexao && string.Equals(camada.Nome, camadaBase, StringComparison.OrdinalIgnoreCase)))
        {
            await JSRuntime.InvokeVoidAsync("alert", "Escolha uma camada ativa como camada-base.");
            return;
        }

        string crsNovo = _crsProjetoConfiguracao.Trim();
        if (string.IsNullOrWhiteSpace(crsNovo))
        {
            await JSRuntime.InvokeVoidAsync("alert", "Informe um código EPSG ou uma definição WKT válida para o SRC do projeto.");
            return;
        }

        string? camadaBaseAnterior = _camadaBaseProjeto;
        string crsAnterior = MapService.ProjetoSRS;
        bool crsAlterado = false;
        _camadaBaseProjeto = camadaBase;
        try
        {
            ValidarTransformacaoCrsProjeto(crsAnterior, crsNovo);
            if (!GeoNex.Services.SrsFactory.IsSame(crsAnterior, crsNovo))
            {
                if (CamadasAtivas.Any(camada => !camada.PendenteReconexao))
                    MapService.TrocarSRCProjeto(crsNovo);
                else
                    MapService.ProjetoSRS = crsNovo;
                crsAlterado = true;
                if (!GeoNex.Services.SrsFactory.IsSame(MapService.ProjetoSRS, crsNovo))
                    throw new InvalidOperationException("O SRC não pôde ser aplicado ao mapa.");
                MapService.RequestRedraw();
            }

            List<string> avisos = await PersistirProjetoAtualAsync(nome);
            _nomeProjetoConfiguracao = ProjetoService.ProjetoAtual?.Nome ?? nome;
            _crsProjetoConfiguracao = MapService.ProjetoSRS;
            _camadaBaseProjeto = ProjetoService.ProjetoAtual?.CamadaBase;
            _camadaBaseConfiguracao = _camadaBaseProjeto;
            _mostrarPropriedadesProjeto = false;
            string detalhes = avisos.Count == 0 ? string.Empty : "\n\n" + string.Join("\n", avisos.Distinct());
            await JSRuntime.InvokeVoidAsync("alert", $"Configurações do projeto salvas.{detalhes}");
        }
        catch (Exception ex)
        {
            if (crsAlterado)
            {
                if (CamadasAtivas.Any(camada => !camada.PendenteReconexao))
                    MapService.TrocarSRCProjeto(crsAnterior);
                else
                    MapService.ProjetoSRS = crsAnterior;
                MapService.RequestRedraw();
                _crsProjetoConfiguracao = crsAnterior;
            }
            _camadaBaseProjeto = camadaBaseAnterior;
            await JSRuntime.InvokeVoidAsync("alert", $"Não foi possível salvar as configurações: {ex.Message}");
        }
    }

    private void ValidarTransformacaoCrsProjeto(string crsOrigem, string crsDestino)
    {
        using var origemParaWgs84 = GeoNex.Services.SrsFactory.CreateTransform(crsOrigem, "EPSG:4326");
        using var wgs84ParaDestino = GeoNex.Services.SrsFactory.CreateTransform("EPSG:4326", crsDestino);
        double[] centro = { MapService.OffsetMundoX, MapService.OffsetMundoY, 0 };
        origemParaWgs84.TransformPoint(centro);
        if (!double.IsFinite(centro[0]) || !double.IsFinite(centro[1]))
            throw new InvalidDataException("O centro atual não pode ser convertido para EPSG:4326.");
        wgs84ParaDestino.TransformPoint(centro);
        if (!double.IsFinite(centro[0]) || !double.IsFinite(centro[1]))
            throw new InvalidDataException("O centro atual não pode ser convertido para o SRC informado.");
    }

    private async Task SalvarProjetoComoAsync()
    {
        FecharMenusSuperiores();
        Projeto? projetoOrigem = ProjetoService.ProjetoAtual;
        if (projetoOrigem is null) return;
        if (!await PodeTrocarProjetoAsync()) return;

        try
        {
            var paginaNativa = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (paginaNativa is null)
                throw new InvalidOperationException("A janela do GeoNex não está disponível para salvar uma cópia.");

            string? nomeProjeto = await paginaNativa.DisplayPromptAsync(
                "Salvar projeto como",
                "Informe o nome da nova cópia:",
                "Continuar",
                "Cancelar",
                "Nome do projeto",
                maxLength: 120,
                initialValue: projetoOrigem.Nome);
            if (string.IsNullOrWhiteSpace(nomeProjeto)) return;

            var pasta = await CommunityToolkit.Maui.Storage.FolderPicker.Default.PickAsync(default);
            if (pasta is null || !pasta.IsSuccessful) return;

            string nomeArquivo = CriarNomeArquivoProjeto(nomeProjeto);
            string destino = Path.Combine(pasta.Folder.Path, nomeArquivo + ".gnx");
            if (File.Exists(destino))
            {
                await JSRuntime.InvokeVoidAsync("alert", "Já existe um arquivo .gnx com esse nome nessa pasta.");
                return;
            }

            List<string> avisos = await PersistirProjetoAtualAsync();
            Projeto copia = await ProjetoService.SalvarComoAsync(destino, nomeProjeto.Trim());
            avisos.AddRange(await CopiarSegredosPostgisAsync(projetoOrigem, copia));
            ProjetoService.AtivarProjeto(copia);
            RegistrarProjetoRecente(copia.CaminhoArquivo);
            _mostrarPropriedadesProjeto = false;
            StateHasChanged();

            string detalhes = avisos.Count == 0 ? string.Empty : "\n\nAvisos:\n• " + string.Join("\n• ", avisos.Distinct());
            await JSRuntime.InvokeVoidAsync("alert", $"Cópia do projeto criada:\n{copia.CaminhoArquivo}{detalhes}");
        }
        catch (Exception ex)
        {
            await JSRuntime.InvokeVoidAsync("alert", $"Não foi possível salvar a cópia do projeto: {ex.Message}");
        }
    }

    private async Task<List<string>> CopiarSegredosPostgisAsync(Projeto origem, Projeto copia)
    {
        var avisos = new List<string>();
        foreach (GeoNex.Models.Camada camada in copia.Camadas.Where(item =>
                     string.Equals(TipoFonteProjeto(item), "PostGIS", StringComparison.OrdinalIgnoreCase)))
        {
            string chaveOrigem = GeoNex.Services.GnxProjectStore.ChaveSeguraPostgis(origem.Id, camada.Nome);
            string chaveDestino = GeoNex.Services.GnxProjectStore.ChaveSeguraPostgis(copia.Id, camada.Nome);
            try
            {
                string? senha = await SecureStorage.Default.GetAsync(chaveOrigem);
                if (!string.IsNullOrEmpty(senha))
                    await SecureStorage.Default.SetAsync(chaveDestino, senha);
                else
                    avisos.Add($"A camada '{camada.Nome}' não tinha uma senha recuperável no armazenamento seguro; será necessário reconectar ao PostGIS.");
            }
            catch
            {
                avisos.Add($"As credenciais de '{camada.Nome}' não puderam ser copiadas para a nova identidade do projeto; será necessário reconectar.");
            }
        }
        return avisos;
    }

    private static string CriarNomeArquivoProjeto(string nome)
    {
        char[] invalidos = Path.GetInvalidFileNameChars();
        string seguro = string.Concat(nome.Trim().Select(caractere => invalidos.Contains(caractere) ? '_' : caractere))
            .Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(seguro))
            throw new InvalidOperationException("O nome informado não pode ser usado como nome de arquivo.");
        return seguro;
    }

    private async Task FecharProjetoAtualAsync()
    {
        FecharMenusSuperiores();
        Projeto? ativo = ProjetoService.ProjetoAtual;
        if (ativo is null) return;
        if (!await PodeTrocarProjetoAsync()) return;

        try
        {
            await PersistirProjetoAtualAsync();
        }
        catch (Exception ex)
        {
            await JSRuntime.InvokeVoidAsync("alert", $"O projeto não foi fechado porque não pôde ser salvo.\n{ex.Message}");
            return;
        }

        Projeto snapshot;
        try { snapshot = await ProjetoService.CarregarProjetoAsync(ativo.CaminhoArquivo); }
        catch (Exception ex)
        {
            await JSRuntime.InvokeVoidAsync("alert", $"O projeto foi salvo, mas não foi possível preparar o fechamento seguro.\n{ex.Message}");
            return;
        }

        try
        {
            await LimparCamadasDoProjetoAtualAsync();
            ProjetoService.FecharProjeto();
            StateHasChanged();
        }
        catch (Exception erroLimpeza)
        {
            try
            {
                await LimparCamadasDoProjetoAtualAsync();
                await CarregarCamadasDoProjetoAsync(snapshot, snapshot.Camadas);
                ProjetoService.AtivarProjeto(snapshot);
            }
            catch (Exception erroRestauro)
            {
                try { await LimparCamadasDoProjetoAtualAsync(); } catch { }
                ProjetoService.FecharProjeto();
                await JSRuntime.InvokeVoidAsync("alert", $"Não foi possível fechar o projeto e restaurá-lo.\nFechamento: {erroLimpeza.Message}\nRestauração: {erroRestauro.Message}");
                return;
            }

            await JSRuntime.InvokeVoidAsync("alert", $"O projeto continua aberto; a limpeza do mapa falhou e o estado foi restaurado.\n{erroLimpeza.Message}");
            return;
        }

        await JSRuntime.InvokeVoidAsync("alert", "Projeto salvo e fechado.");
    }

    private async Task SairDoGeoNexAsync()
    {
        FecharMenusSuperiores();
        if (!await PodeTrocarProjetoAsync()) return;
        if (ProjetoService.ProjetoAtual is not null)
        {
            try { await PersistirProjetoAtualAsync(); }
            catch (Exception ex)
            {
                await JSRuntime.InvokeVoidAsync("alert", $"O GeoNex permaneceu aberto porque o projeto não pôde ser salvo.\n{ex.Message}");
                return;
            }
        }
        Microsoft.Maui.Controls.Application.Current?.Quit();
    }

    private void AbrirCompositorPeloMenuArquivo()
    {
        _mostrarPropriedadesProjeto = false;
        FecharMenusSuperiores();
        AbrirCompositorImpressao();
    }
}
