using System.Globalization;
using System.Text;
using GeoNex.Services;
using GeoNex.Services.Attributes;
using GeoNex.Services.ExpressionEngine;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SkiaSharp;

namespace GeoNex.Components.Pages;

public partial class Home
{
    // =========================================================================
    // TABELA DE ATRIBUTOS MODERNA (ARQUITETURA DE ALTA PERFORMANCE)
    // =========================================================================
    private IAttributeDataProvider? _attributeProvider;
    private IReadOnlyList<AttributeRow> _attributeTableRows = Array.Empty<AttributeRow>();
    private IReadOnlyList<AttributeColumnDefinition> _attributeTableColumns = Array.Empty<AttributeColumnDefinition>();
    private AttributeTransactionManager _attributeTxManager = new(100);

    private int _attributePageIndex = 0;
    private int _attributePageSize = 100;
    private int _attributeTotalPages => _attributeProvider != null && _attributeProvider.FilteredRowCount > 0
        ? (int)Math.Ceiling((double)_attributeProvider.FilteredRowCount / _attributePageSize)
        : 1;

    private string _attributeSearchQuery = string.Empty;
    private bool _attributeIsLoading = false;
    private string _attributeStatusMessage = string.Empty;

    // Estatísticas da coluna
    private bool _mostrarGavetaEstatisticas = false;
    private string? _colunaEstatisticasAtiva;
    private ColumnStatistics? _estatisticasResultado;

    // Modais de ferramentas
    private bool _mostrarModalCalculadora = false;
    private string _calcColunaDestino = string.Empty;
    private bool _calcCriarNovaColuna = false;
    private string _calcNovaColunaNome = string.Empty;
    private AttributeDataType _calcNovaColunaTipo = AttributeDataType.Real;
    private string _calcExpressao = string.Empty;
    private bool _calcApenasSelecionados = false;
    private string? _calcErroExpressao;
    private string? _calcPreviaResultado;

    // Modal de esquema (Adicionar Coluna)
    private bool _mostrarModalAddColuna = false;
    private string _novaColNome = string.Empty;
    private AttributeDataType _novaColTipo = AttributeDataType.String;
    private int _novaColTamanho = 50;
    private int _novaColPrecisao = 0;
    private string? _novaColErro;

    // Filtros dinâmicos
    private bool _filtroViewportAtivo = false;
    private bool _filtroApenasSelecionados = false;

    // =========================================================================
    // ESTADO DA JANELA SEPARADA (PADRÃO QGIS)
    // =========================================================================
    private bool _tabelaModoJanela = true;
    private bool _tabelaMaximizada = false;
    private bool _tabelaMinimizada = false;
    private double _janelaX = 80;
    private double _janelaY = 50;
    private double _janelaLargura = 1060;
    private double _janelaAltura = 620;

    private void AlternarModoJanelaTabela()
    {
        _tabelaModoJanela = !_tabelaModoJanela;
        _tabelaMaximizada = false;
        StateHasChanged();
        if (_tabelaModoJanela)
        {
            JSRuntime.InvokeVoidAsyncSafe("geonexTableWindow.onTableOpened");
        }
    }

    private void AlternarMaximizarJanelaTabela()
    {
        if (!_tabelaModoJanela) return;
        _tabelaMaximizada = !_tabelaMaximizada;
        StateHasChanged();
    }

    private void MinimizarJanelaTabela()
    {
        _tabelaMinimizada = true;
        StateHasChanged();
    }

    private void RestaurarJanelaTabela()
    {
        _tabelaMinimizada = false;
        StateHasChanged();
        JSRuntime.InvokeVoidAsyncSafe("geonexTableWindow.onTableOpened");
    }

    private async Task AbrirTabela(string nomeCamada)
    {
        if (string.IsNullOrWhiteSpace(nomeCamada)) return;

        nomeCamadaAtiva = nomeCamada;
        var camada = CamadasAtivas.FirstOrDefault(c => string.Equals(c.Nome, nomeCamada, StringComparison.OrdinalIgnoreCase));
        string? caminho = camada?.CaminhoArquivo;

        AttributeSession.DefinirCamadaAtiva(nomeCamada, caminho);

        var fids = ObterFidsSelecionadosCamada(nomeCamada);
        if (fids.Count > 0)
        {
            AttributeSession.NotificarSelecaoMapa(nomeCamada, fids);
        }

#if WINDOWS || MACCATALYST
        var win = new Window(new AttributeTablePage(nomeCamada))
        {
            Title = $"Tabela de Atributos — {nomeCamada} - GeoNex",
            Width = 1120,
            Height = 720
        };
        Application.Current?.OpenWindow(win);
#else
        Application.Current?.MainPage?.DisplayAlert("Aviso", "Múltiplas janelas só estão disponíveis no Desktop.", "OK");
#endif
        await Task.CompletedTask;
    }

    private void FecharTabela()
    {
        exibirTabelaAtributos = false;
        _mostrarGavetaEstatisticas = false;
        _attributeProvider?.Dispose();
        _attributeProvider = null;
        _attributeTableRows = Array.Empty<AttributeRow>();
        _attributeTableColumns = Array.Empty<AttributeColumnDefinition>();
    }

    private async Task CarregarPaginaTabelaAsync()
    {
        if (_attributeProvider == null) return;
        _attributeIsLoading = true;
        try
        {
            int start = _attributePageIndex * _attributePageSize;
            _attributeTableRows = await _attributeProvider.GetRowsAsync(start, _attributePageSize);
            _attributeTableColumns = _attributeProvider.Columns;
        }
        finally
        {
            _attributeIsLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task IrParaPaginaTabela(int novaPagina)
    {
        if (_attributeProvider == null) return;
        int maxPage = Math.Max(0, _attributeTotalPages - 1);
        _attributePageIndex = Math.Clamp(novaPagina, 0, maxPage);
        await CarregarPaginaTabelaAsync();
    }

    private async Task AplicarBuscaTabelaAsync()
    {
        if (_attributeProvider == null) return;
        _attributePageIndex = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(_attributeSearchQuery))
            {
                await _attributeProvider.SetFilterAsync(null);
            }
            else
            {
                // Simple search over string columns or direct query
                string q = _attributeSearchQuery.Trim();
                var textCols = _attributeTableColumns
                    .Where(c => c.DataType == AttributeDataType.String)
                    .Take(8)
                    .Select(c => $"\"{c.Name}\" LIKE '%{q}%'");
                string sqlFilter = string.Join(" OR ", textCols);

                if (!string.IsNullOrEmpty(sqlFilter))
                    await _attributeProvider.SetFilterAsync(sqlFilter);
            }
            await CarregarPaginaTabelaAsync();
        }
        catch (Exception ex)
        {
            _attributeStatusMessage = $"Filtro: {ex.Message}";
        }
    }

    private async Task LimparBuscaTabelaAsync()
    {
        _attributeSearchQuery = string.Empty;
        if (_attributeProvider != null)
        {
            _attributePageIndex = 0;
            await _attributeProvider.SetFilterAsync(null);
            await CarregarPaginaTabelaAsync();
        }
    }

    private async Task OrdenarPorColunaAsync(string colName)
    {
        if (_attributeProvider == null) return;

        bool asc = true;
        if (_attributeProvider.SortColumn == colName)
        {
            if (_attributeProvider.SortAscending)
                asc = false;
            else
            {
                await _attributeProvider.ClearSortingAsync();
                await CarregarPaginaTabelaAsync();
                return;
            }
        }

        await _attributeProvider.SetSortingAsync(colName, asc);
        await CarregarPaginaTabelaAsync();
    }

    private async Task AlternarModoEdicaoTabelaAsync()
    {
        if (_attributeProvider == null) return;

        if (_attributeProvider.IsEditing)
        {
            if (_attributeProvider.HasPendingModifications)
            {
                bool confirmar = await JSRuntime.InvokeAsync<bool>("confirm",
                    "Existem alterações não salvas. Deseja descartar as alterações e sair do modo de edição?");
                if (!confirmar) return;
                await _attributeProvider.RollbackTransactionAsync();
                _attributeTxManager.Clear();
            }
            else
            {
                await _attributeProvider.RollbackTransactionAsync();
            }
        }
        else
        {
            await _attributeProvider.BeginTransactionAsync();
        }

        await CarregarPaginaTabelaAsync();
    }

    private async Task SalvarEdicoesTabelaAsync()
    {
        if (_attributeProvider == null || !_attributeProvider.IsEditing) return;

        try
        {
            _attributeIsLoading = true;
            await InvokeAsync(StateHasChanged);

            await _attributeProvider.CommitTransactionAsync();
            _attributeTxManager.Clear();
            _attributeStatusMessage = "Alterações salvas com sucesso no arquivo.";
            await CarregarPaginaTabelaAsync();
            SolicitarNovoFrame(interacaoRapida: false);
        }
        catch (Exception ex)
        {
            _attributeStatusMessage = $"Erro ao salvar: {ex.Message}";
        }
        finally
        {
            _attributeIsLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task AtualizarCelulaTabelaAsync(long fid, string colName, object? novoValor, object? valorAnterior)
    {
        if (_attributeProvider == null || !_attributeProvider.IsEditing) return;

        _attributeTxManager.RecordEdit(fid, colName, valorAnterior, novoValor);
        await _attributeProvider.SetCellValueAsync(fid, colName, novoValor);
        StateHasChanged();
    }

    private async Task DesfazerEdicaoTabelaAsync()
    {
        if (_attributeProvider == null || !_attributeProvider.IsEditing || !_attributeTxManager.CanUndo) return;
        _attributeTxManager.Undo(_attributeProvider);
        await CarregarPaginaTabelaAsync();
    }

    private async Task RefazerEdicaoTabelaAsync()
    {
        if (_attributeProvider == null || !_attributeProvider.IsEditing || !_attributeTxManager.CanRedo) return;
        _attributeTxManager.Redo(_attributeProvider);
        await CarregarPaginaTabelaAsync();
    }

    // =========================================================================
    // FERRAMENTAS ESPACIAIS E SINCRONIZAÇÃO
    // =========================================================================
    private async Task AlternarFiltroViewportTabelaAsync()
    {
        if (_attributeProvider == null) return;
        _filtroViewportAtivo = !_filtroViewportAtivo;

        if (_filtroViewportAtivo)
        {
            var bounds = ObterEnvelopeVisivelTela();
            var fidsVisiveis = new HashSet<long>();
            if (MapService.FeaturesPorCamada.TryGetValue(nomeCamadaAtiva, out var features))
            {
                foreach (var f in features)
                {
                    if (f.Path is { } path)
                    {
                        var b = path.Bounds;
                        if (bounds.Intersects(new NetTopologySuite.Geometries.Envelope(b.Left, b.Right, b.Top, b.Bottom)))
                        {
                            fidsVisiveis.Add(f.FID >= 0 ? f.FID : f.SourceFeatureId);
                        }
                    }
                }
            }
            await _attributeProvider.SetSpatialFilterAsync(fidsVisiveis);
        }
        else
        {
            await _attributeProvider.SetSpatialFilterAsync(null);
        }

        _attributePageIndex = 0;
        await CarregarPaginaTabelaAsync();
    }

    private async Task AlternarFiltroApenasSelecionadosAsync()
    {
        if (_attributeProvider == null) return;
        _filtroApenasSelecionados = !_filtroApenasSelecionados;

        if (_filtroApenasSelecionados)
        {
            var fids = ObterFidsSelecionadosCamada(nomeCamadaAtiva);
            await _attributeProvider.SetSpatialFilterAsync(fids.Count > 0 ? fids : new HashSet<long> { -1 });
        }
        else
        {
            await _attributeProvider.SetSpatialFilterAsync(null);
        }

        _attributePageIndex = 0;
        await CarregarPaginaTabelaAsync();
    }

    private async Task CentralizarFeicaoNoMapaAsync(string camada, long fid)
    {
        if (fid < 0 || string.IsNullOrWhiteSpace(camada)) return;
        nomeCamadaAtiva = camada;
        List<CompiledFeature>? features = null;
        if (!MapService.FeaturesPorCamada.TryGetValue(camada, out features))
        {
            MapService.FeaturesPorCamada.TryGetValue(System.IO.Path.GetFileNameWithoutExtension(camada), out features);
        }

        if (features != null)
        {
            var feat = features.FirstOrDefault(f => f.FID == fid || f.SourceFeatureId == fid);
            if (feat?.Path is { } path)
            {
                var rect = path.Bounds;
                float cx = rect.MidX;
                float cy = rect.MidY;

                var bounds = MapService.GetSceneBounds();
                float midX = MapService.ViewportMidX;
                float midY = MapService.ViewportMidY;
                if ((midX == 0 && midY == 0) || !float.IsFinite(midX) || !float.IsFinite(midY))
                {
                    if (!bounds.IsEmpty)
                    {
                        midX = bounds.MidX;
                        midY = bounds.MidY;
                    }
                }

                float escalaAutoFit = MapService.ViewportEscalaAutoFit;
                if (escalaAutoFit <= 0f && !bounds.IsEmpty && _larguraTela > 0 && _alturaTela > 0)
                {
                    float escalaX = _larguraTela / bounds.Width;
                    float escalaY = _alturaTela / bounds.Height;
                    escalaAutoFit = Math.Min(escalaX, escalaY) * 0.8f;
                }
                if (escalaAutoFit <= 0f || !float.IsFinite(escalaAutoFit)) escalaAutoFit = 1f;

                float zoomReal = (float)(escalaAutoFit * MapService.CameraZoom);
                float targetPanX = (midX - cx) * zoomReal;
                float targetPanY = (midY - cy) * zoomReal;

                long serial = await JSRuntime.InvokeAsync<long>("mapEngine.sincronizarCameraComBlazor",
                    targetPanX, targetPanY, MapService.CameraZoom);
                _ultimaRequisicaoCamera = Math.Max(_ultimaRequisicaoCamera, serial);
                MapService.CameraPanX = targetPanX;
                MapService.CameraPanY = targetPanY;
                MapService.IsPanning = false;
                MapService.DestacarFeicao(feat);
                MapService.RequestRedraw();
                await JSRuntime.InvokeVoidAsync("mapEngine.executarRequisicao", true, true);
            }
        }
    }

    private async Task PiscarFeicaoNoMapaAsync(string camada, long fid)
    {
        if (fid < 0 || string.IsNullOrWhiteSpace(camada)) return;
        nomeCamadaAtiva = camada;
        List<CompiledFeature>? features = null;
        if (!MapService.FeaturesPorCamada.TryGetValue(camada, out features))
        {
            MapService.FeaturesPorCamada.TryGetValue(System.IO.Path.GetFileNameWithoutExtension(camada), out features);
        }

        if (features != null)
        {
            var feat = features.FirstOrDefault(f => f.FID == fid || f.SourceFeatureId == fid);
            if (feat != null)
            {
                MapService.DestacarFeicao(feat);
                SolicitarNovoFrame(interacaoRapida: true);
                await Task.Delay(250);
                MapService.DestacarFeicao(null);
                SolicitarNovoFrame(interacaoRapida: true);
                await Task.Delay(150);
                MapService.DestacarFeicao(feat);
                SolicitarNovoFrame(interacaoRapida: true);
            }
        }
    }

    private void ToggleSelecaoLinhaFid(long fid)
    {
        if (fid < 0 || string.IsNullOrWhiteSpace(nomeCamadaAtiva)) return;
        if (!_fidsSelecionadosTabela.TryGetValue(nomeCamadaAtiva, out var set))
        {
            set = new HashSet<long>();
            _fidsSelecionadosTabela[nomeCamadaAtiva] = set;
        }
        if (set.Contains(fid)) set.Remove(fid);
        else set.Add(fid);
        SincronizarSelecaoTabelaComMapa(nomeCamadaAtiva);
    }

    private async Task ZoomParaLinhaFidAsync(string camada, long fid)
    {
        if (fid < 0 || string.IsNullOrWhiteSpace(camada)) return;
        nomeCamadaAtiva = camada;
        List<CompiledFeature>? features = null;
        if (!MapService.FeaturesPorCamada.TryGetValue(camada, out features))
        {
            MapService.FeaturesPorCamada.TryGetValue(System.IO.Path.GetFileNameWithoutExtension(camada), out features);
        }

        if (features != null)
        {
            var feature = features.FirstOrDefault(f => f.FID == fid || f.SourceFeatureId == fid);
            if (feature?.Path is { } path)
            {
                var rect = path.Bounds;
                float dx = rect.Width * 0.35f;
                float dy = rect.Height * 0.35f;
                rect.Inflate(Math.Max(dx, 30f), Math.Max(dy, 30f));

                if (LayerCameraPolicy.TryFit(MapService.GetSceneBounds(), rect, _larguraTela, _alturaTela, out var camera))
                {
                    long serial = await JSRuntime.InvokeAsync<long>("mapEngine.sincronizarCameraComBlazor",
                        camera.PanX, camera.PanY, camera.Zoom);
                    _ultimaRequisicaoCamera = Math.Max(_ultimaRequisicaoCamera, serial);
                    MapService.CameraPanX = camera.PanX;
                    MapService.CameraPanY = camera.PanY;
                    MapService.CameraZoom = camera.Zoom;
                    MapService.IsPanning = false;
                    MapService.DestacarFeicao(feature);
                    MapService.RequestRedraw();
                    await JSRuntime.InvokeVoidAsync("mapEngine.executarRequisicao", true, true);
                }
            }
        }
    }

    // =========================================================================
    // CALCULADORA DE CAMPOS
    // =========================================================================
    private void AbrirCalculadoraCampos()
    {
        if (_attributeTableColumns.Count == 0) return;
        _calcColunaDestino = _attributeTableColumns.FirstOrDefault(c => !c.IsPrimaryKey)?.Name ?? "";
        _calcCriarNovaColuna = false;
        _calcNovaColunaNome = "NOVO_CAMPO";
        _calcNovaColunaTipo = AttributeDataType.Real;
        _calcExpressao = "$area";
        _calcApenasSelecionados = ObterContagemFeicoesSelecionadas(nomeCamadaAtiva) > 0;
        _calcErroExpressao = null;
        _calcPreviaResultado = null;
        _mostrarModalCalculadora = true;
        AtualizarPreviaCalculadora();
    }

    private void FecharCalculadoraCampos()
    {
        _mostrarModalCalculadora = false;
    }

    private void AtualizarPreviaCalculadora()
    {
        if (string.IsNullOrWhiteSpace(_calcExpressao))
        {
            _calcPreviaResultado = "(Expressão vazia)";
            _calcErroExpressao = null;
            return;
        }

        try
        {
            if (!FieldCalculatorService.ValidateExpression(_calcExpressao, out string? err))
            {
                _calcErroExpressao = err ?? "Sintaxe inválida.";
                _calcPreviaResultado = null;
                return;
            }

            _calcErroExpressao = null;
            // Evaluates on first row as sample
            if (_attributeTableRows.Count > 0)
            {
                var sampleRow = _attributeTableRows[0];
                var sampleDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _attributeTableColumns.Count; i++)
                {
                    sampleDict[_attributeTableColumns[i].Name] = sampleRow.Values[i];
                }

                var sampleGeom = ObterGeometriaFeicao(sampleRow.FID);
                var sampleResult = FieldCalculatorService.EvaluateSingle(_calcExpressao, sampleRow.FID, 1, sampleDict, sampleGeom);
                _calcPreviaResultado = sampleResult != null ? $"{sampleResult}" : "NULL";
            }
            else
            {
                _calcPreviaResultado = "(Sem linhas para amostra)";
            }
        }
        catch (Exception ex)
        {
            _calcErroExpressao = ex.Message;
            _calcPreviaResultado = null;
        }
    }

    private NetTopologySuite.Geometries.Geometry? ObterGeometriaFeicao(long fid)
    {
        if (MapService.FeaturesPorCamada.TryGetValue(nomeCamadaAtiva, out var features))
        {
            var f = features.FirstOrDefault(x => x.FID == fid || x.SourceFeatureId == fid);
            if (f != null && !f.EnvelopeWorld.IsNull)
            {
                var env = f.EnvelopeWorld;
                var gf = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
                return gf.ToGeometry(new NetTopologySuite.Geometries.Envelope(env.MinX, env.MaxX, env.MinY, env.MaxY));
            }
        }
        return null;
    }

    private async Task ExecutarCalculadoraCamposAsync()
    {
        if (_attributeProvider == null) return;
        AtualizarPreviaCalculadora();
        if (_calcErroExpressao != null) return;

        try
        {
            _attributeIsLoading = true;
            StateHasChanged();

            string targetColumn = _calcColunaDestino;
            if (_calcCriarNovaColuna)
            {
                targetColumn = _calcNovaColunaNome.Trim().ToUpperInvariant();
                await _attributeProvider.AddColumnAsync(new AttributeColumnDefinition(targetColumn, _calcNovaColunaTipo));
            }

            IReadOnlySet<long>? targetFids = _calcApenasSelecionados ? ObterFidsSelecionadosCamada(nomeCamadaAtiva) : null;

            await _attributeProvider.CalculateColumnAsync(targetColumn, (fid, dict) =>
            {
                var geom = ObterGeometriaFeicao(fid);
                return FieldCalculatorService.EvaluateSingle(_calcExpressao, fid, fid, dict, geom);
            }, targetFids);

            _mostrarModalCalculadora = false;
            _attributeStatusMessage = $"Coluna '{targetColumn}' calculada com sucesso.";
            await CarregarPaginaTabelaAsync();
        }
        catch (Exception ex)
        {
            _calcErroExpressao = $"Falha ao calcular: {ex.Message}";
        }
        finally
        {
            _attributeIsLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    // =========================================================================
    // DDL DE ESQUEMA (ADICIONAR / EXCLUIR COLUNA)
    // =========================================================================
    private void AbrirAdicionarColuna()
    {
        _novaColNome = "";
        _novaColTipo = AttributeDataType.String;
        _novaColTamanho = 50;
        _novaColPrecisao = 0;
        _novaColErro = null;
        _mostrarModalAddColuna = true;
    }

    private void FecharAdicionarColuna()
    {
        _mostrarModalAddColuna = false;
    }

    private async Task ExecutarAdicionarColunaAsync()
    {
        if (_attributeProvider == null || string.IsNullOrWhiteSpace(_novaColNome)) return;

        string colName = _novaColNome.Trim().ToUpperInvariant();
        if (_attributeTableColumns.Any(c => string.Equals(c.Name, colName, StringComparison.OrdinalIgnoreCase)))
        {
            _novaColErro = $"Já existe uma coluna com o nome '{colName}'.";
            return;
        }

        try
        {
            _attributeIsLoading = true;
            await _attributeProvider.AddColumnAsync(new AttributeColumnDefinition(colName, _novaColTipo, _novaColTamanho, _novaColPrecisao));
            _mostrarModalAddColuna = false;
            _attributeStatusMessage = $"Coluna '{colName}' adicionada.";
            await CarregarPaginaTabelaAsync();
        }
        catch (Exception ex)
        {
            _novaColErro = ex.Message;
        }
        finally
        {
            _attributeIsLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ConfirmarExcluirColunaAsync(string colName)
    {
        if (_attributeProvider == null) return;
        bool confirmar = await JSRuntime.InvokeAsync<bool>("confirm",
            $"Tem certeza que deseja excluir permanentemente a coluna '{colName}'?");
        if (!confirmar) return;

        try
        {
            _attributeIsLoading = true;
            await _attributeProvider.DropColumnAsync(colName);
            _attributeStatusMessage = $"Coluna '{colName}' excluída.";
            await CarregarPaginaTabelaAsync();
        }
        catch (Exception ex)
        {
            _attributeStatusMessage = $"Erro ao excluir: {ex.Message}";
        }
        finally
        {
            _attributeIsLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    // =========================================================================
    // ESTATÍSTICAS
    // =========================================================================
    private async Task AbrirEstatisticasColunaAsync(string colName)
    {
        if (_attributeProvider == null) return;
        _colunaEstatisticasAtiva = colName;
        _mostrarGavetaEstatisticas = true;
        _estatisticasResultado = null;
        StateHasChanged();

        try
        {
            _estatisticasResultado = await _attributeProvider.GetColumnStatisticsAsync(colName);
        }
        catch (Exception ex)
        {
            _attributeStatusMessage = $"Estatísticas: {ex.Message}";
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    // =========================================================================
    // EXPORTAÇÃO
    // =========================================================================
    private async Task ExportarTabelaCsvAsync(bool apenasSelecionados)
    {
        if (_attributeProvider == null) return;

        try
        {
            var sb = new StringBuilder();
            // Header
            sb.AppendLine(string.Join(";", _attributeTableColumns.Select(c => $"\"{c.Name}\"")));

            var selectedFids = apenasSelecionados ? ObterFidsSelecionadosCamada(nomeCamadaAtiva) : null;
            int total = (int)_attributeProvider.FilteredRowCount;
            int batchSize = 256;

            for (int i = 0; i < total; i += batchSize)
            {
                var rows = await _attributeProvider.GetRowsAsync(i, Math.Min(batchSize, total - i));
                foreach (var row in rows)
                {
                    if (selectedFids != null && !selectedFids.Contains(row.FID)) continue;
                    var fields = row.Values.Select(v => $"\"{v?.ToString()?.Replace("\"", "\"\"") ?? ""}\"");
                    sb.AppendLine(string.Join(";", fields));
                }
            }

            string csvContent = sb.ToString();
            byte[] bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csvContent)).ToArray();
            string base64 = Convert.ToBase64String(bytes);
            string fileName = $"{nomeCamadaAtiva}_atributos_{DateTime.Now:yyyyMMdd_HHmm}.csv";

            await JSRuntime.InvokeVoidAsync("geonexDownloadText", csvContent, fileName);
        }
        catch (Exception ex)
        {
            _attributeStatusMessage = $"Falha ao exportar CSV: {ex.Message}";
        }
    }
}
