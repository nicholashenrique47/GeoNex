using System;

namespace GeoNex.Services
{
    /// <summary>
    /// Tipos de operações espaciais suportadas pelo motor de geoprocessamento do GeoNex.
    /// </summary>
    public enum TipoOperacaoGeoprocessamento
    {
        // Fase 1: Fundamentais
        Recortar,
        Interseccao,
        Diferenca,
        Dissolver,
        Buffer,
        EnvoltoriaConvexa,
        Centroides,
        UniaoEspacial,

        // Fase 2: Proximidade & Tesselação
        Voronoi,
        TriangulacaoDelaunay,
        VizinhoMaisProximo,

        // Fase 2: Superposição Avançada
        DiferencaSimetrica,
        UniaoPlena,
        AtualizacaoCamadas,

        // Fase 2: Generalização Cartográfica
        SimplificacaoDouglasPeucker,
        SimplificacaoVisvalingam,
        SuavizacaoChaikin,

        // Fase 2: Decomposição & Conversão
        ExplodirMultipartes,
        AgruparPorAtributo,
        PoligonosParaLinhas,
        PoligonizacaoLinhas,
        ExtrairVertices,

        // Fase 2: Segmentação Linear
        QuebraLinhasInterseccoes,
        EstaqueamentoLinhas,
        ParticaoPoligonosPorLinhas,

        // Fase 2: Malhas & Amostragem
        GradeCartesiana,
        GradeHexagonal,
        AmostragemPoissonDisk,
        AmostragemRegularPontos,

        // Fase 2: Estatísticas Zonais
        ContagemPontosPoligono,
        DensidadeLinearPoligonos,
        SpatialJoinAvancado
    }

    /// <summary>
    /// Estilo de terminação das extremidades em linhas e curvas de buffer.
    /// </summary>
    public enum BufferEndCapStyle
    {
        Round = 1,
        Flat = 2,
        Square = 3
    }

    /// <summary>
    /// Estilo de junção das esquinas convexas em buffers de linhas e polígonos.
    /// </summary>
    public enum BufferJoinStyle
    {
        Round = 1,
        Mitre = 2,
        Bevel = 3
    }

    /// <summary>
    /// Estratégia de agregação para atributos numéricos durante a dissolução de geometrias.
    /// </summary>
    public enum ModoAgregacaoDissolve
    {
        PrimeiroValor,
        Soma,
        Media,
        Contagem
    }

    /// <summary>
    /// Parâmetros de entrada completos para execução de uma operação de geoprocessamento.
    /// </summary>
    public sealed record GeoprocessingParameters(
        TipoOperacaoGeoprocessamento Operacao,
        string NomeCamadaEntrada,
        string? NomeCamadaSobreposicao = null,
        bool ApenasSelecionadasEntrada = false,
        bool ApenasSelecionadasSobreposicao = false,
        double DistanciaBufferMetros = 10.0,
        string? CampoDistanciaBuffer = null,
        int SegmentosQuadranteBuffer = 16,
        BufferEndCapStyle EndCapBuffer = BufferEndCapStyle.Round,
        BufferJoinStyle JoinBuffer = BufferJoinStyle.Round,
        bool DissolverBuffers = false,
        string? CampoDissolucao = null,
        ModoAgregacaoDissolve AgregacaoDissolucao = ModoAgregacaoDissolve.PrimeiroValor,
        bool ModoEnvoltoriaGlobal = true,
        bool UsarPolylabelEmCentroides = true,
        double ToleranciaSnapMetros = 0.001,
        string? CaminhoArquivoSaida = null,
        bool AdicionarAoMapaAoConcluir = true,

        // Parâmetros estendidos da Fase 2
        double ToleranciaSimplificacaoMetros = 5.0,
        double AreaMinimaTrianguloM2 = 25.0,
        int IteracoesSuavizacao = 2,
        double DistanciaEstacaMetros = 20.0,
        double LarguraGradeMetros = 100.0,
        double AlturaGradeMetros = 100.0,
        double RaioHexagonoMetros = 100.0,
        double DistanciaMinimaAmostragemMetros = 50.0,
        int MaxAmostrasPoisson = 500,
        string? CampoValorAgregacaoZonal = null,
        string PredicadoTopologico = "Intersects",
        double DistanciaToleranciaSpatialJoin = 0.0,
        double MargemPercentualVoronoi = 0.2
    );

    /// <summary>
    /// Notificação contínua de progresso transmitida para a interface gráfica.
    /// </summary>
    public readonly record struct GeoprocessingProgress(
        double Percentual,
        string Mensagem,
        int FeicoesProcessadas,
        int TotalFeicoes,
        double FeicoesPorSegundo = 0.0
    );

    /// <summary>
    /// Resultado final retornado após a conclusão ou falha do processo espacial.
    /// </summary>
    public sealed record GeoprocessingResult(
        bool Sucesso,
        int TotalFeicoesCriadas,
        string? CaminhoCamadaGerada,
        string? MensagemErro = null,
        TimeSpan TempoDecorrido = default
    );
}
