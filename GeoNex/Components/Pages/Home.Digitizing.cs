using GeoNex.Services;
using Microsoft.JSInterop;
using SkiaSharp;

namespace GeoNex.Components.Pages;

public partial class Home
{
    private const int PreviewMetricsRefreshRateHz = 30;
    private bool _mostrarMenuPrincipal;
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
            _mostrarInputAbsoluto = true;
            _focoCoordenadaPendente = true;
        }
        _mostrarMenuContexto = false;
        StateHasChanged();
    }

    private void PosicionarEntradaFlutuante(bool polar, double clientX, double clientY)
    {
        bool posicaoValida = double.IsFinite(clientX) && double.IsFinite(clientY) && clientX >= 0 && clientY >= 0;
        double offset = polar ? (_mostrarInputAbsoluto ? 28 : 0) : (_mostrarInputCogo ? 28 : 0);
        double x = posicaoValida ? clientX + 14 + offset : (polar ? _hudCogoX : _hudX);
        double y = posicaoValida ? clientY + 14 + offset : (polar ? _hudCogoY : _hudY);
        x = Math.Max(8, x);
        y = Math.Max(64, y);

        if (polar) { _hudCogoX = x; _hudCogoY = y; }
        else { _hudX = x; _hudY = y; }
        _hudPosicaoPendente = true;
    }

    private void FecharEntradaPrecisa()
    {
        _mostrarInputCogo = _mostrarInputAbsoluto = _focoCogoPendente = _focoCoordenadaPendente = false;
    }

    private void FecharEntradaCogo() { _mostrarInputCogo = _focoCogoPendente = false; }
    private void FecharEntradaAbsoluta() { _mostrarInputAbsoluto = _focoCoordenadaPendente = false; }

    private void AlternarRestricaoPrecisa()
    {
        if (MapService.TravaDistanciaAtiva) { MapService.TravaDistanciaAtiva = false; mensagemVetorizacao = ""; return; }
        if (ConstrucaoParametrica) return;
        try
        {
            MapService.TravaDistanciaValor = MapService.Measurements.MetresToGridUnits(_restricaoDistanciaMetros ?? 0, MapService.ProjetoSRS);
            MapService.TravaDistanciaAtiva = true;
            mensagemVetorizacao = "Restrição ativa em metros de grade.";
        }
        catch (ArgumentException ex) { mensagemVetorizacao = ex.Message; }
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
        _ = SincronizarOverlay(false);
        SolicitarNovoFrame();
        StateHasChanged();
    }

    private void ContinuarMedicao()
    {
        _medicaoConcluida = false;
        _medicaoMensagem = "";
        StateHasChanged();
    }

    private void DefinirModoMedicao(bool area)
    {
        if (MapService.MostrarAreaMedicao == area) return;
        _medicaoConcluida = false;
        LimparCursorDesenho();
        MapService.MostrarAreaMedicao = area;
        RecalcularMedicoes();
        _ = SincronizarOverlay(false);
        SolicitarNovoFrame();
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
        if (!force && _ultimaAtualizacaoMetricasAquisicao != 0 &&
            agora - _ultimaAtualizacaoMetricasAquisicao < System.Diagnostics.Stopwatch.Frequency / 30)
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

    private bool CliqueFechaPoligono(SKPoint cliqueEmTela, SKMatrix matriz)
    {
        if (_ferramentaAtiva != ModoFerramenta.AquisicaoPoligono || ConstrucaoParametrica ||
            MapService.PontosAquisicao.Count < 3) return false;

        var primeiroVerticeEmTela = matriz.MapPoint(MapService.PontosAquisicao[0]);
        if (!float.IsFinite(primeiroVerticeEmTela.X) || !float.IsFinite(primeiroVerticeEmTela.Y)) return false;

        float tolerancia = Math.Clamp(ToleranciaSnapPixels, 6, 30);
        float dx = cliqueEmTela.X - primeiroVerticeEmTela.X;
        float dy = cliqueEmTela.Y - primeiroVerticeEmTela.Y;
        return dx * dx + dy * dy <= tolerancia * tolerancia;
    }

    private bool AdicionarControleAquisicao(SKPoint point)
    {
        if (!FerramentaDesenhoAtiva || string.IsNullOrEmpty(_camadaDestinoAquisicao) || _camadaDestinoAquisicao == "NOVA")
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
        mensagemVetorizacao = "";
        return true;
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
        MapService.PontoCursorMundo = null;
        MapService.PontoCursorSnap = null;
        MapService.PontoCursorSnapTipo = null;
        _ultimoSnapRenderizado = null;
    }

    [JSInvokable]
    public void ReceberMovimentoFerramentas(double pixelX, double pixelY, bool interacaoRapida = true)
    {
        if (_ferramentaAtiva == ModoFerramenta.Medicao && _medicaoConcluida) return;
        if (exibirModalAtributos || _isPanning || !double.IsFinite(pixelX) || !double.IsFinite(pixelY)) return;
        if (_ferramentaAtiva is not (ModoFerramenta.Medicao or ModoFerramenta.AquisicaoPoligono or
            ModoFerramenta.AquisicaoLinha or ModoFerramenta.AquisicaoPonto)) return;

        SKMatrix matrix = ObterMatrizMatematica();
        if (!matrix.TryInvert(out SKMatrix inverse)) return;
        var result = ResolverCursorFerramenta(
            inverse.MapPoint(new SKPoint((float)pixelX, (float)pixelY)), matrix);
        MapService.PontoCursorMundo = result.Position;
        MapService.PontoCursorSnap = result.Snap;
        if (!result.Snap.HasValue) MapService.PontoCursorSnapTipo = null;

        bool measuring = _ferramentaAtiva == ModoFerramenta.Medicao && MapService.PontosMedicao.Count > 0;
        bool snapChanged = !Nullable.Equals(_ultimoSnapRenderizado, result.Snap);
        _ultimoSnapRenderizado = result.Snap;
        if (measuring)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long minimumInterval = System.Diagnostics.Stopwatch.Frequency / PreviewMetricsRefreshRateHz;
            if (_ultimaAtualizacaoPreviewMedicao == 0 || now - _ultimaAtualizacaoPreviewMedicao >= minimumInterval)
            {
                _ultimaAtualizacaoPreviewMedicao = now;
                RecalcularMedicoes();
                StateHasChanged();
            }
        }
        else if (MapService.PontosAquisicao.Count > 0)
        {
            long previousMetrics = _ultimaAtualizacaoMetricasAquisicao;
            RecalcularMetricasAquisicao();
            if (_ultimaAtualizacaoMetricasAquisicao != previousMetrics) StateHasChanged();
        }
        if (measuring || MapService.PontosAquisicao.Count > 0 || snapChanged)
            SolicitarNovoFrame(interacaoRapida: interacaoRapida);
    }

    private void RefazerUltimoPontoAquisicao() => AlterarHistoricoAquisicao(true);

    private void AlterarHistoricoAquisicao(bool redo)
    {
        if (!(redo ? HistoricoAquisicao.Redo() : HistoricoAquisicao.Undo())) return;
        mensagemVetorizacao = "";
        if (!redo) MapService.TravaDistanciaAtiva = false;
        LimparCursorDesenho();
        RecalcularMetricasAquisicao(true);
        SolicitarNovoFrame();
        StateHasChanged();
    }

    private void AlterarHistoricoMedicao(bool redo)
    {
        if (!(redo ? HistoricoMedicao.Redo() : HistoricoMedicao.Undo())) return;
        _medicaoConcluida = false;
        LimparCursorDesenho();
        RecalcularMedicoes();
        _ = SincronizarOverlay(false);
        SolicitarNovoFrame();
        StateHasChanged();
    }
}
