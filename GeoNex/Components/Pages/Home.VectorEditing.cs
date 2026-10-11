using GeoNex.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.Features;
using SkiaSharp;

namespace GeoNex.Components.Pages;

public partial class Home
{
    private readonly record struct VectorFeatureKey(string LayerName, long FeatureId, long SourceFeatureId);

    private sealed class VectorFeatureEditState
    {
        public required VectorFeatureKey Key { get; init; }
        public required Geometry Baseline { get; set; }
        public required Geometry Current { get; set; }
        public IFeature? PendingFeature { get; set; }
        public bool IsDeleted { get; set; }
        public MapRenderingService.DetachedVectorFeature? DetachedFeature { get; set; }
    }

    private sealed record VectorGeometryEditAction(
        VectorFeatureKey Key, Geometry Before, Geometry After, bool BeforeDeleted = false, bool AfterDeleted = false);

    private sealed class ImagePointerPoint
    {
        public double x { get; set; }
        public double y { get; set; }
    }

    private readonly Dictionary<VectorFeatureKey, VectorFeatureEditState> _vectorFeatureEditStates = new();
    private readonly List<VectorGeometryEditAction> _vectorGeometryEditHistory = new();
    private int _vectorGeometryEditHistoryCursor;
    private VectorFeatureKey? _selectedVectorFeatureForEdit;
    private IReadOnlyList<VectorGeometryEditingService.Vertex> _selectedVectorVertices = Array.Empty<VectorGeometryEditingService.Vertex>();
    private VectorGeometryEditingService.VertexAddress? _draggedVectorVertex;
    private bool _draggingWholeVectorFeature;
    private SKPoint? _wholeFeatureDragStart;
    private Geometry? _geometryAtDragStart;
    private Geometry? _geometryDragPreview;
    private bool _vectorEditPointerPressed;
    private int _draggedVectorVertexDisplayIndex = -1;
    private int _hoveredVectorVertexDisplayIndex = -1;
    private bool _insertVectorVertexMode;
    private bool _snapVectorEditVertices = true;
    private bool _snapVectorEditEdges;
    private double _vectorMoveDeltaX;
    private double _vectorMoveDeltaY;
    private double _vectorRotationDegrees = 15;
    private double _vectorScaleFactor = 1.1;
    private string _vectorEditMessage = "Clique em uma feição vetorial local para selecionar seus vértices.";

    private VectorFeatureEditState? SelectedVectorFeatureState =>
        _selectedVectorFeatureForEdit is { } key && _vectorFeatureEditStates.TryGetValue(key, out var state)
            ? state
            : null;

    private int SelectedVectorVertexCount => SelectedVectorFeatureState is { } state
        ? VectorGeometryEditingService.GetVertices(state.Current).Count
        : 0;

    private bool CanUndoVectorGeometryEdit => _vectorGeometryEditHistoryCursor > 0;
    private bool CanRedoVectorGeometryEdit => _vectorGeometryEditHistoryCursor < _vectorGeometryEditHistory.Count;
    private bool CanRemoveHoveredVectorVertex => SelectedVectorFeatureState is not null &&
        _hoveredVectorVertexDisplayIndex >= 0 && _hoveredVectorVertexDisplayIndex < _selectedVectorVertices.Count;

    private async Task SelecionarFeicaoParaEdicaoAsync(CompiledFeature? compiledFeature, string layerName)
    {
        if (compiledFeature is null)
        {
            _selectedVectorFeatureForEdit = null;
            _selectedVectorVertices = Array.Empty<VectorGeometryEditingService.Vertex>();
            _vectorEditMessage = "Nenhuma feição encontrada nesse ponto. Clique sobre um vetor para selecionar.";
            MapService.DestacarFeicao(null);
            MapService.LimparOverlayEdicao();
            return;
        }

        Camada? layer = CamadasAtivas.FirstOrDefault(candidate =>
            string.Equals(candidate.Nome, layerName, StringComparison.Ordinal));
        if (layer is null || layer.Tipo != "Vetor")
        {
            _vectorEditMessage = "A camada selecionada não está disponível para edição.";
            return;
        }
        if (layer.FontePostgis is not null)
        {
            _vectorEditMessage = "Edição PostGIS indisponível: esta conexão ainda não identifica a chave primária real da feição. O GeoNex não usará o índice temporário como identificador.";
            _selectedVectorFeatureForEdit = null;
            _selectedVectorVertices = Array.Empty<VectorGeometryEditingService.Vertex>();
            MapService.DestacarFeicao(null);
            MapService.LimparOverlayEdicao();
            return;
        }
        if (string.IsNullOrWhiteSpace(layer.CaminhoArquivo) || !File.Exists(layer.CaminhoArquivo))
        {
            _vectorEditMessage = "A fonte local da camada não está disponível. Reconecte a camada antes de editar.";
            return;
        }
        if (compiledFeature.SourceFeatureId < 0)
        {
            _vectorEditMessage = "O provedor não forneceu um FID estável para esta feição; a camada permanece somente para visualização e edição não será iniciada.";
            return;
        }

        try
        {
            var key = new VectorFeatureKey(layerName, compiledFeature.FID, compiledFeature.SourceFeatureId);
            if (!_vectorFeatureEditStates.TryGetValue(key, out VectorFeatureEditState? state))
            {
                Geometry geometry = await Task.Run(() =>
                    VectorGeometryEditingService.ReadGeometry(layer.CaminhoArquivo, compiledFeature.SourceFeatureId, MapService.ProjetoSRS));
                state = new VectorFeatureEditState { Key = key, Baseline = geometry, Current = geometry };
                _vectorFeatureEditStates.Add(key, state);
            }

            _selectedVectorFeatureForEdit = key;
            camadaSelecionadaPainel = layerName;
            _selectedVectorVertices = VectorGeometryEditingService.GetVertices(state.Current);
            _vectorEditMessage = $"Feição {compiledFeature.SourceFeatureId} selecionada · arraste um vértice para remodelar ou o corpo para mover.";
            MapService.DestacarFeicao(compiledFeature);
            AtualizarOverlayEdicaoVetorial(state.Current, -1);
            StateHasChanged();
        }
        catch (Exception ex)
        {
            _selectedVectorFeatureForEdit = null;
            _selectedVectorVertices = Array.Empty<VectorGeometryEditingService.Vertex>();
            _vectorEditMessage = $"A feição não pôde ser preparada para edição: {MensagemPostgisSegura(ex)}";
            MapService.DestacarFeicao(null);
            MapService.LimparOverlayEdicao();
            await InvokeAsync(StateHasChanged);
        }
    }

    private void AtualizarOverlayEdicaoVetorial(Geometry geometry, int activeVertexIndex)
    {
        if (_selectedVectorFeatureForEdit is not { } key)
        {
            MapService.LimparOverlayEdicao();
            return;
        }

        IReadOnlyList<VectorGeometryEditingService.Vertex> vertices = VectorGeometryEditingService.GetVertices(geometry);
        _selectedVectorVertices = vertices;
        var localVertices = new SKPoint[vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            VectorGeometryEditingService.Vertex vertex = vertices[i];
            localVertices[i] = new SKPoint(
                (float)(vertex.X - MapService.OffsetMundoX),
                (float)(MapService.OffsetMundoY - vertex.Y));
        }

        SKPath? preview = null;
        try
        {
            CompiledFeature compiled = PostgisGeometryCompiler.Compile(
                geometry, key.FeatureId, key.LayerName, MapService.OffsetMundoX, MapService.OffsetMundoY);
            preview = compiled.Path;
        }
        catch (Exception ex)
        {
            _vectorEditMessage = $"A prévia da geometria não pôde ser construída: {ex.Message}";
        }
        MapService.DefinirOverlayEdicao(preview, localVertices, activeVertexIndex);
    }

    private void AtualizarHoverVerticeEdicao(SKPoint cursor, SKMatrix matrix, SKPoint? pointerStart)
    {
        if (SelectedVectorFeatureState is not { } state || _selectedVectorVertices.Count == 0) return;
        double tolerance = 10d / Math.Max(0.0001, Math.Abs(matrix.ScaleX));
        double toleranceSquared = tolerance * tolerance;
        int nearest = -1;
        double nearestDistance = toleranceSquared;
        SKPoint reference = _vectorEditPointerPressed && _draggedVectorVertex is null && pointerStart is { } start
            ? start
            : cursor;
        for (int i = 0; i < _selectedVectorVertices.Count; i++)
        {
            var vertex = _selectedVectorVertices[i];
            double dx = reference.X - (vertex.X - MapService.OffsetMundoX);
            double dy = reference.Y - (MapService.OffsetMundoY - vertex.Y);
            double distance = dx * dx + dy * dy;
            if (distance <= nearestDistance)
            {
                nearest = i;
                nearestDistance = distance;
            }
        }

        if (!_vectorEditPointerPressed)
        {
            _hoveredVectorVertexDisplayIndex = nearest;
            MapService.DefinirIndiceVerticeEdicao(nearest);
            return;
        }
        if (_draggedVectorVertex is null && !_draggingWholeVectorFeature && nearest >= 0)
        {
            _draggedVectorVertexDisplayIndex = nearest;
            _draggedVectorVertex = _selectedVectorVertices[nearest].Address;
            _geometryAtDragStart = state.Current;
            _geometryDragPreview = state.Current;
            JSRuntime.InvokeVoidAsyncSafe("mapEngine.setVertexEditDragging", true);
            MapService.DefinirIndiceVerticeEdicao(nearest);
        }
        else if (_draggedVectorVertex is null && !_draggingWholeVectorFeature && pointerStart is { } featurePointerStart)
        {
            double worldX = featurePointerStart.X + MapService.OffsetMundoX;
            double worldY = MapService.OffsetMundoY - featurePointerStart.Y;
            float scale = Math.Max(0.0001f, Math.Abs(matrix.ScaleX));
            CompiledFeature? hit = MapService.DispararRaycast(worldY, worldX, 6d / scale, out string hitLayer);
            if (hit is not null && hit.FID == state.Key.FeatureId &&
                string.Equals(hitLayer, state.Key.LayerName, StringComparison.Ordinal))
            {
                _draggingWholeVectorFeature = true;
                _wholeFeatureDragStart = featurePointerStart;
                _geometryAtDragStart = state.Current;
                _geometryDragPreview = state.Current;
                _draggedVectorVertexDisplayIndex = -1;
                JSRuntime.InvokeVoidAsyncSafe("mapEngine.setVertexEditDragging", true);
                MapService.DefinirIndiceVerticeEdicao(-1);
            }
        }

        if (_draggingWholeVectorFeature && _wholeFeatureDragStart is { } featureStart &&
            _geometryAtDragStart is { } featureGeometry)
        {
            try
            {
                MapService.PontoCursorSnap = null;
                MapService.PontoCursorSnapTipo = null;
                _geometryDragPreview = VectorGeometryEditingService.Translate(
                    featureGeometry, cursor.X - featureStart.X, featureStart.Y - cursor.Y);
                AtualizarOverlayEdicaoVetorial(_geometryDragPreview, -1);
                _vectorEditMessage = "Prévia da feição · solte para mover toda a geometria.";
            }
            catch (InvalidOperationException ex)
            {
                _vectorEditMessage = ex.Message;
            }
            return;
        }
        if (_draggedVectorVertex is not { } address || _geometryAtDragStart is not { } origin) return;

        try
        {
            SKPoint adjustedCursor = AjustarCursorSnapEdicao(cursor, matrix);
            _geometryDragPreview = VectorGeometryEditingService.MoveVertex(
                origin,
                address,
                adjustedCursor.X + MapService.OffsetMundoX,
                MapService.OffsetMundoY - adjustedCursor.Y);
            AtualizarOverlayEdicaoVetorial(_geometryDragPreview, _draggedVectorVertexDisplayIndex);
            _vectorEditMessage = "Prévia do vértice · solte para aplicar ao projeto.";
        }
        catch (InvalidOperationException ex)
        {
            _vectorEditMessage = ex.Message;
        }
    }

    private async Task FinalizarArrasteVerticeEdicaoAsync(double clientX, double clientY)
    {
        _vectorEditPointerPressed = false;
        JSRuntime.InvokeVoidAsyncSafe("mapEngine.setVertexEditDragging", false);
        if ((!_draggingWholeVectorFeature && _draggedVectorVertex is null) || _geometryAtDragStart is not { } before ||
            _selectedVectorFeatureForEdit is not { } key || !_vectorFeatureEditStates.TryGetValue(key, out var state))
        {
            LimparEstadoArrasteVertice();
            return;
        }

        Geometry? after = _geometryDragPreview;
        try
        {
            try
            {
                ImagePointerPoint pointer = await JSRuntime.InvokeAsync<ImagePointerPoint>(
                    "mapEngine.obterPontoImagem", CancellationToken.None, new object?[] { clientX, clientY });
                double[]? displayedMatrix = await JSRuntime.InvokeAsync<double[]>(
                    "GeoNexGraphics.obterMatrizApresentada", Array.Empty<object?>());
                SKMatrix matrix = UsarMatrizApresentada(displayedMatrix, ObterMatrizMatematica());
                if (TentarMapearPontoTelaParaLocal(pointer.x, pointer.y, displayedMatrix, matrix, out SKPoint finalPosition))
                {
                    if (_draggingWholeVectorFeature && _wholeFeatureDragStart is { } featureStart)
                    {
                        after = VectorGeometryEditingService.Translate(
                            before, finalPosition.X - featureStart.X, featureStart.Y - finalPosition.Y);
                    }
                    else if (_draggedVectorVertex is { } address)
                    {
                        finalPosition = AjustarCursorSnapEdicao(finalPosition, matrix);
                        after = VectorGeometryEditingService.MoveVertex(
                            before, address,
                            finalPosition.X + MapService.OffsetMundoX,
                            MapService.OffsetMundoY - finalPosition.Y);
                    }
                }
            }
            catch (Exception ex) when (ex is Microsoft.JSInterop.JSException or InvalidOperationException)
            {
                // The last validated preview remains a safe fallback if the browser bridge
                // is already shutting down as the pointer is released.
            }

            if (after is null || before.EqualsExact(after))
            {
                _vectorEditMessage = "A geometria voltou à posição original; nenhuma alteração foi aplicada.";
                return;
            }

            await PublicarGeometriaEdicaoAsync(state, after);
            RegistrarAcaoGeometria(key, before, after);
            _vectorEditMessage = $"Geometria da feição {key.FeatureId} alterada · salve as edições para gravar na fonte.";
        }
        catch (Exception ex)
        {
            _vectorEditMessage = $"A alteração não foi aplicada: {MensagemPostgisSegura(ex)}";
        }
        finally
        {
            LimparEstadoArrasteVertice();
            if (_selectedVectorFeatureForEdit == key && _vectorFeatureEditStates.TryGetValue(key, out var selected))
                AtualizarOverlayEdicaoVetorial(selected.Current, -1);
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task PublicarGeometriaEdicaoAsync(VectorFeatureEditState state, Geometry geometry)
        => await PublicarEstadoGeometriaEdicaoAsync(state, geometry, isDeleted: false);

    private async Task PublicarEstadoGeometriaEdicaoAsync(
        VectorFeatureEditState state, Geometry geometry, bool isDeleted)
    {
        Geometry anterior = state.Current;
        bool estavaExcluida = state.IsDeleted;
        IFeature? pendingAnterior = state.PendingFeature;
        Geometry? geometriaPendenteAnterior = pendingAnterior?.Geometry;
        string indicadorExclusaoAnterior = pendingAnterior is not null && pendingAnterior.Attributes?.Exists(VectorEditPersistenceService.DeleteFeatureAttribute) == true
            ? Convert.ToString(pendingAnterior.Attributes[VectorEditPersistenceService.DeleteFeatureAttribute], System.Globalization.CultureInfo.InvariantCulture) ?? "NAO"
            : "NAO";
        bool recursoRemovidoNestaOperacao = false;
        bool recursoRestauradoNestaOperacao = false;
        state.Current = geometry;
        state.IsDeleted = isDeleted;
        PrepararFeicaoPendenteEdicao(state);
        try
        {
            using IDisposable? pausa = _server is null ? null : await _server.PauseRenderingAsync();
            if (isDeleted)
            {
                if (state.DetachedFeature is null)
                {
                    state.DetachedFeature = MapService.RemoverFeicaoVetorial(state.Key.LayerName, state.Key.FeatureId);
                    recursoRemovidoNestaOperacao = true;
                }
                MapService.DestacarFeicao(null);
                MapService.LimparOverlayEdicao();
            }
            else if (state.DetachedFeature is { } detached)
            {
                MapService.RestaurarFeicaoVetorial(state.Key.LayerName, detached);
                state.DetachedFeature = null;
                recursoRestauradoNestaOperacao = true;
                if (!detached.Feature.EnvelopeWorld.IsNull && !geometry.EqualsExact(state.Baseline))
                    MapService.AtualizarGeometriaVetorial(state.Key.LayerName, state.Key.FeatureId, geometry);
            }
            else
            {
                MapService.AtualizarGeometriaVetorial(state.Key.LayerName, state.Key.FeatureId, geometry);
            }

            if (!isDeleted && _selectedVectorFeatureForEdit == state.Key &&
                MapService.FeaturesPorCamada.TryGetValue(state.Key.LayerName, out var features))
            {
                CompiledFeature? updated = features.FirstOrDefault(feature => feature.FID == state.Key.FeatureId);
                MapService.DestacarFeicao(updated);
            }
        }
        catch
        {
            state.Current = anterior;
            state.IsDeleted = estavaExcluida;
            if (recursoRemovidoNestaOperacao && state.DetachedFeature is { } removed)
            {
                try
                {
                    MapService.RestaurarFeicaoVetorial(state.Key.LayerName, removed);
                    state.DetachedFeature = null;
                }
                catch (Exception rollbackError) { Console.Error.WriteLine($"Falha ao reverter exclusão vetorial temporária: {rollbackError.Message}"); }
            }
            else if (recursoRestauradoNestaOperacao && state.DetachedFeature is null)
            {
                try { state.DetachedFeature = MapService.RemoverFeicaoVetorial(state.Key.LayerName, state.Key.FeatureId); }
                catch (Exception rollbackError) { Console.Error.WriteLine($"Falha ao reverter restauração vetorial temporária: {rollbackError.Message}"); }
            }
            if (pendingAnterior is null)
                RemoverFeicaoPendenteEdicao(state);
            else
            {
                state.PendingFeature = pendingAnterior;
                if (geometriaPendenteAnterior is not null) pendingAnterior.Geometry = geometriaPendenteAnterior;
                if (pendingAnterior.Attributes?.Exists(VectorEditPersistenceService.DeleteFeatureAttribute) == true)
                    pendingAnterior.Attributes[VectorEditPersistenceService.DeleteFeatureAttribute] = indicadorExclusaoAnterior;
            }
            if (_selectedVectorFeatureForEdit == state.Key && !state.IsDeleted)
                AtualizarOverlayEdicaoVetorial(state.Current, -1);
            throw;
        }
        if (_selectedVectorFeatureForEdit == state.Key && !isDeleted)
            AtualizarOverlayEdicaoVetorial(state.Current, -1);
        AtualizarFlagEdicoesPendentes();
    }

    private void PrepararFeicaoPendenteEdicao(VectorFeatureEditState state)
    {
        if (!state.IsDeleted && state.Current.EqualsExact(state.Baseline))
        {
            RemoverFeicaoPendenteEdicao(state);
            return;
        }

        if (state.PendingFeature is not null)
        {
            state.PendingFeature.Geometry = state.Current;
            state.PendingFeature.Attributes![VectorEditPersistenceService.DeleteFeatureAttribute] = state.IsDeleted ? "SIM" : "NAO";
            return;
        }

        if (!MapService.FeicoesOriginais.TryGetValue(state.Key.LayerName, out FeatureCollection? collection))
        {
            collection = new FeatureCollection();
            MapService.FeicoesOriginais[state.Key.LayerName] = collection;
        }
        var attributes = new AttributesTable
        {
            { VectorEditPersistenceService.ExistingFeatureIdAttribute, state.Key.SourceFeatureId },
            { VectorEditPersistenceService.DeleteFeatureAttribute, state.IsDeleted ? "SIM" : "NAO" },
            { "_SYS_GRAVADO", "NAO" }
        };
        state.PendingFeature = new Feature(state.Current, attributes);
        collection.Add(state.PendingFeature);
    }

    private void RemoverFeicaoPendenteEdicao(VectorFeatureEditState state)
    {
        if (state.PendingFeature is null) return;
        if (MapService.FeicoesOriginais.TryGetValue(state.Key.LayerName, out FeatureCollection? collection))
            collection.Remove(state.PendingFeature);
        state.PendingFeature = null;
    }

    private async Task DesfazerEdicaoVetorialAsync()
    {
        if (_vectorGeometryEditHistoryCursor <= 0 || _vectorEditPointerPressed) return;
        VectorGeometryEditAction action = _vectorGeometryEditHistory[_vectorGeometryEditHistoryCursor - 1];
        if (!_vectorFeatureEditStates.TryGetValue(action.Key, out var state)) return;
        await PublicarEstadoGeometriaEdicaoAsync(state, action.Before, action.BeforeDeleted);
        _vectorGeometryEditHistoryCursor--;
        _vectorEditMessage = $"Desfeito · feição {action.Key.SourceFeatureId}.";
        if (state.IsDeleted && _selectedVectorFeatureForEdit == action.Key)
            LimparSelecaoEdicaoVetorial();
        else if (!state.IsDeleted && action.AfterDeleted && !action.BeforeDeleted)
            SelecionarFeicaoRestauradaParaEdicao(state);
        else if (_selectedVectorFeatureForEdit == action.Key)
            AtualizarOverlayEdicaoVetorial(state.Current, -1);
        await InvokeAsync(StateHasChanged);
    }

    private async Task ExcluirFeicaoSelecionadaAsync()
    {
        if (SelectedVectorFeatureState is not { } state || state.IsDeleted) return;
        try
        {
            Geometry current = state.Current;
            await PublicarEstadoGeometriaEdicaoAsync(state, current, isDeleted: true);
            RegistrarAcaoEdicao(state.Key, current, current, beforeDeleted: false, afterDeleted: true);
            if (_selectedVectorFeatureForEdit == state.Key)
            {
                _selectedVectorFeatureForEdit = null;
                _selectedVectorVertices = Array.Empty<VectorGeometryEditingService.Vertex>();
                _hoveredVectorVertexDisplayIndex = -1;
            }
            _vectorEditMessage = $"Feição {state.Key.SourceFeatureId} marcada para exclusão. Use Desfazer ou salve as edições para confirmar.";
        }
        catch (Exception ex)
        {
            _vectorEditMessage = $"A feição não foi excluída: {MensagemPostgisSegura(ex)}";
        }
        await InvokeAsync(StateHasChanged);
    }

    private void SelecionarFeicaoRestauradaParaEdicao(VectorFeatureEditState state)
    {
        _selectedVectorFeatureForEdit = state.Key;
        camadaSelecionadaPainel = state.Key.LayerName;
        _selectedVectorVertices = VectorGeometryEditingService.GetVertices(state.Current);
        if (MapService.FeaturesPorCamada.TryGetValue(state.Key.LayerName, out List<CompiledFeature>? features))
            MapService.DestacarFeicao(features.FirstOrDefault(feature => feature.FID == state.Key.FeatureId));
        AtualizarOverlayEdicaoVetorial(state.Current, -1);
    }

    private void AlternarModoInserirVertice()
    {
        _insertVectorVertexMode = !_insertVectorVertexMode;
        _vectorEditMessage = _insertVectorVertexMode
            ? "Inserção ativa · clique próximo ao meio de uma aresta da feição selecionada."
            : "Inserção de vértices desativada.";
    }

    private async Task InserirVerticeSelecionadoAsync(double x, double y, double pixelScale)
    {
        if (SelectedVectorFeatureState is not { } state) return;
        Geometry before = state.Current;
        try
        {
            double tolerance = 10d / Math.Max(0.0001, Math.Abs(pixelScale));
            VectorGeometryEditingService.VertexInsertion insertion =
                VectorGeometryEditingService.InsertVertexAtPoint(before, x, y, tolerance);
            await PublicarGeometriaEdicaoAsync(state, insertion.Geometry);
            RegistrarAcaoGeometria(state.Key, before, insertion.Geometry);
            _vectorEditMessage = $"Vértice inserido na feição {state.Key.SourceFeatureId} · salve as edições para gravar na fonte.";
            int displayIndex = -1;
            for (int i = 0; i < _selectedVectorVertices.Count; i++)
            {
                if (_selectedVectorVertices[i].Address == insertion.Address)
                {
                    displayIndex = i;
                    break;
                }
            }
            _hoveredVectorVertexDisplayIndex = displayIndex;
            MapService.DefinirIndiceVerticeEdicao(displayIndex);
        }
        catch (Exception ex)
        {
            _vectorEditMessage = ex.Message;
        }
        await InvokeAsync(StateHasChanged);
    }

    private async Task RemoverVerticeSelecionadoAsync()
    {
        if (SelectedVectorFeatureState is not { } state || !CanRemoveHoveredVectorVertex) return;
        Geometry before = state.Current;
        VectorGeometryEditingService.VertexAddress address = _selectedVectorVertices[_hoveredVectorVertexDisplayIndex].Address;
        try
        {
            Geometry after = VectorGeometryEditingService.RemoveVertex(before, address);
            await PublicarGeometriaEdicaoAsync(state, after);
            RegistrarAcaoGeometria(state.Key, before, after);
            _hoveredVectorVertexDisplayIndex = -1;
            MapService.DefinirIndiceVerticeEdicao(-1);
            _vectorEditMessage = $"Vértice removido da feição {state.Key.SourceFeatureId} · salve as edições para gravar na fonte.";
        }
        catch (Exception ex)
        {
            _vectorEditMessage = ex.Message;
        }
        await InvokeAsync(StateHasChanged);
    }

    private void RegistrarAcaoGeometria(VectorFeatureKey key, Geometry before, Geometry after)
        => RegistrarAcaoEdicao(key, before, after, beforeDeleted: false, afterDeleted: false);

    private void RegistrarAcaoEdicao(
        VectorFeatureKey key, Geometry before, Geometry after, bool beforeDeleted, bool afterDeleted)
    {
        if (_vectorGeometryEditHistoryCursor < _vectorGeometryEditHistory.Count)
            _vectorGeometryEditHistory.RemoveRange(
                _vectorGeometryEditHistoryCursor,
                _vectorGeometryEditHistory.Count - _vectorGeometryEditHistoryCursor);
        _vectorGeometryEditHistory.Add(new VectorGeometryEditAction(key, before, after, beforeDeleted, afterDeleted));
        _vectorGeometryEditHistoryCursor++;
    }

    private Task AplicarDeslocamentoPrecisoAsync() => AplicarTransformacaoSelecionadaAsync(
        geometry => VectorGeometryEditingService.Translate(geometry, _vectorMoveDeltaX, _vectorMoveDeltaY),
        "Deslocamento aplicado à feição");

    private Task RotacionarFeicaoAsync() => AplicarTransformacaoSelecionadaAsync(
        geometry => VectorGeometryEditingService.RotateAroundCentroid(geometry, _vectorRotationDegrees),
        $"Rotação de {_vectorRotationDegrees:0.###}° aplicada ao redor do centroide");

    private Task EscalarFeicaoAsync() => AplicarTransformacaoSelecionadaAsync(
        geometry => VectorGeometryEditingService.ScaleAroundCentroid(geometry, _vectorScaleFactor),
        $"Escala {_vectorScaleFactor:0.###} aplicada ao redor do centroide");

    private async Task AplicarTransformacaoSelecionadaAsync(Func<Geometry, Geometry> transform, string successMessage)
    {
        if (SelectedVectorFeatureState is not { } state) return;
        Geometry before = state.Current;
        try
        {
            Geometry after = transform(before);
            if (before.EqualsExact(after))
            {
                _vectorEditMessage = "A transformação não alterou a geometria.";
                return;
            }
            await PublicarGeometriaEdicaoAsync(state, after);
            RegistrarAcaoGeometria(state.Key, before, after);
            AtualizarOverlayEdicaoVetorial(state.Current, -1);
            _vectorEditMessage = $"{successMessage} · salve as edições para gravar na fonte.";
        }
        catch (Exception ex)
        {
            _vectorEditMessage = $"A transformação não foi aplicada: {ex.Message}";
        }
        await InvokeAsync(StateHasChanged);
    }

    private async Task RefazerEdicaoVetorialAsync()
    {
        if (_vectorGeometryEditHistoryCursor >= _vectorGeometryEditHistory.Count || _vectorEditPointerPressed) return;
        VectorGeometryEditAction action = _vectorGeometryEditHistory[_vectorGeometryEditHistoryCursor];
        if (!_vectorFeatureEditStates.TryGetValue(action.Key, out var state)) return;
        await PublicarEstadoGeometriaEdicaoAsync(state, action.After, action.AfterDeleted);
        _vectorGeometryEditHistoryCursor++;
        _vectorEditMessage = $"Refeito · feição {action.Key.SourceFeatureId}.";
        if (state.IsDeleted && _selectedVectorFeatureForEdit == action.Key)
            LimparSelecaoEdicaoVetorial();
        else if (_selectedVectorFeatureForEdit == action.Key)
            AtualizarOverlayEdicaoVetorial(state.Current, -1);
        await InvokeAsync(StateHasChanged);
    }

    private void LimparSelecaoEdicaoVetorial()
    {
        if (_vectorEditPointerPressed) CancelarArrasteVerticeEdicao();
        _selectedVectorFeatureForEdit = null;
        _selectedVectorVertices = Array.Empty<VectorGeometryEditingService.Vertex>();
        _vectorEditMessage = "Clique em uma feição vetorial local para selecionar seus vértices.";
        MapService.DestacarFeicao(null);
        MapService.LimparOverlayEdicao();
    }

    private void LimparEstadoEdicaoDaCamada(string layerName)
    {
        var removedKeys = _vectorFeatureEditStates.Keys
            .Where(key => string.Equals(key.LayerName, layerName, StringComparison.Ordinal))
            .ToHashSet();
        if (_selectedVectorFeatureForEdit is { } selected && removedKeys.Contains(selected))
        {
            _selectedVectorFeatureForEdit = null;
            _selectedVectorVertices = Array.Empty<VectorGeometryEditingService.Vertex>();
            MapService.LimparOverlayEdicao();
        }
        foreach (VectorFeatureKey key in removedKeys)
        {
            VectorFeatureEditState state = _vectorFeatureEditStates[key];
            state.DetachedFeature?.Feature.Path?.Dispose();
            _vectorFeatureEditStates.Remove(key);
        }

        int appliedRemaining = _vectorGeometryEditHistory
            .Take(_vectorGeometryEditHistoryCursor)
            .Count(action => !removedKeys.Contains(action.Key));
        _vectorGeometryEditHistory.RemoveAll(action => removedKeys.Contains(action.Key));
        _vectorGeometryEditHistoryCursor = appliedRemaining;
    }

    private void RegistrarEdicoesGeometricasSalvas()
    {
        var savedKeys = _vectorFeatureEditStates.Values
            .Where(state => state.PendingFeature is not null && VectorEditPersistenceService.IsPersisted(state.PendingFeature))
            .Select(state => state.Key)
            .ToHashSet();
        if (savedKeys.Count == 0) return;

        foreach (VectorFeatureKey key in savedKeys)
        {
            VectorFeatureEditState state = _vectorFeatureEditStates[key];
            RemoverFeicaoPendenteEdicao(state);
            if (state.IsDeleted)
            {
                state.DetachedFeature?.Feature.Path?.Dispose();
                _vectorFeatureEditStates.Remove(key);
                if (_selectedVectorFeatureForEdit == key)
                {
                    _selectedVectorFeatureForEdit = null;
                    _selectedVectorVertices = Array.Empty<VectorGeometryEditingService.Vertex>();
                }
            }
            else
            {
                state.Baseline = state.Current;
            }
        }

        int appliedRemaining = _vectorGeometryEditHistory
            .Take(_vectorGeometryEditHistoryCursor)
            .Count(action => !savedKeys.Contains(action.Key));
        _vectorGeometryEditHistory.RemoveAll(action => savedKeys.Contains(action.Key));
        _vectorGeometryEditHistoryCursor = appliedRemaining;
    }

    private async Task RecarregarCamadaVetorialAposExclusaoAsync(
        Camada layer, IReadOnlyCollection<IFeature>? exclusoesAindaPendentes = null)
    {
        if (string.IsNullOrWhiteSpace(layer.CaminhoArquivo) || !File.Exists(layer.CaminhoArquivo))
            throw new InvalidOperationException("Não foi possível atualizar os FIDs: a fonte vetorial não está disponível.");

        IFeature[] pendentes = exclusoesAindaPendentes?.Where(feature => !VectorEditPersistenceService.IsPersisted(feature)).ToArray()
            ?? Array.Empty<IFeature>();
        using IDisposable? pausa = _server is null ? null : await _server.PauseRenderingAsync();
        LimparEstadoEdicaoDaCamada(layer.Nome);
        MapService.FeicoesOriginais[layer.Nome] = new FeatureCollection();
        MapService.RemoveVectorResources(layer.Nome);

        string sourcePath = layer.CaminhoArquivo;
        await Task.Run(() =>
        {
            string compiledPath = ProjetoService.CompilarParaShapefileNativo(
                sourcePath, out string? sourceFidMappingPath, out bool sourceFidsPreserved);
            ProjetoService.CarregarShapefileParaMotorMapas(
                compiledPath, layer.Nome, MapService,
                gravarLogPerformance: false,
                sourceFidMappingPath: sourceFidMappingPath,
                sourceFidsAreNative: sourceFidsPreserved);
        });
        RecalcularLimitesGlobaisVetoriais();

        var pendingCollection = new FeatureCollection();
        MapService.FeicoesOriginais[layer.Nome] = pendingCollection;
        foreach (IFeature pendingFeature in pendentes)
        {
            if (!VectorEditPersistenceService.TryGetExistingFeatureId(pendingFeature, out long featureId))
                throw new InvalidDataException("Uma exclusão pendente perdeu o FID da fonte durante a atualização da camada.");
            CompiledFeature? compiled = MapService.FeaturesPorCamada.TryGetValue(layer.Nome, out List<CompiledFeature>? loaded)
                ? loaded.FirstOrDefault(feature => feature.SourceFeatureId == featureId)
                : null;
            if (compiled is null)
                throw new InvalidDataException($"A feição {featureId} marcada para exclusão não foi encontrada após atualizar a camada.");

            Geometry geometry = await Task.Run(() => VectorGeometryEditingService.ReadGeometry(
                sourcePath, featureId, MapService.ProjetoSRS));
            var key = new VectorFeatureKey(layer.Nome, compiled.FID, featureId);
            var state = new VectorFeatureEditState
            {
                Key = key,
                Baseline = geometry,
                Current = geometry,
                PendingFeature = pendingFeature,
                IsDeleted = true
            };
            pendingFeature.Geometry = geometry;
            if (pendingFeature.Attributes?.Exists(VectorEditPersistenceService.DeleteFeatureAttribute) == true)
                pendingFeature.Attributes[VectorEditPersistenceService.DeleteFeatureAttribute] = "SIM";
            _vectorFeatureEditStates[key] = state;
            state.DetachedFeature = MapService.RemoverFeicaoVetorial(layer.Nome, compiled.FID);
            pendingCollection.Add(pendingFeature);
            RegistrarAcaoEdicao(key, geometry, geometry, beforeDeleted: false, afterDeleted: true);
        }

        AtualizarFlagEdicoesPendentes();
        MapService.RequestRedraw();
    }

    private void AtualizarFlagEdicoesPendentes()
    {
        temEdicoesPendentes = MapService.FeicoesOriginais.Values
            .SelectMany(collection => collection)
            .Any(feature => !VectorEditPersistenceService.IsPersisted(feature));
    }

    private SKPoint AjustarCursorSnapEdicao(SKPoint cursor, SKMatrix matrix)
    {
        if (_selectedVectorFeatureForEdit is not { } key || !(_snapVectorEditVertices || _snapVectorEditEdges))
        {
            MapService.PontoCursorSnap = null;
            MapService.PontoCursorSnapTipo = null;
            return cursor;
        }

        float tolerance = 10f / Math.Max(0.0001f, Math.Abs(matrix.ScaleX));
        SKPoint? snap = MapService.EncontrarVerticeProximo(
            cursor, tolerance,
            _snapVectorEditVertices,
            _snapVectorEditEdges,
            ignorarCamada: key.LayerName,
            ignorarFid: key.FeatureId);
        MapService.PontoCursorSnap = snap;
        if (!snap.HasValue) MapService.PontoCursorSnapTipo = null;
        return snap ?? cursor;
    }

    private void LimparEstadoArrasteVertice()
    {
        _draggedVectorVertex = null;
        _draggingWholeVectorFeature = false;
        _wholeFeatureDragStart = null;
        _geometryAtDragStart = null;
        _geometryDragPreview = null;
        _draggedVectorVertexDisplayIndex = -1;
        if (!_vectorEditPointerPressed) _hoveredVectorVertexDisplayIndex = -1;
        MapService.DefinirIndiceVerticeEdicao(-1);
    }

    private void CancelarArrasteVerticeEdicao()
    {
        _vectorEditPointerPressed = false;
        JSRuntime.InvokeVoidAsyncSafe("mapEngine.setVertexEditDragging", false);
        LimparEstadoArrasteVertice();
        if (SelectedVectorFeatureState is { } state) AtualizarOverlayEdicaoVetorial(state.Current, -1);
    }

    private void ProcessarMovimentoEdicaoVetorial(SKPoint cursor, SKMatrix matrix, SKPoint? pointerStart)
    {
        if (SelectedVectorFeatureState is null) return;
        AtualizarHoverVerticeEdicao(cursor, matrix, pointerStart);
    }
}
