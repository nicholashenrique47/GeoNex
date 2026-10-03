using GeoNex.Services;
using Microsoft.JSInterop;
using SkiaSharp;

namespace GeoNex.Components.Pages;

public partial class Home
{
    private int ToleranciaSnapPixels { get; set; } = 15;
    private bool _snapAquisicaoMeio;
    private bool _snapAquisicaoIntersecao;
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
        return DigitizingCursor.Resolve(
            cursor,
            matrix,
            Math.Clamp(ToleranciaSnapPixels, 6, 30),
            vertices,
            edges,
            anchor,
            !measuring && MapService.TravaDistanciaAtiva,
            MapService.TravaDistanciaValor,
            MapService.TravaModoFixo,
            (point, tolerance, snapVertices, snapEdges) =>
                MapService.EncontrarVerticeProximo(point, tolerance, snapVertices, snapEdges, midpoints, intersections),
            midpoints, intersections);
    }

    private void LimparCursorDesenho()
    {
        MapService.PontoCursorMundo = null;
        MapService.PontoCursorSnap = null;
        _ultimoSnapRenderizado = null;
    }

    [JSInvokable]
    public void ReceberMovimentoFerramentas(double pixelX, double pixelY)
    {
        if (exibirModalAtributos || _isPanning || !double.IsFinite(pixelX) || !double.IsFinite(pixelY)) return;
        if (_ferramentaAtiva is not (ModoFerramenta.Medicao or ModoFerramenta.AquisicaoPoligono or
            ModoFerramenta.AquisicaoLinha or ModoFerramenta.AquisicaoPonto)) return;

        SKMatrix matrix = ObterMatrizMatematica();
        if (!matrix.TryInvert(out SKMatrix inverse)) return;
        var result = ResolverCursorFerramenta(
            inverse.MapPoint(new SKPoint((float)pixelX, (float)pixelY)), matrix);
        MapService.PontoCursorMundo = result.Position;
        MapService.PontoCursorSnap = result.Snap;

        bool measuring = _ferramentaAtiva == ModoFerramenta.Medicao && MapService.PontosMedicao.Count > 0;
        bool snapChanged = !Nullable.Equals(_ultimoSnapRenderizado, result.Snap);
        _ultimoSnapRenderizado = result.Snap;
        if (measuring)
        {
            RecalcularMedicoes();
            StateHasChanged();
        }
        if (measuring || MapService.PontosAquisicao.Count > 0 || snapChanged) SolicitarNovoFrame();
    }

    private void RefazerUltimoPontoAquisicao() => AlterarHistoricoAquisicao(true);

    private void AlterarHistoricoAquisicao(bool redo)
    {
        if (!(redo ? HistoricoAquisicao.Redo() : HistoricoAquisicao.Undo())) return;
        mensagemVetorizacao = "";
        if (!redo) MapService.TravaDistanciaAtiva = false;
        LimparCursorDesenho();
        SolicitarNovoFrame();
        StateHasChanged();
    }

    private void AlterarHistoricoMedicao(bool redo)
    {
        if (!(redo ? HistoricoMedicao.Redo() : HistoricoMedicao.Undo())) return;
        LimparCursorDesenho();
        RecalcularMedicoes();
        _ = SincronizarOverlay(false);
        SolicitarNovoFrame();
        StateHasChanged();
    }
}
