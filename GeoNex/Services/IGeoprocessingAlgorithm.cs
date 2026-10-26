using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace GeoNex.Services
{
    [Flags]
    public enum TipoGeometriaPermitida
    {
        Nenhum = 0,
        Ponto = 1 << 0,
        Linha = 1 << 1,
        Poligono = 1 << 2,
        Qualquer = Ponto | Linha | Poligono
    }

    public enum TipoParametro
    {
        NumeroReal,
        NumeroInteiro,
        Booleano,
        OpcaoLista,
        CampoAtributo,
        Texto,
        DistanciaMetrica
    }

    public sealed record ParametroDefinicao(
        string Chave,
        string Nome,
        string Descricao,
        TipoParametro Tipo,
        object? ValorPadrao,
        double? Minimo = null,
        double? Maximo = null,
        string[]? OpcoesLista = null
    );

    /// <summary>
    /// Contrato para qualquer algoritmo modular da Processing Toolbox do GeoNex.
    /// </summary>
    public interface IGeoprocessingAlgorithm
    {
        string Id { get; }
        string Nome { get; }
        string Categoria { get; }
        string Descricao { get; }
        string[] Tags { get; }
        TipoGeometriaPermitida GeometriasEntrada { get; }
        bool RequerCamadaSobreposicao { get; }
        TipoGeometriaPermitida GeometriasSobreposicao { get; }
        IReadOnlyList<ParametroDefinicao> ParametrosAdicionais { get; }

        Task<List<IFeature>> ExecutarAsync(
            IReadOnlyList<IFeature> feicoesEntrada,
            IReadOnlyList<IFeature>? feicoesSobreposicao,
            IReadOnlyDictionary<string, object?> parametrosValores,
            IProgress<GeoprocessingProgress>? progresso,
            CancellationToken cancelamento);
    }
}
