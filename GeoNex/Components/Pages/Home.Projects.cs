using GeoNex.Models;
using Microsoft.JSInterop;
using Microsoft.Maui.Storage;
using System.Text.Json;

namespace GeoNex.Components.Pages;

public partial class Home
{
    private const string RecentProjectsPreferenceKey = "GeoNex.RecentProjects.v1";
    private const string ProjectAutosaveIntervalPreferenceKey = "GeoNex.ProjectAutosaveMinutes.v1";
    private const int DefaultProjectAutosaveMinutes = 5;
    private const int MaximumRecentProjects = 8;
    private List<string> _projetosRecentes = new();
    private bool _mostrarTelaInicialProjeto = true;
    private bool _mostrarDialogoNovoProjeto;
    private string _nomeNovoProjetoInicial = "Meu projeto";
    private string _pastaNovoProjetoInicial = string.Empty;
    private bool _mostrarPropriedadesProjeto;
    private string _nomeProjetoConfiguracao = string.Empty;
    private string _crsProjetoConfiguracao = string.Empty;
    private string? _camadaBaseConfiguracao;
    private int _intervaloAutosaveConfiguracao = DefaultProjectAutosaveMinutes;

    private static int LerIntervaloAutosaveProjeto()
    {
        try
        {
            int minutos = Preferences.Default.Get(ProjectAutosaveIntervalPreferenceKey, DefaultProjectAutosaveMinutes);
            return minutos is 0 or 1 or 5 or 10 or 15 ? minutos : DefaultProjectAutosaveMinutes;
        }
        catch
        {
            return DefaultProjectAutosaveMinutes;
        }
    }

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

    private string ObterUltimaModificacaoProjeto(string caminho)
    {
        try { return File.GetLastWriteTime(caminho).ToString("dd MMM yyyy · HH:mm", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")); }
        catch { return "Data indisponível"; }
    }

    private void RemoverProjetoRecente(string caminho)
    {
        _projetosRecentes.RemoveAll(item => string.Equals(item, caminho, StringComparison.OrdinalIgnoreCase));
        try { PersistirProjetosRecentes(); } catch { }
    }

    private void AbrirDialogoNovoProjeto()
    {
        _nomeNovoProjetoInicial = "Meu projeto";
        string documentos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _pastaNovoProjetoInicial = Path.Combine(documentos, "GeoNex", "Projetos");
        _mostrarDialogoNovoProjeto = true;
    }

    private void FecharDialogoNovoProjeto() => _mostrarDialogoNovoProjeto = false;

    private async Task EscolherPastaNovoProjetoAsync()
    {
        try
        {
            var resultado = await CommunityToolkit.Maui.Storage.FolderPicker.Default.PickAsync(default);
            if (resultado is { IsSuccessful: true, Folder: not null })
                _pastaNovoProjetoInicial = resultado.Folder.Path;
        }
        catch (Exception ex)
        {
            await JSRuntime.InvokeVoidAsync("alert", $"Não foi possível selecionar a pasta do projeto: {ex.Message}");
        }
    }

    private async Task CriarProjetoEmBrancoDaTelaInicialAsync()
    {
        string nome = _nomeNovoProjetoInicial.Trim();
        if (string.IsNullOrWhiteSpace(nome) || nome.Length > 120)
        {
            await JSRuntime.InvokeVoidAsync("alert", "Informe um nome de projeto com até 120 caracteres.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_pastaNovoProjetoInicial))
        {
            await JSRuntime.InvokeVoidAsync("alert", "Escolha a pasta onde o projeto será salvo.");
            return;
        }
        if (!await PodeTrocarProjetoAsync()) return;

        try
        {
            string pasta = Path.GetFullPath(_pastaNovoProjetoInicial.Trim());
            string caminho = Path.Combine(pasta, CriarNomeArquivoProjeto(nome) + ".gnx");
            if (File.Exists(caminho))
                throw new IOException("Já existe um projeto com esse nome nessa pasta. Escolha outro nome ou abra o projeto existente.");

            Directory.CreateDirectory(pasta);
            Projeto projeto = await ProjetoService.CriarNovoProjetoAsync(caminho, nome);
            ProjetoService.AtivarProjeto(projeto);
            MapService.ProjetoSRS = projeto.CRS;
            _camadaBaseProjeto = projeto.CamadaBase;
            _mostrarDialogoNovoProjeto = false;
            _mostrarTelaInicialProjeto = false;
            RegistrarProjetoRecente(projeto.CaminhoArquivo);
            MapService.RequestRedraw();
            StateHasChanged();
        }
        catch (Exception ex)
        {
            await JSRuntime.InvokeVoidAsync("alert", $"Não foi possível criar o projeto: {ex.Message}");
        }
    }

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

    private async Task RepararFonteCamadaAsync(Camada camadaOffline)
    {
        if (camadaOffline is null || !camadaOffline.PendenteReconexao ||
            !CamadasAtivas.Any(camada => ReferenceEquals(camada, camadaOffline))) return;
        if (!await PodeTrocarProjetoAsync()) return;

        bool vetor = string.Equals(camadaOffline.Tipo, "Vetor", StringComparison.OrdinalIgnoreCase);
        string[] extensoes = vetor
            ? new[] { ".shp", ".geojson", ".json", ".gpkg", ".kml", ".gml", ".csv" }
            : new[] { ".tif", ".tiff", ".img", ".jp2", ".ecw", ".vrt" };
        string tipoArquivo = vetor ? "vetorial" : "raster";

        try
        {
            var resultado = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = $"Localizar fonte {tipoArquivo} para {camadaOffline.Nome}",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, extensoes }
                })
            });
            if (resultado is null) return;

            string caminho = Path.GetFullPath(resultado.FullPath);
            string nomeNovo = Path.GetFileName(caminho);
            if (CamadasAtivas.Any(camada => !ReferenceEquals(camada, camadaOffline) &&
                    string.Equals(camada.Nome, nomeNovo, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Já existe outra camada chamada '{nomeNovo}'. Remova ou renomeie a duplicata antes de reconectar.");

            await GeoNex.Services.GnxProjectSourceValidator.ValidarAsync(
                new[] { new GeoNex.Models.Camada { Nome = nomeNovo, Tipo = camadaOffline.Tipo, CaminhoFonteOriginal = caminho } },
                MapService.GdalRasterLock);

            int indiceOriginal = CamadasAtivas.IndexOf(camadaOffline);
            if (indiceOriginal < 0 || !camadaOffline.PendenteReconexao) return;
            bool eraCamadaBase = string.Equals(_camadaBaseProjeto, camadaOffline.Nome, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ProjetoService.ProjetoAtual?.CamadaBase, camadaOffline.Nome, StringComparison.OrdinalIgnoreCase);
            GeoNex.Services.EstiloCamada? estilo = MapService.EstilosPorCamada.GetValueOrDefault(camadaOffline.Nome);
            double panX = MapService.CameraPanX;
            double panY = MapService.CameraPanY;
            double zoom = MapService.CameraZoom;
            CamadasAtivas.RemoveAt(indiceOriginal);

            Camada? carregada = null;
            try
            {
                if (vetor)
                {
                    caminhoArquivoVetor = caminho;
                    await AdicionarVetorAoMapa();
                }
                else
                {
                    caminhoArquivoRaster = caminho;
                    await AdicionarCamadaAoMapa();
                }

                carregada = CamadasAtivas.FirstOrDefault(camada =>
                    string.Equals(camada.Nome, nomeNovo, StringComparison.OrdinalIgnoreCase) && !camada.PendenteReconexao);
                if (carregada is null)
                    throw new InvalidDataException("A fonte foi selecionada, mas a camada não pôde ser carregada no mapa.");

                CamadasAtivas.Remove(carregada);
                CamadasAtivas.Insert(Math.Clamp(indiceOriginal, 0, CamadasAtivas.Count), carregada);
                carregada.Visivel = camadaOffline.Visivel;
                carregada.CaminhoArquivo = caminho;
                carregada.FonteJson = SerializarFonteProjeto(carregada);

                if (estilo is not null)
                {
                    MapService.EstilosPorCamada.TryRemove(camadaOffline.Nome, out _);
                    MapService.EstilosPorCamada[nomeNovo] = estilo;
                    if (vetor)
                    {
                        try
                        {
                            if (estilo.TipoSimbologia == "CATEGORIZADA")
                                MapService.CompilarCategorias(nomeNovo, estilo.ColunaSimbologia);
                            if (estilo.ExibirRotulos && !string.IsNullOrWhiteSpace(estilo.ColunaRotulo))
                                MapService.AtualizarRotulosCamada(nomeNovo, estilo.ColunaRotulo);
                        }
                        catch (Exception erroEstilo)
                        {
                            Console.Error.WriteLine($"O estilo de '{nomeNovo}' foi preservado, mas não pôde ser recompilado: {erroEstilo.Message}");
                        }
                    }
                }

                MapService.CamadasInvisiveis.Remove(camadaOffline.Nome);
                if (!carregada.Visivel) MapService.CamadasInvisiveis.Add(nomeNovo);
                if (eraCamadaBase) _camadaBaseProjeto = nomeNovo;
                MapService.CameraPanX = panX;
                MapService.CameraPanY = panY;
                MapService.CameraZoom = zoom;
                SincronizarHierarquia(solicitarFrame: false);
                MapService.RequestRedraw();
                try { await JSRuntime.InvokeVoidAsync("mapEngine.sincronizarCameraComBlazor", panX, panY, zoom); } catch { }

                try
                {
                    List<string> avisos = await PersistirProjetoAtualAsync();
                    string detalhes = avisos.Count == 0 ? string.Empty : " " + string.Join(" ", avisos.Distinct());
                    ExibirNotificacaoSalvamento("Fonte reconectada", $"A camada '{nomeNovo}' foi restaurada e o projeto atualizado.{detalhes}");
                }
                catch (Exception erroSalvamento)
                {
                    ExibirNotificacaoSalvamento("Fonte reconectada", $"A camada está ativa, mas o projeto não pôde ser atualizado: {erroSalvamento.Message}", erro: true);
                }
            }
            catch
            {
                if (carregada is null && !CamadasAtivas.Contains(camadaOffline))
                    CamadasAtivas.Insert(Math.Clamp(indiceOriginal, 0, CamadasAtivas.Count), camadaOffline);
                throw;
            }
        }
        catch (Exception ex)
        {
            await JSRuntime.InvokeVoidAsync("alert", $"Não foi possível reconectar a camada '{camadaOffline.Nome}': {ex.Message}");
        }
    }

    private void AbrirPropriedadesProjeto()
    {
        FecharMenusSuperiores();
        Projeto? projeto = ProjetoService.ProjetoAtual;
        if (projeto is null) return;
        _nomeProjetoConfiguracao = projeto.Nome;
        _crsProjetoConfiguracao = MapService.ProjetoSRS;
        _camadaBaseConfiguracao = _camadaBaseProjeto;
        _intervaloAutosaveConfiguracao = LerIntervaloAutosaveProjeto();
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
            Preferences.Default.Set(ProjectAutosaveIntervalPreferenceKey, _intervaloAutosaveConfiguracao);
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
            _mostrarTelaInicialProjeto = true;
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
