using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.Operation.Union;
using OSGeo.GDAL;
using OSGeo.OGR;
using OSGeo.OSR;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using NtsFeature = NetTopologySuite.Features.Feature;
using OgrGeometry = OSGeo.OGR.Geometry;
using Point = NetTopologySuite.Geometries.Point;
using Geometry = NetTopologySuite.Geometries.Geometry;
using Envelope = NetTopologySuite.Geometries.Envelope;

namespace GeoNex.Services
{
    /// <summary>
    /// Motor central de execução assíncrona de geoprocessamento e persistência em Shapefile.
    /// Executa operações espaciais fora da thread de UI com suporte a cancelamento,
    /// progresso contínuo e reprojeção dinâmica de coordenadas.
    /// </summary>
    public static class GeoprocessingEngine
    {
        private static readonly SemaphoreSlim _semaphore = new(1, 1);

        /// <summary>
        /// Executa uma operação de geoprocessamento de forma assíncrona em thread de segundo plano.
        /// </summary>
        public static async Task<GeoprocessingResult> ExecutarAsync(
            GeoprocessingParameters parametros,
            string caminhoArquivoEntrada,
            string? caminhoArquivoSobreposicao = null,
            HashSet<long>? selecionadosEntrada = null,
            HashSet<long>? selecionadosSobreposicao = null,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(parametros);
            if (string.IsNullOrWhiteSpace(caminhoArquivoEntrada) || !File.Exists(caminhoArquivoEntrada))
            {
                return new GeoprocessingResult(false, 0, null, "O arquivo da camada de entrada não foi encontrado no disco.");
            }

            if (string.IsNullOrWhiteSpace(parametros.CaminhoArquivoSaida))
            {
                return new GeoprocessingResult(false, 0, null, "O caminho de destino do arquivo de saída não foi especificado.");
            }

            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();

            try
            {
                return await Task.Run(() =>
                {
                    progresso?.Report(new GeoprocessingProgress(0.05, "Inicializando motor GDAL/OGR e carregando feições de entrada...", 0, 0));
                    ct.ThrowIfCancellationRequested();

                    Ogr.RegisterAll();

                    // 1. Carregar Feições de Entrada e SRS
                    var (feicoesEntrada, srsEntrada) = CarregarFeicoesDeShapefile(
                        caminhoArquivoEntrada,
                        parametros.ApenasSelecionadasEntrada ? selecionadosEntrada : null);

                    if (feicoesEntrada.Count == 0)
                    {
                        return new GeoprocessingResult(false, 0, null, "A camada de entrada não possui feições válidas para processamento.");
                    }

                    List<IFeature> feicoesResultado;

                    // 2. Despacho por Tipo de Operação
                    switch (parametros.Operacao)
                    {
                        case TipoOperacaoGeoprocessamento.Recortar:
                        {
                            if (string.IsNullOrWhiteSpace(caminhoArquivoSobreposicao) || !File.Exists(caminhoArquivoSobreposicao))
                                return new GeoprocessingResult(false, 0, null, "A camada de máscara de recorte não foi especificada ou não existe.");

                            progresso?.Report(new GeoprocessingProgress(0.15, "Carregando camada de máscara de recorte...", 0, feicoesEntrada.Count));
                            var (feicoesMascara, srsMascara) = CarregarFeicoesDeShapefile(
                                caminhoArquivoSobreposicao,
                                parametros.ApenasSelecionadasSobreposicao ? selecionadosSobreposicao : null,
                                srsEntrada);

                            if (feicoesMascara.Count == 0)
                                return new GeoprocessingResult(false, 0, null, "A camada de máscara de recorte não contém feições válidas.");

                            progresso?.Report(new GeoprocessingProgress(0.25, "Unificando polígonos da máscara em cascata...", 0, feicoesEntrada.Count));
                            var geomsMascara = feicoesMascara.Select(f => f.Geometry).Where(g => g != null && !g.IsEmpty).ToList();
                            var mascaraUnificada = CascadedPolygonUnion.Union(geomsMascara);

                            if (mascaraUnificada == null || mascaraUnificada.IsEmpty)
                                return new GeoprocessingResult(false, 0, null, "A geometria da máscara de recorte é inválida ou vazia.");

                            feicoesResultado = VectorOverlayAlgorithms.ExecutarRecorte(feicoesEntrada, mascaraUnificada, progresso, ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.Interseccao:
                        {
                            if (string.IsNullOrWhiteSpace(caminhoArquivoSobreposicao) || !File.Exists(caminhoArquivoSobreposicao))
                                return new GeoprocessingResult(false, 0, null, "A segunda camada para intersecção não foi especificada.");

                            progresso?.Report(new GeoprocessingProgress(0.15, "Carregando segunda camada para intersecção...", 0, feicoesEntrada.Count));
                            var (feicoesB, _) = CarregarFeicoesDeShapefile(
                                caminhoArquivoSobreposicao,
                                parametros.ApenasSelecionadasSobreposicao ? selecionadosSobreposicao : null,
                                srsEntrada);

                            if (feicoesB.Count == 0)
                                return new GeoprocessingResult(false, 0, null, "A segunda camada não contém feições válidas.");

                            feicoesResultado = VectorOverlayAlgorithms.ExecutarInterseccao(feicoesEntrada, feicoesB, progresso, ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.Diferenca:
                        {
                            if (string.IsNullOrWhiteSpace(caminhoArquivoSobreposicao) || !File.Exists(caminhoArquivoSobreposicao))
                                return new GeoprocessingResult(false, 0, null, "A camada de subtração não foi especificada.");

                            progresso?.Report(new GeoprocessingProgress(0.15, "Carregando camada de subtração...", 0, feicoesEntrada.Count));
                            var (feicoesSub, _) = CarregarFeicoesDeShapefile(
                                caminhoArquivoSobreposicao,
                                parametros.ApenasSelecionadasSobreposicao ? selecionadosSobreposicao : null,
                                srsEntrada);

                            var geomsSub = feicoesSub.Select(f => f.Geometry).Where(g => g != null && !g.IsEmpty).ToList();
                            var mascaraSubtracao = CascadedPolygonUnion.Union(geomsSub);

                            feicoesResultado = VectorOverlayAlgorithms.ExecutarDiferenca(feicoesEntrada, mascaraSubtracao, progresso, ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.Dissolver:
                        {
                            feicoesResultado = VectorAggregationAlgorithms.ExecutarDissolucao(
                                feicoesEntrada,
                                parametros.CampoDissolucao,
                                parametros.AgregacaoDissolucao,
                                progresso,
                                ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.Buffer:
                        {
                            feicoesResultado = VectorGeometryAlgorithms.ExecutarBuffer(
                                feicoesEntrada,
                                parametros.DistanciaBufferMetros,
                                parametros.CampoDistanciaBuffer,
                                parametros.SegmentosQuadranteBuffer,
                                parametros.EndCapBuffer,
                                parametros.JoinBuffer,
                                parametros.DissolverBuffers,
                                progresso,
                                ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.EnvoltoriaConvexa:
                        {
                            feicoesResultado = VectorGeometryAlgorithms.ExecutarEnvoltoriaConvexa(
                                feicoesEntrada,
                                parametros.ModoEnvoltoriaGlobal,
                                parametros.CampoDissolucao,
                                progresso,
                                ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.Centroides:
                        {
                            feicoesResultado = VectorGeometryAlgorithms.ExecutarCentroides(
                                feicoesEntrada,
                                parametros.UsarPolylabelEmCentroides,
                                progresso,
                                ct);
                            break;
                        }

                        // === FASE 2: PROXIMIDADE E TESSELAÇÃO ===
                        case TipoOperacaoGeoprocessamento.Voronoi:
                        {
                            Geometry? mascara = null;
                            if (!string.IsNullOrWhiteSpace(caminhoArquivoSobreposicao) && File.Exists(caminhoArquivoSobreposicao))
                            {
                                var (fMasc, _) = CarregarFeicoesDeShapefile(caminhoArquivoSobreposicao, null, srsEntrada);
                                mascara = CascadedPolygonUnion.Union(fMasc.Select(f => f.Geometry).Where(g => g != null).ToList());
                            }
                            feicoesResultado = ProximityTessellationAlgorithms.ExecutarVoronoi(
                                feicoesEntrada,
                                mascara,
                                parametros.MargemPercentualVoronoi,
                                progresso,
                                ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.TriangulacaoDelaunay:
                        {
                            feicoesResultado = ProximityTessellationAlgorithms.ExecutarTriangulacaoDelaunay(
                                feicoesEntrada,
                                0.0,
                                progresso,
                                ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.VizinhoMaisProximo:
                        {
                            var (linhas, _) = ProximityTessellationAlgorithms.ExecutarVizinhoMaisProximo(
                                feicoesEntrada,
                                null,
                                progresso,
                                ct);
                            feicoesResultado = linhas;
                            break;
                        }

                        // === FASE 2: SUPERPOSIÇÃO AVANÇADA ===
                        case TipoOperacaoGeoprocessamento.DiferencaSimetrica:
                        {
                            if (string.IsNullOrWhiteSpace(caminhoArquivoSobreposicao) || !File.Exists(caminhoArquivoSobreposicao))
                                return new GeoprocessingResult(false, 0, null, "A segunda camada é obrigatória para Diferença Simétrica.");

                            var (feicoesB, _) = CarregarFeicoesDeShapefile(caminhoArquivoSobreposicao, null, srsEntrada);
                            feicoesResultado = AdvancedOverlayAlgorithms.ExecutarDiferencaSimetrica(feicoesEntrada, feicoesB, progresso, ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.UniaoPlena:
                        {
                            if (string.IsNullOrWhiteSpace(caminhoArquivoSobreposicao) || !File.Exists(caminhoArquivoSobreposicao))
                                return new GeoprocessingResult(false, 0, null, "A segunda camada é obrigatória para União Plena.");

                            var (feicoesB, _) = CarregarFeicoesDeShapefile(caminhoArquivoSobreposicao, null, srsEntrada);
                            feicoesResultado = AdvancedOverlayAlgorithms.ExecutarUniaoPlena(feicoesEntrada, feicoesB, progresso, ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.AtualizacaoCamadas:
                        {
                            if (string.IsNullOrWhiteSpace(caminhoArquivoSobreposicao) || !File.Exists(caminhoArquivoSobreposicao))
                                return new GeoprocessingResult(false, 0, null, "A camada prioritária (patch) é obrigatória para Atualização.");

                            var (feicoesB, _) = CarregarFeicoesDeShapefile(caminhoArquivoSobreposicao, null, srsEntrada);
                            feicoesResultado = AdvancedOverlayAlgorithms.ExecutarAtualizacaoCamadas(feicoesEntrada, feicoesB, progresso, ct);
                            break;
                        }

                        // === FASE 2: GENERALIZAÇÃO CARTOGRÁFICA ===
                        case TipoOperacaoGeoprocessamento.SimplificacaoDouglasPeucker:
                        {
                            feicoesResultado = CartographicGeneralizationAlgorithms.ExecutarSimplificacaoDouglasPeucker(
                                feicoesEntrada,
                                parametros.ToleranciaSimplificacaoMetros,
                                progresso,
                                ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.SimplificacaoVisvalingam:
                        {
                            feicoesResultado = CartographicGeneralizationAlgorithms.ExecutarSimplificacaoVisvalingam(
                                feicoesEntrada,
                                parametros.AreaMinimaTrianguloM2,
                                progresso,
                                ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.SuavizacaoChaikin:
                        {
                            feicoesResultado = CartographicGeneralizationAlgorithms.ExecutarSuavizacaoChaikin(
                                feicoesEntrada,
                                parametros.IteracoesSuavizacao,
                                progresso,
                                ct);
                            break;
                        }

                        // === FASE 2: DECOMPOSIÇÃO E CONVERSÃO ===
                        case TipoOperacaoGeoprocessamento.ExplodirMultipartes:
                        {
                            feicoesResultado = GeometricConversionAlgorithms.ExecutarExplodirMultipartes(feicoesEntrada, progresso, ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.AgruparPorAtributo:
                        {
                            feicoesResultado = GeometricConversionAlgorithms.ExecutarAgruparPorAtributo(
                                feicoesEntrada,
                                parametros.CampoDissolucao ?? "",
                                progresso,
                                ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.PoligonosParaLinhas:
                        {
                            feicoesResultado = GeometricConversionAlgorithms.ExecutarPoligonosParaLinhas(feicoesEntrada, progresso, ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.PoligonizacaoLinhas:
                        {
                            feicoesResultado = GeometricConversionAlgorithms.ExecutarPoligonizacaoLinhas(feicoesEntrada, progresso, ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.ExtrairVertices:
                        {
                            feicoesResultado = GeometricConversionAlgorithms.ExecutarExtrairVertices(feicoesEntrada, progresso, ct);
                            break;
                        }

                        // === FASE 2: SEGMENTAÇÃO LINEAR ===
                        case TipoOperacaoGeoprocessamento.QuebraLinhasInterseccoes:
                        {
                            feicoesResultado = LinearSegmentationAlgorithms.ExecutarQuebraLinhasInterseccoes(feicoesEntrada, progresso, ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.EstaqueamentoLinhas:
                        {
                            feicoesResultado = LinearSegmentationAlgorithms.ExecutarEstaqueamentoLinhas(
                                feicoesEntrada,
                                parametros.DistanciaEstacaMetros,
                                progresso,
                                ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.ParticaoPoligonosPorLinhas:
                        {
                            if (string.IsNullOrWhiteSpace(caminhoArquivoSobreposicao) || !File.Exists(caminhoArquivoSobreposicao))
                                return new GeoprocessingResult(false, 0, null, "A camada de linhas de corte é obrigatória.");

                            var (feicoesLinhas, _) = CarregarFeicoesDeShapefile(caminhoArquivoSobreposicao, null, srsEntrada);
                            feicoesResultado = LinearSegmentationAlgorithms.ExecutarParticaoPoligonosPorLinhas(feicoesEntrada, feicoesLinhas, progresso, ct);
                            break;
                        }

                        // === FASE 2: MALHAS E AMOSTRAGEM ===
                        case TipoOperacaoGeoprocessamento.GradeCartesiana:
                        {
                            var env = new Envelope();
                            foreach (var f in feicoesEntrada) if (f.Geometry != null) env.ExpandToInclude(f.Geometry.EnvelopeInternal);
                            feicoesResultado = SpatialGridSamplingAlgorithms.ExecutarGradeCartesiana(
                                env,
                                parametros.LarguraGradeMetros,
                                parametros.AlturaGradeMetros,
                                progresso,
                                ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.GradeHexagonal:
                        {
                            var env = new Envelope();
                            foreach (var f in feicoesEntrada) if (f.Geometry != null) env.ExpandToInclude(f.Geometry.EnvelopeInternal);
                            feicoesResultado = SpatialGridSamplingAlgorithms.ExecutarGradeHexagonal(
                                env,
                                parametros.RaioHexagonoMetros,
                                progresso,
                                ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.AmostragemPoissonDisk:
                        {
                            var geoms = feicoesEntrada.Where(f => f.Geometry != null && !f.Geometry.IsEmpty).Select(f => f.Geometry).ToList();
                            var uniao = CascadedPolygonUnion.Union(geoms);
                            feicoesResultado = SpatialGridSamplingAlgorithms.ExecutarAmostragemPoissonDisk(
                                uniao,
                                parametros.DistanciaMinimaAmostragemMetros,
                                parametros.MaxAmostrasPoisson,
                                progresso,
                                ct);
                            break;
                        }

                        // === FASE 2: ESTATÍSTICAS ZONAIS ===
                        case TipoOperacaoGeoprocessamento.ContagemPontosPoligono:
                        {
                            if (string.IsNullOrWhiteSpace(caminhoArquivoSobreposicao) || !File.Exists(caminhoArquivoSobreposicao))
                                return new GeoprocessingResult(false, 0, null, "A camada de pontos é obrigatória para contagem zonal.");

                            var (feicoesPontos, _) = CarregarFeicoesDeShapefile(caminhoArquivoSobreposicao, null, srsEntrada);
                            feicoesResultado = ZonalStatisticsAlgorithms.ExecutarContagemPontosPoligono(
                                feicoesEntrada,
                                feicoesPontos,
                                parametros.CampoValorAgregacaoZonal,
                                progresso,
                                ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.DensidadeLinearPoligonos:
                        {
                            if (string.IsNullOrWhiteSpace(caminhoArquivoSobreposicao) || !File.Exists(caminhoArquivoSobreposicao))
                                return new GeoprocessingResult(false, 0, null, "A camada de linhas é obrigatória para densidade linear.");

                            var (feicoesLinhas, _) = CarregarFeicoesDeShapefile(caminhoArquivoSobreposicao, null, srsEntrada);
                            feicoesResultado = ZonalStatisticsAlgorithms.ExecutarDensidadeLinearPoligonos(
                                feicoesEntrada,
                                feicoesLinhas,
                                progresso,
                                ct);
                            break;
                        }

                        case TipoOperacaoGeoprocessamento.SpatialJoinAvancado:
                        {
                            if (string.IsNullOrWhiteSpace(caminhoArquivoSobreposicao) || !File.Exists(caminhoArquivoSobreposicao))
                                return new GeoprocessingResult(false, 0, null, "A camada de junção é obrigatória para Spatial Join.");

                            var (feicoesJuncao, _) = CarregarFeicoesDeShapefile(caminhoArquivoSobreposicao, null, srsEntrada);
                            feicoesResultado = ZonalStatisticsAlgorithms.ExecutarSpatialJoinAvancado(
                                feicoesEntrada,
                                feicoesJuncao,
                                parametros.PredicadoTopologico,
                                parametros.DistanciaToleranciaSpatialJoin,
                                progresso,
                                ct);
                            break;
                        }

                        default:
                            return new GeoprocessingResult(false, 0, null, $"Operação de geoprocessamento '{parametros.Operacao}' não suportada.");
                    }

                    if (feicoesResultado.Count == 0)
                    {
                        return new GeoprocessingResult(false, 0, null, "A operação foi concluída, mas nenhuma feição resultante foi gerada (resultado vazio).");
                    }

                    // 3. Persistência em Shapefile
                    progresso?.Report(new GeoprocessingProgress(0.9, "Gravando Shapefile de saída no disco...", feicoesResultado.Count, feicoesResultado.Count));
                    SalvarShapefile(parametros.CaminhoArquivoSaida, feicoesResultado, srsEntrada);

                    progresso?.Report(new GeoprocessingProgress(1.0, "Processamento concluído com sucesso!", feicoesResultado.Count, feicoesResultado.Count));
                    sw.Stop();

                    return new GeoprocessingResult(true, feicoesResultado.Count, parametros.CaminhoArquivoSaida, null, sw.Elapsed);

                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new GeoprocessingResult(false, 0, null, "O processamento foi cancelado pelo usuário.", sw.Elapsed);
            }
            catch (Exception ex)
            {
                return new GeoprocessingResult(false, 0, null, $"Erro interno durante o geoprocessamento: {ex.Message}", sw.Elapsed);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Carrega feições de um arquivo Shapefile com suporte a reprojeção automática e filtro de IDs.
        /// </summary>
        public static (List<IFeature> Feicoes, SpatialReference? Srs) CarregarFeicoesDeShapefile(
            string caminhoShp,
            HashSet<long>? idsSelecionados = null,
            SpatialReference? targetSrs = null)
        {
            var feicoes = new List<IFeature>();
            using var ds = Ogr.Open(caminhoShp, 0);
            if (ds == null || ds.GetLayerCount() == 0) return (feicoes, null);

            using var layer = ds.GetLayerByIndex(0);
            if (layer == null) return (feicoes, null);

            var srs = layer.GetSpatialRef();
            CoordinateTransformation? transform = null;

            if (srs != null && targetSrs != null && srs.IsSame(targetSrs, null) != 1)
            {
                srs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
                targetSrs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
                transform = new CoordinateTransformation(srs, targetSrs);
            }

            var wktReader = new WKTReader();
            using var defn = layer.GetLayerDefn();
            int fieldCount = defn.GetFieldCount();
            layer.ResetReading();

            while (true)
            {
                using var ogrFeature = layer.GetNextFeature();
                if (ogrFeature == null) break;

                long fid = ogrFeature.GetFID();
                if (idsSelecionados != null && idsSelecionados.Count > 0 && !idsSelecionados.Contains(fid))
                    continue;

                using var ogrGeom = ogrFeature.GetGeometryRef()?.Clone();
                if (ogrGeom == null || ogrGeom.IsEmpty()) continue;

                if (transform != null)
                {
                    ogrGeom.Transform(transform);
                }

                ogrGeom.ExportToWkt(out string wkt);
                if (string.IsNullOrEmpty(wkt)) continue;

                var ntsGeom = wktReader.Read(wkt);
                if (ntsGeom == null || ntsGeom.IsEmpty) continue;

                var attrs = new AttributesTable();
                for (int i = 0; i < fieldCount; i++)
                {
                    using var fDefn = defn.GetFieldDefn(i);
                    string fName = fDefn.GetName();
                    if (ogrFeature.IsFieldSetAndNotNull(i))
                    {
                        var fType = fDefn.GetFieldType();
                        if (fType == FieldType.OFTInteger)
                            attrs.Add(fName, ogrFeature.GetFieldAsInteger(i));
                        else if (fType == FieldType.OFTInteger64)
                            attrs.Add(fName, ogrFeature.GetFieldAsInteger64(i));
                        else if (fType == FieldType.OFTReal)
                            attrs.Add(fName, ogrFeature.GetFieldAsDouble(i));
                        else
                            attrs.Add(fName, ogrFeature.GetFieldAsString(i));
                    }
                    else
                    {
                        attrs.Add(fName, null);
                    }
                }

                feicoes.Add(new NtsFeature(ntsGeom, attrs));
            }

            transform?.Dispose();
            return (feicoes, srs);
        }

        /// <summary>
        /// Grava um conjunto de feições NTS diretamente em um novo Shapefile com tabela DBF e projeção WKT.
        /// </summary>
        public static void SalvarShapefile(string caminhoShp, IReadOnlyList<IFeature> feicoes, SpatialReference? srs = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(caminhoShp);
            if (feicoes == null || feicoes.Count == 0) return;

            string dir = Path.GetDirectoryName(Path.GetFullPath(caminhoShp)) ?? AppDomain.CurrentDomain.BaseDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string baseName = Path.GetFileNameWithoutExtension(caminhoShp);
            RemoverShapefileExistente(Path.Combine(dir, baseName));

            var driver = Ogr.GetDriverByName("ESRI Shapefile")
                ?? throw new InvalidOperationException("Driver OGR 'ESRI Shapefile' não está disponível.");

            using var ds = driver.CreateDataSource(dir, []);
            if (ds == null)
                throw new IOException($"Falha ao criar fonte de dados Shapefile no diretório '{dir}'.");

            // Determinar tipo geométrico predominante
            var primeiraGeom = feicoes.FirstOrDefault(f => f.Geometry != null && !f.Geometry.IsEmpty)?.Geometry;
            wkbGeometryType geomTypeOgr = primeiraGeom switch
            {
                Point or MultiPoint => wkbGeometryType.wkbPoint,
                LineString or MultiLineString => wkbGeometryType.wkbLineString,
                _ => wkbGeometryType.wkbPolygon
            };

            using var layer = ds.CreateLayer(baseName, srs, geomTypeOgr, []);
            if (layer == null)
                throw new IOException($"Falha ao criar camada Shapefile '{baseName}'.");

            // Mapear campos a partir da primeira feição com atributos
            var nomesCampos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var feicaoComCampos = feicoes.FirstOrDefault(f => f.Attributes != null && f.Attributes.Count > 0);

            if (feicaoComCampos?.Attributes != null)
            {
                foreach (var nome in feicaoComCampos.Attributes.GetNames())
                {
                    string nomeSanitizado = nome.Length > 10 ? nome.Substring(0, 10) : nome;
                    if (nomesCampos.Contains(nomeSanitizado)) continue;
                    nomesCampos.Add(nomeSanitizado);

                    var valor = feicaoComCampos.Attributes[nome];
                    FieldType ogrType = valor switch
                    {
                        int or short or byte => FieldType.OFTInteger,
                        long => FieldType.OFTInteger64,
                        double or float or decimal => FieldType.OFTReal,
                        _ => FieldType.OFTString
                    };

                    using var fieldDefn = new FieldDefn(nomeSanitizado, ogrType);
                    if (ogrType == FieldType.OFTString) fieldDefn.SetWidth(128);
                    layer.CreateField(fieldDefn, 1);
                }
            }

            var wktWriter = new WKTWriter();
            using var layerDefn = layer.GetLayerDefn();

            for (int i = 0; i < feicoes.Count; i++)
            {
                var f = feicoes[i];
                if (f.Geometry == null || f.Geometry.IsEmpty) continue;

                string wkt = wktWriter.Write(f.Geometry);
                using var ogrGeom = OgrGeometry.CreateFromWkt(wkt);
                if (ogrGeom == null) continue;

                using var ogrFeat = new OSGeo.OGR.Feature(layerDefn);
                ogrFeat.SetGeometry(ogrGeom);

                if (f.Attributes != null)
                {
                    foreach (var nome in f.Attributes.GetNames())
                    {
                        string nomeSanitizado = nome.Length > 10 ? nome.Substring(0, 10) : nome;
                        int fieldIdx = layerDefn.GetFieldIndex(nomeSanitizado);
                        if (fieldIdx >= 0)
                        {
                            var val = f.Attributes[nome];
                            if (val != null)
                                ogrFeat.SetField(fieldIdx, val.ToString());
                            else
                                ogrFeat.SetFieldNull(fieldIdx);
                        }
                    }
                }

                layer.CreateFeature(ogrFeat);
            }

            layer.SyncToDisk();

            // Gravar arquivo .cpg com codificação UTF-8
            try
            {
                string cpgPath = Path.Combine(dir, $"{baseName}.cpg");
                File.WriteAllText(cpgPath, "UTF-8", Encoding.UTF8);
            }
            catch { }
        }

        private static void RemoverShapefileExistente(string basePathSemExtensao)
        {
            string[] extensoes = { ".shp", ".shx", ".dbf", ".prj", ".cpg", ".qpj" };
            foreach (var ext in extensoes)
            {
                string arq = basePathSemExtensao + ext;
                if (File.Exists(arq))
                {
                    try { File.Delete(arq); } catch { }
                }
            }
        }
    }
}
