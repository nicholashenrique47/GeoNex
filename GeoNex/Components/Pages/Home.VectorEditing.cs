using GeoNex.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.Features;
using NetTopologySuite.Index.Strtree;
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
        public bool IsNewFeature { get; init; }
        public MapRenderingService.DetachedVectorFeature? DetachedFeature { get; set; }
    }

    private sealed record VectorGeometryEditAction(
        Guid GroupId, VectorFeatureKey Key, Geometry Before, Geometry After, bool BeforeDeleted = false, bool AfterDeleted = false,
        string? GroupLabel = null);

    private sealed class ImagePointerPoint
    {
        public double x { get; set; }
        public double y { get; set; }
    }

    private readonly Dictionary<VectorFeatureKey, VectorFeatureEditState> _vectorFeatureEditStates = new();
    private readonly List<VectorFeatureKey> _vectorFeatureEditSelection = new();
    private long _nextTemporaryVectorFeatureId = -1;
    private bool _vectorCutMode;
    private bool _vectorRingMode;
    private bool _vectorReshapeMode;
    private bool _vectorFeatureSelectionMode;
    private bool _vectorMoveFeatureMode;
    private string _vectorFeatureSelectionShape = "rectangle";
    private bool _reshapePreviousEdgeSnap;
    private bool _reshapeEdgeSnapSaved;
    private readonly List<VectorGeometryEditAction> _vectorGeometryEditHistory = new();
    private int _vectorGeometryEditHistoryCursor;
    private VectorFeatureKey? _selectedVectorFeatureForEdit;
    private IReadOnlyList<VectorGeometryEditingService.Vertex> _selectedVectorVertices = Array.Empty<VectorGeometryEditingService.Vertex>();
    private VectorGeometryEditingService.FlatVertexSpatialIndex? _selectedVectorVertexSpatialIndex;
    private VectorGeometryEditingService.VertexAddress? _draggedVectorVertex;
    private bool _draggingWholeVectorFeature;
    private SKPoint? _wholeFeatureDragStart;
    private Geometry? _geometryAtDragStart;
    private Geometry? _geometryDragPreview;
    private bool _vectorEditPointerPressed;
    private bool _vectorEditPlacementArmed;
    private bool _vectorEditCommitOnRelease;
    private string? _vectorGeometryValidationMessage;
    private int _draggedVectorVertexDisplayIndex = -1;
    private int _hoveredVectorVertexDisplayIndex = -1;
    private int _selectedVectorVertexDisplayIndex = -1;
    private readonly List<VectorGeometryEditingService.VertexAddress> _selectedVectorVertexAddresses = new();
    private IReadOnlyList<VectorGeometryEditingService.VertexAddress> _draggedVectorVertexAddresses = Array.Empty<VectorGeometryEditingService.VertexAddress>();
    private readonly HashSet<long> _snapIgnoredFeatureIds = new();
    private Coordinate? _draggedVectorVertexOriginal;
    private double? _vectorVertexCoordinateX;
    private double? _vectorVertexCoordinateY;
    private bool _insertVectorVertexMode;
    private bool _snapVectorEditVertices = true;
    private bool _snapVectorEditEdges;
    private bool _snapVectorEditMidpoints;
    private bool _snapVectorEditIntersections;
    private bool _propagateSelectedSharedVertices;
    private bool _vectorEditOperationInProgress;
    private double _vectorMoveDeltaX;
    private double _vectorMoveDeltaY;
    private double _vectorRotationDegrees = 15;
    private double _vectorScaleFactor = 1.1;
    private double _vectorSimplificationTolerance = 1;
    private double _vectorOffsetDistance = 1;
    private string _vectorEditMessage = "Clique em uma feição vetorial local para selecionar seus vértices.";

    private VectorFeatureEditState? SelectedVectorFeatureState =>
        _selectedVectorFeatureForEdit is { } key && _vectorFeatureEditStates.TryGetValue(key, out var state)
            ? state
            : null;

    private string IdentificadorFeicaoExibicao(VectorFeatureKey key) =>
        _vectorFeatureEditStates.TryGetValue(key, out VectorFeatureEditState? state) && state.IsNewFeature
            ? $"temporária {-key.FeatureId}"
            : key.SourceFeatureId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private int SelectedVectorVertexCount => _selectedVectorVertices.Count;

    private void DefinirVerticesSelecionados(IReadOnlyList<VectorGeometryEditingService.Vertex> vertices)
    {
        _selectedVectorVertices = vertices;
        _selectedVectorVertexSpatialIndex = VectorGeometryEditingService.FlatVertexSpatialIndex.TryBuild(vertices);
    }

    private void LimparVerticesSelecionados()
    {
        _selectedVectorVertices = Array.Empty<VectorGeometryEditingService.Vertex>();
        _selectedVectorVertexSpatialIndex = null;
    }

    private bool CanUndoVectorGeometryEdit => !_vectorEditOperationInProgress && _vectorGeometryEditHistoryCursor > 0 &&
        GrupoHistoricoPertenceCamadaAtiva(_vectorGeometryEditHistoryCursor - 1);
    private bool CanRedoVectorGeometryEdit => !_vectorEditOperationInProgress && _vectorGeometryEditHistoryCursor < _vectorGeometryEditHistory.Count &&
        GrupoHistoricoPertenceCamadaAtiva(_vectorGeometryEditHistoryCursor);
    private bool GrupoHistoricoPertenceCamadaAtiva(int indiceAcao)
    {
        if (!CamadaAtivaVetorialDisponivel || string.IsNullOrWhiteSpace(_camadaAtivaFerramentas) ||
            indiceAcao < 0 || indiceAcao >= _vectorGeometryEditHistory.Count) return false;
        Guid grupo = _vectorGeometryEditHistory[indiceAcao].GroupId;
        return _vectorGeometryEditHistory
            .Where(action => action.GroupId == grupo)
            .All(action => string.Equals(action.Key.LayerName, _camadaAtivaFerramentas, StringComparison.OrdinalIgnoreCase));
    }
    private bool CanApplyVectorVertexCoordinates => !_vectorEditOperationInProgress && SelectedVectorFeatureState is not null &&
        _ferramentaAtiva == ModoFerramenta.EdicaoVertice && !_vectorFeatureSelectionMode && !_vectorMoveFeatureMode &&
        _selectedVectorVertexDisplayIndex >= 0 && _selectedVectorVertexDisplayIndex < _selectedVectorVertices.Count &&
        _vectorVertexCoordinateX.HasValue && _vectorVertexCoordinateY.HasValue;
    private bool CanRepairSelectedVectorGeometry => !_vectorEditOperationInProgress && SelectedVectorFeatureState is
        { IsDeleted: false, IsNewFeature: false } && _vectorGeometryValidationMessage is not null;
    private bool CanCutSelectedVectorGeometry => !_vectorEditOperationInProgress && SelectedVectorFeatureState is
        { IsDeleted: false, Current: Polygon or MultiPolygon or LineString or MultiLineString };
    private bool CanEditSelectedPolygonRings => !_vectorEditOperationInProgress && SelectedVectorFeatureState is
        { IsDeleted: false, IsNewFeature: false, Current: Polygon or MultiPolygon };
    private bool VetorizacaoAvancadaAtiva => _modoAvancadoVetorizacao &&
        (FerramentaDesenhoAtiva || _ferramentaAtiva == ModoFerramenta.EdicaoVertice);
    private int NumeroEncaixesEdicaoAtivos =>
        (_snapVectorEditVertices ? 1 : 0) +
        (_snapVectorEditEdges ? 1 : 0) +
        (_snapVectorEditMidpoints ? 1 : 0) +
        (_snapVectorEditIntersections ? 1 : 0);
    private IReadOnlyList<VectorFeatureEditState> SelectedVectorFeatureStates => _vectorFeatureEditSelection
        .Where(key => _vectorFeatureEditStates.ContainsKey(key))
        .Select(key => _vectorFeatureEditStates[key])
        .Where(state => !state.IsDeleted)
        .ToArray();
    private bool CanMergeSelectedPolygons => !_vectorEditOperationInProgress && SelectedVectorFeatureStates.Count >= 2 &&
        SelectedVectorFeatureStates.All(state => state.Key.LayerName == _vectorFeatureEditSelection[0].LayerName &&
            state.Current is Polygon or MultiPolygon);
    private bool CanMergeSelectedLines => !_vectorEditOperationInProgress && SelectedVectorFeatureStates.Count >= 2 &&
        SelectedVectorFeatureStates.All(state => state.Key.LayerName == _vectorFeatureEditSelection[0].LayerName &&
            state.Current is LineString or MultiLineString);
    private bool CanRemoveHoveredVectorVertex => !_vectorEditOperationInProgress && SelectedVectorFeatureState is not null &&
        _ferramentaAtiva == ModoFerramenta.EdicaoVertice &&
        !_vectorFeatureSelectionMode && !_vectorMoveFeatureMode &&
        _hoveredVectorVertexDisplayIndex >= 0 && _hoveredVectorVertexDisplayIndex < _selectedVectorVertices.Count;
    private int SelectedVectorVertexSelectionCount => _selectedVectorVertexAddresses.Count;
    private bool CanRemoveSelectedVectorVertices => !_vectorEditOperationInProgress && SelectedVectorFeatureState is not null &&
        _ferramentaAtiva == ModoFerramenta.EdicaoVertice && !_vectorFeatureSelectionMode && !_vectorMoveFeatureMode &&
        _selectedVectorVertexAddresses.Count > 0;
    private bool CanPropagateSelectedSharedVertices => SelectedVectorFeatureStates.Count >= 2 &&
        SelectedVectorFeatureStates.All(state => state.Key.LayerName == _vectorFeatureEditSelection[0].LayerName);

    private async Task SelecionarFeicaoParaEdicaoAsync(CompiledFeature? compiledFeature, string layerName, bool additiveSelection = false)
    {
        if (!CamadaAtivaVetorialDisponivel)
        {
            _vectorEditMessage = OrientacaoCamadaAtiva;
            return;
        }
        if (compiledFeature is not null &&
            !string.Equals(layerName, _camadaAtivaFerramentas, StringComparison.OrdinalIgnoreCase))
        {
            _vectorEditMessage = "A feição atingida não pertence à camada ativa; nenhuma outra camada será editada.";
            return;
        }

        if (compiledFeature is null)
        {
            if (additiveSelection)
            {
                _vectorEditMessage = "Clique em uma feição da camada ativa para adicioná-la à seleção.";
                return;
            }
            _selectedVectorFeatureForEdit = null;
            LimparVerticesSelecionados();
            _vectorFeatureEditSelection.Clear();
            LimparSelecaoVerticeEdicao();
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
            if (additiveSelection)
            {
                _vectorEditMessage = "A seleção múltipla exige feições locais com FID estável; PostGIS permanece somente para visualização.";
                return;
            }
            _vectorEditMessage = "Edição PostGIS indisponível: esta conexão ainda não identifica a chave primária real da feição. O GeoNex não usará o índice temporário como identificador.";
            _selectedVectorFeatureForEdit = null;
            LimparVerticesSelecionados();
            if (!additiveSelection) _vectorFeatureEditSelection.Clear();
            MapService.DestacarFeicao(null);
            MapService.LimparOverlayEdicao();
            return;
        }
        if (string.IsNullOrWhiteSpace(layer.CaminhoArquivo) || !File.Exists(layer.CaminhoArquivo))
        {
            _vectorEditMessage = "A fonte local da camada não está disponível. Reconecte a camada antes de editar.";
            return;
        }
        try
        {
            LimparSelecaoVerticeEdicao();
            VectorFeatureEditState? state = _vectorFeatureEditStates.Values.FirstOrDefault(candidate =>
                candidate.Key.FeatureId == compiledFeature.FID &&
                string.Equals(candidate.Key.LayerName, layerName, StringComparison.Ordinal) &&
                (candidate.Key.SourceFeatureId == compiledFeature.SourceFeatureId || candidate.IsNewFeature));
            VectorFeatureKey key = state?.Key ?? new VectorFeatureKey(layerName, compiledFeature.FID, compiledFeature.SourceFeatureId);
            if (state is null)
            {
                if (compiledFeature.SourceFeatureId < 0)
                {
                    _vectorEditMessage = "O provedor não forneceu um FID estável para esta feição; a camada permanece somente para visualização e edição não será iniciada.";
                    return;
                }
                Geometry geometry = await Task.Run(() =>
                    VectorGeometryEditingService.ReadGeometry(layer.CaminhoArquivo, compiledFeature.SourceFeatureId, MapService.ProjetoSRS));
                state = new VectorFeatureEditState { Key = key, Baseline = geometry, Current = geometry };
                _vectorFeatureEditStates.Add(key, state);
            }

            if (additiveSelection)
            {
                if (_vectorFeatureEditSelection.Count > 0 &&
                    !string.Equals(_vectorFeatureEditSelection[0].LayerName, key.LayerName, StringComparison.Ordinal))
                {
                    _vectorEditMessage = "A seleção múltipla só pode incluir feições da mesma camada.";
                    return;
                }

                if (_vectorFeatureEditSelection.Remove(key))
                {
                    if (_vectorFeatureEditSelection.Count == 0)
                    {
                        _selectedVectorFeatureForEdit = null;
                        LimparVerticesSelecionados();
                        MapService.DestacarFeicao(null);
                        MapService.LimparOverlayEdicao();
                        _vectorEditMessage = "Seleção de feições limpa.";
                    }
                    else if (_selectedVectorFeatureForEdit == key)
                    {
                        SelecionarFeicaoPrincipal(_vectorFeatureEditSelection[0]);
                    }
                    else
                    {
                        SelecionarFeicaoPrincipal(_vectorFeatureEditSelection[0]);
                        _vectorEditMessage = $"Feição {IdentificadorFeicaoExibicao(key)} removida da seleção.";
                    }
                }
                else
                {
                    _vectorFeatureEditSelection.Add(key);
                    SelecionarFeicaoPrincipal(key);
                    _vectorEditMessage = $"{_vectorFeatureEditSelection.Count} feição(ões) selecionada(s) · a feição {IdentificadorFeicaoExibicao(key)} é a principal.";
                }

                await InvokeAsync(StateHasChanged);
                return;
            }

            _vectorFeatureEditSelection.Clear();
            _vectorFeatureEditSelection.Add(key);
            _selectedVectorFeatureForEdit = key;
            camadaSelecionadaPainel = layerName;
            AtualizarDiagnosticoGeometriaSelecionada();
            _vectorEditMessage = _vectorFeatureSelectionMode
                ? $"Feição {IdentificadorFeicaoExibicao(key)} selecionada · clique em Editar vértices para iniciar a edição."
                : $"Feição {IdentificadorFeicaoExibicao(key)} selecionada · arraste um vértice para remodelar ou o corpo para mover.";
            MapService.DestacarFeicao(compiledFeature);
            if (_vectorFeatureSelectionMode) AtualizarOverlaySelecaoFeicoes();
            else AtualizarOverlayEdicaoVetorial(state.Current, -1);
            StateHasChanged();
        }
        catch (Exception ex)
        {
            _selectedVectorFeatureForEdit = null;
            LimparVerticesSelecionados();
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
            JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.definirPreviaEdicaoVetorial", new object?[] { null });
            MapService.LimparOverlayEdicao();
            return;
        }

        IReadOnlyList<VectorGeometryEditingService.Vertex> vertices = VectorGeometryEditingService.GetVertices(geometry);
        DefinirVerticesSelecionados(vertices);
        if (FerramentaEdicaoVetorialNoCliente())
        {
            bool overlayModeChanged = !MapService.ClientRenderedVectorEditOverlay;
            MapService.ClientRenderedVectorEditOverlay = true;
            AtualizarIndicesVerticesEdicaoSelecionados();
            if (_draggedVectorVertex is not null || _draggingWholeVectorFeature) return;

            MapService.LimparOverlayEdicao();
            var previews = new List<(VectorFeatureKey Key, Geometry Geometry)> { (key, geometry) };
            if (CanPropagateSelectedSharedVertices)
                previews.AddRange(SelectedVectorFeatureStates
                    .Where(state => state.Key != key)
                    .Select(state => (state.Key, state.Current)));
            JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.definirPreviaEdicaoVetorial",
                CriarPayloadPreviaEdicaoVetorial(previews, activeVertexIndex));
            if (overlayModeChanged) SolicitarNovoFrame(interacaoRapida: true);
            return;
        }
        MapService.ClientRenderedVectorEditOverlay = false;
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
        AtualizarIndicesVerticesEdicaoSelecionados();
    }

    private void AtualizarOverlayEdicaoVetorial(
        IReadOnlyList<(VectorFeatureKey Key, Geometry Geometry)> previews, int activeVertexIndex)
    {
        if (_selectedVectorFeatureForEdit is not { } selectedKey)
        {
            MapService.LimparOverlayEdicao();
            return;
        }

        var primary = previews.FirstOrDefault(preview => preview.Key == selectedKey);
        if (primary.Geometry is null)
        {
            MapService.LimparOverlayEdicao();
            return;
        }

        IReadOnlyList<VectorGeometryEditingService.Vertex> vertices = VectorGeometryEditingService.GetVertices(primary.Geometry);
        DefinirVerticesSelecionados(vertices);
        if (FerramentaEdicaoVetorialNoCliente())
        {
            bool overlayModeChanged = !MapService.ClientRenderedVectorEditOverlay;
            MapService.ClientRenderedVectorEditOverlay = true;
            AtualizarIndicesVerticesEdicaoSelecionados();
            if (_draggedVectorVertex is not null || _draggingWholeVectorFeature) return;

            MapService.LimparOverlayEdicao();
            JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.definirPreviaEdicaoVetorial",
                CriarPayloadPreviaEdicaoVetorial(previews, activeVertexIndex));
            if (overlayModeChanged) SolicitarNovoFrame(interacaoRapida: true);
            return;
        }
        MapService.ClientRenderedVectorEditOverlay = false;
        var localVertices = vertices.Select(vertex => new SKPoint(
            (float)(vertex.X - MapService.OffsetMundoX),
            (float)(MapService.OffsetMundoY - vertex.Y))).ToArray();
        var paths = new List<SKPath>(previews.Count);
        try
        {
            foreach ((VectorFeatureKey key, Geometry geometry) in previews)
            {
                CompiledFeature compiled = PostgisGeometryCompiler.Compile(
                    geometry, key.FeatureId, key.LayerName, MapService.OffsetMundoX, MapService.OffsetMundoY);
                if (compiled.Path is { } path) paths.Add(path);
            }
            MapService.DefinirOverlayEdicao(paths, localVertices, activeVertexIndex);
            AtualizarIndicesVerticesEdicaoSelecionados();
        }
        catch (Exception ex)
        {
            foreach (SKPath path in paths) path.Dispose();
            _vectorEditMessage = $"A prévia topológica não pôde ser construída: {MensagemPostgisSegura(ex)}";
            AtualizarOverlayEdicaoVetorial(primary.Geometry, activeVertexIndex);
        }
    }

    private void AtualizarIndicesVerticesEdicaoSelecionados()
    {
        var indices = new List<int>(_selectedVectorVertexAddresses.Count);
        if (_selectedVectorVertexAddresses.Count == 0)
        {
            MapService.DefinirIndicesVerticesEdicaoSelecionados(indices);
            return;
        }

        var selectedAddresses = _selectedVectorVertexAddresses.ToHashSet();
        for (int i = 0; i < _selectedVectorVertices.Count; i++)
            if (selectedAddresses.Contains(_selectedVectorVertices[i].Address))
                indices.Add(i);
        MapService.DefinirIndicesVerticesEdicaoSelecionados(indices);
    }

    private bool AtualizarPreviaVerticeCompartilhado(
        VectorFeatureEditState primary,
        IReadOnlyList<VectorGeometryEditingService.VertexAddress> addresses,
        VectorGeometryEditingService.VertexAddress activeAddress,
        Geometry primaryPreview,
        int activeVertexIndex)
    {
        if (!_propagateSelectedSharedVertices || _draggedVectorVertexOriginal is not { } origin ||
            SelectedVectorFeatureStates.Count < 2)
            return false;

        VectorFeatureEditState[] states = SelectedVectorFeatureStates
            .Where(state => state.Key.LayerName == primary.Key.LayerName)
            .ToArray();
        int primaryIndex = Array.FindIndex(states, state => state.Key == primary.Key);
        if (primaryIndex < 0) return false;
        VectorGeometryEditingService.Vertex? movedVertex = VectorGeometryEditingService.GetVertices(primaryPreview)
            .Where(vertex => vertex.Address == activeAddress)
            .Select(vertex => (VectorGeometryEditingService.Vertex?)vertex)
            .FirstOrDefault();
        if (movedVertex is not { } target) return false;
        IReadOnlyList<Geometry> geometries = VectorGeometryEditingService.MoveCoincidentVerticesByDelta(
            states.Select(state => state.Current).ToArray(),
            primaryIndex,
            addresses,
            target.X - origin.X,
            target.Y - origin.Y);
        var previews = new List<(VectorFeatureKey Key, Geometry Geometry)>(states.Length);
        for (int i = 0; i < states.Length; i++)
        {
            if (!geometries[i].EqualsExact(states[i].Current))
                previews.Add((states[i].Key, geometries[i]));
        }
        if (previews.Count < 2) return false;
        _geometryDragPreview = geometries[primaryIndex];
        AtualizarOverlayEdicaoVetorial(previews, activeVertexIndex);
        _vectorEditMessage = $"Prévia de nó compartilhado · {previews.Count} feições selecionadas serão atualizadas.";
        return true;
    }

    private void AtualizarHoverVerticeEdicao(
        SKPoint cursor, SKMatrix matrix, SKPoint? pointerStart, bool shiftVertexSelection)
    {
        if (SelectedVectorFeatureState is not { } state || _selectedVectorVertices.Count == 0) return;
        double tolerance = 10d / Math.Max(0.0001, Math.Abs(matrix.ScaleX));
        double toleranceSquared = tolerance * tolerance;
        int nearest = -1;
        bool geometryDragActive = _draggedVectorVertex is not null || _draggingWholeVectorFeature;
        if (!geometryDragActive)
        {
            double nearestDistance = toleranceSquared;
            SKPoint reference = _vectorEditPointerPressed && pointerStart is { } start ? start : cursor;
            if (_selectedVectorVertexSpatialIndex is { } spatialIndex)
            {
                double worldX = reference.X + MapService.OffsetMundoX;
                double worldY = MapService.OffsetMundoY - reference.Y;
                nearest = spatialIndex.FindNearest(worldX, worldY, tolerance);
            }
            else
            {
                for (int i = 0; i < _selectedVectorVertices.Count; i++) ConsiderVertex(i);
            }

            void ConsiderVertex(int i)
            {
                VectorGeometryEditingService.Vertex vertex = _selectedVectorVertices[i];
                double dx = reference.X - (vertex.X - MapService.OffsetMundoX);
                double dy = reference.Y - (MapService.OffsetMundoY - vertex.Y);
                double distance = dx * dx + dy * dy;
                if (distance <= nearestDistance)
                {
                    nearest = i;
                    nearestDistance = distance;
                }
            }
        }

        if (_vectorEditPointerPressed && _draggedVectorVertex is null && !_draggingWholeVectorFeature &&
            pointerStart is { } dragStart &&
            (cursor.X - dragStart.X) * (cursor.X - dragStart.X) *
                matrix.ScaleX * matrix.ScaleX +
            (cursor.Y - dragStart.Y) * (cursor.Y - dragStart.Y) *
                matrix.ScaleY * matrix.ScaleY < 9)
        {
            _hoveredVectorVertexDisplayIndex = nearest;
            MapService.DefinirIndiceVerticeEdicao(nearest);
            return;
        }

        if (_vectorEditPointerPressed && shiftVertexSelection && !geometryDragActive)
        {
            _hoveredVectorVertexDisplayIndex = nearest;
            MapService.DefinirIndiceVerticeEdicao(nearest);
            return;
        }

        if (!_vectorEditPointerPressed && !_vectorEditPlacementArmed)
        {
            _hoveredVectorVertexDisplayIndex = nearest;
            MapService.DefinirIndiceVerticeEdicao(nearest);
            return;
        }
        if (!_vectorMoveFeatureMode && _draggedVectorVertex is null && !_draggingWholeVectorFeature && nearest >= 0)
        {
            _draggedVectorVertexDisplayIndex = nearest;
            _draggedVectorVertex = _selectedVectorVertices[nearest].Address;
            VectorGeometryEditingService.Vertex draggedVertex = _selectedVectorVertices[nearest];
            if (!_selectedVectorVertexAddresses.Contains(draggedVertex.Address))
            {
                _selectedVectorVertexAddresses.Clear();
                _selectedVectorVertexAddresses.Add(draggedVertex.Address);
            }
            _draggedVectorVertexAddresses = _selectedVectorVertexAddresses.ToArray();
            AtualizarIndicesVerticesEdicaoSelecionados();
            _draggedVectorVertexOriginal = new Coordinate(draggedVertex.X, draggedVertex.Y);
            _geometryAtDragStart = state.Current;
            _geometryDragPreview = state.Current;
            JSRuntime.InvokeVoidAsyncSafe("mapEngine.setVertexEditDragging", true);
            JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.iniciarArrasteVerticeEdicao",
                nearest, pointerStart?.X ?? draggedVertex.X - MapService.OffsetMundoX,
                pointerStart?.Y ?? MapService.OffsetMundoY - draggedVertex.Y);
            MapService.DefinirIndiceVerticeEdicao(nearest);
        }
        else if (_vectorMoveFeatureMode && _draggedVectorVertex is null && !_draggingWholeVectorFeature &&
            pointerStart is { } featurePointerStart)
        {
            double worldX = featurePointerStart.X + MapService.OffsetMundoX;
            double worldY = MapService.OffsetMundoY - featurePointerStart.Y;
            float scale = Math.Max(0.0001f, Math.Abs(matrix.ScaleX));
            CompiledFeature? hit = MapService.DispararRaycast(worldY, worldX, 6d / scale, out string hitLayer, state.Key.LayerName);
            if (hit is not null && hit.FID == state.Key.FeatureId &&
                string.Equals(hitLayer, state.Key.LayerName, StringComparison.Ordinal))
            {
                _draggingWholeVectorFeature = true;
                _wholeFeatureDragStart = featurePointerStart;
                _geometryAtDragStart = state.Current;
                _geometryDragPreview = state.Current;
                _draggedVectorVertexDisplayIndex = -1;
                JSRuntime.InvokeVoidAsyncSafe("mapEngine.setVertexEditDragging", true);
                JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.iniciarArrasteFeicaoEdicao",
                    featurePointerStart.X, featurePointerStart.Y);
                MapService.DefinirIndiceVerticeEdicao(-1);
            }
        }

        if (_draggingWholeVectorFeature && _wholeFeatureDragStart is { } featureStart &&
            _geometryAtDragStart is { } featureGeometry)
        {
            if (FerramentaEdicaoVetorialNoCliente())
            {
                MapService.PontoCursorSnap = null;
                MapService.PontoCursorSnapTipo = null;
                _vectorEditMessage = "Prévia da feição · solte para mover toda a geometria.";
                return;
            }
            try
            {
                MapService.PontoCursorSnap = null;
                MapService.PontoCursorSnapTipo = null;
                _geometryDragPreview = VectorGeometryEditingService.TranslatePreview(
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
            bool deferSnapForHeavyPreview = FerramentaEdicaoVetorialNoCliente() &&
                _selectedVectorVertices.Count >= 10_000 &&
                (_vectorEditPointerPressed || _vectorEditPlacementArmed);
            SKPoint adjustedCursor = deferSnapForHeavyPreview ? cursor : AjustarCursorSnapEdicao(cursor, matrix);
            double targetX = adjustedCursor.X + MapService.OffsetMundoX;
            double targetY = MapService.OffsetMundoY - adjustedCursor.Y;
            Coordinate vertexOrigin = _draggedVectorVertexOriginal ?? throw new InvalidOperationException("O vértice inicial não está disponível.");
            IReadOnlyList<VectorGeometryEditingService.VertexAddress> addresses = _draggedVectorVertexAddresses.Count > 0
                ? _draggedVectorVertexAddresses
                : [address];
            if (FerramentaEdicaoVetorialNoCliente())
            {
                _vectorEditMessage = deferSnapForHeavyPreview
                    ? "Prévia leve · o encaixe será calculado ao aplicar; Esc cancela."
                    : "Prévia do vértice · mova o cursor e clique para aplicar; Esc cancela.";
                return;
            }
            _geometryDragPreview = addresses.Count > 1
                ? VectorGeometryEditingService.MoveVerticesByDeltaPreview(
                    origin, addresses, targetX - vertexOrigin.X, targetY - vertexOrigin.Y)
                : VectorGeometryEditingService.MoveVertexPreview(origin, address, targetX, targetY);
            if (!AtualizarPreviaVerticeCompartilhado(
                state, addresses, address, _geometryDragPreview, _draggedVectorVertexDisplayIndex))
                AtualizarOverlayEdicaoVetorial(_geometryDragPreview, _draggedVectorVertexDisplayIndex);
            _vectorEditMessage = "Prévia do vértice · mova o cursor e clique para aplicar; Esc cancela.";
        }
        catch (InvalidOperationException ex)
        {
            _vectorEditMessage = ex.Message;
        }
    }

    private async Task FinalizarArrasteVerticeEdicaoAsync(double clientX, double clientY)
    {
        _vectorEditPointerPressed = false;
        _vectorEditPlacementArmed = false;
        _vectorEditCommitOnRelease = false;
        JSRuntime.InvokeVoidAsyncSafe("mapEngine.setVertexEditPlacementArmed", false);
        JSRuntime.InvokeVoidAsyncSafe("mapEngine.setVertexEditDragging", false);
        if ((!_draggingWholeVectorFeature && _draggedVectorVertex is null) || _geometryAtDragStart is not { } before ||
            _selectedVectorFeatureForEdit is not { } key || !_vectorFeatureEditStates.TryGetValue(key, out var state))
        {
            LimparEstadoArrasteVertice();
            return;
        }

        _vectorEditOperationInProgress = true;
        _vectorEditMessage = "Validando a edição da geometria…";
        await InvokeAsync(StateHasChanged);
        Geometry? after = _geometryDragPreview;
        double? sharedVertexDeltaX = null;
        double? sharedVertexDeltaY = null;
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
                        double deltaX = finalPosition.X - featureStart.X;
                        double deltaY = featureStart.Y - finalPosition.Y;
                        after = await Task.Run(() => VectorGeometryEditingService.Translate(before, deltaX, deltaY));
                    }
                    else if (_draggedVectorVertex is { } address)
                    {
                        finalPosition = AjustarCursorSnapEdicao(finalPosition, matrix);
                        double targetX = finalPosition.X + MapService.OffsetMundoX;
                        double targetY = MapService.OffsetMundoY - finalPosition.Y;
                        Coordinate vertexOrigin = _draggedVectorVertexOriginal ?? throw new InvalidOperationException("O vértice inicial não está disponível.");
                        IReadOnlyList<VectorGeometryEditingService.VertexAddress> addresses = _draggedVectorVertexAddresses.Count > 0
                            ? _draggedVectorVertexAddresses
                            : [address];
                        double deltaX = targetX - vertexOrigin.X;
                        double deltaY = targetY - vertexOrigin.Y;
                        sharedVertexDeltaX = deltaX;
                        sharedVertexDeltaY = deltaY;
                        after = await Task.Run(() => addresses.Count > 1
                            ? VectorGeometryEditingService.MoveVerticesByDelta(before, addresses, deltaX, deltaY)
                            : VectorGeometryEditingService.MoveVertex(before, address, targetX, targetY));
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

            if (_draggedVectorVertex is { } sharedAddress && sharedVertexDeltaX is { } sharedDeltaX &&
                sharedVertexDeltaY is { } sharedDeltaY && _propagateSelectedSharedVertices &&
                CanPropagateSelectedSharedVertices)
            {
                VectorFeatureEditState[] selectedStates = SelectedVectorFeatureStates
                    .Where(selected => selected.Key.LayerName == key.LayerName)
                    .ToArray();
                int primaryIndex = Array.FindIndex(selectedStates, selected => selected.Key == key);
                if (primaryIndex >= 0)
                {
                    IReadOnlyList<VectorGeometryEditingService.VertexAddress> addresses = _draggedVectorVertexAddresses.Count > 0
                        ? _draggedVectorVertexAddresses
                        : [sharedAddress];
                    Geometry[] sourceGeometries = selectedStates.Select(selected => selected.Current).ToArray();
                    IReadOnlyList<Geometry> geometries = await Task.Run(() =>
                        VectorGeometryEditingService.MoveCoincidentVerticesByDelta(
                            sourceGeometries, primaryIndex, addresses, sharedDeltaX, sharedDeltaY));
                    Guid groupId = Guid.NewGuid();
                    var actions = new List<VectorGeometryEditAction>(selectedStates.Length);
                    for (int i = 0; i < selectedStates.Length; i++)
                    {
                        VectorFeatureEditState selected = selectedStates[i];
                        if (selected.Current.EqualsExact(geometries[i])) continue;
                        actions.Add(new VectorGeometryEditAction(
                            groupId,
                            selected.Key,
                            selected.Current,
                            geometries[i],
                            GroupLabel: "Edição topológica"));
                    }

                    if (actions.Count > 1)
                    {
                        await AplicarAcoesGeometriaAsync(actions, forward: true);
                        RegistrarAcoesEdicao(actions);
                        _vectorEditMessage = $"Nó compartilhado movido em {actions.Count} feições; use Desfazer para reverter o grupo.";
                        return;
                    }
                }
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
            _vectorEditOperationInProgress = false;
            LimparEstadoArrasteVertice();
            if (_selectedVectorFeatureForEdit == key && _vectorFeatureEditStates.TryGetValue(key, out var selected))
                AtualizarOverlayEdicaoVetorial(selected.Current, -1);
            SolicitarNovoFrame();
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
            else if (state.IsNewFeature)
            {
                CompiledFeature compiled = PostgisGeometryCompiler.Compile(
                    geometry, state.Key.FeatureId, state.Key.LayerName, MapService.OffsetMundoX, MapService.OffsetMundoY);
                MapService.AdicionarFeicaoVetorial(state.Key.LayerName, compiled);
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
        AtualizarDiagnosticoGeometriaSelecionada();
        LimparSelecaoVerticeEdicao();
        AtualizarFlagEdicoesPendentes();
    }

    private void PrepararFeicaoPendenteEdicao(VectorFeatureEditState state)
    {
        if (state.IsNewFeature)
        {
            if (state.PendingFeature is null)
                throw new InvalidOperationException("A feição criada para o corte perdeu seus atributos pendentes.");
            if (state.IsDeleted)
            {
                RemoverFeicaoPendenteEdicao(state);
                return;
            }

            state.PendingFeature.Geometry = state.Current;
            state.PendingFeature.Attributes![VectorEditPersistenceService.DeleteFeatureAttribute] = "NAO";
            if (!MapService.FeicoesOriginais.TryGetValue(state.Key.LayerName, out FeatureCollection? newCollection))
            {
                newCollection = new FeatureCollection();
                MapService.FeicoesOriginais[state.Key.LayerName] = newCollection;
            }
            if (!newCollection.Contains(state.PendingFeature)) newCollection.Add(state.PendingFeature);
            return;
        }

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
        if (!state.IsNewFeature) state.PendingFeature = null;
    }

    private long ProximoFidTemporario() => _nextTemporaryVectorFeatureId--;

    private async Task DesfazerEdicaoVetorialAsync()
    {
        if (!CanUndoVectorGeometryEdit || _vectorEditPointerPressed) return;
        Guid groupId = _vectorGeometryEditHistory[_vectorGeometryEditHistoryCursor - 1].GroupId;
        int start = _vectorGeometryEditHistoryCursor - 1;
        while (start > 0 && _vectorGeometryEditHistory[start - 1].GroupId == groupId) start--;
        VectorGeometryEditAction[] actions = _vectorGeometryEditHistory
            .Skip(start)
            .Take(_vectorGeometryEditHistoryCursor - start)
            .ToArray();
        try
        {
            await AplicarAcoesGeometriaAsync(actions.Reverse().ToArray(), forward: false);
        }
        catch (Exception ex)
        {
            _vectorEditMessage = $"Não foi possível desfazer a edição: {MensagemPostgisSegura(ex)}";
            await InvokeAsync(StateHasChanged);
            return;
        }

        _vectorGeometryEditHistoryCursor = start;
        VectorGeometryEditAction? mergePrimary = actions.FirstOrDefault(action => !action.AfterDeleted);
        if (actions.Length > 1 && mergePrimary is not null && actions.Any(action => action.AfterDeleted && !action.BeforeDeleted))
        {
            _vectorFeatureEditSelection.Clear();
            _vectorFeatureEditSelection.AddRange(actions.Select(action => action.Key));
            SelecionarFeicaoPrincipal(mergePrimary.Key);
        }
        else
        {
            VectorGeometryEditAction action = actions[0];
            VectorFeatureEditState state = _vectorFeatureEditStates[action.Key];
            if (state.IsDeleted && _selectedVectorFeatureForEdit == action.Key)
                LimparSelecaoEdicaoVetorial();
            else if (!state.IsDeleted && action.AfterDeleted && !action.BeforeDeleted)
                SelecionarFeicaoRestauradaParaEdicao(state);
            else if (_selectedVectorFeatureForEdit == action.Key)
                AtualizarOverlayEdicaoVetorial(state.Current, -1);
        }
        string? groupLabel = actions.Select(action => action.GroupLabel).FirstOrDefault(label => !string.IsNullOrWhiteSpace(label));
        _vectorEditMessage = groupLabel is not null
            ? $"{groupLabel} desfeito."
            : actions.Length > 1
            ? actions.Any(action => action.BeforeDeleted && !action.AfterDeleted)
                ? "Corte desfeito; a geometria original foi restaurada."
                : "Mesclagem desfeita; todas as feições foram restauradas."
            : $"Desfeito · feição {actions[0].Key.SourceFeatureId}.";
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
                LimparVerticesSelecionados();
                _hoveredVectorVertexDisplayIndex = -1;
            }
            _vectorFeatureEditSelection.Clear();
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
        _vectorFeatureEditSelection.Clear();
        _vectorFeatureEditSelection.Add(state.Key);
        SelecionarFeicaoPrincipal(state.Key);
    }

    private void SelecionarFeicaoPrincipal(VectorFeatureKey key)
    {
        if (!_vectorFeatureEditStates.TryGetValue(key, out VectorFeatureEditState? state) || state.IsDeleted) return;
        LimparSelecaoVerticeEdicao();
        _selectedVectorFeatureForEdit = state.Key;
        camadaSelecionadaPainel = state.Key.LayerName;
        AtualizarDiagnosticoGeometriaSelecionada();
        if (MapService.FeaturesPorCamada.TryGetValue(state.Key.LayerName, out List<CompiledFeature>? features))
            MapService.DestacarFeicao(features.FirstOrDefault(feature => feature.FID == state.Key.FeatureId));
        if (_vectorFeatureSelectionMode)
            AtualizarOverlaySelecaoFeicoes();
        else
            AtualizarOverlayEdicaoVetorial(state.Current, -1);
    }

    private bool FerramentaEdicaoVetorialNoCliente() =>
        _ferramentaAtiva == ModoFerramenta.EdicaoVertice && !_vectorFeatureSelectionMode;

    private object CriarPayloadPreviaEdicaoVetorial(
        IReadOnlyList<(VectorFeatureKey Key, Geometry Geometry)> previews, int activeVertexIndex)
    {
        VectorFeatureKey primaryKey = _selectedVectorFeatureForEdit ?? previews[0].Key;
        var ordered = previews
            .OrderBy(preview => preview.Key == primaryKey ? 0 : 1)
            .ToArray();
        var features = new List<object>(ordered.Length);
        var featureVertices = new List<IReadOnlyList<VectorGeometryEditingService.Vertex>>(ordered.Length);
        foreach ((VectorFeatureKey key, Geometry geometry) in ordered)
        {
            var paths = new List<object>();
            var vertices = new List<object>();
            AppendGeometry(geometry, paths, vertices);
            featureVertices.Add(VectorGeometryEditingService.GetVertices(geometry));
            features.Add(new
            {
                polygon = geometry is Polygon or MultiPolygon,
                paths,
                vertices
            });
        }

        IReadOnlyList<VectorGeometryEditingService.Vertex> primaryVertices = featureVertices[0];
        HashSet<VectorGeometryEditingService.VertexAddress> selectedAddresses =
            _selectedVectorVertexAddresses.ToHashSet();
        int[] selectedIndices = primaryVertices
            .Select((vertex, index) => (vertex, index))
            .Where(item => selectedAddresses.Contains(item.vertex.Address))
            .Select(item => item.index)
            .ToArray();
        var movingVertices = new int[ordered.Length][];
        Coordinate[] movingOrigins = primaryVertices
            .Where(vertex => _selectedVectorVertexAddresses.Contains(vertex.Address) ||
                vertex.Address == _draggedVectorVertex)
            .Select(vertex => new Coordinate(vertex.X, vertex.Y))
            .ToArray();
        for (int featureIndex = 0; featureIndex < ordered.Length; featureIndex++)
        {
            IReadOnlyList<VectorGeometryEditingService.Vertex> candidates = featureVertices[featureIndex];
            movingVertices[featureIndex] = candidates
                .Select((vertex, index) => (vertex, index))
                .Where(item => featureIndex == 0
                    ? _selectedVectorVertexAddresses.Contains(item.vertex.Address) || item.vertex.Address == _draggedVectorVertex
                    : movingOrigins.Any(origin =>
                        Math.Abs(item.vertex.X - origin.X) <= Math.Max(1e-9, Math.Abs(origin.X) * 1e-12) &&
                        Math.Abs(item.vertex.Y - origin.Y) <= Math.Max(1e-9, Math.Abs(origin.Y) * 1e-12)))
                .Select(item => item.index)
                .ToArray();
        }

        return new
        {
            features,
            primaryFeatureIndex = 0,
            activeVertexIndex,
            selectedVertexIndices = selectedIndices,
            movingVertices
        };

        void AppendGeometry(Geometry geometry, List<object> paths, List<object> vertices)
        {
            switch (geometry)
            {
                case Polygon polygon:
                    AppendLine(polygon.ExteriorRing, closed: true);
                    for (int i = 0; i < polygon.NumInteriorRings; i++)
                        AppendLine(polygon.GetInteriorRingN(i), closed: true);
                    break;
                case LineString line:
                    AppendLine(line, line is LinearRing);
                    break;
                case NetTopologySuite.Geometries.Point point:
                    AppendCoordinates(point.Coordinates, closed: false);
                    break;
                case GeometryCollection collection:
                    for (int i = 0; i < collection.NumGeometries; i++)
                        AppendGeometry(collection.GetGeometryN(i), paths, vertices);
                    break;
            }

            void AppendLine(LineString line, bool closed)
            {
                AppendCoordinates(line.Coordinates, closed);
            }

            void AppendCoordinates(Coordinate[] coordinates, bool closed)
            {
                int count = coordinates.Length;
                bool hasClosure = closed && count > 2 && coordinates[0].Equals2D(coordinates[^1]);
                if (hasClosure) count--;
                int firstVertexIndex = vertices.Count;
                var pathPoints = new List<object>(count + (hasClosure ? 1 : 0));
                for (int i = 0; i < count; i++)
                {
                    Coordinate coordinate = coordinates[i];
                    double x = coordinate.X - MapService.OffsetMundoX;
                    double y = MapService.OffsetMundoY - coordinate.Y;
                    vertices.Add(new { x, y });
                    pathPoints.Add(new { x, y, vertexIndex = firstVertexIndex + i });
                }
                if (hasClosure && count > 0)
                {
                    Coordinate coordinate = coordinates[0];
                    pathPoints.Add(new
                    {
                        x = coordinate.X - MapService.OffsetMundoX,
                        y = MapService.OffsetMundoY - coordinate.Y,
                        vertexIndex = firstVertexIndex
                    });
                }
                if (pathPoints.Count > 0) paths.Add(new { closed, points = pathPoints });
            }
        }
    }

    private void AtualizarOverlaySelecaoFeicoes()
    {
        bool overlayModeChanged = MapService.ClientRenderedVectorEditOverlay;
        MapService.ClientRenderedVectorEditOverlay = false;
        JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.definirPreviaEdicaoVetorial", new object?[] { null });
        if (SelectedVectorFeatureState is not { } primary)
        {
            MapService.LimparOverlayEdicao();
            if (overlayModeChanged) SolicitarNovoFrame(interacaoRapida: true);
            return;
        }

        var previews = new List<SKPath>(_vectorFeatureEditSelection.Count);
        try
        {
            foreach (VectorFeatureEditState state in SelectedVectorFeatureStates)
            {
                CompiledFeature compiled = PostgisGeometryCompiler.Compile(
                    state.Current, state.Key.FeatureId, state.Key.LayerName,
                    MapService.OffsetMundoX, MapService.OffsetMundoY);
                if (compiled.Path is { } path) previews.Add(path);
            }

            DefinirVerticesSelecionados(VectorGeometryEditingService.GetVertices(primary.Current));
            SKPoint[] pointHandles = primary.Current is NetTopologySuite.Geometries.Point or NetTopologySuite.Geometries.MultiPoint
                ? _selectedVectorVertices.Select(vertex => new SKPoint(
                    (float)(vertex.X - MapService.OffsetMundoX),
                    (float)(MapService.OffsetMundoY - vertex.Y))).ToArray()
                : Array.Empty<SKPoint>();
            MapService.DefinirOverlayEdicao(previews, pointHandles);
            MapService.DefinirIndicesVerticesEdicaoSelecionados(Array.Empty<int>());
            if (overlayModeChanged) SolicitarNovoFrame(interacaoRapida: true);
        }
        catch (Exception ex)
        {
            foreach (SKPath preview in previews) preview.Dispose();
            _vectorEditMessage = $"Não foi possível realçar todas as feições selecionadas: {MensagemPostgisSegura(ex)}";
            MapService.LimparOverlayEdicao();
        }
    }

    private void DefinirFeicaoPrincipal(VectorFeatureKey key)
    {
        if (!_vectorFeatureEditSelection.Contains(key)) return;
        SelecionarFeicaoPrincipal(key);
        _vectorEditMessage = $"Feição {IdentificadorFeicaoExibicao(key)} definida como principal; ela manterá os atributos na mesclagem.";
    }

    private void AtualizarDiagnosticoGeometriaSelecionada()
    {
        _vectorGeometryValidationMessage = SelectedVectorFeatureState is { IsDeleted: false } state
            ? VectorGeometryEditingService.GetGeometryValidationMessage(state.Current)
            : null;
    }

    private async Task RepararGeometriaSelecionadaAsync()
    {
        if (SelectedVectorFeatureState is not { IsDeleted: false, IsNewFeature: false } state ||
            _vectorGeometryValidationMessage is null)
            return;

        Camada? layer = CamadasAtivas.FirstOrDefault(candidate =>
            string.Equals(candidate.Nome, state.Key.LayerName, StringComparison.Ordinal) && candidate.Tipo == "Vetor");
        if (layer is null || layer.FontePostgis is not null ||
            string.IsNullOrWhiteSpace(layer.CaminhoArquivo) || !File.Exists(layer.CaminhoArquivo))
        {
            _vectorEditMessage = "A camada local da geometria não está disponível; reconecte-a antes de reparar.";
            await InvokeAsync(StateHasChanged);
            return;
        }

        var newStates = new List<VectorFeatureEditState>();
        try
        {
            Geometry before = state.Current;
            Geometry repaired = VectorGeometryEditingService.FixGeometry(before);
            Geometry[] repairedParts = before is Polygon && repaired is MultiPolygon multiPolygon
                ? multiPolygon.Geometries
                    .Cast<Geometry>()
                    .OrderByDescending(part => part.Area)
                    .ThenBy(part => part.Centroid.X)
                    .ThenBy(part => part.Centroid.Y)
                    .ToArray()
                : [repaired];
            if (repairedParts.Length == 0)
                throw new InvalidOperationException("O reparo não produziu partes de polígono utilizáveis.");

            Guid groupId = Guid.NewGuid();
            const string groupLabel = "Reparo de geometria";
            var actions = new List<VectorGeometryEditAction>(repairedParts.Length)
            {
                new(groupId, state.Key, before, repairedParts[0], GroupLabel: groupLabel)
            };

            if (repairedParts.Length > 1)
            {
                AttributesTable sourceAttributes = VectorGeometryEditingService.ReadAttributes(
                    layer.CaminhoArquivo, state.Key.SourceFeatureId);
                foreach (Geometry part in repairedParts.Skip(1))
                {
                    var attributes = new AttributesTable();
                    foreach (string name in sourceAttributes.GetNames())
                    {
                        if (name.StartsWith("_SYS_", StringComparison.OrdinalIgnoreCase)) continue;
                        attributes.Add(name, sourceAttributes[name]);
                    }
                    attributes.Add(VectorEditPersistenceService.NewFeatureAttribute, "SIM");
                    attributes.Add(VectorEditPersistenceService.DeleteFeatureAttribute, "NAO");
                    attributes.Add("_SYS_GRAVADO", "NAO");

                    var pendingFeature = new Feature(part, attributes);
                    var key = new VectorFeatureKey(layer.Nome, ProximoFidTemporario(), -1);
                    var newState = new VectorFeatureEditState
                    {
                        Key = key,
                        Baseline = part,
                        Current = part,
                        PendingFeature = pendingFeature,
                        IsDeleted = true,
                        IsNewFeature = true
                    };
                    _vectorFeatureEditStates.Add(key, newState);
                    newStates.Add(newState);
                    actions.Add(new VectorGeometryEditAction(
                        groupId, key, part, part, BeforeDeleted: true, AfterDeleted: false, GroupLabel: groupLabel));
                }
            }

            await AplicarAcoesGeometriaAsync(actions, forward: true);
            RegistrarAcoesEdicao(actions);
            _vectorFeatureEditSelection.Clear();
            _vectorFeatureEditSelection.Add(state.Key);
            SelecionarFeicaoPrincipal(state.Key);
            _vectorEditMessage = repairedParts.Length > 1
                ? $"Geometria reparada em {repairedParts.Length} partes; a maior manteve o FID e as demais serao salvas como novas feicoes."
                : "Geometria reparada e validada; salve as edicoes para gravar na fonte.";
        }
        catch (Exception ex)
        {
            foreach (VectorFeatureEditState newState in newStates)
            {
                if (MapService.FeaturesPorCamada.TryGetValue(newState.Key.LayerName, out List<CompiledFeature>? features) &&
                    features.Any(feature => feature.FID == newState.Key.FeatureId))
                {
                    try { MapService.RemoverFeicaoVetorial(newState.Key.LayerName, newState.Key.FeatureId).Feature.Path?.Dispose(); }
                    catch (Exception rollbackError) { Console.Error.WriteLine($"Falha ao limpar feicao temporaria do reparo: {rollbackError.Message}"); }
                }
                if (newState.DetachedFeature is { } detached) detached.Feature.Path?.Dispose();
                if (MapService.FeicoesOriginais.TryGetValue(newState.Key.LayerName, out FeatureCollection? collection))
                    collection.Remove(newState.PendingFeature);
                _vectorFeatureEditStates.Remove(newState.Key);
            }
            _vectorEditMessage = $"O reparo não foi aplicado; a geometria original foi preservada. {MensagemPostgisSegura(ex)}";
        }

        await InvokeAsync(StateHasChanged);
    }

    private void PrepararMesclagemPoligonos()
    {
        if (FerramentaDesenhoAtiva && (MapService.PontosAquisicao.Count > 0 || _coordenadaAbsolutaTravada))
        {
            mensagemVetorizacao = "Conclua ou cancele o esboço atual antes de entrar na edição de feições.";
            _ = InvokeAsync(StateHasChanged);
            return;
        }
        PrepararSelecaoFeicoes();
        _ = InvokeAsync(StateHasChanged);
    }

    private void PrepararSelecaoFeicoes()
    {
        _ribbonCommandFeedback = string.Empty;
        if (!CamadaAtivaVetorialDisponivel)
        {
            _vectorEditMessage = OrientacaoCamadaAtiva;
            return;
        }
        if (MapService.PontosAquisicao.Count > 0 || _coordenadaAbsolutaTravada)
        {
            _vectorEditMessage = "Conclua ou cancele o esboço e libere a coordenada F6 antes de selecionar feições.";
            return;
        }
        if (_ferramentaAtiva != ModoFerramenta.EdicaoVertice)
            TrocarFerramenta(ModoFerramenta.EdicaoVertice);
        if (_ferramentaAtiva != ModoFerramenta.EdicaoVertice) return;

        _modoAvancadoVetorizacao = true;
        _vectorFeatureSelectionMode = true;
        _vectorMoveFeatureMode = false;
        _insertVectorVertexMode = false;
        _hoveredVectorVertexDisplayIndex = -1;
        LimparSelecaoVerticeEdicao();
        JSRuntime.InvokeVoidAsyncSafe("mapEngine.setFeatureSelectionMode", true, _vectorFeatureSelectionShape);
        JSRuntime.InvokeVoidAsyncSafe("mapEngine.setVertexMoveFeatureMode", false);
        if (SelectedVectorFeatureState is { IsDeleted: false } state)
            SelecionarFeicaoPrincipal(state.Key);
        _vectorEditMessage = "Seleção ativa · escolha uma feição; depois clique em Editar vértices. Ctrl/Shift alterna feições. Arraste para selecionar por área.";
    }

    private void DefinirFormaSelecaoFeicoes(string shape)
    {
        if (shape is not ("rectangle" or "polygon" or "freehand" or "radius")) return;
        _vectorFeatureSelectionShape = shape;
        if (_vectorFeatureSelectionMode)
            JSRuntime.InvokeVoidAsyncSafe("mapEngine.setFeatureSelectionShape", shape);
        _vectorEditMessage = shape switch
        {
            "polygon" => "Seleção por polígono · clique para adicionar pontos e clique direito para concluir.",
            "freehand" => "Seleção à mão livre · arraste ao redor das feições da camada ativa.",
            "radius" => "Seleção por raio · arraste do centro até definir o raio da área.",
            _ => "Seleção retangular · arraste para selecionar; clique seleciona uma feição."
        };
    }

    private void SelecionarFormaRetangular() => DefinirFormaSelecaoFeicoes("rectangle");
    private void SelecionarFormaPoligonal() => DefinirFormaSelecaoFeicoes("polygon");
    private void SelecionarFormaMaoLivre() => DefinirFormaSelecaoFeicoes("freehand");
    private void SelecionarFormaRaio() => DefinirFormaSelecaoFeicoes("radius");

    private void AlternarModoMoverFeicao()
    {
        if (SelectedVectorFeatureState is not { IsDeleted: false })
        {
            _vectorEditMessage = "Selecione uma feição antes de ativar o movimento.";
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        _vectorMoveFeatureMode = !_vectorMoveFeatureMode;
        _vectorFeatureSelectionMode = false;
        _insertVectorVertexMode = false;
        LimparSelecaoVerticeEdicao();
        JSRuntime.InvokeVoidAsyncSafe("mapEngine.setFeatureSelectionMode", false, _vectorFeatureSelectionShape);
        JSRuntime.InvokeVoidAsyncSafe("mapEngine.setVertexMoveFeatureMode", _vectorMoveFeatureMode);
        _vectorEditMessage = _vectorMoveFeatureMode
            ? "Mover feição ativo · arraste o corpo da feição selecionada para deslocá-la."
            : "Editor de vértices ativo · arraste nós ou uma área para selecioná-los.";
    }

    [Microsoft.JSInterop.JSInvokable]
    public async Task ProcessarSelecaoPorForma(
        double[] imagePoints, double[]? displayedMatrix = null,
        bool additiveSelection = false, bool subtractSelection = false,
        bool intersectSelection = false, bool containedOnly = false)
    {
        if (!_vectorFeatureSelectionMode) return;
        if (!CamadaAtivaVetorialDisponivel)
        {
            _vectorEditMessage = OrientacaoCamadaAtiva;
            await InvokeAsync(StateHasChanged);
            return;
        }
        if (imagePoints is null || imagePoints.Length < 6 || imagePoints.Length % 2 != 0)
        {
            _vectorEditMessage = "A área ficou pequena demais. Arraste uma área maior e tente novamente.";
            await InvokeAsync(StateHasChanged);
            return;
        }

        Camada? layer = CamadasAtivas.FirstOrDefault(candidate =>
            string.Equals(candidate.Nome, _camadaAtivaFerramentas, StringComparison.Ordinal) && candidate.Tipo == "Vetor");
        if (layer is null || layer.FontePostgis is not null || string.IsNullOrWhiteSpace(layer.CaminhoArquivo) || !File.Exists(layer.CaminhoArquivo))
        {
            _vectorEditMessage = "A seleção por área exige uma camada vetorial local disponível.";
            await InvokeAsync(StateHasChanged);
            return;
        }

        try
        {
            _vectorEditMessage = "Procurando feições dentro da área…";
            await InvokeAsync(StateHasChanged);
            SKMatrix matrix = UsarMatrizApresentada(displayedMatrix, ObterMatrizMatematica());
            var coordinates = new List<Coordinate>(imagePoints.Length / 2 + 1);
            for (int i = 0; i < imagePoints.Length; i += 2)
            {
                if (!TentarMapearPontoTelaParaLocal(imagePoints[i], imagePoints[i + 1], displayedMatrix, matrix, out SKPoint local))
                {
                    _vectorEditMessage = "Não foi possível converter os limites da área para o mapa. Tente selecionar novamente.";
                    await InvokeAsync(StateHasChanged);
                    return;
                }
                coordinates.Add(new Coordinate(local.X + MapService.OffsetMundoX, MapService.OffsetMundoY - local.Y));
            }
            coordinates = coordinates.Where((coordinate, index) => index == 0 || !coordinate.Equals2D(coordinates[index - 1])).ToList();
            if (coordinates.Count > 1 && coordinates[0].Equals2D(coordinates[^1])) coordinates.RemoveAt(coordinates.Count - 1);
            if (coordinates.DistinctBy(coordinate => (coordinate.X, coordinate.Y)).Count() < 3)
            {
                _vectorEditMessage = "A área ficou pequena demais. Arraste uma área maior e tente novamente.";
                await InvokeAsync(StateHasChanged);
                return;
            }

            var ringCoordinates = coordinates.Select(coordinate => coordinate.Copy()).Append(coordinates[0].Copy()).ToArray();
            Geometry selectionArea = new GeometryFactory().CreatePolygon(new LinearRing(ringCoordinates));
            if (!selectionArea.IsValid) selectionArea = NetTopologySuite.Geometries.Utilities.GeometryFixer.Fix(selectionArea);
            if (selectionArea.IsEmpty)
            {
                _vectorEditMessage = "A área de seleção não formou um polígono válido. Desenhe outra área.";
                await InvokeAsync(StateHasChanged);
                return;
            }

            var candidates = new List<CompiledFeature>();
            using (ResourceLease<NativeShapeSpatialIndex>? indexLease = MapService.AcquireSpatialIndex(layer.Nome))
            {
                if (indexLease is not null)
                {
                    using NativeFeatureQuery query = indexLease.Resource.Query(selectionArea.EnvelopeInternal);
                    foreach (CompiledFeature candidate in query) candidates.Add(candidate);
                }
                else if (MapService.FeaturesPorCamada.TryGetValue(layer.Nome, out List<CompiledFeature>? features))
                {
                    candidates.AddRange(features.Where(feature =>
                    {
                        FeatureEnvelope envelope = feature.EnvelopeWorld;
                        return !envelope.IsNull && envelope.MinX <= selectionArea.EnvelopeInternal.MaxX &&
                            envelope.MaxX >= selectionArea.EnvelopeInternal.MinX && envelope.MinY <= selectionArea.EnvelopeInternal.MaxY &&
                            envelope.MaxY >= selectionArea.EnvelopeInternal.MinY;
                    }));
                }
            }

            Dictionary<long, VectorFeatureEditState> statesByFeatureId = _vectorFeatureEditStates.Values
                .Where(state => string.Equals(state.Key.LayerName, layer.Nome, StringComparison.Ordinal))
                .GroupBy(state => state.Key.FeatureId)
                .ToDictionary(group => group.Key, group => group.First());
            var staged = candidates.Select(feature =>
            {
                statesByFeatureId.TryGetValue(feature.FID, out VectorFeatureEditState? state);
                if (state is not null && state.Key.SourceFeatureId != feature.SourceFeatureId && !state.IsNewFeature)
                    state = null;
                return (Feature: feature, State: state, Key: state?.Key ?? new VectorFeatureKey(layer.Nome, feature.FID, feature.SourceFeatureId));
            }).ToArray();
            var matches = await Task.Run(() =>
            {
                var selected = new List<(CompiledFeature Feature, VectorFeatureKey Key, Geometry Geometry)>();
                foreach (var item in staged)
                {
                    if (item.State?.IsDeleted == true) continue;
                    Geometry geometry;
                    if (item.State is not null) geometry = item.State.Current;
                    else
                    {
                        if (item.Feature.SourceFeatureId < 0) continue;
                        try
                        {
                            geometry = VectorGeometryEditingService.ReadGeometry(
                                layer.CaminhoArquivo, item.Feature.SourceFeatureId, MapService.ProjetoSRS);
                        }
                        catch { continue; }
                    }

                    bool matchesArea;
                    try
                    {
                        matchesArea = containedOnly ? selectionArea.Covers(geometry) : selectionArea.Intersects(geometry);
                    }
                    catch
                    {
                        try
                        {
                            Geometry repaired = NetTopologySuite.Geometries.Utilities.GeometryFixer.Fix(geometry);
                            matchesArea = containedOnly ? selectionArea.Covers(repaired) : selectionArea.Intersects(repaired);
                        }
                        catch { matchesArea = false; }
                    }
                    if (matchesArea) selected.Add((item.Feature, item.Key, geometry));
                }
                return selected;
            });

            if (!_vectorFeatureSelectionMode || !string.Equals(_camadaAtivaFerramentas, layer.Nome, StringComparison.Ordinal))
                return;

            foreach (var match in matches)
            {
                if (!_vectorFeatureEditStates.ContainsKey(match.Key))
                    _vectorFeatureEditStates.Add(match.Key, new VectorFeatureEditState
                    {
                        Key = match.Key,
                        Baseline = match.Geometry,
                        Current = match.Geometry
                    });
            }

            List<VectorFeatureKey> orderedMatchKeys = matches.Select(match => match.Key).Distinct().ToList();
            var matchKeys = orderedMatchKeys.ToHashSet();
            if (!additiveSelection && !subtractSelection && !intersectSelection)
            {
                _vectorFeatureEditSelection.Clear();
                _vectorFeatureEditSelection.AddRange(orderedMatchKeys);
            }
            else if (subtractSelection)
            {
                _vectorFeatureEditSelection.RemoveAll(matchKeys.Contains);
            }
            else if (intersectSelection)
            {
                _vectorFeatureEditSelection.RemoveAll(key => !matchKeys.Contains(key));
            }
            else
            {
                foreach (VectorFeatureKey key in orderedMatchKeys)
                    if (!_vectorFeatureEditSelection.Contains(key)) _vectorFeatureEditSelection.Add(key);
            }

            if (_vectorFeatureEditSelection.Count == 0)
            {
                _selectedVectorFeatureForEdit = null;
                LimparVerticesSelecionados();
                LimparSelecaoVerticeEdicao();
                MapService.DestacarFeicao(null);
                MapService.LimparOverlayEdicao();
                _vectorEditMessage = matches.Count == 0
                    ? "Nenhuma feição da camada ativa cruza a área de seleção."
                    : "A seleção de feições foi limpa.";
            }
            else
            {
                VectorFeatureKey primary = _selectedVectorFeatureForEdit is { } current && _vectorFeatureEditSelection.Contains(current)
                    ? current
                    : _vectorFeatureEditSelection[^1];
                SelecionarFeicaoPrincipal(primary);
                _vectorEditMessage = $"{_vectorFeatureEditSelection.Count} feição(ões) selecionada(s) na camada ativa.";
            }
        }
        catch (Exception ex)
        {
            _vectorEditMessage = $"A seleção por área não pôde ser concluída: {MensagemPostgisSegura(ex)}";
        }

        await InvokeAsync(StateHasChanged);
    }

    [Microsoft.JSInterop.JSInvokable]
    public async Task ProcessarSelecaoVerticesPorRetangulo(
        double[] imagePoints, double[]? displayedMatrix = null,
        bool additiveSelection = false, bool subtractSelection = false)
    {
        if (_vectorFeatureSelectionMode || _vectorMoveFeatureMode || _draggedVectorVertex is not null || _draggingWholeVectorFeature ||
            SelectedVectorFeatureState is not { IsDeleted: false } state || imagePoints.Length < 8)
            return;

        SKMatrix matrix = UsarMatrizApresentada(displayedMatrix, ObterMatrizMatematica());
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        for (int i = 0; i + 1 < imagePoints.Length; i += 2)
        {
            if (!TentarMapearPontoTelaParaLocal(imagePoints[i], imagePoints[i + 1], displayedMatrix, matrix, out SKPoint point))
                return;
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        if (!additiveSelection && !subtractSelection) _selectedVectorVertexAddresses.Clear();
        for (int i = 0; i < _selectedVectorVertices.Count; i++)
        {
            VectorGeometryEditingService.Vertex vertex = _selectedVectorVertices[i];
            double localX = vertex.X - MapService.OffsetMundoX;
            double localY = MapService.OffsetMundoY - vertex.Y;
            if (localX < minX || localX > maxX || localY < minY || localY > maxY) continue;
            if (subtractSelection) _selectedVectorVertexAddresses.Remove(vertex.Address);
            else if (!_selectedVectorVertexAddresses.Contains(vertex.Address)) _selectedVectorVertexAddresses.Add(vertex.Address);
        }

        _selectedVectorVertexDisplayIndex = _selectedVectorVertices
            .Select((vertex, index) => (vertex, index))
            .Where(item => _selectedVectorVertexAddresses.Contains(item.vertex.Address))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .Last();
        _hoveredVectorVertexDisplayIndex = -1;
        MapService.DefinirIndiceVerticeEdicao(-1);
        if (_selectedVectorVertexDisplayIndex >= 0)
        {
            VectorGeometryEditingService.Vertex selectedVertex = _selectedVectorVertices[_selectedVectorVertexDisplayIndex];
            _vectorVertexCoordinateX = selectedVertex.X;
            _vectorVertexCoordinateY = selectedVertex.Y;
        }
        else
        {
            _vectorVertexCoordinateX = null;
            _vectorVertexCoordinateY = null;
        }
        AtualizarIndicesVerticesEdicaoSelecionados();
        _vectorEditMessage = $"{_selectedVectorVertexAddresses.Count} vértice(s) selecionado(s) na feição {IdentificadorFeicaoExibicao(state.Key)}.";
        await InvokeAsync(StateHasChanged);
    }

    private void AbrirEdicaoVetorialAvancada()
    {
        if (FerramentaDesenhoAtiva && MapService.PontosAquisicao.Count > 0)
        {
            mensagemVetorizacao = "Conclua ou descarte o esboço antes de abrir o editor de feições.";
            _ = InvokeAsync(StateHasChanged);
            return;
        }
        if (MapService.PontosAquisicao.Count > 0 || _coordenadaAbsolutaTravada)
        {
            _ribbonCommandFeedback = "Libere a coordenada F6 ou conclua/cancele o esboço antes de editar feições.";
            return;
        }
        if (SelectedVectorFeatureState is not { IsDeleted: false } selectedState)
        {
            _vectorEditMessage = "Primeiro use Selecionar feições e escolha uma feição. Depois, ative Editar vértices.";
            _ribbonCommandFeedback = _vectorEditMessage;
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        _ribbonCommandFeedback = string.Empty;
        _modoAvancadoVetorizacao = true;
        _vectorCutMode = false;
        _vectorRingMode = false;
        _vectorReshapeMode = false;
        _vectorFeatureSelectionMode = false;
        _vectorMoveFeatureMode = false;
        JSRuntime.InvokeVoidAsyncSafe("mapEngine.setFeatureSelectionMode", false, _vectorFeatureSelectionShape);
        JSRuntime.InvokeVoidAsyncSafe("mapEngine.setVertexMoveFeatureMode", false);
        RestaurarSnapAposRemodelagem();
        TrocarFerramenta(ModoFerramenta.EdicaoVertice);
        SelecionarFeicaoPrincipal(selectedState.Key);
        _painelIdentificarAberto = false;
        _vectorEditMessage = $"Edição ativa para a feição {IdentificadorFeicaoExibicao(selectedState.Key)}. Para trocar de feição, volte a Selecionar feições.";
    }

    private void RetornarAVetorizacaoAvancada()
    {
        if (MapService.PontosAquisicao.Count > 0 || _coordenadaAbsolutaTravada)
        {
            _vectorEditMessage = "Conclua ou cancele o esboço e libere a coordenada F6 antes de trocar de ferramenta.";
            return;
        }

        string targetLayerName = SelectedVectorFeatureState?.Key.LayerName ?? CamadaDestinoAquisicao;
        Camada? targetLayer = CamadasAtivas.FirstOrDefault(layer =>
            string.Equals(layer.Nome, targetLayerName, StringComparison.Ordinal) && layer.Tipo == "Vetor")
            ?? CamadasAtivas.FirstOrDefault(layer => layer.Tipo == "Vetor");
        if (targetLayer is null)
        {
            _vectorEditMessage = "Escolha uma camada vetorial de destino antes de retornar à vetorização.";
            return;
        }

        DefinirCamadaAtiva(targetLayer);
        _modoAvancadoVetorizacao = true;
        _painelIdentificarAberto = false;
        TrocarFerramenta(targetLayer.Geometria switch
        {
            "PONTO" => ModoFerramenta.AquisicaoPonto,
            "LINHA" => ModoFerramenta.AquisicaoLinha,
            _ => ModoFerramenta.AquisicaoPoligono
        });
        mensagemVetorizacao = $"Vetorização avançada pronta · destino: {targetLayer.Nome}.";
    }

    private void PrepararCorteGeometria()
    {
        if (FerramentaDesenhoAtiva && (MapService.PontosAquisicao.Count > 0 || _coordenadaAbsolutaTravada))
        {
            mensagemVetorizacao = "Conclua ou cancele o esboço atual antes de iniciar o corte.";
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        if (SelectedVectorFeatureState is not { IsDeleted: false } state ||
            state.Current is not (Polygon or MultiPolygon or LineString or MultiLineString))
        {
            _vectorEditMessage = "Selecione um polígono ou linha local no modo de edição de vértices antes de iniciar o corte.";
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        Camada? layer = CamadasAtivas.FirstOrDefault(candidate =>
            string.Equals(candidate.Nome, state.Key.LayerName, StringComparison.Ordinal) && candidate.Tipo == "Vetor");
        if (layer is null || layer.FontePostgis is not null || string.IsNullOrWhiteSpace(layer.CaminhoArquivo) || !File.Exists(layer.CaminhoArquivo))
        {
            _vectorEditMessage = "O corte exige uma camada vetorial local disponível para edição.";
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        _vectorFeatureEditSelection.Clear();
        _vectorFeatureEditSelection.Add(state.Key);
        _selectedVectorFeatureForEdit = state.Key;
        _vectorFeatureSelectionMode = false;
        DefinirCamadaAtiva(layer);
        _modoAvancadoVetorizacao = true;
        ModoConstrucao = ConstructionMode.Vertices;
        if (_ferramentaAtiva != ModoFerramenta.EdicaoVertice)
            TrocarFerramenta(ModoFerramenta.EdicaoVertice);
        _vectorCutMode = true;
        TrocarFerramenta(ModoFerramenta.AquisicaoLinha);
        mensagemVetorizacao = "Desenhe uma linha que atravesse a feição e a divida em partes; conclua o corte.";
        _vectorEditMessage = $"Desenhe a linha de corte sobre a feição {state.Key.SourceFeatureId}.";
        _ = InvokeAsync(StateHasChanged);
    }

    private void PrepararAdicaoAnel()
    {
        if (FerramentaDesenhoAtiva && (MapService.PontosAquisicao.Count > 0 || _coordenadaAbsolutaTravada))
        {
            mensagemVetorizacao = "Conclua ou cancele o esboço atual antes de iniciar o anel interno.";
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        if (SelectedVectorFeatureState is not { IsDeleted: false, IsNewFeature: false } state ||
            state.Current is not (Polygon or MultiPolygon))
        {
            _vectorEditMessage = "Selecione um polígono local no modo de edição antes de adicionar um anel interno.";
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        Camada? layer = CamadasAtivas.FirstOrDefault(candidate =>
            string.Equals(candidate.Nome, state.Key.LayerName, StringComparison.Ordinal) && candidate.Tipo == "Vetor");
        if (layer is null || layer.FontePostgis is not null || string.IsNullOrWhiteSpace(layer.CaminhoArquivo) || !File.Exists(layer.CaminhoArquivo))
        {
            _vectorEditMessage = "A edição de anéis exige uma camada vetorial local disponível.";
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        _vectorFeatureEditSelection.Clear();
        _vectorFeatureEditSelection.Add(state.Key);
        _selectedVectorFeatureForEdit = state.Key;
        DefinirCamadaAtiva(layer);
        _modoAvancadoVetorizacao = true;
        ModoConstrucao = ConstructionMode.Vertices;
        if (_ferramentaAtiva != ModoFerramenta.EdicaoVertice)
            TrocarFerramenta(ModoFerramenta.EdicaoVertice);
        _vectorRingMode = true;
        TrocarFerramenta(ModoFerramenta.AquisicaoPoligono);
        mensagemVetorizacao = "Desenhe o contorno do furo inteiramente dentro do polígono e conclua.";
        _vectorEditMessage = $"Desenhando anel interno no polígono {state.Key.SourceFeatureId}.";
        _ = InvokeAsync(StateHasChanged);
    }

    private void PrepararRemodelagemGeometria()
    {
        if (FerramentaDesenhoAtiva && (MapService.PontosAquisicao.Count > 0 || _coordenadaAbsolutaTravada))
        {
            mensagemVetorizacao = "Conclua ou cancele o esboço atual antes de remodelar uma feição.";
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        if (SelectedVectorFeatureState is not { IsDeleted: false, IsNewFeature: false } state ||
            state.Current is not (Polygon or MultiPolygon))
        {
            _vectorEditMessage = "Selecione um polígono local no editor de vértices antes de remodelar sua borda.";
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        Camada? layer = CamadasAtivas.FirstOrDefault(candidate =>
            string.Equals(candidate.Nome, state.Key.LayerName, StringComparison.Ordinal) && candidate.Tipo == "Vetor");
        if (layer is null || layer.FontePostgis is not null ||
            string.IsNullOrWhiteSpace(layer.CaminhoArquivo) || !File.Exists(layer.CaminhoArquivo))
        {
            _vectorEditMessage = "A remodelagem exige uma camada poligonal local disponível para edição.";
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        _vectorFeatureEditSelection.Clear();
        _vectorFeatureEditSelection.Add(state.Key);
        _selectedVectorFeatureForEdit = state.Key;
        DefinirCamadaAtiva(layer);
        _modoAvancadoVetorizacao = true;
        ModoConstrucao = ConstructionMode.Vertices;
        _reshapePreviousEdgeSnap = _snapAquisicaoAresta;
        _reshapeEdgeSnapSaved = true;
        _snapAquisicaoAresta = true;
        if (_ferramentaAtiva != ModoFerramenta.EdicaoVertice)
            TrocarFerramenta(ModoFerramenta.EdicaoVertice);
        _vectorReshapeMode = true;
        TrocarFerramenta(ModoFerramenta.AquisicaoLinha);
        mensagemVetorizacao = "Conecte dois pontos da borda: traço externo acrescenta uma saliência; traço interno remove a menor região delimitada. Snap de aresta ativo.";
        _vectorEditMessage = $"Remodelando a borda da feição {state.Key.SourceFeatureId}; Enter aplica uma edição única que pode ser desfeita.";
        _ = InvokeAsync(StateHasChanged);
    }

    private async Task ConcluirRemodelagemGeometriaAsync()
    {
        if (_vectorEditOperationInProgress || !_vectorReshapeMode || SelectedVectorFeatureState is not { IsDeleted: false, IsNewFeature: false } state)
            return;
        if (MapService.PontosAquisicao.Count < 2)
        {
            mensagemVetorizacao = "Adicione pelo menos dois pontos sobre a borda da feição.";
            await InvokeAsync(StateHasChanged);
            return;
        }

        _vectorEditOperationInProgress = true;
        _vectorEditMessage = "Calculando a remodelagem do polígono...";
        await InvokeAsync(StateHasChanged);
        try
        {
            Coordinate[] coordinates = MapService.PontosAquisicao
                .Select(point => new Coordinate(point.X + MapService.OffsetMundoX, -point.Y + MapService.OffsetMundoY))
                .ToArray();
            LineString reshapeLine = state.Current.Factory.CreateLineString(coordinates);
            Geometry before = state.Current;
            Geometry after = await Task.Run(() => VectorGeometryEditingService.ReshapePolygon(before, reshapeLine));
            await PublicarGeometriaEdicaoAsync(state, after);
            RegistrarAcaoGeometria(state.Key, before, after);
            HistoricoAquisicao.Clear();
            LimparCursorDesenho();
            LimparMetricasAquisicao();
            _vectorReshapeMode = false;
            RestaurarSnapAposRemodelagem();
            _mostrarMenuContexto = false;
            TrocarFerramenta(ModoFerramenta.EdicaoVertice);
            _vectorEditMessage = "Borda remodelada sem dividir a feição; salve para gravar na fonte.";
            mensagemVetorizacao = string.Empty;
        }
        catch (Exception ex)
        {
            mensagemVetorizacao = $"A remodelagem não foi aplicada; o esboço foi preservado. {MensagemPostgisSegura(ex)}";
            _vectorEditMessage = mensagemVetorizacao;
        }

        finally { _vectorEditOperationInProgress = false; }
        await InvokeAsync(StateHasChanged);
    }

    private void RestaurarSnapAposRemodelagem()
    {
        if (!_reshapeEdgeSnapSaved) return;
        _snapAquisicaoAresta = _reshapePreviousEdgeSnap;
        _reshapeEdgeSnapSaved = false;
    }

    private async Task ConcluirAdicaoAnelAsync()
    {
        if (_vectorEditOperationInProgress || !_vectorRingMode || SelectedVectorFeatureState is not { IsDeleted: false, IsNewFeature: false } state)
            return;
        if (MapService.PontosAquisicao.Count < 3)
        {
            mensagemVetorizacao = "Adicione pelo menos três vértices para fechar o anel interno.";
            await InvokeAsync(StateHasChanged);
            return;
        }

        _vectorEditOperationInProgress = true;
        _vectorEditMessage = "Adicionando anel interno...";
        await InvokeAsync(StateHasChanged);
        try
        {
            Coordinate[] coordinates = MapService.PontosAquisicao
                .Select(point => new Coordinate(point.X + MapService.OffsetMundoX, -point.Y + MapService.OffsetMundoY))
                .ToArray();
            Polygon interiorRing = (Polygon)DigitizingGeometry.Create(DigitizingGeometry.Kind.Polygon, coordinates);
            Geometry before = state.Current;
            Geometry after = await Task.Run(() => VectorGeometryEditingService.AddInteriorRing(before, interiorRing));
            await PublicarGeometriaEdicaoAsync(state, after);
            RegistrarAcaoGeometria(state.Key, before, after);
            HistoricoAquisicao.Clear();
            LimparCursorDesenho();
            LimparMetricasAquisicao();
            _vectorRingMode = false;
            _mostrarMenuContexto = false;
            TrocarFerramenta(ModoFerramenta.EdicaoVertice);
            _vectorEditMessage = "Anel interno adicionado; a área dentro do contorno virou um furo. Salve para gravar na fonte.";
            mensagemVetorizacao = string.Empty;
        }
        catch (Exception ex)
        {
            mensagemVetorizacao = $"O anel não foi aplicado; o esboço foi preservado. {MensagemPostgisSegura(ex)}";
            _vectorEditMessage = mensagemVetorizacao;
        }

        finally { _vectorEditOperationInProgress = false; }
        await InvokeAsync(StateHasChanged);
    }

    private async Task ConcluirCorteGeometriaAsync()
    {
        if (_vectorEditOperationInProgress || !_vectorCutMode || SelectedVectorFeatureState is not { IsDeleted: false } state)
            return;
        if (MapService.PontosAquisicao.Count < 2)
        {
            mensagemVetorizacao = "Adicione pelo menos dois pontos à linha de corte.";
            await InvokeAsync(StateHasChanged);
            return;
        }

        Camada? layer = CamadasAtivas.FirstOrDefault(candidate =>
            string.Equals(candidate.Nome, state.Key.LayerName, StringComparison.Ordinal) && candidate.Tipo == "Vetor");
        if (layer is null || layer.FontePostgis is not null || string.IsNullOrWhiteSpace(layer.CaminhoArquivo))
        {
            mensagemVetorizacao = "A camada local do polígono não está mais disponível; o esboço foi preservado.";
            await InvokeAsync(StateHasChanged);
            return;
        }

        var newStates = new List<VectorFeatureEditState>();
        _vectorEditOperationInProgress = true;
        _vectorEditMessage = "Calculando a divisão da geometria...";
        await InvokeAsync(StateHasChanged);
        try
        {
            Coordinate[] coordinates = MapService.PontosAquisicao
                .Select(point => new Coordinate(point.X + MapService.OffsetMundoX, -point.Y + MapService.OffsetMundoY))
                .ToArray();
            LineString cutter = state.Current.Factory.CreateLineString(coordinates);
            IReadOnlyList<Geometry> pieces = await Task.Run(() =>
                VectorGeometryEditingService.SplitGeometryByLine(state.Current, cutter));
            AttributesTable sourceAttributes;
            if (state.IsNewFeature)
            {
                if (state.PendingFeature?.Attributes is not { } pendingAttributes)
                    throw new InvalidOperationException("A feição ainda não salva perdeu seus atributos pendentes.");
                sourceAttributes = new AttributesTable();
                foreach (string name in pendingAttributes.GetNames())
                {
                    if (name.StartsWith("_SYS_", StringComparison.OrdinalIgnoreCase)) continue;
                    sourceAttributes.Add(name, pendingAttributes[name]);
                }
            }
            else
            {
                sourceAttributes = VectorGeometryEditingService.ReadAttributes(layer.CaminhoArquivo, state.Key.SourceFeatureId);
            }
            Guid groupId = Guid.NewGuid();
            var actions = new List<VectorGeometryEditAction>(pieces.Count)
            {
                new(groupId, state.Key, state.Current, pieces[0], BeforeDeleted: false, AfterDeleted: false)
            };

            foreach (Geometry part in pieces.Skip(1))
            {
                var attributes = new AttributesTable();
                foreach (string name in sourceAttributes.GetNames())
                {
                    if (name.StartsWith("_SYS_", StringComparison.OrdinalIgnoreCase)) continue;
                    attributes.Add(name, sourceAttributes[name]);
                }
                attributes.Add(VectorEditPersistenceService.NewFeatureAttribute, "SIM");
                attributes.Add(VectorEditPersistenceService.DeleteFeatureAttribute, "NAO");
                attributes.Add("_SYS_GRAVADO", "NAO");

                var pendingFeature = new Feature(part, attributes);
                long temporaryFid = ProximoFidTemporario();
                var key = new VectorFeatureKey(layer.Nome, temporaryFid, -1);
                var newState = new VectorFeatureEditState
                {
                    Key = key,
                    Baseline = part,
                    Current = part,
                    PendingFeature = pendingFeature,
                    IsDeleted = true,
                    IsNewFeature = true
                };
                _vectorFeatureEditStates.Add(key, newState);
                newStates.Add(newState);
                actions.Add(new VectorGeometryEditAction(groupId, key, part, part, BeforeDeleted: true, AfterDeleted: false));
            }

            await AplicarAcoesGeometriaAsync(actions, forward: true);
            RegistrarAcoesEdicao(actions);
            _vectorFeatureEditSelection.Clear();
            _vectorFeatureEditSelection.Add(state.Key);
            SelecionarFeicaoPrincipal(state.Key);
            HistoricoAquisicao.Clear();
            LimparCursorDesenho();
            LimparMetricasAquisicao();
            _vectorCutMode = false;
            _mostrarMenuContexto = false;
            TrocarFerramenta(ModoFerramenta.EdicaoVertice);
            _vectorEditMessage = $"Corte concluído em {pieces.Count} partes; a maior manteve o FID e as demais receberam novos FIDs ao salvar.";
            mensagemVetorizacao = string.Empty;
        }
        catch (Exception ex)
        {
            foreach (VectorFeatureEditState newState in newStates)
            {
                if (MapService.FeaturesPorCamada.TryGetValue(newState.Key.LayerName, out List<CompiledFeature>? features) &&
                    features.Any(feature => feature.FID == newState.Key.FeatureId))
                {
                    try { MapService.RemoverFeicaoVetorial(newState.Key.LayerName, newState.Key.FeatureId).Feature.Path?.Dispose(); }
                    catch (Exception rollbackError) { Console.Error.WriteLine($"Falha ao limpar feição temporária do corte: {rollbackError.Message}"); }
                }
                if (newState.DetachedFeature is { } detached) detached.Feature.Path?.Dispose();
                if (MapService.FeicoesOriginais.TryGetValue(newState.Key.LayerName, out FeatureCollection? collection))
                    collection.Remove(newState.PendingFeature);
                _vectorFeatureEditStates.Remove(newState.Key);
            }
            mensagemVetorizacao = $"O corte não foi aplicado; o esboço foi preservado. {MensagemPostgisSegura(ex)}";
            _vectorEditMessage = mensagemVetorizacao;
        }

        finally { _vectorEditOperationInProgress = false; }
        await InvokeAsync(StateHasChanged);
    }

    private void SelecionarVerticeSobCursor()
    {
        if (!PrepararInteracaoDeVertice()) return;
        if (_hoveredVectorVertexDisplayIndex < 0 || _hoveredVectorVertexDisplayIndex >= _selectedVectorVertices.Count)
        {
            _vectorEditMessage = "Passe o cursor sobre um vértice no mapa e tente novamente.";
            return;
        }

        int index = _hoveredVectorVertexDisplayIndex;
        VectorGeometryEditingService.Vertex vertex = _selectedVectorVertices[index];
        _selectedVectorVertexAddresses.Clear();
        _selectedVectorVertexAddresses.Add(vertex.Address);
        _selectedVectorVertexDisplayIndex = index;
        _vectorVertexCoordinateX = vertex.X;
        _vectorVertexCoordinateY = vertex.Y;
        MapService.DefinirIndiceVerticeEdicao(index);
        AtualizarIndicesVerticesEdicaoSelecionados();
        _vectorEditMessage = $"Vértice {index + 1} selecionado para edição por coordenadas.";
    }

    private bool SelecionarVerticeNoPonto(SKPoint point, SKMatrix matrix, bool add, bool remove = false)
    {
        if (SelectedVectorFeatureState is null || _selectedVectorVertices.Count == 0) return false;
        double tolerance = 10d / Math.Max(0.0001, Math.Abs(matrix.ScaleX));
        double nearestDistance = tolerance * tolerance;
        int nearest = -1;
        if (_selectedVectorVertexSpatialIndex is { } spatialIndex)
        {
            double worldX = point.X + MapService.OffsetMundoX;
            double worldY = MapService.OffsetMundoY - point.Y;
            nearest = spatialIndex.FindNearest(worldX, worldY, tolerance);
        }
        else for (int i = 0; i < _selectedVectorVertices.Count; i++) ConsiderVertex(i);

        void ConsiderVertex(int index)
        {
            VectorGeometryEditingService.Vertex vertex = _selectedVectorVertices[index];
            double dx = point.X - (vertex.X - MapService.OffsetMundoX);
            double dy = point.Y - (MapService.OffsetMundoY - vertex.Y);
            double distance = dx * dx + dy * dy;
            if (distance > nearestDistance) return;
            nearest = index;
            nearestDistance = distance;
        }

        if (nearest < 0)
        {
            if (add || remove) _vectorEditMessage = "Clique próximo a um nó para alterar a seleção de vértices.";
            return false;
        }

        VectorGeometryEditingService.VertexAddress address = _selectedVectorVertices[nearest].Address;
        if (remove)
        {
            if (!_selectedVectorVertexAddresses.Remove(address)) return true;
        }
        else if (add)
        {
            if (_selectedVectorVertexAddresses.Contains(address)) return true;
            _selectedVectorVertexAddresses.Add(address);
        }
        else
        {
            _selectedVectorVertexAddresses.Clear();
            _selectedVectorVertexAddresses.Add(address);
        }

        if (_selectedVectorVertexAddresses.Count == 0)
        {
            _selectedVectorVertexDisplayIndex = -1;
            _vectorVertexCoordinateX = null;
            _vectorVertexCoordinateY = null;
            MapService.DefinirIndiceVerticeEdicao(-1);
        }
        else
        {
            int anchorIndex = nearest;
            if (!_selectedVectorVertexAddresses.Contains(address))
            {
                VectorGeometryEditingService.VertexAddress anchorAddress = _selectedVectorVertexAddresses[^1];
                anchorIndex = -1;
                for (int i = 0; i < _selectedVectorVertices.Count; i++)
                    if (_selectedVectorVertices[i].Address == anchorAddress) anchorIndex = i;
            }
            SelecionarVerticeEdicao(anchorIndex, _selectedVectorVertexAddresses.ToArray());
        }
        AtualizarIndicesVerticesEdicaoSelecionados();
        _vectorEditMessage = _selectedVectorVertexAddresses.Count switch
        {
            0 => "Seleção de nós limpa.",
            1 => "1 nó selecionado. Shift adiciona; Ctrl remove; arraste para mover.",
            int count => $"{count} nós selecionados. Arraste um nó selecionado ou edite as coordenadas para mover o grupo."
        };
        return true;
    }

    private async Task AplicarCoordenadasVerticeAsync()
    {
        if (SelectedVectorFeatureState is not { } state || !CanApplyVectorVertexCoordinates) return;

        int index = _selectedVectorVertexDisplayIndex;
        VectorGeometryEditingService.Vertex selectedVertex = _selectedVectorVertices[index];
        VectorGeometryEditingService.VertexAddress[] addresses = _selectedVectorVertexAddresses
            .Contains(selectedVertex.Address)
            ? _selectedVectorVertexAddresses.ToArray()
            : [selectedVertex.Address];
        Geometry before = state.Current;
        _vectorEditOperationInProgress = true;
        _vectorEditMessage = "Validando as coordenadas do vértice…";
        await InvokeAsync(StateHasChanged);
        try
        {
            double targetX = _vectorVertexCoordinateX!.Value;
            double targetY = _vectorVertexCoordinateY!.Value;
            double deltaX = targetX - selectedVertex.X;
            double deltaY = targetY - selectedVertex.Y;
            Geometry after = await Task.Run(() => addresses.Length > 1
                ? VectorGeometryEditingService.MoveVerticesByDelta(before, addresses, deltaX, deltaY)
                : VectorGeometryEditingService.MoveVertex(before, selectedVertex.Address, targetX, targetY));
            if (before.EqualsExact(after))
            {
                _vectorEditMessage = "As coordenadas informadas são iguais à posição atual.";
                return;
            }

            await PublicarGeometriaEdicaoAsync(state, after);
            RegistrarAcaoGeometria(state.Key, before, after);
            SelecionarVerticeEdicao(index, addresses);
            _vectorEditMessage = $"Coordenadas do vértice {index + 1} aplicadas · salve as edições para gravar na fonte.";
        }
        catch (Exception ex)
        {
            _vectorEditMessage = $"As coordenadas não foram aplicadas: {MensagemPostgisSegura(ex)}";
        }
        finally { _vectorEditOperationInProgress = false; }
        await InvokeAsync(StateHasChanged);
    }

    private void SelecionarVerticeEdicao(
        int index, IEnumerable<VectorGeometryEditingService.VertexAddress>? addresses = null)
    {
        if (index < 0 || index >= _selectedVectorVertices.Count) return;
        VectorGeometryEditingService.Vertex vertex = _selectedVectorVertices[index];
        _selectedVectorVertexAddresses.Clear();
        foreach (VectorGeometryEditingService.VertexAddress address in addresses ?? [vertex.Address])
            if (_selectedVectorVertices.Any(candidate => candidate.Address == address) &&
                !_selectedVectorVertexAddresses.Contains(address))
                _selectedVectorVertexAddresses.Add(address);
        if (!_selectedVectorVertexAddresses.Contains(vertex.Address))
            _selectedVectorVertexAddresses.Add(vertex.Address);
        _selectedVectorVertexDisplayIndex = index;
        _vectorVertexCoordinateX = vertex.X;
        _vectorVertexCoordinateY = vertex.Y;
        MapService.DefinirIndiceVerticeEdicao(index);
        AtualizarIndicesVerticesEdicaoSelecionados();
    }

    private void LimparSelecaoVerticeEdicao()
    {
        _selectedVectorVertexAddresses.Clear();
        _selectedVectorVertexDisplayIndex = -1;
        _vectorVertexCoordinateX = null;
        _vectorVertexCoordinateY = null;
        MapService.DefinirIndicesVerticesEdicaoSelecionados(Array.Empty<int>());
    }

    private void AlternarModoInserirVertice()
    {
        if (!PrepararInteracaoDeVertice()) return;
        _insertVectorVertexMode = !_insertVectorVertexMode;
        _vectorEditMessage = _insertVectorVertexMode
            ? "Inserção ativa · clique próximo ao meio de uma aresta da feição selecionada."
            : "Inserção de vértices desativada.";
    }

    private bool PrepararInteracaoDeVertice()
    {
        if (SelectedVectorFeatureState is not { IsDeleted: false })
        {
            _vectorEditMessage = "Selecione uma feição vetorial ativa antes de editar vértices.";
            return false;
        }
        if (!CamadaAtivaVetorialDisponivel)
        {
            _ribbonCommandFeedback = OrientacaoCamadaAtiva;
            return false;
        }
        if (_coordenadaAbsolutaTravada)
        {
            _ribbonCommandFeedback = "Libere a coordenada F6 antes de entrar na edição de vértices.";
            return false;
        }
        if (_vectorFeatureSelectionMode || _ferramentaAtiva != ModoFerramenta.EdicaoVertice)
        {
            _vectorEditMessage = "Ative Editar vértices depois de selecionar a feição para usar esta ferramenta.";
            return false;
        }

        return true;
    }

    private void ArmarReposicionamentoVerticeSelecionado()
    {
        if (_vectorMoveFeatureMode || SelectedVectorFeatureState is not { IsDeleted: false } state ||
            _selectedVectorVertexDisplayIndex < 0 || _selectedVectorVertexDisplayIndex >= _selectedVectorVertices.Count)
            return;

        VectorGeometryEditingService.Vertex vertex = _selectedVectorVertices[_selectedVectorVertexDisplayIndex];
        _draggedVectorVertex = vertex.Address;
        _draggedVectorVertexDisplayIndex = _selectedVectorVertexDisplayIndex;
        _draggedVectorVertexAddresses = _selectedVectorVertexAddresses.Contains(vertex.Address)
            ? _selectedVectorVertexAddresses.ToArray()
            : [vertex.Address];
        _draggedVectorVertexOriginal = new Coordinate(vertex.X, vertex.Y);
        _geometryAtDragStart = state.Current;
        _geometryDragPreview = state.Current;
        _draggingWholeVectorFeature = false;
        _vectorEditPlacementArmed = true;
        _vectorEditCommitOnRelease = false;
        JSRuntime.InvokeVoidAsyncSafe("mapEngine.setVertexEditPlacementArmed", true);
        JSRuntime.InvokeVoidAsyncSafe("mapEngine.setVertexEditDragging", false);
        JSRuntime.InvokeVoidAsyncSafe("GeoNexGraphics.iniciarReposicionamentoVerticeEdicao",
            _selectedVectorVertexDisplayIndex);
        MapService.DefinirIndiceVerticeEdicao(_selectedVectorVertexDisplayIndex);
        _vectorEditMessage = $"Vértice {_selectedVectorVertexDisplayIndex + 1} preso ao cursor · clique para aplicar; Esc cancela.";
    }

    private async Task InserirVerticeSelecionadoAsync(double x, double y, double pixelScale)
    {
        if (_vectorEditOperationInProgress || SelectedVectorFeatureState is not { } state) return;
        Geometry before = state.Current;
        _vectorEditOperationInProgress = true;
        try
        {
            double tolerance = 10d / Math.Max(0.0001, Math.Abs(pixelScale));
            VectorGeometryEditingService.VertexInsertion insertion =
                await Task.Run(() => VectorGeometryEditingService.InsertVertexAtPoint(before, x, y, tolerance));
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
        finally { _vectorEditOperationInProgress = false; }
        await InvokeAsync(StateHasChanged);
    }

    private async Task RemoverVerticeSelecionadoAsync()
    {
        if (_vectorEditOperationInProgress || SelectedVectorFeatureState is not { } state || !CanRemoveHoveredVectorVertex) return;
        Geometry before = state.Current;
        VectorGeometryEditingService.VertexAddress address = _selectedVectorVertices[_hoveredVectorVertexDisplayIndex].Address;
        _vectorEditOperationInProgress = true;
        try
        {
            Geometry after = await Task.Run(() => VectorGeometryEditingService.RemoveVertex(before, address));
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
        finally { _vectorEditOperationInProgress = false; }
        await InvokeAsync(StateHasChanged);
    }

    private async Task RemoverVerticesSelecionadosAsync()
    {
        if (_vectorEditOperationInProgress || SelectedVectorFeatureState is not { } state || !CanRemoveSelectedVectorVertices) return;
        Geometry before = state.Current;
        VectorGeometryEditingService.VertexAddress[] addresses = _selectedVectorVertexAddresses.ToArray();
        _vectorEditOperationInProgress = true;
        try
        {
            Geometry after = await Task.Run(() => VectorGeometryEditingService.RemoveVertices(before, addresses));
            await PublicarGeometriaEdicaoAsync(state, after);
            RegistrarAcaoGeometria(state.Key, before, after);
            _hoveredVectorVertexDisplayIndex = -1;
            MapService.DefinirIndiceVerticeEdicao(-1);
            _vectorEditMessage = $"{addresses.Length} vértice(s) removido(s) da feição {state.Key.SourceFeatureId}; salve para gravar na fonte.";
        }
        catch (Exception ex)
        {
            _vectorEditMessage = $"Os vértices selecionados não foram removidos: {MensagemPostgisSegura(ex)}";
        }
        finally { _vectorEditOperationInProgress = false; }
        await InvokeAsync(StateHasChanged);
    }

    private async Task RemoverAnelInternoSobCursorAsync()
    {
        if (_vectorEditOperationInProgress || SelectedVectorFeatureState is not { } state || !CanRemoveHoveredVectorVertex) return;
        Geometry before = state.Current;
        VectorGeometryEditingService.VertexAddress address = _selectedVectorVertices[_hoveredVectorVertexDisplayIndex].Address;
        _vectorEditOperationInProgress = true;
        try
        {
            Geometry after = await Task.Run(() => VectorGeometryEditingService.RemoveInteriorRing(before, address));
            await PublicarGeometriaEdicaoAsync(state, after);
            RegistrarAcaoGeometria(state.Key, before, after);
            _hoveredVectorVertexDisplayIndex = -1;
            MapService.DefinirIndiceVerticeEdicao(-1);
            _vectorEditMessage = $"Anel interno removido da feição {state.Key.SourceFeatureId}; a área foi restaurada. Salve para gravar na fonte.";
        }
        catch (Exception ex)
        {
            _vectorEditMessage = ex.Message;
        }
        finally { _vectorEditOperationInProgress = false; }
        await InvokeAsync(StateHasChanged);
    }

    private async Task MesclarPoligonosSelecionadosAsync()
    {
        if (_vectorEditOperationInProgress || !CanMergeSelectedPolygons || _selectedVectorFeatureForEdit is not { } primaryKey) return;

        VectorFeatureEditState[] selectedStates = SelectedVectorFeatureStates.ToArray();
        int primaryIndex = Array.FindIndex(selectedStates, state => state.Key == primaryKey);
        if (primaryIndex < 0) return;

        _vectorEditOperationInProgress = true;
        _vectorEditMessage = "Mesclando e validando polígonos...";
        await InvokeAsync(StateHasChanged);
        try
        {
            Geometry[] sourceGeometries = selectedStates.Select(state => state.Current).ToArray();
            Geometry merged = await Task.Run(() => VectorGeometryEditingService.MergePolygons(sourceGeometries, primaryIndex));
            Guid groupId = Guid.NewGuid();
            var actions = new List<VectorGeometryEditAction>(selectedStates.Length);
            VectorFeatureEditState primary = selectedStates[primaryIndex];
            actions.Add(new VectorGeometryEditAction(
                groupId, primary.Key, primary.Current, merged, BeforeDeleted: false, AfterDeleted: false));
            foreach (VectorFeatureEditState state in selectedStates)
            {
                if (state.Key == primaryKey) continue;
                actions.Add(new VectorGeometryEditAction(
                    groupId, state.Key, state.Current, state.Current, BeforeDeleted: false, AfterDeleted: true));
            }

            await AplicarAcoesGeometriaAsync(actions, forward: true);
            RegistrarAcoesEdicao(actions);
            _vectorFeatureEditSelection.Clear();
            _vectorFeatureEditSelection.Add(primaryKey);
            SelecionarFeicaoPrincipal(primaryKey);
            _vectorEditMessage = $"{selectedStates.Length} polígonos mesclados na feição {primaryKey.SourceFeatureId} · os demais atributos foram descartados. Salve para gravar na fonte.";
        }
        catch (Exception ex)
        {
            _vectorEditMessage = $"Não foi possível mesclar os polígonos: {MensagemPostgisSegura(ex)}";
        }

        finally { _vectorEditOperationInProgress = false; }
        await InvokeAsync(StateHasChanged);
    }

    private async Task MesclarLinhasSelecionadasAsync()
    {
        if (_vectorEditOperationInProgress || !CanMergeSelectedLines || _selectedVectorFeatureForEdit is not { } primaryKey) return;

        VectorFeatureEditState[] selectedStates = SelectedVectorFeatureStates.ToArray();
        int primaryIndex = Array.FindIndex(selectedStates, state => state.Key == primaryKey);
        if (primaryIndex < 0) return;

        _vectorEditOperationInProgress = true;
        _vectorEditMessage = "Mesclando e validando linhas...";
        await InvokeAsync(StateHasChanged);
        try
        {
            Geometry[] sourceGeometries = selectedStates.Select(state => state.Current).ToArray();
            Geometry merged = await Task.Run(() => VectorGeometryEditingService.MergeLines(sourceGeometries, primaryIndex));
            Guid groupId = Guid.NewGuid();
            var actions = new List<VectorGeometryEditAction>(selectedStates.Length);
            VectorFeatureEditState primary = selectedStates[primaryIndex];
            actions.Add(new VectorGeometryEditAction(
                groupId, primary.Key, primary.Current, merged, BeforeDeleted: false, AfterDeleted: false));
            foreach (VectorFeatureEditState state in selectedStates)
            {
                if (state.Key == primaryKey) continue;
                actions.Add(new VectorGeometryEditAction(
                    groupId, state.Key, state.Current, state.Current, BeforeDeleted: false, AfterDeleted: true));
            }

            await AplicarAcoesGeometriaAsync(actions, forward: true);
            RegistrarAcoesEdicao(actions);
            _vectorFeatureEditSelection.Clear();
            _vectorFeatureEditSelection.Add(primaryKey);
            SelecionarFeicaoPrincipal(primaryKey);
            _vectorEditMessage = $"{selectedStates.Length} linhas mescladas na feição principal {primaryKey.SourceFeatureId}; salve para gravar na fonte.";
        }
        catch (Exception ex)
        {
            _vectorEditMessage = $"Não foi possível mesclar as linhas: {MensagemPostgisSegura(ex)}";
        }

        finally { _vectorEditOperationInProgress = false; }
        await InvokeAsync(StateHasChanged);
    }

    private async Task AplicarAcoesGeometriaAsync(IReadOnlyList<VectorGeometryEditAction> actions, bool forward)
    {
        var applied = new List<VectorGeometryEditAction>(actions.Count);
        try
        {
            foreach (VectorGeometryEditAction action in actions)
            {
                if (!_vectorFeatureEditStates.TryGetValue(action.Key, out VectorFeatureEditState? state))
                    throw new InvalidOperationException($"A feição {action.Key.SourceFeatureId} não está mais carregada para edição.");
                await PublicarEstadoGeometriaEdicaoAsync(
                    state,
                    forward ? action.After : action.Before,
                    forward ? action.AfterDeleted : action.BeforeDeleted);
                applied.Add(action);
            }
        }
        catch
        {
            foreach (VectorGeometryEditAction action in applied.AsEnumerable().Reverse())
            {
                if (!_vectorFeatureEditStates.TryGetValue(action.Key, out VectorFeatureEditState? state)) continue;
                try
                {
                    await PublicarEstadoGeometriaEdicaoAsync(
                        state,
                        forward ? action.Before : action.After,
                        forward ? action.BeforeDeleted : action.AfterDeleted);
                }
                catch (Exception rollbackError)
                {
                    Console.Error.WriteLine($"Falha ao reverter a operação vetorial {action.Key.SourceFeatureId}: {rollbackError.Message}");
                }
            }
            throw;
        }
    }

    private void RegistrarAcaoGeometria(VectorFeatureKey key, Geometry before, Geometry after)
        => RegistrarAcaoEdicao(key, before, after, beforeDeleted: false, afterDeleted: false);

    private void RegistrarAcaoEdicao(
        VectorFeatureKey key, Geometry before, Geometry after, bool beforeDeleted, bool afterDeleted, Guid? groupId = null)
    {
        if (_vectorGeometryEditHistoryCursor < _vectorGeometryEditHistory.Count)
            _vectorGeometryEditHistory.RemoveRange(
                _vectorGeometryEditHistoryCursor,
                _vectorGeometryEditHistory.Count - _vectorGeometryEditHistoryCursor);
        _vectorGeometryEditHistory.Add(new VectorGeometryEditAction(groupId ?? Guid.NewGuid(), key, before, after, beforeDeleted, afterDeleted));
        _vectorGeometryEditHistoryCursor++;
    }

    private void RegistrarAcoesEdicao(IReadOnlyCollection<VectorGeometryEditAction> actions)
    {
        if (actions.Count == 0) return;
        if (_vectorGeometryEditHistoryCursor < _vectorGeometryEditHistory.Count)
            _vectorGeometryEditHistory.RemoveRange(
                _vectorGeometryEditHistoryCursor,
                _vectorGeometryEditHistory.Count - _vectorGeometryEditHistoryCursor);
        _vectorGeometryEditHistory.AddRange(actions);
        _vectorGeometryEditHistoryCursor += actions.Count;
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

    private Task SimplificarFeicaoAsync() => AplicarTransformacaoSelecionadaAsync(
        geometry => VectorGeometryEditingService.Simplify(geometry, _vectorSimplificationTolerance),
        $"Simplificação topológica aplicada · tolerância {_vectorSimplificationTolerance:0.###} unidades do SRC");

    private Task DeslocarLinhaParaEsquerdaAsync() => AplicarTransformacaoSelecionadaAsync(
        geometry => VectorGeometryEditingService.OffsetLine(geometry, _vectorOffsetDistance),
        $"Linha deslocada {_vectorOffsetDistance:0.###} unidades para a esquerda do sentido digitalizado");

    private Task DeslocarLinhaParaDireitaAsync() => AplicarTransformacaoSelecionadaAsync(
        geometry => VectorGeometryEditingService.OffsetLine(geometry, -_vectorOffsetDistance),
        $"Linha deslocada {_vectorOffsetDistance:0.###} unidades para a direita do sentido digitalizado");

    private Task RefletirFeicaoVerticalmenteAsync() => AplicarTransformacaoSelecionadaAsync(
        VectorGeometryEditingService.ReflectAcrossVerticalAxis,
        "Reflexão no eixo vertical aplicada ao redor do centroide");

    private Task RefletirFeicaoHorizontalmenteAsync() => AplicarTransformacaoSelecionadaAsync(
        VectorGeometryEditingService.ReflectAcrossHorizontalAxis,
        "Reflexão no eixo horizontal aplicada ao redor do centroide");

    private async Task AplicarTransformacaoSelecionadaAsync(Func<Geometry, Geometry> transform, string successMessage)
    {
        if (_vectorEditOperationInProgress || SelectedVectorFeatureState is not { } state) return;
        Geometry before = state.Current;
        _vectorEditOperationInProgress = true;
        _vectorEditMessage = "Calculando a operação geométrica...";
        await InvokeAsync(StateHasChanged);
        try
        {
            Geometry after = await Task.Run(() => transform(before));
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
        finally { _vectorEditOperationInProgress = false; }
        await InvokeAsync(StateHasChanged);
    }

    private async Task RefazerEdicaoVetorialAsync()
    {
        if (!CanRedoVectorGeometryEdit || _vectorEditPointerPressed) return;
        Guid groupId = _vectorGeometryEditHistory[_vectorGeometryEditHistoryCursor].GroupId;
        int end = _vectorGeometryEditHistoryCursor + 1;
        while (end < _vectorGeometryEditHistory.Count && _vectorGeometryEditHistory[end].GroupId == groupId) end++;
        VectorGeometryEditAction[] actions = _vectorGeometryEditHistory
            .Skip(_vectorGeometryEditHistoryCursor)
            .Take(end - _vectorGeometryEditHistoryCursor)
            .ToArray();
        try
        {
            await AplicarAcoesGeometriaAsync(actions, forward: true);
        }
        catch (Exception ex)
        {
            _vectorEditMessage = $"Não foi possível refazer a edição: {MensagemPostgisSegura(ex)}";
            await InvokeAsync(StateHasChanged);
            return;
        }

        _vectorGeometryEditHistoryCursor = end;
        VectorGeometryEditAction? mergePrimary = actions.FirstOrDefault(action => !action.AfterDeleted);
        if (actions.Length > 1 && mergePrimary is not null && actions.Any(action => action.AfterDeleted && !action.BeforeDeleted))
        {
            _vectorFeatureEditSelection.Clear();
            _vectorFeatureEditSelection.Add(mergePrimary.Key);
            SelecionarFeicaoPrincipal(mergePrimary.Key);
        }
        else
        {
            VectorGeometryEditAction action = actions[0];
            VectorFeatureEditState state = _vectorFeatureEditStates[action.Key];
            if (state.IsDeleted && _selectedVectorFeatureForEdit == action.Key)
                LimparSelecaoEdicaoVetorial();
            else if (_selectedVectorFeatureForEdit == action.Key)
                AtualizarOverlayEdicaoVetorial(state.Current, -1);
        }
        string? groupLabel = actions.Select(action => action.GroupLabel).FirstOrDefault(label => !string.IsNullOrWhiteSpace(label));
        _vectorEditMessage = groupLabel is not null
            ? $"{groupLabel} refeito."
            : actions.Length > 1
            ? actions.Any(action => action.BeforeDeleted && !action.AfterDeleted)
                ? "Corte refeito."
                : "Mesclagem refeita."
            : $"Refeito · feição {actions[0].Key.SourceFeatureId}.";
        await InvokeAsync(StateHasChanged);
    }

    private void LimparSelecaoEdicaoVetorial()
    {
        if (_vectorEditPointerPressed) CancelarArrasteVerticeEdicao();
        _selectedVectorFeatureForEdit = null;
        LimparVerticesSelecionados();
        _vectorFeatureEditSelection.Clear();
        LimparSelecaoVerticeEdicao();
        _vectorEditMessage = _vectorFeatureSelectionMode
            ? "Seleção limpa · clique seleciona; Ctrl/Shift alterna feições. Arraste para selecionar por área."
            : "Clique em uma feição vetorial local para selecionar seus vértices.";
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
            LimparVerticesSelecionados();
            LimparSelecaoVerticeEdicao();
            MapService.LimparOverlayEdicao();
        }
        _vectorFeatureEditSelection.RemoveAll(removedKeys.Contains);
        foreach (VectorFeatureKey key in removedKeys)
        {
            VectorFeatureEditState state = _vectorFeatureEditStates[key];
            state.DetachedFeature?.Feature.Path?.Dispose();
            _vectorFeatureEditStates.Remove(key);
        }

        HashSet<Guid> removedGroups = _vectorGeometryEditHistory
            .Where(action => removedKeys.Contains(action.Key))
            .Select(action => action.GroupId)
            .ToHashSet();
        bool RemoverAcao(VectorGeometryEditAction action) => removedKeys.Contains(action.Key) || removedGroups.Contains(action.GroupId);
        int appliedRemaining = _vectorGeometryEditHistory.Take(_vectorGeometryEditHistoryCursor).Count(action => !RemoverAcao(action));
        _vectorGeometryEditHistory.RemoveAll(RemoverAcao);
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
                _vectorFeatureEditSelection.Remove(key);
                if (_selectedVectorFeatureForEdit == key)
                {
                    _selectedVectorFeatureForEdit = null;
                    LimparVerticesSelecionados();
                    LimparSelecaoVerticeEdicao();
                }
            }
            else
            {
                state.Baseline = state.Current;
            }
        }

        HashSet<Guid> savedGroups = _vectorGeometryEditHistory
            .Where(action => savedKeys.Contains(action.Key))
            .Select(action => action.GroupId)
            .ToHashSet();
        bool RemoverAcao(VectorGeometryEditAction action) => savedKeys.Contains(action.Key) || savedGroups.Contains(action.GroupId);
        int appliedRemaining = _vectorGeometryEditHistory.Take(_vectorGeometryEditHistoryCursor).Count(action => !RemoverAcao(action));
        _vectorGeometryEditHistory.RemoveAll(RemoverAcao);
        _vectorGeometryEditHistoryCursor = appliedRemaining;
    }

    private async Task RecarregarCamadaVetorialAposExclusaoAsync(
        Camada layer, IReadOnlyCollection<IFeature>? edicoesAindaPendentes = null)
    {
        if (string.IsNullOrWhiteSpace(layer.CaminhoArquivo) || !File.Exists(layer.CaminhoArquivo))
            throw new InvalidOperationException("Não foi possível atualizar os FIDs: a fonte vetorial não está disponível.");

        IFeature[] pendentes = edicoesAindaPendentes?.Where(feature => !VectorEditPersistenceService.IsPersisted(feature)).ToArray()
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
            if (VectorEditPersistenceService.IsNewFeature(pendingFeature))
            {
                Geometry newGeometry = pendingFeature.Geometry
                    ?? throw new InvalidDataException("Uma feição nova pendente não possui geometria.");
                long temporaryFid = ProximoFidTemporario();
                var newKey = new VectorFeatureKey(layer.Nome, temporaryFid, -1);
                var newState = new VectorFeatureEditState
                {
                    Key = newKey,
                    Baseline = newGeometry,
                    Current = newGeometry,
                    PendingFeature = pendingFeature,
                    IsNewFeature = true
                };
                _vectorFeatureEditStates[newKey] = newState;
                pendingCollection.Add(pendingFeature);
                var compiledNew = PostgisGeometryCompiler.Compile(
                    newGeometry, temporaryFid, layer.Nome, MapService.OffsetMundoX, MapService.OffsetMundoY);
                MapService.AdicionarFeicaoVetorial(layer.Nome, compiledNew);
                RegistrarAcaoEdicao(newKey, newGeometry, newGeometry, beforeDeleted: true, afterDeleted: false);
                continue;
            }

            if (!VectorEditPersistenceService.TryGetExistingFeatureId(pendingFeature, out long featureId))
                throw new InvalidDataException("Uma edição pendente perdeu o FID da fonte durante a atualização da camada.");
            CompiledFeature? compiled = MapService.FeaturesPorCamada.TryGetValue(layer.Nome, out List<CompiledFeature>? loaded)
                ? loaded.FirstOrDefault(feature => feature.SourceFeatureId == featureId)
                : null;
            if (compiled is null)
                throw new InvalidDataException($"A feição {featureId} editada não foi encontrada após atualizar a camada.");

            Geometry geometry = await Task.Run(() => VectorGeometryEditingService.ReadGeometry(
                sourcePath, featureId, MapService.ProjetoSRS));
            Geometry currentGeometry = pendingFeature.Geometry ?? geometry;
            bool isDeleted = VectorEditPersistenceService.IsDeletionPending(pendingFeature);
            var key = new VectorFeatureKey(layer.Nome, compiled.FID, featureId);
            var state = new VectorFeatureEditState
            {
                Key = key,
                Baseline = geometry,
                Current = currentGeometry,
                PendingFeature = pendingFeature,
                IsDeleted = isDeleted
            };
            pendingFeature.Geometry = currentGeometry;
            _vectorFeatureEditStates[key] = state;
            if (isDeleted)
                state.DetachedFeature = MapService.RemoverFeicaoVetorial(layer.Nome, compiled.FID);
            else if (!geometry.EqualsExact(currentGeometry))
                MapService.AtualizarGeometriaVetorial(layer.Nome, compiled.FID, currentGeometry);
            pendingCollection.Add(pendingFeature);
            RegistrarAcaoEdicao(key, geometry, currentGeometry, beforeDeleted: false, afterDeleted: isDeleted);
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
        if (_selectedVectorFeatureForEdit is not { } key ||
            !(_snapVectorEditVertices || _snapVectorEditEdges || _snapVectorEditMidpoints || _snapVectorEditIntersections))
        {
            MapService.PontoCursorSnap = null;
            MapService.PontoCursorSnapTipo = null;
            return cursor;
        }

        float tolerance = Math.Clamp(ToleranciaSnapPixels, 6, 30) /
            Math.Max(0.0001f, Math.Abs(matrix.ScaleX));
        HashSet<long>? ignoredFeatureIds = null;
        if (_propagateSelectedSharedVertices)
        {
            _snapIgnoredFeatureIds.Clear();
            foreach (VectorFeatureKey selectedKey in _vectorFeatureEditSelection)
            {
                if (selectedKey.LayerName != key.LayerName ||
                    !_vectorFeatureEditStates.TryGetValue(selectedKey, out VectorFeatureEditState? state) ||
                    state.IsDeleted)
                    continue;
                _snapIgnoredFeatureIds.Add(selectedKey.FeatureId);
            }
            ignoredFeatureIds = _snapIgnoredFeatureIds;
        }

        SKPoint? snap = MapService.EncontrarVerticeProximo(
            cursor, tolerance,
            _snapVectorEditVertices,
            _snapVectorEditEdges,
            checarMeios: _snapVectorEditMidpoints,
            checarIntersecoes: _snapVectorEditIntersections,
            ignorarCamada: key.LayerName,
            ignorarFid: key.FeatureId,
            ignorarFidsNaCamada: ignoredFeatureIds);
        MapService.PontoCursorSnap = snap;
        if (!snap.HasValue) MapService.PontoCursorSnapTipo = null;
        return snap ?? cursor;
    }

    private void LimparEstadoArrasteVertice()
    {
        _vectorEditPlacementArmed = false;
        _vectorEditCommitOnRelease = false;
        JSRuntime.InvokeVoidAsyncSafe("mapEngine.setVertexEditPlacementArmed", false);
        _draggedVectorVertex = null;
        _draggedVectorVertexAddresses = Array.Empty<VectorGeometryEditingService.VertexAddress>();
        _draggedVectorVertexOriginal = null;
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
        _vectorEditPlacementArmed = false;
        _vectorEditCommitOnRelease = false;
        JSRuntime.InvokeVoidAsyncSafe("mapEngine.setVertexEditPlacementArmed", false);
        JSRuntime.InvokeVoidAsyncSafe("mapEngine.setVertexEditDragging", false);
        LimparEstadoArrasteVertice();
        if (SelectedVectorFeatureState is { } state) AtualizarOverlayEdicaoVetorial(state.Current, -1);
    }

    private void ProcessarMovimentoEdicaoVetorial(
        SKPoint cursor, SKMatrix matrix, SKPoint? pointerStart, bool shiftVertexSelection)
    {
        if (_vectorFeatureSelectionMode || SelectedVectorFeatureState is null) return;
        AtualizarHoverVerticeEdicao(cursor, matrix, pointerStart, shiftVertexSelection);
    }
}
