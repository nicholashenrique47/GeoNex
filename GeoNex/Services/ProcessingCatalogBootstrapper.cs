using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;

namespace GeoNex.Services
{
    public delegate Task<List<IFeature>> GeoprocessingExecutionDelegate(
        IReadOnlyList<IFeature> feicoesEntrada,
        IReadOnlyList<IFeature>? feicoesSobreposicao,
        IReadOnlyDictionary<string, object?> parametrosValores,
        IProgress<GeoprocessingProgress>? progresso,
        CancellationToken cancelamento);

    public sealed class DelegateGeoprocessingAlgorithm : IGeoprocessingAlgorithm
    {
        private readonly GeoprocessingExecutionDelegate _executar;

        public DelegateGeoprocessingAlgorithm(
            string id,
            string nome,
            string categoria,
            string descricao,
            string[] tags,
            TipoGeometriaPermitida geometriasEntrada,
            bool requerSobreposicao,
            TipoGeometriaPermitida geometriasSobreposicao,
            IReadOnlyList<ParametroDefinicao> parametros,
            GeoprocessingExecutionDelegate executar)
        {
            Id = id;
            Nome = nome;
            Categoria = categoria;
            Descricao = descricao;
            Tags = tags;
            GeometriasEntrada = geometriasEntrada;
            RequerCamadaSobreposicao = requerSobreposicao;
            GeometriasSobreposicao = geometriasSobreposicao;
            ParametrosAdicionais = parametros;
            _executar = executar ?? throw new ArgumentNullException(nameof(executar));
        }

        public string Id { get; }
        public string Nome { get; }
        public string Categoria { get; }
        public string Descricao { get; }
        public string[] Tags { get; }
        public TipoGeometriaPermitida GeometriasEntrada { get; }
        public bool RequerCamadaSobreposicao { get; }
        public TipoGeometriaPermitida GeometriasSobreposicao { get; }
        public IReadOnlyList<ParametroDefinicao> ParametrosAdicionais { get; }

        public Task<List<IFeature>> ExecutarAsync(
            IReadOnlyList<IFeature> feicoesEntrada,
            IReadOnlyList<IFeature>? feicoesSobreposicao,
            IReadOnlyDictionary<string, object?> parametrosValores,
            IProgress<GeoprocessingProgress>? progresso,
            CancellationToken cancelamento)
        {
            return _executar(feicoesEntrada, feicoesSobreposicao, parametrosValores, progresso, cancelamento);
        }
    }

    /// <summary>
    /// Inicializador que popula o catálogo central de algoritmos com os 29 algoritmos do GeoNex.
    /// </summary>
    public static class ProcessingCatalogBootstrapper
    {
        public static void RegistrarTodos(ProcessingAlgorithmRegistry reg)
        {
            // === 1. FUNDAMENTAIS / OVERLAY FASE 1 ===
            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "foundation_clip",
                "Recortar Camada (Clip)",
                "Superposição Topológica",
                "Recorta a camada de entrada pelos limites externos da camada de máscara.",
                new[] { "clip", "recorte", "mascara", "overlay" },
                TipoGeometriaPermitida.Qualquer,
                true,
                TipoGeometriaPermitida.Poligono,
                Array.Empty<ParametroDefinicao>(),
                (entrada, sobrep, pars, prog, ct) =>
                {
                    var geoms = sobrep?.Where(s => s.Geometry != null && !s.Geometry.IsEmpty).Select(s => s.Geometry).ToList() ?? new List<Geometry>();
                    var uniao = CascadedPolygonUnion.Union(geoms);
                    return Task.FromResult(VectorOverlayAlgorithms.ExecutarRecorte(entrada, uniao, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "foundation_intersection",
                "Intersecção Espacial (Intersection)",
                "Superposição Topológica",
                "Calcula a porção comum entre duas camadas e funde suas tabelas de atributos.",
                new[] { "intersect", "interseccao", "overlay", "fusao" },
                TipoGeometriaPermitida.Qualquer,
                true,
                TipoGeometriaPermitida.Qualquer,
                Array.Empty<ParametroDefinicao>(),
                (entrada, sobrep, pars, prog, ct) =>
                {
                    return Task.FromResult(VectorOverlayAlgorithms.ExecutarInterseccao(entrada, sobrep ?? new List<IFeature>(), prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "foundation_difference",
                "Diferença Espacial (Erase)",
                "Superposição Topológica",
                "Subtrai da camada de entrada as regiões coincidentes com a camada de corte.",
                new[] { "difference", "diferenca", "erase", "subtracao" },
                TipoGeometriaPermitida.Qualquer,
                true,
                TipoGeometriaPermitida.Poligono,
                Array.Empty<ParametroDefinicao>(),
                (entrada, sobrep, pars, prog, ct) =>
                {
                    var geoms = sobrep?.Where(s => s.Geometry != null && !s.Geometry.IsEmpty).Select(s => s.Geometry).ToList() ?? new List<Geometry>();
                    var uniao = CascadedPolygonUnion.Union(geoms);
                    return Task.FromResult(VectorOverlayAlgorithms.ExecutarDiferenca(entrada, uniao, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "foundation_dissolve",
                "Dissolver Geometrias (Dissolve)",
                "Agregação e Fusão",
                "Une feições adjacentes ou com o mesmo atributo categórico via CascadedPolygonUnion.",
                new[] { "dissolve", "dissolver", "uniao", "agregacao" },
                TipoGeometriaPermitida.Linha | TipoGeometriaPermitida.Poligono,
                false,
                TipoGeometriaPermitida.Nenhum,
                new[] { new ParametroDefinicao("CampoDissolucao", "Campo de Agrupamento", "Campo para agregação categórica", TipoParametro.CampoAtributo, null) },
                (entrada, sobrep, pars, prog, ct) =>
                {
                    string? campo = pars.GetValueOrDefault("CampoDissolucao")?.ToString();
                    return Task.FromResult(VectorAggregationAlgorithms.ExecutarDissolucao(entrada, campo, ModoAgregacaoDissolve.PrimeiroValor, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "foundation_buffer",
                "Zona de Amortecimento (Buffer)",
                "Geometrias Construtivas",
                "Gera polígonos equidistantes em torno de pontos, linhas ou polígonos.",
                new[] { "buffer", "amortecimento", "faixa", "distancia" },
                TipoGeometriaPermitida.Qualquer,
                false,
                TipoGeometriaPermitida.Nenhum,
                new[] {
                    new ParametroDefinicao("DistanciaMetros", "Distância (m)", "Distância métrica do buffer", TipoParametro.DistanciaMetrica, 10.0, 0.01, 1000000.0),
                    new ParametroDefinicao("Dissolver", "Dissolver Resultados", "Une buffers sobrepostos", TipoParametro.Booleano, false)
                },
                (entrada, sobrep, pars, prog, ct) =>
                {
                    double dist = double.TryParse(pars.GetValueOrDefault("DistanciaMetros")?.ToString(), out var d) ? d : 10.0;
                    bool diss = bool.TryParse(pars.GetValueOrDefault("Dissolver")?.ToString(), out var b) && b;
                    return Task.FromResult(VectorGeometryAlgorithms.ExecutarBuffer(entrada, dist, null, 16, BufferEndCapStyle.Round, BufferJoinStyle.Round, diss, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "foundation_convex_hull",
                "Envoltória Convexa (Convex Hull)",
                "Geometrias Construtivas",
                "Calcula o menor polígono convexo envolvente para feições isoladas ou nuvem global.",
                new[] { "convex hull", "envoltoria", "casca", "poligono" },
                TipoGeometriaPermitida.Qualquer,
                false,
                TipoGeometriaPermitida.Nenhum,
                new[] { new ParametroDefinicao("ModoGlobal", "Envoltória Global", "Gera uma única envoltória para todas as feições", TipoParametro.Booleano, true) },
                (entrada, sobrep, pars, prog, ct) =>
                {
                    bool global = !bool.TryParse(pars.GetValueOrDefault("ModoGlobal")?.ToString(), out var g) || g;
                    return Task.FromResult(VectorGeometryAlgorithms.ExecutarEnvoltoriaConvexa(entrada, global, null, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "foundation_centroids",
                "Extrair Centroides (Centroids)",
                "Geometrias Construtivas",
                "Calcula o centro de massa ponderado ou polo de inacessibilidade (Polylabel).",
                new[] { "centroid", "centroide", "polylabel", "ponto central" },
                TipoGeometriaPermitida.Linha | TipoGeometriaPermitida.Poligono,
                false,
                TipoGeometriaPermitida.Nenhum,
                new[] { new ParametroDefinicao("Polylabel", "Polo de Inacessibilidade", "Garante ponto estritamente no interior", TipoParametro.Booleano, true) },
                (entrada, sobrep, pars, prog, ct) =>
                {
                    bool poly = !bool.TryParse(pars.GetValueOrDefault("Polylabel")?.ToString(), out var pl) || pl;
                    return Task.FromResult(VectorGeometryAlgorithms.ExecutarCentroides(entrada, poly, prog, ct));
                }));

            // === 2. PROXIMIDADE E TESSELAÇÃO (FASE 2) ===
            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "proximity_voronoi",
                "Polígonos de Voronoi (Thiessen)",
                "Análise de Proximidade e Tesselação",
                "Gera polígonos de Dirichlet/Thiessen a partir de pontos com herança integral de atributos.",
                new[] { "voronoi", "thiessen", "tesselacao", "proximidade", "dirichlet" },
                TipoGeometriaPermitida.Ponto,
                false,
                TipoGeometriaPermitida.Poligono,
                new[] { new ParametroDefinicao("MargemPercentual", "Margem de Expansão", "Fração de folga do envelope (ex: 0.2)", TipoParametro.NumeroReal, 0.2, 0.01, 5.0) },
                (entrada, sobrep, pars, prog, ct) =>
                {
                    double margem = double.TryParse(pars.GetValueOrDefault("MargemPercentual")?.ToString(), out var m) ? m : 0.2;
                    Geometry? masc = sobrep?.FirstOrDefault(s => s.Geometry != null)?.Geometry;
                    return Task.FromResult(ProximityTessellationAlgorithms.ExecutarVoronoi(entrada, masc, margem, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "proximity_delaunay",
                "Triangulação de Delaunay (TIN)",
                "Análise de Proximidade e Tesselação",
                "Gera rede irregular de triângulos planos conformes (TIN) a partir de coordenadas 2D/3D.",
                new[] { "delaunay", "tin", "triangulacao", "malha", "terreno" },
                TipoGeometriaPermitida.Ponto,
                false,
                TipoGeometriaPermitida.Nenhum,
                new[] { new ParametroDefinicao("Tolerancia", "Tolerância de Ponto", "Distância de colapso de nós coincidentes", TipoParametro.NumeroReal, 0.0, 0.0, 100.0) },
                (entrada, sobrep, pars, prog, ct) =>
                {
                    double tol = double.TryParse(pars.GetValueOrDefault("Tolerancia")?.ToString(), out var t) ? t : 0.0;
                    return Task.FromResult(ProximityTessellationAlgorithms.ExecutarTriangulacaoDelaunay(entrada, tol, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "proximity_clark_evans",
                "Análise do Vizinho Mais Próximo (Clark-Evans)",
                "Análise de Proximidade e Tesselação",
                "Calcula o índice R de agregação espacial de Clark-Evans e gera as linhas ao vizinho mais próximo.",
                new[] { "vizinho", "nearest neighbor", "clark evans", "distancia", "cluster" },
                TipoGeometriaPermitida.Ponto,
                false,
                TipoGeometriaPermitida.Nenhum,
                Array.Empty<ParametroDefinicao>(),
                (entrada, sobrep, pars, prog, ct) =>
                {
                    var (linhas, _) = ProximityTessellationAlgorithms.ExecutarVizinhoMaisProximo(entrada, null, prog, ct);
                    return Task.FromResult(linhas);
                }));

            // === 3. SUPERPOSIÇÃO AVANÇADA (FASE 2) ===
            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "overlay_sym_difference",
                "Diferença Simétrica (XOR Espacial)",
                "Superposição Topológica Avançada",
                "Retém as porções espaciais presentes em A ou em B, mas não em ambas simultaneamente.",
                new[] { "xor", "diferenca simetrica", "symdiff", "overlay" },
                TipoGeometriaPermitida.Poligono,
                true,
                TipoGeometriaPermitida.Poligono,
                Array.Empty<ParametroDefinicao>(),
                (entrada, sobrep, pars, prog, ct) =>
                {
                    return Task.FromResult(AdvancedOverlayAlgorithms.ExecutarDiferencaSimetrica(entrada, sobrep ?? new List<IFeature>(), prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "overlay_full_union",
                "União Topológica Plena (Full Topology Union)",
                "Superposição Topológica Avançada",
                "Particiona todas as geometrias em todos os pontos de contato gerando cobertura contígua com fusão total de atributos.",
                new[] { "uniao plena", "full union", "planar noding", "overlay" },
                TipoGeometriaPermitida.Poligono,
                true,
                TipoGeometriaPermitida.Poligono,
                Array.Empty<ParametroDefinicao>(),
                (entrada, sobrep, pars, prog, ct) =>
                {
                    return Task.FromResult(AdvancedOverlayAlgorithms.ExecutarUniaoPlena(entrada, sobrep ?? new List<IFeature>(), prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "overlay_spatial_update",
                "Atualização Espacial de Camadas (Patch)",
                "Superposição Topológica Avançada",
                "Aplica a camada B como um remendo sobre A, substituindo o trecho coincidente.",
                new[] { "update", "patch", "remendo", "atualizacao cadastral" },
                TipoGeometriaPermitida.Poligono,
                true,
                TipoGeometriaPermitida.Poligono,
                Array.Empty<ParametroDefinicao>(),
                (entrada, sobrep, pars, prog, ct) =>
                {
                    return Task.FromResult(AdvancedOverlayAlgorithms.ExecutarAtualizacaoCamadas(entrada, sobrep ?? new List<IFeature>(), prog, ct));
                }));

            // === 4. GENERALIZAÇÃO CARTOGRÁFICA (FASE 2) ===
            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "generalization_douglas_peucker",
                "Simplificação Douglas-Peucker Topológica",
                "Generalização Cartográfica",
                "Reduz o número de nós mantendo anéis fechados e sem auto-intersecções.",
                new[] { "douglas peucker", "simplificar", "generalizar", "tolerancia" },
                TipoGeometriaPermitida.Linha | TipoGeometriaPermitida.Poligono,
                false,
                TipoGeometriaPermitida.Nenhum,
                new[] { new ParametroDefinicao("ToleranciaMetros", "Tolerância (m)", "Desvio máximo perpendicular", TipoParametro.DistanciaMetrica, 5.0, 0.001, 100000.0) },
                (entrada, sobrep, pars, prog, ct) =>
                {
                    double tol = double.TryParse(pars.GetValueOrDefault("ToleranciaMetros")?.ToString(), out var t) ? t : 5.0;
                    return Task.FromResult(CartographicGeneralizationAlgorithms.ExecutarSimplificacaoDouglasPeucker(entrada, tol, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "generalization_visvalingam_whyatt",
                "Simplificação por Área Efetiva (Visvalingam-Whyatt)",
                "Generalização Cartográfica",
                "Generaliza geometrias avaliando a área triangular formada por 3 vértices consecutivos.",
                new[] { "visvalingam", "area triangular", "generalizacao", "suavizar" },
                TipoGeometriaPermitida.Linha | TipoGeometriaPermitida.Poligono,
                false,
                TipoGeometriaPermitida.Nenhum,
                new[] { new ParametroDefinicao("AreaMinimaM2", "Área Mínima (m²)", "Área triangular efetiva de corte", TipoParametro.NumeroReal, 25.0, 0.01, 1000000.0) },
                (entrada, sobrep, pars, prog, ct) =>
                {
                    double area = double.TryParse(pars.GetValueOrDefault("AreaMinimaM2")?.ToString(), out var a) ? a : 25.0;
                    return Task.FromResult(CartographicGeneralizationAlgorithms.ExecutarSimplificacaoVisvalingam(entrada, area, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "generalization_chaikin",
                "Suavização Cartográfica de Chaikin",
                "Generalização Cartográfica",
                "Gera curvas contínuas (aproximação B-spline) cortando cantos em 1/4 e 3/4.",
                new[] { "chaikin", "suavizacao", "curvas", "corner cutting" },
                TipoGeometriaPermitida.Linha | TipoGeometriaPermitida.Poligono,
                false,
                TipoGeometriaPermitida.Nenhum,
                new[] { new ParametroDefinicao("Iteracoes", "Número de Iterações", "Grau de suavização (1 a 5)", TipoParametro.NumeroInteiro, 2, 1, 5) },
                (entrada, sobrep, pars, prog, ct) =>
                {
                    int it = int.TryParse(pars.GetValueOrDefault("Iteracoes")?.ToString(), out var i) ? i : 2;
                    return Task.FromResult(CartographicGeneralizationAlgorithms.ExecutarSuavizacaoChaikin(entrada, it, prog, ct));
                }));

            // === 5. DECOMPOSIÇÃO E CONVERSÃO (FASE 2) ===
            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "conversion_explode_multipart",
                "Decompor Multipartes para Partes Simples",
                "Decomposição e Conversão",
                "Desmembra feições multipartes em feições individuais mantendo atributos e PART_IDX.",
                new[] { "explode", "decompor", "multipart", "singlepart" },
                TipoGeometriaPermitida.Qualquer,
                false,
                TipoGeometriaPermitida.Nenhum,
                Array.Empty<ParametroDefinicao>(),
                (entrada, sobrep, pars, prog, ct) =>
                {
                    return Task.FromResult(GeometricConversionAlgorithms.ExecutarExplodirMultipartes(entrada, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "conversion_combine_singlepart",
                "Agrupar Partes Simples em Multipartes",
                "Decomposição e Conversão",
                "Agrupa múltiplas feições isoladas em geometrias multipartes com base em um campo chave.",
                new[] { "agrupar", "combine", "multipart", "chave" },
                TipoGeometriaPermitida.Qualquer,
                false,
                TipoGeometriaPermitida.Nenhum,
                new[] { new ParametroDefinicao("CampoChave", "Campo Chave", "Campo para agrupamento", TipoParametro.CampoAtributo, "") },
                (entrada, sobrep, pars, prog, ct) =>
                {
                    string campo = pars.GetValueOrDefault("CampoChave")?.ToString() ?? "";
                    return Task.FromResult(GeometricConversionAlgorithms.ExecutarAgruparPorAtributo(entrada, campo, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "conversion_polygon_to_lines",
                "Polígonos para Linhas de Borda",
                "Decomposição e Conversão",
                "Extrai anel externo e anéis internos de polígonos como feições lineares.",
                new[] { "polygon to lines", "linhas de borda", "contorno", "anel" },
                TipoGeometriaPermitida.Poligono,
                false,
                TipoGeometriaPermitida.Nenhum,
                Array.Empty<ParametroDefinicao>(),
                (entrada, sobrep, pars, prog, ct) =>
                {
                    return Task.FromResult(GeometricConversionAlgorithms.ExecutarPoligonosParaLinhas(entrada, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "conversion_line_polygonizer",
                "Poligonização de Linhas (Polygonizer)",
                "Decomposição e Conversão",
                "Monta grafo planar e extrai todas as faces poligonais fechadas.",
                new[] { "polygonizer", "poligonizar", "fechar linhas", "faces" },
                TipoGeometriaPermitida.Linha,
                false,
                TipoGeometriaPermitida.Nenhum,
                Array.Empty<ParametroDefinicao>(),
                (entrada, sobrep, pars, prog, ct) =>
                {
                    return Task.FromResult(GeometricConversionAlgorithms.ExecutarPoligonizacaoLinhas(entrada, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "conversion_extract_vertices",
                "Extração Analítica de Vértices",
                "Decomposição e Conversão",
                "Extrai todos os nós calculando azimute cartesiano (0-360°) e distância acumulada.",
                new[] { "vertices", "nos", "azimute", "estacas", "pontos" },
                TipoGeometriaPermitida.Linha | TipoGeometriaPermitida.Poligono,
                false,
                TipoGeometriaPermitida.Nenhum,
                Array.Empty<ParametroDefinicao>(),
                (entrada, sobrep, pars, prog, ct) =>
                {
                    return Task.FromResult(GeometricConversionAlgorithms.ExecutarExtrairVertices(entrada, prog, ct));
                }));

            // === 6. SEGMENTAÇÃO LINEAR (FASE 2) ===
            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "segmentation_split_intersections",
                "Quebrar Linhas em Intersecções",
                "Segmentação Linear e Partição",
                "Insere nós topológicos e divide linhas em cruzamentos e toques mútuos.",
                new[] { "split lines", "quebrar linhas", "cruzamento", "interseccao linear" },
                TipoGeometriaPermitida.Linha,
                false,
                TipoGeometriaPermitida.Nenhum,
                Array.Empty<ParametroDefinicao>(),
                (entrada, sobrep, pars, prog, ct) =>
                {
                    return Task.FromResult(LinearSegmentationAlgorithms.ExecutarQuebraLinhasInterseccoes(entrada, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "segmentation_line_chaining",
                "Quebrar Linhas por Comprimento (Estaqueamento)",
                "Segmentação Linear e Partição",
                "Subdivide linhas em estacas métricas uniformes de comprimento pré-determinado.",
                new[] { "chaining", "estaqueamento", "quilometragem", "subdividir" },
                TipoGeometriaPermitida.Linha,
                false,
                TipoGeometriaPermitida.Nenhum,
                new[] { new ParametroDefinicao("DistanciaEstacaMetros", "Comprimento da Estaca (m)", "Extensão métrica de cada estaca", TipoParametro.DistanciaMetrica, 20.0, 0.1, 100000.0) },
                (entrada, sobrep, pars, prog, ct) =>
                {
                    double d = double.TryParse(pars.GetValueOrDefault("DistanciaEstacaMetros")?.ToString(), out var dist) ? dist : 20.0;
                    return Task.FromResult(LinearSegmentationAlgorithms.ExecutarEstaqueamentoLinhas(entrada, d, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "segmentation_split_polygons",
                "Partição de Polígonos por Linhas de Corte",
                "Segmentação Linear e Partição",
                "Subdivide polígonos utilizando linhas como lâminas cortantes.",
                new[] { "split polygons", "cortar poligonos", "laminas", "particao" },
                TipoGeometriaPermitida.Poligono,
                true,
                TipoGeometriaPermitida.Linha,
                Array.Empty<ParametroDefinicao>(),
                (entrada, sobrep, pars, prog, ct) =>
                {
                    return Task.FromResult(LinearSegmentationAlgorithms.ExecutarParticaoPoligonosPorLinhas(entrada, sobrep ?? new List<IFeature>(), prog, ct));
                }));

            // === 7. MALHAS ESPACIAIS E AMOSTRAGEM (FASE 2) ===
            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "grid_cartesian",
                "Grade Regular Cartesiana (Retângulos)",
                "Malhas Espaciais e Amostragem",
                "Gera uma malha ortogonal contígua de células poligonais a partir do envelope.",
                new[] { "grade", "grid", "cartesiana", "retangulos", "malha" },
                TipoGeometriaPermitida.Qualquer,
                false,
                TipoGeometriaPermitida.Nenhum,
                new[] {
                    new ParametroDefinicao("LarguraMetros", "Largura (m)", "Tamanho da célula em X", TipoParametro.DistanciaMetrica, 100.0, 0.1, 1000000.0),
                    new ParametroDefinicao("AlturaMetros", "Altura (m)", "Tamanho da célula em Y", TipoParametro.DistanciaMetrica, 100.0, 0.1, 1000000.0)
                },
                (entrada, sobrep, pars, prog, ct) =>
                {
                    var env = new Envelope();
                    foreach (var f in entrada) if (f.Geometry != null) env.ExpandToInclude(f.Geometry.EnvelopeInternal);
                    double w = double.TryParse(pars.GetValueOrDefault("LarguraMetros")?.ToString(), out var dw) ? dw : 100.0;
                    double h = double.TryParse(pars.GetValueOrDefault("AlturaMetros")?.ToString(), out var dh) ? dh : 100.0;
                    return Task.FromResult(SpatialGridSamplingAlgorithms.ExecutarGradeCartesiana(env, w, h, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "grid_hexagonal_binning",
                "Grade Hexagonal Tesselada (Hexbins)",
                "Malhas Espaciais e Amostragem",
                "Gera malha contígua de hexágonos regulares de raio R com deslocamento alternado.",
                new[] { "hexbin", "hexagonal", "grade hexagonal", "densidade" },
                TipoGeometriaPermitida.Qualquer,
                false,
                TipoGeometriaPermitida.Nenhum,
                new[] { new ParametroDefinicao("RaioMetros", "Raio do Hexágono (m)", "Raio do centro ao vértice", TipoParametro.DistanciaMetrica, 100.0, 0.1, 1000000.0) },
                (entrada, sobrep, pars, prog, ct) =>
                {
                    var env = new Envelope();
                    foreach (var f in entrada) if (f.Geometry != null) env.ExpandToInclude(f.Geometry.EnvelopeInternal);
                    double r = double.TryParse(pars.GetValueOrDefault("RaioMetros")?.ToString(), out var dr) ? dr : 100.0;
                    return Task.FromResult(SpatialGridSamplingAlgorithms.ExecutarGradeHexagonal(env, r, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "sampling_poisson_disk",
                "Amostragem Aleatória Poisson Disk",
                "Malhas Espaciais e Amostragem",
                "Gera pontos pseudoaleatórios com distância euclidiana mínima entre si.",
                new[] { "poisson", "amostragem", "pontos aleatorios", "dispersao" },
                TipoGeometriaPermitida.Poligono,
                false,
                TipoGeometriaPermitida.Nenhum,
                new[] {
                    new ParametroDefinicao("DistanciaMinimaMetros", "Distância Mínima (m)", "Raio de exclusão entre pontos", TipoParametro.DistanciaMetrica, 50.0, 0.1, 100000.0),
                    new ParametroDefinicao("MaxPontos", "Máximo de Amostras", "Número máximo de pontos gerados", TipoParametro.NumeroInteiro, 500, 1, 50000)
                },
                (entrada, sobrep, pars, prog, ct) =>
                {
                    double d = double.TryParse(pars.GetValueOrDefault("DistanciaMinimaMetros")?.ToString(), out var dist) ? dist : 50.0;
                    int max = int.TryParse(pars.GetValueOrDefault("MaxPontos")?.ToString(), out var m) ? m : 500;
                    var geoms = entrada.Where(f => f.Geometry != null && !f.Geometry.IsEmpty).Select(f => f.Geometry).ToList();
                    var uniao = CascadedPolygonUnion.Union(geoms);
                    return Task.FromResult(SpatialGridSamplingAlgorithms.ExecutarAmostragemPoissonDisk(uniao, d, max, prog, ct));
                }));

            // === 8. ESTATÍSTICAS ZONAIS E AGREGAÇÃO (FASE 2) ===
            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "zonal_count_points",
                "Contagem e Soma de Pontos em Polígonos",
                "Estatísticas Zonais e Agregação",
                "Conta e soma pontos contidos em cada zona via IPreparedGeometry.",
                new[] { "contagem zonal", "pontos em poligono", "soma zonal", "estatistica" },
                TipoGeometriaPermitida.Poligono,
                true,
                TipoGeometriaPermitida.Ponto,
                new[] { new ParametroDefinicao("CampoValorSomar", "Campo Numérico a Somar", "Campo de atributo dos pontos", TipoParametro.CampoAtributo, null) },
                (entrada, sobrep, pars, prog, ct) =>
                {
                    string? campo = pars.GetValueOrDefault("CampoValorSomar")?.ToString();
                    return Task.FromResult(ZonalStatisticsAlgorithms.ExecutarContagemPontosPoligono(entrada, sobrep ?? new List<IFeature>(), campo, prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "zonal_line_density",
                "Densidade e Comprimento de Linhas em Polígonos",
                "Estatísticas Zonais e Agregação",
                "Calcula a extensão métrica de linhas que cruzam polígonos e densidade em km/km².",
                new[] { "densidade linear", "comprimento zonal", "quilometros de via", "densidade" },
                TipoGeometriaPermitida.Poligono,
                true,
                TipoGeometriaPermitida.Linha,
                Array.Empty<ParametroDefinicao>(),
                (entrada, sobrep, pars, prog, ct) =>
                {
                    return Task.FromResult(ZonalStatisticsAlgorithms.ExecutarDensidadeLinearPoligonos(entrada, sobrep ?? new List<IFeature>(), prog, ct));
                }));

            reg.Registrar(new DelegateGeoprocessingAlgorithm(
                "zonal_spatial_join",
                "Junção Espacial Avançada (Spatial Join)",
                "Estatísticas Zonais e Agregação",
                "Funde atributos tabulares entre camadas com base em predicados espaciais estritos.",
                new[] { "spatial join", "juncao espacial", "de-9im", "predicados", "atributos" },
                TipoGeometriaPermitida.Qualquer,
                true,
                TipoGeometriaPermitida.Qualquer,
                new[] {
                    new ParametroDefinicao("PredicadoTopologico", "Predicado Topológico", "Tipo de relacionamento OGC", TipoParametro.OpcaoLista, "Intersects", OpcoesLista: new[] { "Intersects", "Contains", "Within", "Touches", "Crosses", "WithinDistance" }),
                    new ParametroDefinicao("DistanciaToleranciaMetros", "Distância de Tolerância (m)", "Usado com WithinDistance", TipoParametro.DistanciaMetrica, 0.0, 0.0, 100000.0)
                },
                (entrada, sobrep, pars, prog, ct) =>
                {
                    string pred = pars.GetValueOrDefault("PredicadoTopologico")?.ToString() ?? "Intersects";
                    double dist = double.TryParse(pars.GetValueOrDefault("DistanciaToleranciaMetros")?.ToString(), out var dt) ? dt : 0.0;
                    return Task.FromResult(ZonalStatisticsAlgorithms.ExecutarSpatialJoinAvancado(entrada, sobrep ?? new List<IFeature>(), pred, dist, prog, ct));
                }));
        }
    }
}
