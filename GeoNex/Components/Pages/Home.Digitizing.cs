using GeoNex.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;
using SkiaSharp;
using System.Net.Sockets;

namespace GeoNex.Components.Pages;

public partial class Home
{
    private long _digitizingPreviewRevision;
    private long _digitizingPreviewClearAfterFrameId;
    private long _digitizingPreviewClearRevision;
    private bool _mostrarMenuPrincipal;
    private string? _menuSuperiorAberto;
    private string? _submenuDadosAberto;
    private string? _focoMenuDadosPendente;
    private Microsoft.AspNetCore.Components.ElementReference _menuDadosAdicionarRef;
    private Microsoft.AspNetCore.Components.ElementReference _menuDadosCriarRef;
    private Microsoft.AspNetCore.Components.ElementReference _menuDadosVoltarRef;
    private PostgisConnectionOptions _postgisFormulario = new();
    private PostgisConnectionOptions? _postgisConnection;
    private IReadOnlyList<PostgisSpatialTable> _postgisTabelas = Array.Empty<PostgisSpatialTable>();
    private string _tabelaPontosCaminho = string.Empty;
    private DelimitedPointTableService.Preview? _tabelaPontosPreview;
    private string _tabelaPontosSeparador = ";";
    private int _tabelaPontosColunaX;
    private int _tabelaPontosColunaY = 1;
    private IReadOnlyList<DelimitedPointTableService.CoordinateReferenceSystem> _tabelaPontosCrsCatalogo = Array.Empty<DelimitedPointTableService.CoordinateReferenceSystem>();
    private string _tabelaPontosCrsBusca = string.Empty;
    private string _tabelaPontosCrsSelecionado = "EPSG:4326";
    private bool _tabelaPontosImportando;
    private bool _tabelaPontosErro;
    private string _tabelaPontosMensagem = string.Empty;
    private bool _postgisConectando;
    private bool _postgisStatusErro;
    private bool _salvandoEdicoes;
    private bool exibirNotificacaoSalvamento;
    private bool notificacaoSalvamentoErro;
    private string tituloNotificacaoSalvamento = string.Empty;
    private string mensagemNotificacaoSalvamento = string.Empty;
    private CancellationTokenSource? notificacaoSalvamentoCts;
    private string _postgisMensagem = string.Empty;
    private string? _postgisCamadaCarregando;
    private bool _modoAvancadoVetorizacao;
    private Microsoft.AspNetCore.Components.ElementReference _contextoVetorizacaoRef;
    private bool _focoContextoVetorizacaoPendente;
    private void DefinirModoVetorizacaoAvancado(bool avancado)
    {
        if (!avancado && ConstrucaoParametrica && MapService.PontosAquisicao.Count > 0) return;
        if (!avancado && ConstrucaoParametrica) ModoConstrucao = ConstructionMode.Vertices;
        _modoAvancadoVetorizacao = avancado;
        _mostrarMenuContexto = false;
    }

    private void AlternarMenuPrincipal()
    {
        _mostrarMenuPrincipal = !_mostrarMenuPrincipal;
        _menuSuperiorAberto = null;
        _submenuDadosAberto = null;
        _focoMenuDadosPendente = null;
    }

    private void AlternarMenuSuperior(string menu)
    {
        _menuSuperiorAberto = _menuSuperiorAberto == menu ? null : menu;
        _submenuDadosAberto = null;
        _focoMenuDadosPendente = null;
    }

    private void AbrirSubmenuDados(string pagina)
    {
        if (pagina is not ("adicionar" or "criar")) return;
        _submenuDadosAberto = pagina;
        _focoMenuDadosPendente = "voltar";
    }

    private void VoltarMenuDados()
    {
        if (_submenuDadosAberto is null) return;
        _focoMenuDadosPendente = _submenuDadosAberto;
        _submenuDadosAberto = null;
    }

    private void AbrirModalNovaCamadaFormato(string formato)
    {
        if (formato is not ("SHP" or "GEOJSON" or "GPKG")) return;
        novaCamadaNome = string.Empty;
        novaCamadaFormato = formato;
        novaCamadaGeometria = "POLIGONO";
        novaCamadaCrs = "EPSG:31982";
        pastaDestinoSelecionada = string.Empty;
        exibirModalNovaCamada = true;
        FecharMenusSuperiores();
    }

    private void FecharMenusSuperiores()
    {
        if (_menuSuperiorAberto is null && !_mostrarMenuPrincipal && _submenuDadosAberto is null) return;
        _menuSuperiorAberto = null;
        _submenuDadosAberto = null;
        _mostrarMenuPrincipal = false;
        _focoMenuDadosPendente = null;
    }

    private void TecladoMenuSuperior(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (e.Key != "Escape") return;
        if (_menuSuperiorAberto == "dados" && _submenuDadosAberto is not null) VoltarMenuDados();
        else FecharMenusSuperiores();
    }

    private void AbrirConexaoPostgis()
    {
        _postgisFormulario = ClonarConexao(_postgisConnection ?? _postgisFormulario);
        _postgisMensagem = _postgisConnection is null
            ? string.Empty
            : $"Conectado. {_postgisTabelas.Count:N0} camada(s) espacial(is) disponível(is).";
        _postgisStatusErro = false;
        AbaModalAtiva = "Nuvem";
        exibirModalImportacao = true;
        FecharMenusSuperiores();
    }

    private void CredenciaisPostgisAlteradas()
    {
        _postgisConnection = null;
        _postgisTabelas = Array.Empty<PostgisSpatialTable>();
        _postgisMensagem = string.Empty;
        _postgisStatusErro = false;
    }

    private async Task ConectarPostgisAsync()
    {
        if (_postgisConectando) return;
        _postgisConectando = true;
        _postgisStatusErro = false;
        _postgisMensagem = "Validando conexão e consultando camadas espaciais…";
        PostgisConnectionOptions candidate = ClonarConexao(_postgisFormulario);
        await InvokeAsync(StateHasChanged);
        try
        {
            await PostgisDataService.TestConnectionAsync(candidate);
            _postgisTabelas = await PostgisDataService.ListSpatialTablesAsync(candidate);
            _postgisConnection = candidate;
            _postgisFormulario = ClonarConexao(candidate);
            _postgisMensagem = $"Conectado. {_postgisTabelas.Count:N0} camada(s) espacial(is) disponível(is).";
        }
        catch (Exception ex)
        {
            _postgisStatusErro = true;
            _postgisMensagem = MensagemPostgisSegura(ex);
        }
        finally
        {
            _postgisConectando = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task AtualizarListaPostgisAsync()
    {
        if (_postgisConnection is null || _postgisConectando) return;
        _postgisConectando = true;
        _postgisStatusErro = false;
        _postgisMensagem = "Atualizando lista de camadas…";
        try
        {
            _postgisTabelas = await PostgisDataService.ListSpatialTablesAsync(_postgisConnection);
            _postgisMensagem = $"Lista atualizada. {_postgisTabelas.Count:N0} camada(s) espacial(is).";
        }
        catch (Exception ex)
        {
            _postgisStatusErro = true;
            _postgisMensagem = MensagemPostgisSegura(ex);
        }
        finally
        {
            _postgisConectando = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task AdicionarCamadaPostgisAsync(PostgisSpatialTable table)
    {
        if (_postgisConnection is null || _postgisCamadaCarregando is not null) return;
        PostgisConnectionOptions connection = _postgisConnection;
        string layerKey = PostgisLayerKey(table);
        string layerName = $"{table.Schema}.{table.Table} · {table.GeometryColumn}";
        bool firstVectorLayer = !CamadasAtivas.Any(layer =>
            string.Equals(layer.Tipo, "Vetor", StringComparison.OrdinalIgnoreCase) && !layer.PendenteReconexao);
        Camada? offlinePlaceholder = CamadasAtivas.FirstOrDefault(layer =>
            string.Equals(layer.Nome, layerName, StringComparison.OrdinalIgnoreCase));
        if (offlinePlaceholder is not null && !offlinePlaceholder.PendenteReconexao)
        {
            _postgisMensagem = $"A camada {layerName} já está no mapa.";
            _postgisStatusErro = false;
            return;
        }
        int placeholderIndex = offlinePlaceholder is null ? -1 : CamadasAtivas.IndexOf(offlinePlaceholder);
        EstiloCamada? estiloOffline = null;
        if (offlinePlaceholder is not null && MapService.EstilosPorCamada.TryGetValue(layerName, out var estiloSalvo))
            estiloOffline = estiloSalvo;

        bool nomeReservado = offlinePlaceholder is null
            ? ReservarNomeCamada(layerName)
            : _nomesCamadasCarregando.TryAdd(layerName, 0);
        if (!nomeReservado)
        {
            _postgisMensagem = $"A camada {layerName} já está sendo carregada.";
            _postgisStatusErro = true;
            return;
        }

        string crsAnterior = MapService.ProjetoSRS;
        double offsetXAnterior = MapService.OffsetMundoX;
        double offsetYAnterior = MapService.OffsetMundoY;
        bool offsetDefinidoAnterior = MapService.OffsetMundoDefinido;
        double panXAnterior = MapService.CameraPanX;
        double panYAnterior = MapService.CameraPanY;
        double zoomAnterior = MapService.CameraZoom;
        SkiaSharp.SKRect limitesVetoriaisAnteriores = MapService.LimitesGlobaisVetor;
        string? camadaBaseAnterior = _camadaBaseProjeto;
        bool invisivelAnterior = MapService.CamadasInvisiveis.Contains(layerName);
        int ordemRenderAnterior = MapService.OrdemCamadas.FindIndex(name =>
            string.Equals(name, layerName, StringComparison.OrdinalIgnoreCase));

        _postgisCamadaCarregando = layerKey;
        _postgisStatusErro = false;
        _postgisMensagem = $"Carregando {layerName}…";
        await InvokeAsync(StateHasChanged);
        PostgisLoadedLayer? loaded = null;
        List<CompiledFeature>? renderFeatures = null;
        bool layerRegistered = false;
        try
        {
            int targetSrid = PostgisDataService.ResolveEpsg(MapService.ProjetoSRS);
            loaded = await PostgisDataService.LoadLayerAsync(
                connection, table, targetSrid, offsetXAnterior, offsetYAnterior);
            if (loaded.Features.Count == 0)
                throw new InvalidOperationException("A tabela não contém geometrias 2D válidas para exibir.");

            using (IDisposable? renderPause = _server is null ? null : await _server.PauseRenderingAsync())
            {
                if (!MapService.OffsetMundoDefinido && !loaded.Bounds.IsNull)
                {
                    MapService.DefinirOffset(loaded.Bounds.MinX, loaded.Bounds.MaxX, loaded.Bounds.MinY, loaded.Bounds.MaxY);
                    PostgisGeometryCompiler.Rebase(
                        loaded.Features,
                        MapService.OffsetMundoX - offsetXAnterior,
                        MapService.OffsetMundoY - offsetYAnterior);
                }

                var camada = new Camada
                {
                    Nome = layerName,
                    Tipo = "Vetor",
                    Visivel = offlinePlaceholder?.Visivel ?? true,
                    Geometria = table.GeometryType.Contains("POINT", StringComparison.OrdinalIgnoreCase) ? "PONTO"
                        : table.GeometryType.Contains("LINE", StringComparison.OrdinalIgnoreCase) ? "LINHA"
                        : "POLIGONO",
                    FontePostgis = new PostgisLayerSource(ClonarConexao(connection), table),
                    FonteJson = offlinePlaceholder?.FonteJson
                };
                if (offlinePlaceholder is not null && placeholderIndex >= 0)
                {
                    CamadasAtivas[placeholderIndex] = camada;
                    layerRegistered = true;
                }
                else if (!RegistrarNovaCamada(camada, inserirNoInicio: true, reservaDoChamador: true))
                    throw new InvalidOperationException($"A camada {layerName} já está no mapa.");
                else layerRegistered = true;

                MapService.EstilosPorCamada[layerName] = estiloOffline ?? new EstiloCamada
                {
                    CorPreenchimento = "#10b981", CorBorda = "#047857", Tamanho = 2,
                    TipoSimbologia = "UNICA", Opacidade = 0.5f
                };
                if (!MapService.OrdemCamadas.Contains(layerName, StringComparer.Ordinal))
                    MapService.OrdemCamadas.Insert(0, layerName);
                if (camada.Visivel) MapService.CamadasInvisiveis.Remove(layerName);
                else MapService.CamadasInvisiveis.Add(layerName);
                SincronizarHierarquia(solicitarFrame: false);
                renderFeatures = loaded.Features.ToList();
                MapService.PreCompilarPoligonos(layerName, renderFeatures);
                _camadaDestinoAquisicao = layerName;
                _postgisMensagem = $"Adicionada: {layerName} · {loaded.FeatureCount:N0} feição(ões) · EPSG:{loaded.Srid}.";
                exibirModalImportacao = false;
            }

            if (firstVectorLayer) await EnquadrarCamadaAsync(layerName);
            else SolicitarNovoFrame();
        }
        catch (Exception ex)
        {
            using (IDisposable? renderPause = _server is null ? null : await _server.PauseRenderingAsync())
            {
                if (layerRegistered)
                {
                    CamadasAtivas.RemoveAll(layer => string.Equals(layer.Nome, layerName, StringComparison.OrdinalIgnoreCase));
                    if (offlinePlaceholder is not null)
                        CamadasAtivas.Insert(Math.Clamp(placeholderIndex, 0, CamadasAtivas.Count), offlinePlaceholder);
                }
                RemoverRecursosVetoriais(layerName);
                if (offlinePlaceholder is not null && estiloOffline is not null)
                    MapService.EstilosPorCamada[layerName] = estiloOffline;
                if (ordemRenderAnterior >= 0)
                    MapService.OrdemCamadas.Insert(Math.Clamp(ordemRenderAnterior, 0, MapService.OrdemCamadas.Count), layerName);
                if (invisivelAnterior) MapService.CamadasInvisiveis.Add(layerName);
                else MapService.CamadasInvisiveis.Remove(layerName);
                MapService.ProjetoSRS = crsAnterior;
                MapService.OffsetMundoX = offsetXAnterior;
                MapService.OffsetMundoY = offsetYAnterior;
                MapService.OffsetMundoDefinido = offsetDefinidoAnterior;
                MapService.CameraPanX = panXAnterior;
                MapService.CameraPanY = panYAnterior;
                MapService.CameraZoom = zoomAnterior;
                MapService.LimitesGlobaisVetor = limitesVetoriaisAnteriores;
                _camadaBaseProjeto = camadaBaseAnterior;
                SincronizarHierarquia(solicitarFrame: false);
                MapService.RequestRedraw();
                try
                {
                    await JSRuntime.InvokeVoidAsync(
                        "mapEngine.sincronizarCameraComBlazor", panXAnterior, panYAnterior, zoomAnterior);
                }
                catch { }
            }

            if (loaded is not null && !ReferenceEquals(renderFeatures, MapService.FeaturesPorCamada.GetValueOrDefault(layerName)))
            {
                foreach (CompiledFeature feature in loaded.Features)
                    System.Threading.Interlocked.Exchange(ref feature.Path, null)?.Dispose();
            }
            _postgisStatusErro = true;
            _postgisMensagem = MensagemPostgisSegura(ex);
        }
        finally
        {
            LiberarNomeCamada(layerName);
            _postgisCamadaCarregando = null;
            await InvokeAsync(StateHasChanged);
        }
    }

    private static PostgisConnectionOptions ClonarConexao(PostgisConnectionOptions source) => new()
    {
        Host = source.Host,
        Port = source.Port,
        Database = source.Database,
        Username = source.Username,
        Password = source.Password,
        SslMode = source.SslMode
    };

    private static string PostgisLayerKey(PostgisSpatialTable table) =>
        $"{table.Schema}.{table.Table}.{table.GeometryColumn}";

    private static string MensagemPostgisSegura(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is not SocketException socket) continue;
            if (socket.SocketErrorCode == SocketError.ConnectionRefused)
                return "Conexão recusada. ‘localhost’ é este PC; informe o hostname/IP do servidor HostGator e confirme se o plano permite PostgreSQL remoto na porta 5432. Se for hospedagem compartilhada, a HostGator informa que PostgreSQL não está disponível nesse plano.";
            if (socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
                return "O hostname do servidor não foi encontrado. Confira o nome/IP nas informações da hospedagem ou solicite o endereço do servidor PostgreSQL à HostGator.";
            if (socket.SocketErrorCode is SocketError.TimedOut or SocketError.NetworkUnreachable or SocketError.HostUnreachable)
                return "Sem resposta do servidor PostgreSQL. Confira o hostname, a porta e se a HostGator liberou o acesso remoto para o IP público deste computador.";
        }

        string detail = exception switch
        {
            Npgsql.PostgresException postgres => postgres.MessageText,
            Npgsql.NpgsqlException npgsql => npgsql.InnerException?.Message ?? npgsql.Message,
            _ => exception.Message
        };
        return string.IsNullOrWhiteSpace(detail) ? "Falha na operação PostGIS." : detail;
    }

    private async Task ConfirmarTodasEdicoes()
    {
        if (_salvandoEdicoes) return;

        string? avisoEsboco = null;
        if (MapService.PontosAquisicao.Count > 0)
        {
            int pontosAntesDeConcluir = MapService.PontosAquisicao.Count;
            if (pontosAntesDeConcluir >= MinimoPontosAquisicao)
            {
                ConcluirAquisicaoGeometrica();
                if (MapService.PontosAquisicao.Count > 0)
                    avisoEsboco = string.IsNullOrWhiteSpace(mensagemVetorizacao)
                        ? "O desenho atual não foi concluído e continua preservado na tela."
                        : mensagemVetorizacao;
            }
            else
            {
                avisoEsboco = $"O desenho ainda está incompleto. Adicione pelo menos {MinimoPontosAquisicao - pontosAntesDeConcluir} ponto(s) antes de gravá-lo.";
                mensagemVetorizacao = avisoEsboco;
                StateHasChanged();
            }
        }

        if (!temEdicoesPendentes)
        {
            ExibirNotificacaoSalvamento(
                avisoEsboco is null ? "SEM ALTERAÇÕES" : "RASCUNHO NÃO GRAVADO",
                avisoEsboco ?? "Não há feições concluídas pendentes para gravar.",
                avisoEsboco is not null);
            return;
        }

        _salvandoEdicoes = true;
        await InvokeAsync(StateHasChanged);
        int totalGravados = 0;
        var falhas = new List<string>();

        try
        {
            foreach (Camada camada in CamadasAtivas.Where(camada => camada.Tipo == "Vetor"))
            {
                if (!MapService.FeicoesOriginais.TryGetValue(camada.Nome, out var feicoes))
                    continue;

                var pendentes = feicoes
                    .Where(feicao => !VectorEditPersistenceService.IsPersisted(feicao))
                    .ToList();
                if (pendentes.Count == 0) continue;

                try
                {
                    if (camada.FontePostgis is { } fontePostgis)
                    {
                        totalGravados += await PostgisDataService.InsertFeaturesAsync(
                            fontePostgis, MapService.ProjetoSRS, pendentes);
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(camada.CaminhoArquivo))
                            throw new IOException("A camada não possui arquivo de destino nem conexão PostGIS.");
                        totalGravados += VectorEditPersistenceService.SaveToOgr(
                            camada.CaminhoArquivo, pendentes, MapService.ProjetoSRS);
                    }
                }
                catch (Exception ex)
                {
                    // Non-transactional OGR drivers may have flushed earlier features before
                    // a later feature fails. Count those successful writes and keep only the
                    // remaining features pending for a safe retry.
                    totalGravados += pendentes.Count(VectorEditPersistenceService.IsPersisted);
                    falhas.Add($"{camada.Nome}: {MensagemPostgisSegura(ex)}");
                }
            }

            temEdicoesPendentes = CamadasAtivas
                .Where(camada => camada.Tipo == "Vetor" && MapService.FeicoesOriginais.ContainsKey(camada.Nome))
                .SelectMany(camada => MapService.FeicoesOriginais[camada.Nome])
                .Any(feicao => !VectorEditPersistenceService.IsPersisted(feicao));

            if (falhas.Count > 0)
            {
                string resumoFalhas = string.Join("\n", falhas.Take(6).Select(falha => $"• {falha}"));
                string complemento = falhas.Count > 6 ? $"\n… e mais {falhas.Count - 6} camada(s)." : string.Empty;
                if (!string.IsNullOrWhiteSpace(avisoEsboco)) complemento += $"\n\n{avisoEsboco}";
                string resultadoParcial = totalGravados > 0
                    ? $"{totalGravados:N0} feição(ões) gravada(s). As falhas continuam pendentes:\n\n{resumoFalhas}{complemento}"
                    : $"Nenhuma feição foi gravada. As falhas continuam pendentes:\n\n{resumoFalhas}{complemento}";
                ExibirNotificacaoSalvamento(totalGravados > 0 ? "SALVAMENTO PARCIAL" : "FALHA AO SALVAR",
                    resultadoParcial, erro: true);
            }
            else if (totalGravados > 0)
            {
                string complementoEsboco = string.IsNullOrWhiteSpace(avisoEsboco) ? string.Empty : $"\n\n{avisoEsboco}";
                ExibirNotificacaoSalvamento("EDIÇÕES SALVAS",
                    $"{totalGravados:N0} feição(ões) gravada(s) com sucesso.{complementoEsboco}");
            }
            else
            {
                ExibirNotificacaoSalvamento(
                    avisoEsboco is null ? "SEM ALTERAÇÕES" : "RASCUNHO NÃO GRAVADO",
                    avisoEsboco ?? "Não há feições concluídas pendentes para gravar.",
                    avisoEsboco is not null);
            }
        }
        catch (Exception ex)
        {
            ExibirNotificacaoSalvamento("FALHA AO SALVAR", MensagemPostgisSegura(ex), erro: true);
        }
        finally
        {
            _salvandoEdicoes = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void ExibirNotificacaoSalvamento(string titulo, string mensagem, bool erro = false)
    {
        notificacaoSalvamentoCts?.Cancel();
        var cts = new CancellationTokenSource();
        notificacaoSalvamentoCts = cts;
        exibirNotificacaoSalvamento = true;
        notificacaoSalvamentoErro = erro;
        tituloNotificacaoSalvamento = titulo;
        mensagemNotificacaoSalvamento = mensagem;
        StateHasChanged();
        _ = OcultarNotificacaoSalvamentoAsync(cts);
    }

    private async Task OcultarNotificacaoSalvamentoAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(notificacaoSalvamentoErro ? 12 : 5), cts.Token);
            if (ReferenceEquals(notificacaoSalvamentoCts, cts))
            {
                exibirNotificacaoSalvamento = false;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(notificacaoSalvamentoCts, cts))
                notificacaoSalvamentoCts = null;
            cts.Dispose();
        }
    }

    private void TecladoContextoVetorizacao(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (e.Key == "Escape") { _mostrarMenuContexto = false; _focoMapaPendente = true; }
    }
    private bool _focoMapaPendente;
    private bool _focoCogoPendente;
    private bool _focoCoordenadaPendente;
    private bool _hudPosicaoPendente;
    private void AbrirEntradaPrecisa(bool polar, double clientX = double.NaN, double clientY = double.NaN)
    {
        PosicionarEntradaFlutuante(polar, clientX, clientY);
        if (polar)
        {
            _mostrarInputCogo = true;
            _focoCogoPendente = true;
        }
        else
        {
            if (!_mostrarInputAbsoluto)
                PrepararSrsEntradaAbsoluta();
            _mostrarInputAbsoluto = true;
            _focoCoordenadaPendente = true;
        }
        _mostrarMenuContexto = false;
        StateHasChanged();
    }

    private void PosicionarEntradaFlutuante(bool polar, double clientX, double clientY)
    {
        bool outroPainelAberto = polar ? _mostrarInputAbsoluto : _mostrarInputCogo;
        bool posicaoValida = double.IsFinite(clientX) && double.IsFinite(clientY) && clientX >= 0 && clientY >= 0;
        double x = posicaoValida ? clientX + 14 : (polar ? _hudCogoX : _hudX);
        double y = posicaoValida ? clientY + 14 : (polar ? _hudCogoY : _hudY);

        if (outroPainelAberto)
        {
            // Quando os dois auxiliares estão abertos, o JS mede a altura real
            // após o render e os organiza em coluna, COGO acima do F6.
            x = posicaoValida ? x : Math.Min(_hudX, _hudCogoX);
            y = polar ? 64 : 424;
            _hudCogoX = _hudX = Math.Max(8, x);
            _hudCogoY = 64;
            _hudY = 424;
        }
        else
        {
            x = Math.Max(8, x);
            y = Math.Max(64, y);
        }

        if (polar) { _hudCogoX = x; _hudCogoY = y; }
        else { _hudX = x; _hudY = y; }
        _hudPosicaoPendente = true;
    }

    private void FecharEntradaPrecisa()
    {
        if (_coordenadaAbsolutaTravada) DestravarCoordenadaAbsoluta(solicitarRedesenho: false);
        _mostrarInputCogo = _mostrarInputAbsoluto = _focoCogoPendente = _focoCoordenadaPendente = false;
    }

    private void FecharEntradaCogo() { _mostrarInputCogo = _focoCogoPendente = false; _hudPosicaoPendente = true; }
    private void FecharEntradaAbsoluta()
    {
        DestravarCoordenadaAbsoluta(solicitarRedesenho: false);
        _mostrarInputAbsoluto = _focoCoordenadaPendente = false;
        _hudPosicaoPendente = true;
    }

    private void PrepararSrsEntradaAbsoluta()
    {
        string srsProjeto = string.IsNullOrWhiteSpace(MapService.ProjetoSRS) ? "EPSG:4326" : MapService.ProjetoSRS;
        _textoSrsProjetoEntrada = RotuloSrs(srsProjeto);
        _srsCamadaDestinoEntrada = string.Empty;

        Camada? destino = CamadasAtivas.FirstOrDefault(c => c.Tipo == "Vetor" &&
            string.Equals(c.Nome, _camadaDestinoAquisicao, StringComparison.Ordinal));
        if (destino?.FontePostgis is { Table.Srid: > 0 } fontePostgis)
        {
            _srsCamadaDestinoEntrada = $"EPSG:{fontePostgis.Table.Srid}";
        }
        else if (destino is not null)
        {
            using ResourceLease<MemoryMappedShapefile>? shapefile = MapService.AcquireShapefile(destino.Nome);
            _srsCamadaDestinoEntrada = shapefile?.Resource.LayerSRS ?? string.Empty;
        }

        _textoSrsCamadaEntrada = !string.IsNullOrWhiteSpace(_srsCamadaDestinoEntrada)
            ? RotuloSrs(_srsCamadaDestinoEntrada)
            : string.Empty;
        try
        {
            using var inputReference = SrsFactory.FromUserInput(ObterSrsEntradaAbsoluta());
            _entradaAbsolutaGeografica = inputReference.IsGeographic() == 1;
        }
        catch (Exception)
        {
            _entradaAbsolutaGeografica = false;
        }
    }

    private string TextoSrsEntradaAbsoluta => !string.IsNullOrWhiteSpace(_srsCamadaDestinoEntrada)
        ? $"Camada de destino · {_textoSrsCamadaEntrada}"
        : $"SRC do projeto · {_textoSrsProjetoEntrada}";

    private string ObterSrsEntradaAbsoluta() => !string.IsNullOrWhiteSpace(_srsCamadaDestinoEntrada)
        ? _srsCamadaDestinoEntrada
        : string.IsNullOrWhiteSpace(MapService.ProjetoSRS) ? "EPSG:4326" : MapService.ProjetoSRS;

    private static string RotuloSrs(string srs)
    {
        if (string.IsNullOrWhiteSpace(srs)) return "SRC desconhecido";
        try
        {
            using var reference = srs.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(srs.AsSpan(5), out int epsg)
                    ? SrsFactory.FromEPSG(epsg)
                    : SrsFactory.FromUserInput(srs);
            reference.AutoIdentifyEPSG();
            string? authority = reference.GetAuthorityName(null);
            string? code = reference.GetAuthorityCode(null);
            if (string.Equals(authority, "EPSG", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(code))
            {
                string? identifiedName = reference.GetName();
                return !string.IsNullOrWhiteSpace(identifiedName)
                    ? $"{identifiedName} · EPSG:{code}"
                    : $"EPSG:{code}";
            }
            string? name = reference.GetName();
            return !string.IsNullOrWhiteSpace(name) ? name : "SRC da camada";
        }
        catch
        {
            return srs.Length <= 48 ? srs : "SRC da camada";
        }
    }

    private void DestravarCoordenadaAbsoluta(bool solicitarRedesenho = true)
    {
        if (!_coordenadaAbsolutaTravada && !MapService.PontoRestricaoAbsoluta.HasValue) return;
        _coordenadaAbsolutaTravada = false;
        MapService.PontoRestricaoAbsoluta = null;
        JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.definirCoordenadaAbsolutaAquisicao", false, 0d, 0d);
        MapService.PontoCursorMundo = null;
        MapService.PontoCursorSnap = null;
        MapService.PontoCursorSnapTipo = null;
        _ultimoSnapRenderizado = null;
        if (solicitarRedesenho) SolicitarNovoFrame();
    }

    private void AlternarRestricaoPrecisa()
    {
        if (MapService.TravaDistanciaAtiva)
        {
            MapService.TravaDistanciaAtiva = false;
            mensagemVetorizacao = "";
            AtualizarRestricaoDistanciaCliente();
            return;
        }
        if (ConstrucaoParametrica) return;
        try
        {
            MapService.TravaDistanciaValor = MapService.Measurements.MetresToGridUnits(_restricaoDistanciaMetros ?? 0, MapService.ProjetoSRS);
            MapService.TravaDistanciaAtiva = true;
            mensagemVetorizacao = $"Raio de {_restricaoDistanciaMetros:0.##} m ativo a partir do último vértice.";
            AtualizarRestricaoDistanciaCliente();
        }
        catch (ArgumentException ex) { mensagemVetorizacao = ex.Message; }
    }

    private void AlterarModoRestricaoDistancia(string? valor)
    {
        MapService.TravaModoFixo = valor == "true";
        if (MapService.TravaDistanciaAtiva) AtualizarRestricaoDistanciaCliente();
    }

    private void AtualizarRestricaoDistanciaCliente()
    {
        double raio = MapService.TravaDistanciaValor;
        bool ativa = MapService.TravaDistanciaAtiva && !ConstrucaoParametrica &&
            double.IsFinite(raio) && raio > 0;
        JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.definirRestricaoDistanciaAquisicao",
            ativa, raio, MapService.TravaModoFixo);
        if (!PreviaVetorizacaoClienteAtiva) SolicitarNovoFrame(interacaoRapida: true);
    }
    private bool _medicaoConcluida;
    private bool _medicaoFalhou;
    private int MinimoPontosMedicao => MapService.MostrarAreaMedicao ? 3 : 2;
    private string EstadoMedicao => _medicaoConcluida ? "Resultado confirmado" :
        MapService.PontosMedicao.Count == 0 ? "Aguardando pontos" :
        MapService.PontoCursorMundo.HasValue ? "Prévia · inclui o cursor" : "Em edição · pontos marcados";

    private void ConcluirMedicao()
    {
        if (_medicaoConcluida || MapService.PontosMedicao.Count < MinimoPontosMedicao) return;
        LimparCursorDesenho();
        RecalcularMedicoes(false);
        if (!_medicaoFalhou && _medicaoAreaErro == null) _medicaoConcluida = true;
        JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.definirCursorMedicaoAtivo", !_medicaoConcluida);
        _ = SincronizarOverlay(false);
        StateHasChanged();
    }

    private void ContinuarMedicao()
    {
        _medicaoConcluida = false;
        _medicaoMensagem = "";
        JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.definirCursorMedicaoAtivo", true);
        StateHasChanged();
    }

    private void DefinirModoMedicao(bool area)
    {
        if (MapService.MostrarAreaMedicao == area) return;
        _medicaoConcluida = false;
        LimparCursorDesenho();
        MapService.MostrarAreaMedicao = area;
        JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.definirCursorMedicaoAtivo", true);
        RecalcularMedicoes();
        _ = SincronizarOverlay(false);
    }
    private bool FerramentaDesenhoAtiva => _ferramentaAtiva is ModoFerramenta.AquisicaoPonto or ModoFerramenta.AquisicaoLinha or ModoFerramenta.AquisicaoPoligono;
    private int NumeroEncaixesAtivos => (_snapAquisicaoVertice ? 1 : 0) + (_snapAquisicaoAresta ? 1 : 0) + (_snapAquisicaoMeio ? 1 : 0) + (_snapAquisicaoIntersecao ? 1 : 0);
    private double _aquisicaoComprimentoMetros;
    private double _aquisicaoAreaM2;
    private string? _aquisicaoAreaErro;
    private string _aquisicaoMetodo = "";
    private double? _aquisicaoAzimuteGraus;
    private long _ultimaAtualizacaoMetricasAquisicao;
    private long _ultimaAtualizacaoPreviewMedicao;

    private string TextoMetricasAquisicao
    {
        get
        {
            var metricas = new List<string>();
            if (_aquisicaoComprimentoMetros > 0)
                metricas.Add($"Comprimento {_aquisicaoComprimentoMetros:N2} m");
            if (_ferramentaAtiva == ModoFerramenta.AquisicaoPoligono && _aquisicaoAreaM2 > 0)
                metricas.Add($"Área {_aquisicaoAreaM2:N2} m²");
            if (_aquisicaoAzimuteGraus is { } azimute)
                metricas.Add($"Azimute {azimute:N1}°");
            return metricas.Count == 0 ? "Defina o próximo vértice" : string.Join(" · ", metricas);
        }
    }

    private void LimparMetricasAquisicao()
    {
        _aquisicaoComprimentoMetros = 0;
        _aquisicaoAreaM2 = 0;
        _aquisicaoAreaErro = null;
        _aquisicaoMetodo = "";
        _aquisicaoAzimuteGraus = null;
        _ultimaAtualizacaoMetricasAquisicao = 0;
    }

    /// <summary>Calcula o mesmo esboço que será concluído, incluindo a prévia sob o cursor.</summary>
    private void RecalcularMetricasAquisicao(bool force = false)
    {
        if (_ferramentaAtiva is not (ModoFerramenta.AquisicaoPonto or ModoFerramenta.AquisicaoLinha or ModoFerramenta.AquisicaoPoligono) ||
            MapService.PontosAquisicao.Count == 0)
        {
            LimparMetricasAquisicao();
            return;
        }

        long agora = System.Diagnostics.Stopwatch.GetTimestamp();
        int metricRefreshRate = MapService.PontosAquisicao.Count switch
        {
            <= 128 => 30,
            <= 512 => 15,
            <= 2_048 => 8,
            <= 8_192 => 4,
            _ => 2
        };
        if (!force && _ultimaAtualizacaoMetricasAquisicao != 0 &&
            agora - _ultimaAtualizacaoMetricasAquisicao < System.Diagnostics.Stopwatch.Frequency / metricRefreshRate)
            return;
        _ultimaAtualizacaoMetricasAquisicao = agora;

        var pontos = MapService.PontosAquisicao.ToList();
        if (ConstrucaoParametrica)
        {
            var previa = SketchConstruction.Preview(ModoConstrucao, pontos, MapService.PontoCursorMundo, LadosConstrucao);
            pontos = previa.Closed ? previa.Points.ToList() : previa.Points.ToList();
        }
        else if (MapService.PontoCursorMundo is { } cursor)
        {
            pontos.Add(cursor);
        }

        _aquisicaoComprimentoMetros = 0;
        _aquisicaoAreaM2 = 0;
        _aquisicaoAreaErro = null;
        _aquisicaoMetodo = "";
        _aquisicaoAzimuteGraus = null;
        if (pontos.Count < 2) return;

        try
        {
            bool medirArea = _ferramentaAtiva == ModoFerramenta.AquisicaoPoligono;
            var resultado = MapService.Measurements.Measure(pontos, MapService.ProjetoSRS,
                MapService.OffsetMundoX, MapService.OffsetMundoY, medirArea);
            _aquisicaoComprimentoMetros = resultado.LengthMetres;
            _aquisicaoAreaM2 = resultado.AreaSquareMetres ?? 0;
            _aquisicaoAreaErro = resultado.AreaError;
            _aquisicaoMetodo = resultado.Method;
            _aquisicaoAzimuteGraus = MapMeasurementService.GridAzimuth(pontos[^2], pontos[^1]);
        }
        catch (ArgumentException ex)
        {
            _aquisicaoMetodo = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            _aquisicaoMetodo = ex.Message;
        }
    }

    private int ToleranciaSnapPixels { get; set; } = 15;
    private int LadosConstrucao
    {
        get => Math.Clamp(MapService.ConstrucaoLados, 3, 32);
        set
        {
            MapService.ConstrucaoLados = Math.Clamp(value, 3, 32);
            SolicitarNovoFrame();
        }
    }
    private bool _painelCamadasAberto;
    private bool _painelIdentificarAberto;
    private ConstructionMode ModoConstrucao
    {
        get => MapService.ConstrucaoAtiva;
        set
        {
            if (MapService.PontosAquisicao.Count > 0) return;
            MapService.ConstrucaoAtiva = value;
            if (value != ConstructionMode.Vertices)
            {
                _modoOrtogonalAquisicao = false;
                MapService.TravaDistanciaAtiva = false;
            }
            historicoAquisicao?.Clear();
            LimparCursorDesenho();
            LimparMetricasAquisicao();
            SincronizarPreviaVetorizacaoCliente();
            SolicitarNovoFrame();
        }
    }
    private bool ConstrucaoParametrica => ModoConstrucao != ConstructionMode.Vertices;
    private int MinimoPontosAquisicao => ConstrucaoParametrica
        ? SketchConstruction.RequiredControls(ModoConstrucao)
        : _ferramentaAtiva == ModoFerramenta.AquisicaoPonto ? 1
        : _ferramentaAtiva == ModoFerramenta.AquisicaoLinha ? 2 : 3;
    private string InstrucaoConstrucao => !ConstrucaoParametrica
        ? _ferramentaAtiva == ModoFerramenta.AquisicaoPoligono && MapService.PontosAquisicao.Count >= 3
            ? "Clique no primeiro vértice para fechar · Enter também conclui."
            : "Clique para adicionar vértices. Enter: concluir."
        : MapService.PontosAquisicao.Count >= MinimoPontosAquisicao ? "Prévia pronta · Enter: concluir · Desfazer: ajustar"
        : ModoConstrucao switch
        {
            ConstructionMode.Rectangle => MapService.PontosAquisicao.Count == 0 ? "Clique no primeiro canto." : "Clique no canto oposto.",
            ConstructionMode.Circle => MapService.PontosAquisicao.Count == 0 ? "Clique no centro." : "Clique para definir o raio.",
            ConstructionMode.Ellipse => MapService.PontosAquisicao.Count switch
            {
                0 => "Clique no centro.",
                1 => "Clique para definir o primeiro eixo.",
                _ => "Clique para definir o segundo eixo."
            },
            ConstructionMode.RegularPolygon => MapService.PontosAquisicao.Count == 0 ? "Clique no centro." : $"Clique para definir o raio · {LadosConstrucao} lados.",
            _ => MapService.PontosAquisicao.Count switch { 0 => "Clique no início da base.", 1 => "Clique no fim da base.", _ => "Clique para definir a largura e o lado." }
        };

    private bool CliqueFechaPoligono(SKPoint cliqueEmTela, SKMatrix matriz, double[]? matrizApresentada = null)
    {
        if (_ferramentaAtiva != ModoFerramenta.AquisicaoPoligono || ConstrucaoParametrica ||
            MapService.PontosAquisicao.Count < 3) return false;

        var primeiroVerticeEmTela = MapearPontoLocalParaTela(MapService.PontosAquisicao[0], matrizApresentada, matriz);
        if (!float.IsFinite(primeiroVerticeEmTela.X) || !float.IsFinite(primeiroVerticeEmTela.Y)) return false;

        float tolerancia = Math.Clamp(ToleranciaSnapPixels, 6, 30);
        float dx = cliqueEmTela.X - primeiroVerticeEmTela.X;
        float dy = cliqueEmTela.Y - primeiroVerticeEmTela.Y;
        return dx * dx + dy * dy <= tolerancia * tolerancia;
    }

    private bool AdicionarControleAquisicao(SKPoint point)
    {
        if (!FerramentaDesenhoAtiva || string.IsNullOrEmpty(_camadaDestinoAquisicao))
        {
            mensagemVetorizacao = "Selecione uma camada de destino antes de adicionar pontos.";
            return false;
        }
        if (ConstrucaoParametrica && MapService.PontosAquisicao.Count >= MinimoPontosAquisicao)
        {
            mensagemVetorizacao = "Construção pronta. Conclua ou desfaça o último ponto para ajustar.";
            StateHasChanged();
            return false;
        }
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
        {
            mensagemVetorizacao = "Coordenada fora do intervalo suportado.";
            return false;
        }
        if (MapService.PontosAquisicao.Count > 0 && MapService.PontosAquisicao[^1] == point)
        {
            mensagemVetorizacao = "O ponto coincide com o último vértice na precisão atual.";
            return false;
        }
        HistoricoAquisicao.Add(point);
        LimparCursorDesenho();
        if (PreviaVetorizacaoClienteAtiva)
        {
            long revision = ++_digitizingPreviewRevision;
            JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.adicionarVerticeAquisicao",
                revision, point.X, point.Y);
        }
        else if (MapService.ClientRenderedDigitizingPreview)
        {
            _digitizingPreviewClearAfterFrameId = 0;
            _digitizingPreviewClearRevision = 0;
            MapService.ClientRenderedDigitizingPreview = false;
            long revision = ++_digitizingPreviewRevision;
            JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.definirAquisicao",
                (object)Array.Empty<object>(), false, null,
                revision, MapService.TipoGeometriaAtiva);
        }
        mensagemVetorizacao = "";
        return true;
    }

    private bool PreviaVetorizacaoClienteAtiva =>
        FerramentaDesenhoAtiva && MapService.ConstrucaoAtiva == ConstructionMode.Vertices;

    private double[] MatrizPreviaVetorizacaoCliente()
    {
        SKMatrix matrix = ObterMatrizMatematica();
        MapLocalCoordinate origin = MapCoordinateSpace.ApplyCssPanToPreciseLocalCenter(
            new MapLocalCoordinate(_ultimoMidXUI, _ultimoMidYUI), matrix.ScaleX,
            MapService.CameraPanX, MapService.CameraPanY);
        // Keep the preview in camera-relative coordinates. Large world-space
        // translations lose screen-pixel precision in Canvas2D at high zoom.
        return [matrix.ScaleX, matrix.SkewY, matrix.SkewX, matrix.ScaleY,
            _larguraTela / 2d, _alturaTela / 2d, origin.X, origin.Y];
    }

    private static SKPoint MapearPontoLocalParaTela(SKPoint point, double[]? matrix, SKMatrix fallback)
    {
        if (matrix is { Length: 8 } && matrix.All(double.IsFinite))
        {
            double x = point.X - matrix[6], y = point.Y - matrix[7];
            return new((float)(matrix[0] * x + matrix[2] * y + matrix[4]),
                (float)(matrix[1] * x + matrix[3] * y + matrix[5]));
        }
        return fallback.MapPoint(point);
    }

    private static bool TentarMapearPontoTelaParaLocal(
        double x, double y, double[]? matrix, SKMatrix fallback, out SKPoint point)
    {
        if (matrix is { Length: 8 } && matrix.All(double.IsFinite))
        {
            double determinant = matrix[0] * matrix[3] - matrix[1] * matrix[2];
            double dx = x - matrix[4], dy = y - matrix[5];
            if (double.IsFinite(determinant) && Math.Abs(determinant) > 1e-18)
            {
                double localX = matrix[6] + (matrix[3] * dx - matrix[2] * dy) / determinant;
                double localY = matrix[7] + (matrix[0] * dy - matrix[1] * dx) / determinant;
                if (double.IsFinite(localX) && double.IsFinite(localY) &&
                    Math.Abs(localX) <= float.MaxValue && Math.Abs(localY) <= float.MaxValue)
                {
                    point = new((float)localX, (float)localY);
                    return float.IsFinite(point.X) && float.IsFinite(point.Y);
                }
            }
        }

        if (fallback.TryInvert(out SKMatrix inverse))
        {
            point = inverse.MapPoint(new SKPoint((float)x, (float)y));
            return float.IsFinite(point.X) && float.IsFinite(point.Y);
        }

        point = default;
        return false;
    }

    private static SKMatrix UsarMatrizApresentada(double[]? values, SKMatrix fallback)
    {
        if (values is not { Length: 6 or 8 } || !values.All(double.IsFinite)) return fallback;
        var matrix = SKMatrix.Identity;
        matrix.ScaleX = (float)values[0];
        matrix.SkewY = (float)values[1];
        matrix.SkewX = (float)values[2];
        matrix.ScaleY = (float)values[3];
        // The centered 8-value form intentionally avoids rebuilding its large
        // world translation in float. Cursor snapping only needs the linear part.
        matrix.TransX = values.Length == 6 ? (float)values[4] : 0;
        matrix.TransY = values.Length == 6 ? (float)values[5] : 0;
        return float.IsFinite(matrix.ScaleX) && float.IsFinite(matrix.ScaleY) &&
            Math.Abs(matrix.ScaleX * matrix.ScaleY - matrix.SkewX * matrix.SkewY) > 1e-12f
            ? matrix
            : fallback;
    }

    private void SincronizarPreviaVetorizacaoCliente()
    {
        bool active = PreviaVetorizacaoClienteAtiva;
        bool serverPreviewNeeded = _ferramentaAtiva == ModoFerramenta.Medicao ||
            (FerramentaDesenhoAtiva && !active);
        bool preserveCommittedSketch = !active && !serverPreviewNeeded &&
            _digitizingPreviewClearAfterFrameId > 0;
        MapService.ClientRenderedDigitizingPreview = active || preserveCommittedSketch;

        if (preserveCommittedSketch)
        {
            JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.definirAquisicaoAtiva", false);
            return;
        }

        long revision = ++_digitizingPreviewRevision;
        object points = MapService.PontosAquisicao
            .Select(point => (object)new { x = point.X, y = point.Y })
            .ToArray();
        JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.definirAquisicao",
            points, active, null, revision, MapService.TipoGeometriaAtiva,
            MapService.TravaDistanciaAtiva && !ConstrucaoParametrica,
            MapService.TravaDistanciaValor, MapService.TravaModoFixo);
    }
    private bool _snapAquisicaoMeio;
    private bool _snapAquisicaoIntersecao;
    private bool _modoOrtogonalAquisicao;
    private bool _snapMedicaoAresta;
    private bool _snapMedicaoMeio;
    private bool _snapMedicaoIntersecao;
    private string mensagemVetorizacao = "";
    private SketchVertexHistory<SKPoint>? historicoAquisicao;
    private SketchVertexHistory<SKPoint>? historicoMedicao;
    private SketchVertexHistory<SKPoint> HistoricoAquisicao =>
        historicoAquisicao ??= new SketchVertexHistory<SKPoint>(MapService.PontosAquisicao);
    private SketchVertexHistory<SKPoint> HistoricoMedicao =>
        historicoMedicao ??= new SketchVertexHistory<SKPoint>(MapService.PontosMedicao);

    private DigitizingCursor.Result ResolverCursorFerramenta(SKPoint cursor, SKMatrix matrix)
    {
        bool measuring = _ferramentaAtiva == ModoFerramenta.Medicao;
        SKPoint? anchor = !measuring && MapService.PontosAquisicao.Count > 0
            ? MapService.PontosAquisicao[^1]
            : null;
        bool vertices = measuring ? _snapMedicaoAtivo : _snapAquisicaoVertice;
        bool edges = measuring ? _snapMedicaoAresta : _snapAquisicaoAresta;
        bool midpoints = measuring ? _snapMedicaoMeio : _snapAquisicaoMeio;
        bool intersections = measuring ? _snapMedicaoIntersecao : _snapAquisicaoIntersecao;
        bool orthogonal = !measuring && !ConstrucaoParametrica && _modoOrtogonalAquisicao;
        return DigitizingCursor.Resolve(
            cursor,
            matrix,
            Math.Clamp(ToleranciaSnapPixels, 6, 30),
            vertices,
            edges,
            anchor,
            !measuring && !ConstrucaoParametrica && MapService.TravaDistanciaAtiva,
            MapService.TravaDistanciaValor,
            MapService.TravaModoFixo,
            (point, tolerance, snapVertices, snapEdges) =>
                MapService.EncontrarVerticeProximo(point, tolerance, snapVertices, snapEdges, midpoints, intersections),
            midpoints, intersections, orthogonal);
    }

    private void LimparCursorDesenho()
    {
        _coordenadaAbsolutaTravada = false;
        MapService.PontoRestricaoAbsoluta = null;
        JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.definirCoordenadaAbsolutaAquisicao", false, 0d, 0d);
        MapService.PontoCursorMundo = null;
        MapService.PontoCursorSnap = null;
        MapService.PontoCursorSnapTipo = null;
        _ultimoSnapRenderizado = null;
        if (_ferramentaAtiva == ModoFerramenta.Medicao)
            JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.definirMouseESnap", null, null, null, null, null, 0);
    }

    [JSInvokable]
    public void ReceberMovimentoFerramentas(
        double pixelX,
        double pixelY,
        bool interacaoRapida = true,
        long pointerSequence = 0,
        double[]? displayedMatrix = null)
    {
        if (_ferramentaAtiva == ModoFerramenta.Medicao && _medicaoConcluida) return;
        if (exibirModalAtributos || _isPanning || !double.IsFinite(pixelX) || !double.IsFinite(pixelY)) return;
        if (_ferramentaAtiva is not (ModoFerramenta.Medicao or ModoFerramenta.AquisicaoPoligono or
            ModoFerramenta.AquisicaoLinha or ModoFerramenta.AquisicaoPonto)) return;

        SKMatrix matrix = UsarMatrizApresentada(displayedMatrix, ObterMatrizMatematica());
        if (!TentarMapearPontoTelaParaLocal(pixelX, pixelY, displayedMatrix, matrix, out SKPoint cursor)) return;
        var result = _coordenadaAbsolutaTravada && FerramentaDesenhoAtiva &&
            MapService.PontoRestricaoAbsoluta is { } absolutePoint
                ? new DigitizingCursor.Result(absolutePoint, null)
                : ResolverCursorFerramenta(
                    cursor, matrix);
        MapService.PontoCursorMundo = result.Position;
        MapService.PontoCursorSnap = result.Snap;
        if (!result.Snap.HasValue) MapService.PontoCursorSnapTipo = null;

        bool measurementTool = _ferramentaAtiva == ModoFerramenta.Medicao;
        bool measuring = measurementTool && MapService.PontosMedicao.Count > 0;
        bool snapChanged = !Nullable.Equals(_ultimoSnapRenderizado, result.Snap);
        _ultimoSnapRenderizado = result.Snap;
        if (measuring)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            int metricsRate = MapService.PontosMedicao.Count switch
            {
                <= 128 => 24,
                <= 512 => 16,
                <= 2_048 => 8,
                _ => 4
            };
            long minimumInterval = System.Diagnostics.Stopwatch.Frequency / metricsRate;
            if (_ultimaAtualizacaoPreviewMedicao == 0 || now - _ultimaAtualizacaoPreviewMedicao >= minimumInterval)
            {
                _ultimaAtualizacaoPreviewMedicao = now;
                RecalcularMedicoes();
                StateHasChanged();
            }

        }
        if (measurementTool)
        {
            // Keep the measurement overlay in the coordinate system of the
            // frame currently presented in the browser.
            var cursorImagem = MapearPontoLocalParaTela(result.Position, displayedMatrix, matrix);
            var snapImagem = result.Snap is { } snapPosition
                ? MapearPontoLocalParaTela(snapPosition, displayedMatrix, matrix)
                : (SkiaSharp.SKPoint?)null;
            JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.definirMouseESnap",
                cursorImagem.X, cursorImagem.Y,
                snapImagem?.X, snapImagem?.Y,
                result.Snap.HasValue ? MapService.PontoCursorSnapTipo?.ToString() : null,
                pointerSequence);
        }
        bool clientRenderedDigitizingPreview = PreviaVetorizacaoClienteAtiva;
        if (clientRenderedDigitizingPreview && pointerSequence > 0)
        {
            JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.definirCursorResolvidoAquisicao",
                pointerSequence,
                result.Position.X,
                result.Position.Y,
                result.Snap?.X,
                result.Snap?.Y,
                result.Snap.HasValue ? MapService.PontoCursorSnapTipo?.ToString() : null);
        }
        if (!measurementTool && MapService.PontosAquisicao.Count > 0)
        {
            long previousMetrics = _ultimaAtualizacaoMetricasAquisicao;
            RecalcularMetricasAquisicao();
            if (_ultimaAtualizacaoMetricasAquisicao != previousMetrics) StateHasChanged();
        }
        if (!measurementTool && (!clientRenderedDigitizingPreview &&
            (MapService.PontosAquisicao.Count > 0 || snapChanged)))
            SolicitarNovoFrame(interacaoRapida: interacaoRapida);
    }

    private void RefazerUltimoPontoAquisicao() => AlterarHistoricoAquisicao(true);

    private void AlterarHistoricoAquisicao(bool redo)
    {
        if (!(redo ? HistoricoAquisicao.Redo() : HistoricoAquisicao.Undo())) return;
        mensagemVetorizacao = "";
        LimparCursorDesenho();
        RecalcularMetricasAquisicao(true);
        if (PreviaVetorizacaoClienteAtiva) SincronizarPreviaVetorizacaoCliente();
        else SolicitarNovoFrame();
        StateHasChanged();
    }

    private void AlterarHistoricoMedicao(bool redo)
    {
        if (!(redo ? HistoricoMedicao.Redo() : HistoricoMedicao.Undo())) return;
        _medicaoConcluida = false;
        LimparCursorDesenho();
        RecalcularMedicoes();
        _ = SincronizarOverlay(false);
        StateHasChanged();
    }

    private string DelimitadorTabelaPontosValor => _tabelaPontosSeparador == "tab" ? "tab" : _tabelaPontosSeparador;

    private async Task AbrirImportacaoTabelaPontos()
    {
        AbrirModalImportacao();
        TrocarAbaModal("Texto");
        _tabelaPontosMensagem = string.Empty;
        _tabelaPontosErro = false;
        if (_tabelaPontosCrsCatalogo.Count == 0)
        {
            try
            {
                _tabelaPontosCrsCatalogo = await Task.Run(DelimitedPointTableService.ReadCoordinateReferenceSystems);
            }
            catch (Exception ex)
            {
                _tabelaPontosMensagem = $"O catálogo de SRC não pôde ser carregado. Você ainda pode informar um identificador EPSG/ESRI manualmente. {ex.Message}";
            }
        }
    }

    private IReadOnlyList<DelimitedPointTableService.CoordinateReferenceSystem> _tabelaPontosCrsResultados
    {
        get
        {
            if (_tabelaPontosCrsCatalogo.Count == 0) return Array.Empty<DelimitedPointTableService.CoordinateReferenceSystem>();
            string query = _tabelaPontosCrsBusca.Trim();
            if (query.Length == 0 || string.Equals(query, _tabelaPontosCrsSelecionado, StringComparison.OrdinalIgnoreCase))
                return Array.Empty<DelimitedPointTableService.CoordinateReferenceSystem>();

            return _tabelaPontosCrsCatalogo
                .Where(crs => crs.Identifier.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                              crs.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                              crs.Kind.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(40)
                .ToArray();
        }
    }

    private IReadOnlyList<DelimitedPointTableService.CoordinateReferenceSystem> _tabelaPontosCrsAtalhos
    {
        get
        {
            string[] featured = ["EPSG:4326", "EPSG:3857", "EPSG:31982", "EPSG:4674"];
            return featured.Select(code => _tabelaPontosCrsCatalogo.FirstOrDefault(crs =>
                    string.Equals(crs.Identifier, code, StringComparison.OrdinalIgnoreCase)))
                .Where(crs => crs is not null)
                .Cast<DelimitedPointTableService.CoordinateReferenceSystem>()
                .ToArray();
        }
    }

    private string RotuloCrsTabelaPontosSelecionado
    {
        get
        {
            DelimitedPointTableService.CoordinateReferenceSystem? crs = _tabelaPontosCrsCatalogo.FirstOrDefault(item =>
                string.Equals(item.Identifier, _tabelaPontosCrsSelecionado, StringComparison.OrdinalIgnoreCase));
            return crs is null ? _tabelaPontosCrsSelecionado : $"{crs.Identifier} — {crs.Name}";
        }
    }

    private void FiltrarCrsTabelaPontos(ChangeEventArgs args) =>
        _tabelaPontosCrsBusca = args.Value?.ToString() ?? string.Empty;

    private void SelecionarCrsTabelaPontos(DelimitedPointTableService.CoordinateReferenceSystem crs)
    {
        _tabelaPontosCrsSelecionado = crs.Identifier;
        _tabelaPontosCrsBusca = crs.Identifier;
    }

    private void UsarCrsDigitadoTabelaPontos()
    {
        string crs = _tabelaPontosCrsBusca.Trim();
        if (crs.Length > 0) _tabelaPontosCrsSelecionado = crs;
    }

    private async Task SelecionarArquivoTabelaPontos()
    {
        try
        {
            var options = new PickOptions
            {
                PickerTitle = "Selecione uma tabela CSV ou de texto",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.WinUI] = [".csv", ".txt", ".tsv"]
                })
            };
            var selected = await FilePicker.Default.PickAsync(options);
            if (selected is null) return;

            _tabelaPontosCaminho = selected.FullPath;
            await AtualizarPreviaTabelaPontosAsync(null);
        }
        catch (Exception ex)
        {
            _tabelaPontosPreview = null;
            _tabelaPontosErro = true;
            _tabelaPontosMensagem = $"Não foi possível abrir a tabela: {ex.Message}";
        }
    }

    private async Task AlterarDelimitadorTabelaPontos(ChangeEventArgs args)
    {
        string value = args.Value?.ToString() ?? ";";
        _tabelaPontosSeparador = value == "tab" ? "tab" : value;
        await AtualizarPreviaTabelaPontosAsync(ObterDelimitadorTabelaPontos());
    }

    private async Task AtualizarPreviaTabelaPontosAsync(char? delimiter)
    {
        if (string.IsNullOrWhiteSpace(_tabelaPontosCaminho)) return;
        try
        {
            var preview = await Task.Run(() => DelimitedPointTableService.ReadPreview(_tabelaPontosCaminho, delimiter));
            _tabelaPontosPreview = preview;
            _tabelaPontosSeparador = preview.Delimiter == '\t' ? "tab" : preview.Delimiter.ToString();
            _tabelaPontosColunaX = DelimitedPointTableService.GuessCoordinateColumn(preview.Columns, xAxis: true);
            _tabelaPontosColunaY = DelimitedPointTableService.GuessCoordinateColumn(preview.Columns, xAxis: false);
            _tabelaPontosErro = false;
            _tabelaPontosMensagem = $"Separador detectado: {NomeDelimitadorTabelaPontos(preview.Delimiter)}. Confira as colunas e o SRC antes de importar.";
        }
        catch (Exception ex)
        {
            _tabelaPontosPreview = null;
            _tabelaPontosErro = true;
            _tabelaPontosMensagem = ex.Message;
        }
    }

    private char ObterDelimitadorTabelaPontos() => _tabelaPontosSeparador == "tab"
        ? '\t'
        : _tabelaPontosSeparador.Length == 1 ? _tabelaPontosSeparador[0] : ';';

    private static string NomeDelimitadorTabelaPontos(char delimiter) => delimiter switch
    {
        ',' => "vírgula",
        ';' => "ponto e vírgula",
        '\t' => "tabulação",
        '|' => "barra vertical",
        _ => delimiter.ToString()
    };

    private async Task AdicionarTabelaPontosAsync()
    {
        if (_tabelaPontosImportando || _tabelaPontosPreview is null ||
            _tabelaPontosColunaX == _tabelaPontosColunaY) return;

        _tabelaPontosImportando = true;
        _tabelaPontosErro = false;
        _tabelaPontosMensagem = "Lendo coordenadas e criando a camada de pontos…";
        await InvokeAsync(StateHasChanged);

        DelimitedPointTableService.ImportResult? result = null;
        string layerName = NomeTabelaPontosDisponivel(Path.GetFileNameWithoutExtension(_tabelaPontosCaminho));
        if (!ReservarNomeCamada(layerName))
        {
            _tabelaPontosImportando = false;
            _tabelaPontosErro = true;
            _tabelaPontosMensagem = $"Já existe uma camada chamada '{layerName}' ou ela está sendo carregada.";
            await InvokeAsync(StateHasChanged);
            return;
        }

        string sourcePath = _tabelaPontosCaminho;
        int xColumn = _tabelaPontosColunaX;
        int yColumn = _tabelaPontosColunaY;
        string sourceCrs = _tabelaPontosCrsSelecionado;
        char delimiter = ObterDelimitadorTabelaPontos();
        bool firstVectorLayer = !CamadasAtivas.Any(layer =>
            string.Equals(layer.Tipo, "Vetor", StringComparison.OrdinalIgnoreCase) && !layer.PendenteReconexao);
        bool layerRegistered = false;
        string crsAnterior = MapService.ProjetoSRS;
        double offsetXAnterior = MapService.OffsetMundoX;
        double offsetYAnterior = MapService.OffsetMundoY;
        bool offsetDefinidoAnterior = MapService.OffsetMundoDefinido;
        double panXAnterior = MapService.CameraPanX;
        double panYAnterior = MapService.CameraPanY;
        double zoomAnterior = MapService.CameraZoom;
        SkiaSharp.SKRect limitesVetoriaisAnteriores = MapService.LimitesGlobaisVetor;
        string? camadaBaseAnterior = _camadaBaseProjeto;
        try
        {
            string destination = Path.Combine(FileSystem.AppDataDirectory, "ImportedPointTables");
            result = await Task.Run(() => DelimitedPointTableService.ImportToShapefile(
                sourcePath,
                xColumn,
                yColumn,
                sourceCrs,
                delimiter,
                destination));

            using (IDisposable? renderPause = _server is null ? null : await _server.PauseRenderingAsync())
            {
                await Task.Run(() => ProjetoService.CarregarShapefileParaMotorMapas(result.ShapefilePath, layerName, MapService));
                if (!RegistrarNovaCamada(new Camada
                {
                    Nome = layerName,
                    Tipo = "Vetor",
                    Visivel = true,
                    CaminhoArquivo = result.ShapefilePath,
                    Geometria = "PONTO"
                }, inserirNoInicio: true, reservaDoChamador: true))
                    throw new InvalidOperationException($"A camada '{layerName}' já existe no mapa.");

                layerRegistered = true;
                MapService.EstilosPorCamada[layerName] = new EstiloCamada
                {
                    CorPreenchimento = "#f97316",
                    CorBorda = "#7c2d12",
                    Tamanho = 5,
                    EspessuraBorda = 1.5f,
                    TipoSimbologia = "UNICA",
                    Opacidade = 1f
                };

                SincronizarHierarquia(solicitarFrame: false);
            }
            exibirModalImportacao = false;
            if (firstVectorLayer) await EnquadrarCamadaAsync(layerName);
            else SolicitarNovoFrame();

            string summary = $"Tabela adicionada: {result.ImportedCount:N0} ponto(s)" +
                (result.SkippedCount > 0 ? $"; {result.SkippedCount:N0} linha(s) ignorada(s) por coordenadas inválidas." : ".");
            await JSRuntime.InvokeVoidAsync("alert", summary);
        }
        catch (Exception ex)
        {
            if (layerRegistered) CamadasAtivas.RemoveAll(layer => layer.Nome == layerName);
            if (result is not null)
            {
                using (IDisposable? renderPause = _server is null ? null : await _server.PauseRenderingAsync())
                {
                    RemoverRecursosVetoriais(layerName);
                    MapService.ProjetoSRS = crsAnterior;
                    MapService.OffsetMundoX = offsetXAnterior;
                    MapService.OffsetMundoY = offsetYAnterior;
                    MapService.OffsetMundoDefinido = offsetDefinidoAnterior;
                    MapService.CameraPanX = panXAnterior;
                    MapService.CameraPanY = panYAnterior;
                    MapService.CameraZoom = zoomAnterior;
                    MapService.LimitesGlobaisVetor = limitesVetoriaisAnteriores;
                    _camadaBaseProjeto = camadaBaseAnterior;
                    MapService.RequestRedraw();
                    try
                    {
                        await JSRuntime.InvokeVoidAsync(
                            "mapEngine.sincronizarCameraComBlazor", panXAnterior, panYAnterior, zoomAnterior);
                    }
                    catch { }
                }

                foreach (string extension in new[] { ".shp", ".shx", ".dbf", ".prj", ".cpg" })
                {
                    try { File.Delete(Path.ChangeExtension(result.ShapefilePath, extension)); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            _tabelaPontosErro = true;
            _tabelaPontosMensagem = $"Falha ao importar a tabela: {ex.Message}";
        }
        finally
        {
            LiberarNomeCamada(layerName);
            _tabelaPontosImportando = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private string NomeTabelaPontosDisponivel(string sourceName)
    {
        string baseName = string.IsNullOrWhiteSpace(sourceName) ? "Tabela de pontos" : sourceName;
        string candidate = baseName;
        int suffix = 2;
        while (CamadasAtivas.Any(layer => string.Equals(layer.Nome, candidate, StringComparison.OrdinalIgnoreCase)) ||
               _nomesCamadasCarregando.ContainsKey(candidate))
            candidate = $"{baseName} ({suffix++})";
        return candidate;
    }
}
