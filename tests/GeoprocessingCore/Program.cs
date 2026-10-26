using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GeoNex.Services;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.Operation.Buffer;
using Point = NetTopologySuite.Geometries.Point;

int assertions = 0;
void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException($"FALHA: {message}");
    assertions++;
}

Console.WriteLine("=== Iniciando Suíte Completa de Testes do Motor de Geoprocessamento GeoNex (Fases 1 e 2) ===");

var gf = GeometryFactory.Default;
var wktReader = new WKTReader();

// =========================================================================
// MÓDULO 1: RECORTAR (VECTOR CLIP)
// =========================================================================
Console.WriteLine("[1/17] Testando Recorte de Polígonos, Linhas e Pontos...");

var polyEntrada = (Polygon)wktReader.Read("POLYGON((0 0, 100 0, 100 100, 0 100, 0 0))");
var polyMascara = (Polygon)wktReader.Read("POLYGON((50 0, 150 0, 150 100, 50 100, 50 0))");

var attrs1 = new AttributesTable();
attrs1.Add("ID", 101);
attrs1.Add("NOME", "Talhao_A");
var feicoesClipIn = new List<IFeature> { new Feature(polyEntrada, attrs1) };

var clipResult = VectorOverlayAlgorithms.ExecutarRecorte(feicoesClipIn, polyMascara);
Assert(clipResult.Count == 1, "Recorte de polígono deve gerar 1 feição resultante");
Assert(Math.Abs(clipResult[0].Geometry.Area - 5000.0) < 1e-3, "Área resultante do recorte deve ser exatamente 5000 (50 x 100)");
Assert(clipResult[0].Attributes["NOME"].ToString() == "Talhao_A", "Atributos da feição de entrada devem ser preservados");

// Linha atravessando a máscara
var linhaEntrada = (LineString)wktReader.Read("LINESTRING(0 50, 200 50)");
var feicoesLinhaIn = new List<IFeature> { new Feature(linhaEntrada, attrs1) };
var clipLinhaResult = VectorOverlayAlgorithms.ExecutarRecorte(feicoesLinhaIn, polyMascara);
Assert(clipLinhaResult.Count == 1, "Recorte de linha deve gerar 1 feição linear");
Assert(Math.Abs(clipLinhaResult[0].Geometry.Length - 100.0) < 1e-3, "Comprimento da linha recortada deve ser exatamente 100 (de X=50 a X=150)");

// Nuvem de Pontos: Ponto dentro (75, 50) e Ponto fora (20, 20)
var pIn = new Feature(gf.CreatePoint(new Coordinate(75, 50)), attrs1);
var pOut = new Feature(gf.CreatePoint(new Coordinate(20, 20)), attrs1);
var clipPontos = VectorOverlayAlgorithms.ExecutarRecorte(new[] { pIn, pOut }, polyMascara);
Assert(clipPontos.Count == 1, "Apenas o ponto dentro da máscara deve ser preservado");
Assert(clipPontos[0].Geometry.Coordinate.X == 75 && clipPontos[0].Geometry.Coordinate.Y == 50, "Coordenadas do ponto interno devem ser exatas");

// =========================================================================
// MÓDULO 2: INTERSECÇÃO (INTERSECTION) & FUSÃO DE ESQUEMAS DBF
// =========================================================================
Console.WriteLine("[2/17] Testando Intersecção com Fusão de Esquemas e Resolução de Nomes...");

var attrsA = new AttributesTable();
attrsA.Add("NOME", "Floresta");
attrsA.Add("AREA_KM2", 15.5);

var attrsB = new AttributesTable();
attrsB.Add("NOME", "Bacia_Rio_Doce");
attrsB.Add("COD_BACIA", 801);

var featA = new Feature(polyEntrada, attrsA);
var featB = new Feature(polyMascara, attrsB);

var interResult = VectorOverlayAlgorithms.ExecutarInterseccao(new[] { featA }, new[] { featB });
Assert(interResult.Count == 1, "Intersecção deve gerar 1 feição de sobreposição");
Assert(Math.Abs(interResult[0].Geometry.Area - 5000.0) < 1e-3, "Área da intersecção deve ser 5000");

var attrsFundidos = interResult[0].Attributes;
Assert(attrsFundidos.Exists("NOME"), "Campo NOME da camada A deve existir");
Assert(attrsFundidos["NOME"].ToString() == "Floresta", "Valor do campo NOME de A deve ser Floresta");
Assert(attrsFundidos.Exists("NOME_1"), "Campo conflitante da camada B deve receber sufixo _1");
Assert(attrsFundidos["NOME_1"].ToString() == "Bacia_Rio_Doce", "Valor do campo NOME_1 deve ser Bacia_Rio_Doce");
Assert(attrsFundidos.Exists("COD_BACIA"), "Campo único da camada B deve existir");

// =========================================================================
// MÓDULO 3: DIFERENÇA ESPACIAL (DIFFERENCE / ERASE)
// =========================================================================
Console.WriteLine("[3/17] Testando Diferença Espacial (Furos, Fragmentação e MultiPolígonos)...");

var polyBase = (Polygon)wktReader.Read("POLYGON((0 0, 100 0, 100 100, 0 100, 0 0))");
var polyFuro = (Polygon)wktReader.Read("POLYGON((30 30, 70 30, 70 70, 30 70, 30 30))");

var diffHole = VectorOverlayAlgorithms.ExecutarDiferenca(new[] { new Feature(polyBase, attrs1) }, polyFuro);
Assert(diffHole.Count == 1, "Diferença interna deve produzir 1 feição poligonal");
Assert(Math.Abs(diffHole[0].Geometry.Area - (10000.0 - 1600.0)) < 1e-3, "Área resultante deve ser 8400 (10000 - 1600)");
var polyComAnel = (Polygon)diffHole[0].Geometry;
Assert(polyComAnel.NumInteriorRings == 1, "Polígono resultante deve possuir exatamente 1 anel interno");

var polyCorte = (Polygon)wktReader.Read("POLYGON((40 -10, 60 -10, 60 110, 40 110, 40 -10))");
var diffMulti = VectorOverlayAlgorithms.ExecutarDiferenca(new[] { new Feature(polyBase, attrs1) }, polyCorte);
Assert(diffMulti.Count == 1, "Diferença dividindo polígono deve gerar 1 feição");
Assert(diffMulti[0].Geometry is MultiPolygon, "Feição dividida deve ser encapsulada como MultiPolygon");
var mp = (MultiPolygon)diffMulti[0].Geometry;
Assert(mp.NumGeometries == 2, "MultiPolygon deve possuir exatamente 2 polígonos independentes");

// =========================================================================
// MÓDULO 4: DISSOLVER GEOMETRIAS (DISSOLVE)
// =========================================================================
Console.WriteLine("[4/17] Testando Dissolução Global e por Atributo Categórico...");

var q1 = (Polygon)wktReader.Read("POLYGON((0 0, 50 0, 50 50, 0 50, 0 0))");
var q2 = (Polygon)wktReader.Read("POLYGON((50 0, 100 0, 100 50, 50 50, 50 0))");
var q3 = (Polygon)wktReader.Read("POLYGON((0 50, 50 50, 50 100, 0 100, 0 50))");
var q4 = (Polygon)wktReader.Read("POLYGON((50 50, 100 50, 100 100, 50 100, 50 50))");

var attrsQ1 = new AttributesTable { { "REGIAO", "NORTE" }, { "VALOR", 10 } };
var attrsQ2 = new AttributesTable { { "REGIAO", "NORTE" }, { "VALOR", 20 } };
var attrsQ3 = new AttributesTable { { "REGIAO", "SUL" }, { "VALOR", 30 } };
var attrsQ4 = new AttributesTable { { "REGIAO", "SUL" }, { "VALOR", 40 } };

var quatroFeicoes = new[]
{
    new Feature(q1, attrsQ1),
    new Feature(q2, attrsQ2),
    new Feature(q3, attrsQ3),
    new Feature(q4, attrsQ4)
};

var dissolveAll = VectorAggregationAlgorithms.ExecutarDissolucao(quatroFeicoes, null);
Assert(dissolveAll.Count == 1, "Dissolve All deve produzir exatamente 1 feição única");
Assert(Math.Abs(dissolveAll[0].Geometry.Area - 10000.0) < 1e-3, "Área unificada deve ser exatamente 10.000");

var dissolveCat = VectorAggregationAlgorithms.ExecutarDissolucao(
    quatroFeicoes,
    "REGIAO",
    ModoAgregacaoDissolve.Soma);

Assert(dissolveCat.Count == 2, "Dissolução por REGIAO deve produzir exatamente 2 grupos (NORTE e SUL)");
var grupoNorte = dissolveCat.First(f => f.Attributes["REGIAO"].ToString() == "NORTE");
var grupoSul = dissolveCat.First(f => f.Attributes["REGIAO"].ToString() == "SUL");

Assert(Math.Abs(grupoNorte.Geometry.Area - 5000.0) < 1e-3, "Área do grupo NORTE deve ser 5000");
Assert(Math.Abs(grupoSul.Geometry.Area - 5000.0) < 1e-3, "Área do grupo SUL deve ser 5000");
Assert(Convert.ToDouble(grupoNorte.Attributes["VALOR"]) == 30.0, "Soma dos valores de NORTE deve ser 30 (10 + 20)");
Assert(Convert.ToDouble(grupoSul.Attributes["VALOR"]) == 70.0, "Soma dos valores de SUL deve ser 70 (30 + 40)");
Assert(Convert.ToInt32(grupoNorte.Attributes["CONTAGEM"]) == 2, "Contagem de NORTE deve ser 2");

// =========================================================================
// MÓDULO 5: ZONA DE AMORTECIMENTO EM LOTE (BUFFER ENGINE)
// =========================================================================
Console.WriteLine("[5/17] Testando Buffer Paramétrico, Pontas, Junções e Dissolução...");

var pCentro = gf.CreatePoint(new Coordinate(0, 0));
var featPonto = new Feature(pCentro, attrs1);
var bufPonto = VectorGeometryAlgorithms.ExecutarBuffer(new[] { featPonto }, 10.0, segmentosQuadrante: 32);
Assert(bufPonto.Count == 1, "Buffer de ponto deve gerar 1 feição");
double areaPonto = bufPonto[0].Geometry.Area;
Assert(Math.Abs(areaPonto - Math.PI * 100.0) < 2.0, $"Área do buffer circular ({areaPonto:F2}) deve aproximar π*r² ({Math.PI * 100:F2})");

var linH = gf.CreateLineString(new[] { new Coordinate(0, 0), new Coordinate(100, 0) });
var featLinH = new Feature(linH, attrs1);
var bufLinFlat = VectorGeometryAlgorithms.ExecutarBuffer(
    new[] { featLinH },
    10.0,
    segmentosQuadrante: 16,
    endCap: BufferEndCapStyle.Flat);
Assert(Math.Abs(bufLinFlat[0].Geometry.Area - 2000.0) < 1e-2, "Buffer Flat de linha deve ter área exatamente 2000 (100 x 20)");

var p1 = new Feature(gf.CreatePoint(new Coordinate(0, 0)), attrs1);
var p2 = new Feature(gf.CreatePoint(new Coordinate(10, 0)), attrs1);
var bufDissolvido = VectorGeometryAlgorithms.ExecutarBuffer(new[] { p1, p2 }, 10.0, dissolver: true);
Assert(bufDissolvido.Count == 1, "Dois buffers sobrepostos com opção dissolver devem gerar 1 único polígono contínuo");
Assert(bufDissolvido[0].Geometry.Area < (areaPonto * 2.0), "Área dos buffers dissolvidos deve ser menor que a soma isolada devido à sobreposição");

// =========================================================================
// MÓDULO 6: ENVOLTÓRIA CONVEXA (CONVEX HULL)
// =========================================================================
Console.WriteLine("[6/17] Testando Envoltória Convexa (Global e Nuvem de Pontos)...");

var polyL = (Polygon)wktReader.Read("POLYGON((0 0, 20 0, 20 10, 10 10, 10 20, 0 20, 0 0))");
var featL = new Feature(polyL, attrs1);

var hullL = VectorGeometryAlgorithms.ExecutarEnvoltoriaConvexa(new[] { featL }, global: true);
Assert(hullL.Count == 1, "Envoltória convexa deve gerar 1 feição");
Assert(hullL[0].Geometry.Area > polyL.Area, "Área da envoltória convexa deve ser estritamente maior que o polígono côncavo 'L'");
Assert(hullL[0].Geometry.Covers(polyL), "A envoltória convexa deve cobrir 100% do polígono original");

var rnd = new Random(42);
var pontosAleatorios = Enumerable.Range(0, 60)
    .Select(_ => new Feature(gf.CreatePoint(new Coordinate(rnd.NextDouble() * 100, rnd.NextDouble() * 100)), attrs1))
    .ToList();

var hullNuvem = VectorGeometryAlgorithms.ExecutarEnvoltoriaConvexa(pontosAleatorios, global: true);
Assert(hullNuvem.Count == 1, "Envoltória da nuvem de pontos deve ser 1 polígono");
foreach (var pt in pontosAleatorios)
{
    Assert(hullNuvem[0].Geometry.Covers(pt.Geometry), "Todos os pontos da nuvem devem estar dentro ou na borda da envoltória");
}

// =========================================================================
// MÓDULO 7: CENTROIDES & POLOS DE INACESSIBILIDADE (POLYLABEL)
// =========================================================================
Console.WriteLine("[7/17] Testando Extração de Centroides e Polo de Inacessibilidade em Formas Côncavas...");

var polyFerradura = (Polygon)wktReader.Read("POLYGON((0 0, 100 0, 100 30, 30 30, 30 70, 100 70, 100 100, 0 100, 0 0))");
var featFerradura = new Feature(polyFerradura, attrs1);

var centroides = VectorGeometryAlgorithms.ExecutarCentroides(new[] { featFerradura }, usarPolylabel: true);
Assert(centroides.Count == 1, "Extração de centroide deve gerar 1 feição pontual");
var ptCentroide = (Point)centroides[0].Geometry;

Assert(polyFerradura.Contains(ptCentroide), "O Polo de Inacessibilidade (Polylabel) DEVE estar estritamente no INTERIOR do polígono côncavo");
Assert(centroides[0].Attributes["NOME"].ToString() == "Talhao_A", "Atributos da feição poligonal devem ser integralmente preservados no ponto");

// =========================================================================
// MÓDULO 8: BENCHMARK DE DESEMPENHO E ESTRESSE COM STRTREE
// =========================================================================
Console.WriteLine("[8/17] Testando Desempenho e Estresse com Indexação STRtree...");

var feicoesSinteticas = new List<IFeature>(2000);
for (int i = 0; i < 2000; i++)
{
    double bx = (i % 50) * 20.0;
    double by = (i / 50) * 20.0;
    var quadrado = gf.CreatePolygon(new[]
    {
        new Coordinate(bx, by),
        new Coordinate(bx + 15, by),
        new Coordinate(bx + 15, by + 15),
        new Coordinate(bx, by + 15),
        new Coordinate(bx, by)
    });
    var at = new AttributesTable { { "FID", i }, { "VAL", i * 1.5 } };
    feicoesSinteticas.Add(new Feature(quadrado, at));
}

var mascaraEstresse = (Polygon)wktReader.Read("POLYGON((200 200, 600 200, 600 600, 200 600, 200 200))");

var swEstresse = Stopwatch.StartNew();
var resultadoClipEstresse = VectorOverlayAlgorithms.ExecutarRecorte(feicoesSinteticas, mascaraEstresse);
swEstresse.Stop();

Assert(resultadoClipEstresse.Count > 100, "Diversas feições devem ser recortadas com sucesso");
Assert(swEstresse.ElapsedMilliseconds < 500, $"Recorte de 2.000 feições via STRtree/BBox deve rodar em <500ms: gastou {swEstresse.ElapsedMilliseconds}ms");

// =========================================================================
// MÓDULO 9: PROXIMIDADE E TESSELAÇÃO (VORONOI, DELAUNAY TIN, CLARK-EVANS)
// =========================================================================
Console.WriteLine("[9/17] Testando Voronoi (Thiessen), Delaunay (TIN) e Vizinho Mais Próximo (Clark-Evans)...");

var ptsTess = new List<IFeature>
{
    new Feature(gf.CreatePoint(new Coordinate(25, 25)), new AttributesTable { { "P_ID", 1 } }),
    new Feature(gf.CreatePoint(new Coordinate(75, 25)), new AttributesTable { { "P_ID", 2 } }),
    new Feature(gf.CreatePoint(new Coordinate(25, 75)), new AttributesTable { { "P_ID", 3 } }),
    new Feature(gf.CreatePoint(new Coordinate(75, 75)), new AttributesTable { { "P_ID", 4 } })
};

// Voronoi com máscara poligonal 100x100
var voronoiResult = ProximityTessellationAlgorithms.ExecutarVoronoi(ptsTess, polyEntrada, margemPercentual: 0.2);
Assert(voronoiResult.Count == 4, $"Voronoi com 4 geradores deve gerar exatamente 4 polígonos (gerou {voronoiResult.Count})");
double areaTotalVoronoi = voronoiResult.Sum(f => f.Geometry.Area);
Assert(Math.Abs(areaTotalVoronoi - 10000.0) < 1.0, $"Soma das áreas de Voronoi recortado deve ser 10000 (deu {areaTotalVoronoi:F2})");
foreach (var vf in voronoiResult)
{
    Assert(vf.Attributes.Exists("P_ID"), "Atributos do ponto gerador devem ser transferidos ao polígono Voronoi correspondente");
}

// Delaunay Triangulation
var delaunayResult = ProximityTessellationAlgorithms.ExecutarTriangulacaoDelaunay(ptsTess);
Assert(delaunayResult.Count >= 2, $"Delaunay de 4 cantos deve gerar ao menos 2 triângulos (gerou {delaunayResult.Count})");
foreach (var tri in delaunayResult)
{
    Assert(tri.Geometry is Polygon poly && poly.ExteriorRing.NumPoints == 4, "Cada triângulo Delaunay deve ter 3 vértices (+ fechamento)");
}

// Clark-Evans
var (linhasNna, relatorioNna) = ProximityTessellationAlgorithms.ExecutarVizinhoMaisProximo(ptsTess, 10000.0);
Assert(linhasNna.Count == 4, $"Vizinho mais próximo para 4 pontos deve gerar 4 vetores de conexão (gerou {linhasNna.Count})");
Assert(!string.IsNullOrEmpty(relatorioNna) && relatorioNna.Contains("Clark-Evans R:"), "Relatório estatístico de Clark-Evans deve conter o índice R");

// =========================================================================
// MÓDULO 10: SOBREPOSIÇÃO TOPOLÓGICA AVANÇADA (XOR, FULL UNION, PATCH)
// =========================================================================
Console.WriteLine("[10/17] Testando Diferença Simétrica (XOR), União Topológica Plena e Atualização (Patch)...");

var polyXorA = (Polygon)wktReader.Read("POLYGON((0 0, 80 0, 80 100, 0 100, 0 0))"); // 8000
var polyXorB = (Polygon)wktReader.Read("POLYGON((20 0, 100 0, 100 100, 20 100, 20 0))"); // 8000
var featXorA = new Feature(polyXorA, new AttributesTable { { "ORIGEM", "A" } });
var featXorB = new Feature(polyXorB, new AttributesTable { { "ORIGEM", "B" } });

// Symmetrical Difference (XOR)
var xorResult = AdvancedOverlayAlgorithms.ExecutarDiferencaSimetrica(new[] { featXorA }, new[] { featXorB });
double areaXor = xorResult.Sum(f => f.Geometry.Area);
Assert(Math.Abs(areaXor - 4000.0) < 1e-2, $"Área da Diferença Simétrica (XOR) deve ser 4000 (deu {areaXor:F2})");
Assert(xorResult.Count == 2, $"XOR deve gerar 2 feições disjuntas (gerou {xorResult.Count})");

// Full Topology Union
var unionResult = AdvancedOverlayAlgorithms.ExecutarUniaoPlena(new[] { featXorA }, new[] { featXorB });
double areaFullUnion = unionResult.Sum(f => f.Geometry.Area);
Assert(Math.Abs(areaFullUnion - 10000.0) < 1e-2, $"Área total da União Topológica deve cobrir todo o envelope 10000 (deu {areaFullUnion:F2})");
Assert(unionResult.Count == 3, $"União Topológica Plena deve gerar 3 partições atômicas: A\\B, A inter B, B\\A (gerou {unionResult.Count})");

// Spatial Update (Patch)
var patchBase = new Feature(polyEntrada, new AttributesTable { { "STATUS", "ANTIGO" } });
var patchOverlay = new Feature(
    (Polygon)wktReader.Read("POLYGON((20 20, 80 20, 80 80, 20 80, 20 20))"),
    new AttributesTable { { "STATUS", "NOVO_PATCH" } });
var patchResult = AdvancedOverlayAlgorithms.ExecutarAtualizacaoCamadas(new[] { patchBase }, new[] { patchOverlay });
double areaPatchTotal = patchResult.Sum(f => f.Geometry.Area);
Assert(Math.Abs(areaPatchTotal - 10000.0) < 1e-2, $"Área total após Patch deve manter 10000 (deu {areaPatchTotal:F2})");
var patchFeat = patchResult.First(f => f.Attributes["STATUS"].ToString() == "NOVO_PATCH");
Assert(Math.Abs(patchFeat.Geometry.Area - 3600.0) < 1e-2, "Feição de patch deve ter área exatamente 3600");

// =========================================================================
// MÓDULO 11: GENERALIZAÇÃO CARTOGRÁFICA & SUAVIZAÇÃO
// =========================================================================
Console.WriteLine("[11/17] Testando Douglas-Peucker Topológico, Visvalingam-Whyatt e Chaikin Corner-Cutting...");

var linhaComplexa = gf.CreateLineString(new[]
{
    new Coordinate(0, 0),
    new Coordinate(25, 0.4),
    new Coordinate(50, 0),
    new Coordinate(75, -0.4),
    new Coordinate(100, 0)
});
var featLinhaComplexa = new Feature(linhaComplexa, attrs1);

// Douglas-Peucker
var dpResult = CartographicGeneralizationAlgorithms.ExecutarSimplificacaoDouglasPeucker(new[] { featLinhaComplexa }, toleranciaMetros: 1.0);
Assert(dpResult[0].Geometry.NumPoints == 2, $"Douglas-Peucker com tolerância 1.0 deve reduzir de 5 para 2 pontos (reduziu para {dpResult[0].Geometry.NumPoints})");

// Visvalingam-Whyatt
var vwResult = CartographicGeneralizationAlgorithms.ExecutarSimplificacaoVisvalingam(new[] { featLinhaComplexa }, areaMinimaM2: 20.0);
Assert(vwResult[0].Geometry.NumPoints < linhaComplexa.NumPoints, "Visvalingam-Whyatt deve simplificar vértices com área triangular insignificante");

// Chaikin Corner-Cutting
var linhaCurva = gf.CreateLineString(new[]
{
    new Coordinate(0, 0),
    new Coordinate(50, 50),
    new Coordinate(100, 0)
});
var featLinhaCurva = new Feature(linhaCurva, attrs1);
var chaikinResult = CartographicGeneralizationAlgorithms.ExecutarSuavizacaoChaikin(new[] { featLinhaCurva }, iteracoes: 2);
Assert(chaikinResult[0].Geometry.NumPoints > linhaCurva.NumPoints, $"Chaikin deve aumentar número de vértices suavizando os cantos (gerou {chaikinResult[0].Geometry.NumPoints} pontos)");
Assert(chaikinResult[0].Geometry.Coordinates.First().Equals2D(new Coordinate(0, 0)), "Chaikin deve preservar extremidade inicial da linha");
Assert(chaikinResult[0].Geometry.Coordinates.Last().Equals2D(new Coordinate(100, 0)), "Chaikin deve preservar extremidade final da linha");

// =========================================================================
// MÓDULO 12: CONVERSÃO GEOMÉTRICA ESTRUTURAL
// =========================================================================
Console.WriteLine("[12/17] Testando Explodir Multipartes, Agrupamento por Atributo, Polígonos para Linhas, Poligonizador e Vértices...");

// Explodir Multipartes
var mpGeom = gf.CreateMultiPolygon(new[] { q1, q4 });
var featMp = new Feature(mpGeom, new AttributesTable { { "GRUPO", "DIAGONAL" } });
var exploded = GeometricConversionAlgorithms.ExecutarExplodirMultipartes(new[] { featMp });
Assert(exploded.Count == 2, $"Explodir MultiPolygon com 2 partes deve gerar 2 feições (gerou {exploded.Count})");
Assert(exploded.All(f => f.Geometry is Polygon), "Todas as feições explodidas devem ser Polygons simples");
Assert(exploded.All(f => f.Attributes["GRUPO"].ToString() == "DIAGONAL"), "Atributos devem ser clonados em cada feição individual");

// Agrupar por Atributo (Singlepart to Multipart)
var grouped = GeometricConversionAlgorithms.ExecutarAgruparPorAtributo(quatroFeicoes, "REGIAO");
Assert(grouped.Count == 2, $"Agrupamento por REGIAO deve gerar 2 feições agregadas (gerou {grouped.Count})");

// Polígonos para Linhas
var polyLines = GeometricConversionAlgorithms.ExecutarPoligonosParaLinhas(new[] { new Feature(polyEntrada, attrs1) });
Assert(polyLines.Count == 1 && (polyLines[0].Geometry is LineString || polyLines[0].Geometry is MultiLineString), "Polígono para Linhas deve converter o anel perimetral em geometria linear");
Assert(Math.Abs(polyLines[0].Geometry.Length - 400.0) < 1e-3, "Perímetro da linha gerada do quadrado 100x100 deve ser 400");

// Poligonizador
var bordas = new List<IFeature>
{
    new Feature(gf.CreateLineString(new[] { new Coordinate(0, 0), new Coordinate(100, 0) }), null),
    new Feature(gf.CreateLineString(new[] { new Coordinate(100, 0), new Coordinate(100, 100) }), null),
    new Feature(gf.CreateLineString(new[] { new Coordinate(100, 100), new Coordinate(0, 100) }), null),
    new Feature(gf.CreateLineString(new[] { new Coordinate(0, 100), new Coordinate(0, 0) }), null)
};
var polygonized = GeometricConversionAlgorithms.ExecutarPoligonizacaoLinhas(bordas);
Assert(polygonized.Count == 1, $"Poligonizador de 4 bordas conectadas deve gerar 1 polígono (gerou {polygonized.Count})");
Assert(Math.Abs(polygonized[0].Geometry.Area - 10000.0) < 1e-3, "Área do polígono poligonizado deve ser exatamente 10000");

// Extrair Vértices Analíticos
var vertices = GeometricConversionAlgorithms.ExecutarExtrairVertices(new[] { featLinhaCurva });
Assert(vertices.Count == 3, $"Extração de vértices de linha com 3 pontos deve gerar 3 pontos (gerou {vertices.Count})");
Assert(vertices[0].Attributes.Exists("V_IDX"), "Atributo V_IDX deve existir");
Assert(vertices[0].Attributes.Exists("DIST_M"), "Atributo DIST_M deve existir");
Assert(vertices[0].Attributes.Exists("AZIM_DEG"), "Atributo AZIM_DEG deve existir");

// =========================================================================
// MÓDULO 13: SEGMENTAÇÃO LINEAR & INTERSECÇÕES
// =========================================================================
Console.WriteLine("[13/17] Testando Quebra em Intersecções, Estaqueamento / Chaining e Partição de Polígonos...");

// Quebra de Linhas em Intersecções
var lineH = gf.CreateLineString(new[] { new Coordinate(0, 50), new Coordinate(100, 50) });
var lineV = gf.CreateLineString(new[] { new Coordinate(50, 0), new Coordinate(50, 100) });
var splitLines = LinearSegmentationAlgorithms.ExecutarQuebraLinhasInterseccoes(new[]
{
    new Feature(lineH, attrs1),
    new Feature(lineV, attrs1)
});
Assert(splitLines.Count == 4, $"Quebra de 2 linhas em cruz no ponto central deve gerar 4 segmentos (gerou {splitLines.Count})");
Assert(splitLines.All(f => Math.Abs(f.Geometry.Length - 50.0) < 1e-3), "Cada segmento de linha resultante deve ter comprimento 50");

// Estaqueamento Linear (Stationing / Chaining)
var estaqueamento = LinearSegmentationAlgorithms.ExecutarEstaqueamentoLinhas(
    new[] { new Feature(lineH, attrs1) },
    distanciaEstacaM: 25.0);
Assert(estaqueamento.Count == 4, $"Estaqueamento de linha de 100m a cada 25m deve gerar 4 estacas de 25m (gerou {estaqueamento.Count})");
Assert(estaqueamento.All(f => f.Attributes.Exists("STA_ID") && f.Attributes.Exists("SEG_LEN")), "Atributos STA_ID e SEG_LEN devem existir");

// Partição de Polígonos por Linhas (Split Polygons with Lines)
var polyToSplit = new Feature(polyEntrada, attrs1);
var cuttingLine = new Feature(gf.CreateLineString(new[] { new Coordinate(-10, 50), new Coordinate(110, 50) }), null);
var splitPolys = LinearSegmentationAlgorithms.ExecutarParticaoPoligonosPorLinhas(
    new[] { polyToSplit },
    new[] { cuttingLine });
Assert(splitPolys.Count == 2, $"Corte de polígono com linha horizontal deve gerar 2 polígonos (gerou {splitPolys.Count})");
Assert(splitPolys.All(f => Math.Abs(f.Geometry.Area - 5000.0) < 1e-2), "Cada polígono partido deve ter área de 5000");

// =========================================================================
// MÓDULO 14: AMOSTRAGEM E GRADES ESPACIAIS
// =========================================================================
Console.WriteLine("[14/17] Testando Grades Cartesiana, Hexagonal (Hexbins), Poisson Disk e Amostragem Regular...");

var envelopeTeste = new Envelope(0, 100, 0, 100);

// Grade Cartesiana
var gridCart = SpatialGridSamplingAlgorithms.ExecutarGradeCartesiana(envelopeTeste, 50.0, 50.0);
Assert(gridCart.Count == 4, $"Grade Cartesiana 50x50 em envelope 100x100 deve gerar 4 células (gerou {gridCart.Count})");
Assert(gridCart.All(c => Math.Abs(c.Geometry.Area - 2500.0) < 1e-3), "Cada célula quadrada deve ter área 2500");

// Grade Hexagonal
var gridHex = SpatialGridSamplingAlgorithms.ExecutarGradeHexagonal(envelopeTeste, raioMetros: 20.0);
Assert(gridHex.Count >= 4, $"Grade Hexagonal deve gerar células hexagonais regulares cobrindo o envelope (gerou {gridHex.Count})");
Assert(gridHex.All(c => c.Geometry is Polygon poly && poly.ExteriorRing.NumPoints == 7), "Cada célula de grade hexagonal deve ter 6 lados (+ fechamento = 7 vértices)");

// Poisson Disk Sampling
var poisson = SpatialGridSamplingAlgorithms.ExecutarAmostragemPoissonDisk(polyEntrada, distanciaMinimaM: 20.0, maxPontos: 30);
Assert(poisson.Count > 0, "Poisson Disk deve gerar amostras pontuais");
for (int i = 0; i < poisson.Count; i++)
{
    for (int j = i + 1; j < poisson.Count; j++)
    {
        double dist = poisson[i].Geometry.Distance(poisson[j].Geometry);
        Assert(dist >= 19.99, $"Distância entre pontos de Poisson ({dist:F2}) não pode ser inferior ao raio mínimo de 20");
    }
}

// Amostragem Regular de Pontos
var regularPts = SpatialGridSamplingAlgorithms.ExecutarAmostragemRegularPontos(polyEntrada, espacamentoM: 25.0);
Assert(regularPts.Count == 25, $"Amostragem regular 5x5 deve produzir exatamente 25 pontos (gerou {regularPts.Count})");

// =========================================================================
// MÓDULO 15: ESTATÍSTICAS ZONAIS & ANÁLISE PONTUAL/LINEAR
// =========================================================================
Console.WriteLine("[15/17] Testando Contagem/Soma de Pontos em Polígonos, Densidade Linear e Spatial Join...");

var poligonosZonais = new List<IFeature>
{
    new Feature(q1, new AttributesTable { { "ZONA", "Q1" } }),
    new Feature(q2, new AttributesTable { { "ZONA", "Q2" } })
};

var pontosZonais = new List<IFeature>
{
    new Feature(gf.CreatePoint(new Coordinate(10, 10)), new AttributesTable { { "PESO", 5.0 } }),
    new Feature(gf.CreatePoint(new Coordinate(20, 20)), new AttributesTable { { "PESO", 15.0 } }),
    new Feature(gf.CreatePoint(new Coordinate(60, 20)), new AttributesTable { { "PESO", 30.0 } })
};

// Contagem e Soma de Pontos
var contagemPontos = ZonalStatisticsAlgorithms.ExecutarContagemPontosPoligono(
    poligonosZonais,
    pontosZonais,
    campoValorSomar: "PESO");
var resQ1 = contagemPontos.First(f => f.Attributes["ZONA"].ToString() == "Q1");
var resQ2 = contagemPontos.First(f => f.Attributes["ZONA"].ToString() == "Q2");
Assert(Convert.ToInt32(resQ1.Attributes["PT_COUNT"]) == 2, $"Q1 deve conter 2 pontos (tem {resQ1.Attributes["PT_COUNT"]})");
Assert(Convert.ToDouble(resQ1.Attributes["PT_SUM"]) == 20.0, $"Soma de PESO em Q1 deve ser 20 (tem {resQ1.Attributes["PT_SUM"]})");
Assert(Convert.ToInt32(resQ2.Attributes["PT_COUNT"]) == 1, $"Q2 deve conter 1 ponto (tem {resQ2.Attributes["PT_COUNT"]})");
Assert(Convert.ToDouble(resQ2.Attributes["PT_SUM"]) == 30.0, $"Soma de PESO em Q2 deve ser 30 (tem {resQ2.Attributes["PT_SUM"]})");

// Comprimento e Densidade Linear
var estradas = new List<IFeature>
{
    new Feature(gf.CreateLineString(new[] { new Coordinate(0, 25), new Coordinate(50, 25) }), null),
    new Feature(gf.CreateLineString(new[] { new Coordinate(25, 0), new Coordinate(25, 50) }), null)
};
var densidade = ZonalStatisticsAlgorithms.ExecutarDensidadeLinearPoligonos(
    new[] { poligonosZonais[0] },
    estradas);
Assert(Convert.ToDouble(densidade[0].Attributes["LINE_LEN_M"]) == 100.0, "Comprimento linear total dentro de Q1 deve ser 100m");
Assert(Convert.ToDouble(densidade[0].Attributes["DENS_KM_KM2"]) > 0, "Densidade linear (km/km²) deve ser maior que zero");

// Junção Espacial Avançada (Spatial Join)
var joinResult = ZonalStatisticsAlgorithms.ExecutarSpatialJoinAvancado(
    poligonosZonais,
    pontosZonais,
    predicadoTopologico: "Intersects");
Assert(joinResult.Count == 2, $"Spatial Join deve produzir 2 registros na camada alvo (gerou {joinResult.Count})");
Assert(joinResult[0].Attributes.Exists("PESO"), "Atributos da camada de junção devem ter sido unidos");

// =========================================================================
// MÓDULO 16: CATÁLOGO CENTRAL E BOOTSTRAPPER (29+ ALGORITMOS)
// =========================================================================
Console.WriteLine("[16/17] Testando Bootstrapper do Catálogo Central e Registro de Algoritmos...");

var catalogo = new ProcessingAlgorithmRegistry();
ProcessingCatalogBootstrapper.RegistrarTodos(catalogo);
var todos = catalogo.ObterTodos();
Assert(todos.Count >= 29, $"O catálogo central de geoprocessamento deve conter ao menos 29 algoritmos registrados (contém {todos.Count})");

var algoVoronoi = catalogo.ObterPorId("proximity_voronoi");
Assert(algoVoronoi != null, "Algoritmo proximity_voronoi deve existir no catálogo");
Assert(algoVoronoi.Nome == "Polígonos de Voronoi (Thiessen)", "Nome do algoritmo de Voronoi deve corresponder");
Assert(algoVoronoi.ParametrosAdicionais.Count > 0, "Algoritmo de Voronoi deve declarar parâmetros formais");

var categorias = catalogo.ObterCategorias();
Assert(categorias.Count >= 6, $"Catálogo deve estar estruturado em no mínimo 6 categorias formais (tem {categorias.Count})");
Assert(categorias.ContainsKey("Superposição Topológica"), "Categoria 'Superposição Topológica' deve existir");
Assert(categorias.ContainsKey("Malhas Espaciais e Amostragem"), "Categoria 'Malhas Espaciais e Amostragem' deve existir");
Assert(categorias.ContainsKey("Análise de Proximidade e Tesselação"), "Categoria 'Análise de Proximidade e Tesselação' deve existir");

var buscaFuzzy = catalogo.Buscar("Delaunay");
Assert(buscaFuzzy.Any(a => a.Id == "proximity_delaunay"), "Busca fuzzy por 'Delaunay' deve localizar proximity_delaunay");

// =========================================================================
// MÓDULO 17: FILA EM LOTE MULTITHREAD ASSÍNCRONA (BATCH QUEUE)
// =========================================================================
Console.WriteLine("[17/17] Testando Fila de Processamento em Lote (BatchGeoprocessingEngine)...");

var batchEngine = new BatchGeoprocessingEngine(catalogo);
var taskItem1 = new BatchTaskItem(
    "job_1",
    "Lote Voronoi Teste",
    "caminho_inexistente_1.shp",
    "saida_inexistente_1.shp",
    "proximity_voronoi",
    new Dictionary<string, object?>()
);
var taskItem2 = new BatchTaskItem(
    "job_2",
    "Lote Delaunay Teste",
    "caminho_inexistente_2.shp",
    "saida_inexistente_2.shp",
    "proximity_delaunay",
    new Dictionary<string, object?>()
);

batchEngine.EnfileirarTarefa(taskItem1);
batchEngine.EnfileirarTarefa(taskItem2);

Assert(batchEngine.QuantidadeNaFila == 2, "Motor em lote deve registrar 2 trabalhos enfileirados");

// Teste de esvaziamento e fila
batchEngine.LimparFila();
Assert(batchEngine.QuantidadeNaFila == 0, "Fila deve ficar vazia após LimparFila()");

// Re-enfileirar e testar execução segura com arquivo inexistente (recuperação transacional)
batchEngine.EnfileirarTarefa(taskItem1);
var batchResultados = batchEngine.ExecutarFilaAsync().GetAwaiter().GetResult();
Assert(batchResultados.Count == 1, "Execução deve produzir 1 resultado de trabalho");
Assert(!batchResultados[0].Sucesso, "Job com arquivo inexistente deve reportar falha controlada sem quebrar a execução");
Assert(batchResultados[0].MensagemErro!.Contains("não existe"), "Mensagem de erro descritiva deve ser retornada");

Console.WriteLine($"\n==================================================================================");
Console.WriteLine($">>> SUCESSO TOTAL! TODOS OS 17 MÓDULOS DE GEOPROCESSAMENTO FORAM VALIDADOS! <<<");
Console.WriteLine($">>> Total de asserções executadas com 100% de precisão: {assertions} <<<");
Console.WriteLine($"==================================================================================\n");
