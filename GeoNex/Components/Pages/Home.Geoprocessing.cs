using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GeoNex.Services;
using Microsoft.JSInterop;

namespace GeoNex.Components.Pages;

public partial class Home
{
    // =========================================================================
    // CATÁLOGO E METADADOS DA CAIXA DE FERRAMENTAS DE GEOPROCESSAMENTO
    // =========================================================================
    public sealed record GeoToolMetadata(
        TipoOperacaoGeoprocessamento Operacao,
        string Nome,
        string Categoria,
        string Subtitulo,
        string IconeSvg,
        bool RequerSobreposicao,
        string DescricaoDetalhada
    );

    private static readonly GeoToolMetadata[] CatalogoGeoprocessamento =
    [
        // 1. Superposição Topológica (Overlay)
        new(TipoOperacaoGeoprocessamento.Recortar, "Recortar Camada (Clip)", "Superposição Topológica", "Corte por máscara poligonal", "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M14 2v6h6", true,
            "Recorta as feições de entrada delimitando-as exatamente pela geometria externa da camada de máscara. Preserva os atributos originais da camada de entrada."),
        new(TipoOperacaoGeoprocessamento.Interseccao, "Intersecção Espacial", "Superposição Topológica", "Cruzamento e fusão de atributos", "circle cx 9 cy 9 r 7 circle cx 15 cy 15 r 7", true,
            "Calcula a área de sobreposição compartilhada entre duas camadas e funde as colunas de atributos de ambas as tabelas para as áreas comuns."),
        new(TipoOperacaoGeoprocessamento.Diferenca, "Diferença Espacial (Erase)", "Superposição Topológica", "Subtração booleana de áreas", "rect x 3 y 3 width 18 height 18 rx 2", true,
            "Remove da camada de entrada todas as porções geográficas que sobrepõem a camada de subtração/máscara."),
        new(TipoOperacaoGeoprocessamento.DiferencaSimetrica, "Diferença Simétrica (XOR)", "Superposição Topológica", "Áreas exclusivas de cada camada", "path d M3 3h18v18H3z", true,
            "Extrai todas as áreas que ocorrem em apenas uma das camadas de entrada, eliminando a intersecção mútua."),
        new(TipoOperacaoGeoprocessamento.UniaoPlena, "União Topológica Plena", "Superposição Topológica", "Fusão e partição completa", "path d M2 2h10v10H2z M12 12h10v10H12z", true,
            "Combina todas as geometrias das duas camadas, particionando-as em todas as intersecções mútuas com agregação de atributos."),
        new(TipoOperacaoGeoprocessamento.AtualizacaoCamadas, "Atualização Espacial (Patch)", "Superposição Topológica", "Sobreposição prioritária", "rect x 4 y 4 width 16 height 16", true,
            "Substitui as geometrias da camada de entrada pelas geometrias da camada prioritária (patch) nas áreas onde há sobreposição."),

        // 2. Agregação & Dissolução
        new(TipoOperacaoGeoprocessamento.Dissolver, "Dissolver Geometrias", "Agregação e Fusão", "Funde feições e fronteiras internas", "rect x 3 y 3 width 18 height 18", false,
            "Funde geometrias adjacentes ou que compartilham o mesmo valor de atributo, eliminando as linhas divisórias internas."),

        // 3. Geometrias Construtivas & Buffers
        new(TipoOperacaoGeoprocessamento.Buffer, "Zona de Amortecimento (Buffer)", "Geometrias Construtivas", "Faixas de afastamento equidistantes", "circle cx 12 cy 12 r 10 circle cx 12 cy 12 r 4", false,
            "Gera zonas de amortecimento métricas ao redor de pontos, linhas ou polígonos com suporte a terminações retas, curvas e cantos chanfrados."),
        new(TipoOperacaoGeoprocessamento.EnvoltoriaConvexa, "Envoltória Convexa (Convex Hull)", "Geometrias Construtivas", "Menor polígono convexo envolvente", "polygon points 12 2 22 8.5 22 15.5 12 22 2 15.5 2 8.5", false,
            "Calcula o menor polígono convexo que engloba um conjunto de geometrias (efeito elástico esticado ao redor dos pontos extremos)."),
        new(TipoOperacaoGeoprocessamento.Centroides, "Centroides e Polos (Polylabel)", "Geometrias Construtivas", "Ponto de massa ou polo interno", "circle cx 12 cy 12 r 3 line x1 12 y1 2 x2 12 y2 22 line x1 2 y1 12 x2 22 y2 12", false,
            "Extrai pontos centrais de feições. Permite ativar o Polo de Inacessibilidade (Polylabel) para garantir ponto 100% interno em polígonos côncavos."),

        // 4. Proximidade & Relevo
        new(TipoOperacaoGeoprocessamento.TriangulacaoDelaunay, "Triangulação de Delaunay (TIN)", "Proximidade e Relevo", "Malha triangular de relevo 3D", "polygon points 12 3 2 21 22 21 line x1 12 y1 3 x2 12 y2 21", false,
            "Cria a malha triangular irregular de Delaunay para modelagem contínua de superfícies topográficas e interpolação tridimensional."),
        new(TipoOperacaoGeoprocessamento.Voronoi, "Polígonos de Voronoi (Thiessen)", "Proximidade e Relevo", "Áreas de influência mais próxima", "polygon points 12 2 21 8 18 19 6 19 3 8", false,
            "Gera diagramas de Thiessen onde cada polígono delimita a região do plano mais próxima de cada ponto gerador."),
        new(TipoOperacaoGeoprocessamento.VizinhoMaisProximo, "Vizinho Mais Próximo (Clark-Evans)", "Proximidade e Relevo", "Linhas de conexão mais próximas", "line x1 4 y1 4 x2 20 y2 20 circle cx 4 cy 4 r 2 circle cx 20 cy 20 r 2", false,
            "Conecta cada feição à sua feição geometricamente mais próxima, permitindo calcular o índice de agregação espacial de Clark-Evans."),

        // 5. Generalização Cartográfica
        new(TipoOperacaoGeoprocessamento.SimplificacaoDouglasPeucker, "Simplificação Douglas-Peucker", "Generalização Cartográfica", "Redução por tolerância linear", "polyline points 2 12 8 5 16 19 22 12", false,
            "Reduz o número de vértices de polilinhas pesadas mantendo a integridade morfológica geral através do algoritmo de desvio perpendicular."),
        new(TipoOperacaoGeoprocessamento.SimplificacaoVisvalingam, "Simplificação Visvalingam-Whyatt", "Generalização Cartográfica", "Preserva curvaturas de rios", "path d M2 12 C8 4 16 20 22 12", false,
            "Elimina vértices que formam os triângulos de menor área efetiva, preservando a estética de rios, taludes e curvas de nível."),
        new(TipoOperacaoGeoprocessamento.SuavizacaoChaikin, "Suavização Cartográfica Chaikin", "Generalização Cartográfica", "Arredonda cantos pontiagudos", "path d M2 12 Q 12 2 22 12", false,
            "Aplica o algoritmo de Chaikin gerando cantos arredondados e transições de concordância suaves em curvas de nível e estradas."),

        // 6. Decomposição e Conversão
        new(TipoOperacaoGeoprocessamento.ExplodirMultipartes, "Decompor Multipartes para Simples", "Decomposição e Conversão", "Separa ilhas e coleções", "rect x 3 y 3 width 7 height 7 rect x 14 y 14 width 7 height 7", false,
            "Converte geometrias MultiPolygon e MultiLineString em geometrias simples individuais com replicação dos atributos originais."),
        new(TipoOperacaoGeoprocessamento.AgruparPorAtributo, "Agrupar Partes em Multipartes", "Decomposição e Conversão", "Funde feições por ID comum", "rect x 4 y 4 width 16 height 16", false,
            "Agrupa geometrias individuais que possuem o mesmo valor de chave de atributo em uma única feição composta multipartes."),
        new(TipoOperacaoGeoprocessamento.PoligonosParaLinhas, "Polígonos para Linhas de Borda", "Decomposição e Conversão", "Extrai perímetros como linhas", "rect x 4 y 4 width 16 height 16 rx 2 fill none stroke currentColor", false,
            "Extrai as fronteiras externas e os anéis internos de polígonos como polilinhas vetoriais independentes."),
        new(TipoOperacaoGeoprocessamento.PoligonizacaoLinhas, "Poligonização de Linhas (Polygonizer)", "Decomposição e Conversão", "Fecha anéis de linhas em polígonos", "polygon points 4 4 20 4 20 20 4 20", false,
            "Identifica anéis fechados em redes de linhas (como projetos de loteamento ou cadastros antigos) e cria polígonos com atributos."),
        new(TipoOperacaoGeoprocessamento.ExtrairVertices, "Extração Analítica de Vértices", "Decomposição e Conversão", "Gera pontos em cada nó geométrico", "circle cx 5 cy 5 r 2 circle cx 19 cy 5 r 2 circle cx 12 cy 19 r 2", false,
            "Converte cada vértice de linhas ou polígonos em um ponto individual contendo ordem, coordenadas UTM X/Y e cota Z."),

        // 7. Segmentação Linear e Partição
        new(TipoOperacaoGeoprocessamento.QuebraLinhasInterseccoes, "Quebrar Linhas em Intersecções", "Segmentação Linear", "Segmenta eixos nos cruzamentos", "line x1 4 y1 12 x2 20 y2 12 line x1 12 y1 4 x2 12 y2 20", false,
            "Segmenta redes viárias ou de drenagem dividindo as linhas exatamente em todos os nós onde há encontro ou cruzamento geométrico."),
        new(TipoOperacaoGeoprocessamento.EstaqueamentoLinhas, "Estaqueamento por Comprimento", "Segmentação Linear", "Gera pontos em distâncias fixas", "line x1 2 y1 12 x2 22 y2 12 circle cx 7 cy 12 r 2 circle cx 17 cy 12 r 2", false,
            "Gera estacas ou pontos ao longo de eixos de estradas e ferrovias em distâncias regulares pré-definidas (ex: 20 em 20 metros)."),
        new(TipoOperacaoGeoprocessamento.ParticaoPoligonosPorLinhas, "Partição de Polígonos por Linhas", "Segmentação Linear", "Divide glebas por eixos lineares", "rect x 4 y 4 width 16 height 16 line x1 4 y1 4 x2 20 y2 20", true,
            "Utiliza linhas (estradas, rios ou cercas) como navalhas de corte para dividir glebas em múltiplos polígonos independentes."),

        // 8. Malhas Espaciais e Amostragem
        new(TipoOperacaoGeoprocessamento.GradeCartesiana, "Grade Regular Cartesiana (Grids)", "Malhas e Amostragem", "Malhas quadrangulares UTM", "grid line x1 3 y1 9 x2 21 y2 9 line x1 3 y1 15 x2 21 y2 15", false,
            "Gera grades retangulares alinhadas aos eixos cartesianos com dimensões configuráveis de célula para pranchas e inventários."),
        new(TipoOperacaoGeoprocessamento.GradeHexagonal, "Grade Hexagonal Tesselada (Hexbins)", "Malhas e Amostragem", "Tesselação em colmeia regular", "polygon points 12 3 20 8 20 16 12 21 4 16 4 8", false,
            "Gera malhas hexagonais perfeitas com distância isotrópica constante para geoestatística e zoneamento de amostragem."),
        new(TipoOperacaoGeoprocessamento.AmostragemPoissonDisk, "Amostragem Poisson Disk", "Malhas e Amostragem", "Pontos aleatórios equidistantes", "circle cx 6 cy 8 r 2 circle cx 18 cy 7 r 2 circle cx 12 cy 16 r 2", false,
            "Distribui pontos aleatórios dentro de polígonos garantindo uma distância métrica mínima de isolamento mútuo entre quaisquer dois pontos."),

        // 9. Estatísticas Zonais & Joins
        new(TipoOperacaoGeoprocessamento.ContagemPontosPoligono, "Contagem de Pontos em Polígonos", "Estatísticas Zonais", "Agrega ocorrências em polígonos", "rect x 3 y 3 width 18 height 18 circle cx 8 cy 8 r 1.5 circle cx 14 cy 12 r 1.5", true,
            "Varre uma camada de pontos e contabiliza o total de ocorrências contidas dentro de cada feição poligonal, somando e calculando médias."),
        new(TipoOperacaoGeoprocessamento.DensidadeLinearPoligonos, "Densidade de Linhas por Polígono", "Estatísticas Zonais", "Soma extensão de linhas internas", "rect x 3 y 3 width 18 height 18 line x1 6 y1 6 x2 18 y2 18", true,
            "Calcula o comprimento total em metros de estradas, drenagens ou cercas contidas no interior de cada polígono."),
        new(TipoOperacaoGeoprocessamento.SpatialJoinAvancado, "Junção Espacial Avançada (Spatial Join)", "Estatísticas Zonais", "Une tabelas por proximidade topológica", "rect x 4 y 4 width 12 height 12 circle cx 16 cy 16 r 6", true,
            "Transfere colunas da tabela de uma camada para outra com base nas relações topológicas de Intersecção, Contenção ou Proximidade.")
    ];

    // =========================================================================
    // ESTADOS DE CONTROLE DA CAIXA DE FERRAMENTAS
    // =========================================================================
    private string _filtroBuscaGeoprocessamento = "";
    private string _abaAtivaDialogoGeo = "Parametros"; // "Parametros", "Ajuda", "Log"
    private readonly HashSet<string> _categoriasGeoRecolhidas = new(StringComparer.OrdinalIgnoreCase);

    // Parâmetros estendidos da Fase 2
    private double _geoToleranciaSimplificacao = 2.0;
    private double _geoAreaMinimaTriangulo = 15.0;
    private int _geoIteracoesSuavizacao = 2;
    private double _geoDistanciaEstaca = 20.0;
    private double _geoLarguraGrade = 100.0;
    private double _geoAlturaGrade = 100.0;
    private double _geoRaioHexagono = 100.0;
    private double _geoDistanciaMinimaAmostragem = 30.0;
    private int _geoMaxAmostrasPoisson = 500;
    private string _geoCampoValorAgregacaoZonal = "";
    private string _geoPredicadoTopologico = "Intersects";
    private double _geoDistanciaToleranciaSpatialJoin = 0.0;
    private double _geoMargemPercentualVoronoi = 0.2;

    public IEnumerable<GeoToolMetadata> ObterFerramentasFiltradas()
    {
        if (string.IsNullOrWhiteSpace(_filtroBuscaGeoprocessamento))
            return CatalogoGeoprocessamento;

        string termo = _filtroBuscaGeoprocessamento.Trim().ToLowerInvariant();
        return CatalogoGeoprocessamento.Where(t =>
            t.Nome.ToLowerInvariant().Contains(termo) ||
            t.Categoria.ToLowerInvariant().Contains(termo) ||
            t.Subtitulo.ToLowerInvariant().Contains(termo) ||
            t.DescricaoDetalhada.ToLowerInvariant().Contains(termo));
    }

    public IEnumerable<string> ObterCategoriasGeoprocessamento()
    {
        return ObterFerramentasFiltradas().Select(t => t.Categoria).Distinct();
    }

    public void AlternarRecolhimentoCategoriaGeo(string categoria)
    {
        if (_categoriasGeoRecolhidas.Contains(categoria))
            _categoriasGeoRecolhidas.Remove(categoria);
        else
            _categoriasGeoRecolhidas.Add(categoria);
    }

    public bool CategoriaGeoEstaRecolhida(string categoria) => _categoriasGeoRecolhidas.Contains(categoria);

    public GeoToolMetadata? ObterMetadadosFerramentaAtiva()
    {
        return CatalogoGeoprocessamento.FirstOrDefault(t => t.Operacao == _geoOperacaoAtiva);
    }
}
