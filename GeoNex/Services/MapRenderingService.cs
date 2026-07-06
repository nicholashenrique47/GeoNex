using SkiaSharp;
using OSGeo.GDAL;
using System;
using System.Collections.Generic;
using NetTopologySuite.Features;
using NetTopologySuite.Index.Strtree;

namespace GeoNex.Services
{
    public class MapRenderingService
    {

        public float CameraZoom { get; set; } = 1.0f;
        public float CameraPanX { get; set; } = 0f;
        public float CameraPanY { get; set; } = 0f;
        public double DistanciaTotal { get; set; }
        public double AreaTotal { get; set; }
        public SkiaSharp.SKPath? CaminhoFeicaoDestacada { get; set; }
        public SKPaint PincelFill { get; private set; }
        public SKPaint PincelBorda { get; private set; }
        // === FERRAMENTAS DE SELEÇÃO VETORIAL ===
        public SKPath? CaminhoDestaque { get; private set; }
        // === FERRAMENTAS DE SELEÇÃO VETORIAL ===
        public SKPath? CaminhoDestaquePoligono { get; private set; }
        public SKPath? CaminhoDestaqueLinha { get; private set; }
        public SKPaint PincelDestaqueFill { get; private set; } = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Yellow.WithAlpha(100), IsAntialias = true };
        public SKPaint PincelDestaqueBorda { get; private set; } = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Yellow, StrokeWidth = 2, IsAntialias = true };
        public Dictionary<string, SKPath> VetoresPorCamada { get; private set; } = new();
        public Dictionary<string, SKPath> LinhasPorCamada { get; private set; } = new();
        public Dictionary<string, SKPath> PontosPorCamada { get; private set; } = new();
        // === NOVOS DICIONÁRIOS PARA O MODO CATEGORIZADO ===
        public Dictionary<string, FeatureCollection> FeicoesOriginais { get; private set; } = new();
        public Dictionary<string, Dictionary<string, SKPath>> VetoresCategorizados { get; private set; } = new();
        public List<string> OrdemCamadas { get; set; } = new();
        public List<SkiaSharp.SKPoint> PontosMedicao { get; set; } = new();
        public List<SkiaSharp.SKPoint> PontosAquisicao { get; set; } = new();
        // === INDICADOR VISUAL DO SNAP (ARCGIS HOVER) ===
        public SkiaSharp.SKPoint? PontoCursorSnap { get; set; }
        public SkiaSharp.SKPoint? PontoCursorMundo { get; set; }
        public bool MostrarAreaMedicao { get; set; } = false; // Controla se desenha a área

        // === MOTOR DE SNAP (ATRAÇÃO MAGNÉTICA) ===
        // 2. MOTOR DE SNAP MAGNÉTICO (Procura vértices em TODAS as camadas)
        // 2. MOTOR DE SNAP MAGNÉTICO OTIMIZADO
        // 2. MOTOR DE SNAP MAGNÉTICO OTIMIZADO (Vértices + Arestas)
        // 2. MOTOR DE SNAP MAGNÉTICO OTIMIZADO (Vértices ou Arestas)
        // 2. MOTOR DE SNAP MAGNÉTICO OTIMIZADO (Vértices e/ou Arestas isolados)
        public SkiaSharp.SKPoint? EncontrarVerticeProximo(SkiaSharp.SKPoint ptClique, float toleranciaMundo, bool checarVertices = true, bool checarArestas = false)
        {
            SkiaSharp.SKPoint? melhorPonto = null;
            float menorDistanciaSq = toleranciaMundo * toleranciaMundo;

            // === OTIMIZAÇÃO EXTREMA: A CAIXA ESPACIAL ===
            // Cria um retângulo minúsculo à volta do rato.
            var rectClique = new SkiaSharp.SKRect(
                ptClique.X - toleranciaMundo,
                ptClique.Y - toleranciaMundo,
                ptClique.X + toleranciaMundo,
                ptClique.Y + toleranciaMundo
            );

            void ChecarGeometrias(Dictionary<string, SkiaSharp.SKPath> dicionarioPaths, bool avaliarArestas)
            {
                foreach (var path in dicionarioPaths.Values)
                {
                    // A BARREIRA: Se a geometria inteira estiver fora do quadrado do rato, 
                    // abortamos imediatamente. Isto corta 99.9% do processamento da GPU!
                    if (!path.Bounds.IntersectsWith(rectClique)) continue;

                    // Como agora só 1 ou 2 polígonos sobrevivem ao filtro, 
                    // podemos usar o array nativo de altíssima velocidade sem sobrecarregar a memória
                    var pontosDaGeometria = path.Points;
                    int numPontos = pontosDaGeometria.Length;

                    if (numPontos < 2) continue;

                    // 1. CHECAR VÉRTICES
                    if (checarVertices)
                    {
                        for (int i = 0; i < numPontos; i++)
                        {
                            var pt = pontosDaGeometria[i];
                            float distSq = (pt.X - ptClique.X) * (pt.X - ptClique.X) + (pt.Y - ptClique.Y) * (pt.Y - ptClique.Y);

                            if (distSq < menorDistanciaSq)
                            {
                                menorDistanciaSq = distSq;
                                melhorPonto = pt;
                            }
                        }
                    }

                    // 2. CHECAR ARESTAS
                    if (avaliarArestas && checarArestas)
                    {
                        for (int i = 0; i < numPontos - 1; i++)
                        {
                            var p1 = pontosDaGeometria[i];
                            var p2 = pontosDaGeometria[i + 1];

                            float l2 = (p1.X - p2.X) * (p1.X - p2.X) + (p1.Y - p2.Y) * (p1.Y - p2.Y);
                            if (l2 == 0) continue;

                            float t = Math.Max(0, Math.Min(1, ((ptClique.X - p1.X) * (p2.X - p1.X) + (ptClique.Y - p1.Y) * (p2.Y - p1.Y)) / l2));
                            float projX = p1.X + t * (p2.X - p1.X);
                            float projY = p1.Y + t * (p2.Y - p1.Y);

                            float distSqSegmento = (ptClique.X - projX) * (ptClique.X - projX) + (ptClique.Y - projY) * (ptClique.Y - projY);

                            if (distSqSegmento < menorDistanciaSq)
                            {
                                menorDistanciaSq = distSqSegmento;
                                melhorPonto = new SkiaSharp.SKPoint(projX, projY);
                            }
                        }
                    }
                }
            }

            ChecarGeometrias(VetoresPorCamada, true);
            ChecarGeometrias(LinhasPorCamada, true);
            ChecarGeometrias(PontosPorCamada, false);

            // SNAP NO PRÓPRIO RASCUNHO (Permite fechar o polígono)
            if (checarVertices && PontosAquisicao.Count > 0)
            {
                foreach (var pt in PontosAquisicao)
                {
                    // O rascunho tem poucos pontos, não requer a caixa espacial
                    float distSq = (pt.X - ptClique.X) * (pt.X - ptClique.X) + (pt.Y - ptClique.Y) * (pt.Y - ptClique.Y);
                    if (distSq < menorDistanciaSq)
                    {
                        menorDistanciaSq = distSq;
                        melhorPonto = pt;
                    }
                }
            }

            return melhorPonto;
        }
        public string NomeRasterAtivo { get; set; } = "";

        // O MOTOR ANALÍTICO ESPACIAL (RAM C#) - Agora utilizando IFeature rigorosamente
        public Dictionary<string, STRtree<IFeature>> ArvoresEspaciais { get; private set; } = new();
        public Dictionary<string, List<(SkiaSharp.SKPoint Ponto, NetTopologySuite.Features.IAttributesTable Atributos, float LarguraMundo)>> PontosAncoragemRotulo { get; set; } = new();        //simbologia
        public System.Collections.Concurrent.ConcurrentDictionary<string, EstiloCamada> EstilosPorCamada { get; } = new();
        public Dataset DatasetRaster { get; set; }
        public bool TemRaster { get; set; } = false;
        public SkiaSharp.SKRect LimitesRasterGlobal { get; set; }
        // === CACHE DE ALTA PERFORMANCE PARA O RATO ===
        public SKBitmap? RasterCache { get; set; }
        public string CacheKey { get; set; } = "";
        public float CachePanX { get; set; } = 0;
        public float CachePanY { get; set; } = 0;
        public bool IsPanning { get; set; } = false; // Avisa a GPU que estamos a arrastar
        // === FERRAMENTAS DE MEDIÇÃO ===
        public event Action? OnMapInvalidated;

        public MapRenderingService()
        {
            PincelBorda = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Cyan.WithAlpha(200), StrokeWidth = 0, IsAntialias = true };
            PincelFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Cyan.WithAlpha(25), IsAntialias = true };
        }

        public void RequestRedraw() => OnMapInvalidated?.Invoke();

        // === ÂNCORA DE PRECISÃO MATEMÁTICA (Impede a distorção Float em UTM) ===
        public double OffsetMundoX { get; set; } = 0;
        public double OffsetMundoY { get; set; } = 0;
        public bool OffsetMundoDefinido { get; set; } = false;

        public void PreCompilarPoligonos(string nomeCamada, FeatureCollection feicoes)
        {
            FeicoesOriginais[nomeCamada] = feicoes;

            if (VetoresPorCamada.ContainsKey(nomeCamada)) VetoresPorCamada[nomeCamada].Dispose();
            if (LinhasPorCamada.ContainsKey(nomeCamada)) LinhasPorCamada[nomeCamada].Dispose();
            if (PontosPorCamada.ContainsKey(nomeCamada)) PontosPorCamada[nomeCamada].Dispose();

            // NOVO: Limpa os rótulos antigos e cria um novo cache para a camada
            if (PontosAncoragemRotulo.ContainsKey(nomeCamada)) PontosAncoragemRotulo.Remove(nomeCamada);
            PontosAncoragemRotulo[nomeCamada] = new();

            var polyPath = new SKPath { FillType = SKPathFillType.EvenOdd };
            var linePath = new SKPath();
            var pointPath = new SKPath();

            foreach (IFeature feicao in feicoes)
            {
                if (feicao.Geometry == null) continue;

                // NOVO: Calcula o "Centroide" geométrico e guarda para o texto ser desenhado
                // Usa o InteriorPoint para garantir que o texto cai dentro da terra, e não na água/fora
                var pontoAncora = feicao.Geometry.InteriorPoint ?? feicao.Geometry.Centroid;
                if (pontoAncora != null)
                {
                    float cx = (float)(pontoAncora.X - OffsetMundoX);
                    float cy = -(float)(pontoAncora.Y - OffsetMundoY);

                    float larguraMundo = (float)feicao.Geometry.EnvelopeInternal.Width;
                    // Se for um Ponto (ex: Poste), a largura é 0, então forçamos um valor gigante para aparecer sempre
                    if (larguraMundo == 0) larguraMundo = 999999f;

                    PontosAncoragemRotulo[nomeCamada].Add((new SkiaSharp.SKPoint(cx, cy), feicao.Attributes, larguraMundo));
                }

                if (!OffsetMundoDefinido)
                {
                    OffsetMundoX = feicao.Geometry.Coordinates[0].X;
                    OffsetMundoY = feicao.Geometry.Coordinates[0].Y;
                    OffsetMundoDefinido = true;
                }

                for (int i = 0; i < feicao.Geometry.NumGeometries; i++)
                {
                    var subGeom = feicao.Geometry.GetGeometryN(i);

                    if (subGeom is NetTopologySuite.Geometries.Polygon poly)
                    {
                        var extPath = new SKPath();
                        var extCoords = poly.ExteriorRing.Coordinates;
                        for (int j = 0; j < extCoords.Length; j++)
                        {
                            float x = (float)(extCoords[j].X - OffsetMundoX);
                            float y = -(float)(extCoords[j].Y - OffsetMundoY);
                            if (j == 0) extPath.MoveTo(x, y); else extPath.LineTo(x, y);
                        }
                        extPath.Close();
                        polyPath.AddPath(extPath);

                        foreach (var hole in poly.InteriorRings)
                        {
                            var holePath = new SKPath();
                            var holeCoords = hole.Coordinates;
                            for (int j = 0; j < holeCoords.Length; j++)
                            {
                                float x = (float)(holeCoords[j].X - OffsetMundoX);
                                float y = -(float)(holeCoords[j].Y - OffsetMundoY);
                                if (j == 0) holePath.MoveTo(x, y); else holePath.LineTo(x, y);
                            }
                            holePath.Close();
                            polyPath.AddPath(holePath);
                        }
                    }
                    else if (subGeom is NetTopologySuite.Geometries.LineString line)
                    {
                        var lp = new SKPath();
                        var coords = line.Coordinates;
                        for (int j = 0; j < coords.Length; j++)
                        {
                            float x = (float)(coords[j].X - OffsetMundoX);
                            float y = -(float)(coords[j].Y - OffsetMundoY);
                            if (j == 0) lp.MoveTo(x, y); else lp.LineTo(x, y);
                        }
                        linePath.AddPath(lp);
                    }
                    else if (subGeom is NetTopologySuite.Geometries.Point pt)
                    {
                        float x = (float)(pt.Coordinate.X - OffsetMundoX);
                        float y = -(float)(pt.Coordinate.Y - OffsetMundoY);
                        pointPath.AddCircle(x, y, 2.5f);
                    }
                }
            }

            // Guarda separadamente apenas o que existe
            if (!polyPath.IsEmpty) VetoresPorCamada[nomeCamada] = polyPath;
            if (!linePath.IsEmpty) LinhasPorCamada[nomeCamada] = linePath;
            if (!pointPath.IsEmpty) PontosPorCamada[nomeCamada] = pointPath;

            RequestRedraw();
        }
        // === COMPILADOR DE SIMBOLOGIA CATEGORIZADA ===
        public void CompilarCategorias(string nomeCamada, string colunaSimbologia)
        {
            if (!FeicoesOriginais.ContainsKey(nomeCamada)) return;

            var feicoes = FeicoesOriginais[nomeCamada];
            var dicCategorias = new Dictionary<string, SKPath>();

            foreach (IFeature feicao in feicoes)
            {
                if (feicao.Geometry == null) continue;

                // 1. Descobre a qual Categoria este polígono pertence (ex: "ELIANA")
                string valorCategoria = "NULO";
                if (feicao.Attributes != null && feicao.Attributes.Exists(colunaSimbologia))
                {
                    var objVal = feicao.Attributes[colunaSimbologia];
                    valorCategoria = objVal != null ? objVal.ToString() : "NULO";
                }

                // 2. Se a categoria é nova, cria um "Saco" (SKPath) para ela
                if (!dicCategorias.ContainsKey(valorCategoria))
                {
                    dicCategorias[valorCategoria] = new SKPath { FillType = SKPathFillType.EvenOdd };
                }

                // 3. Joga o polígono desenhado dentro do "Saco" certo
                var polyPath = dicCategorias[valorCategoria];

                for (int i = 0; i < feicao.Geometry.NumGeometries; i++)
                {
                    var subGeom = feicao.Geometry.GetGeometryN(i);
                    if (subGeom is NetTopologySuite.Geometries.Polygon poly)
                    {
                        var extPath = new SKPath();
                        var extCoords = poly.ExteriorRing.Coordinates;
                        for (int j = 0; j < extCoords.Length; j++)
                        {
                            float x = (float)(extCoords[j].X - OffsetMundoX);
                            float y = -(float)(extCoords[j].Y - OffsetMundoY);
                            if (j == 0) extPath.MoveTo(x, y); else extPath.LineTo(x, y);
                        }
                        extPath.Close();
                        polyPath.AddPath(extPath);

                        foreach (var hole in poly.InteriorRings)
                        {
                            var holePath = new SKPath();
                            var holeCoords = hole.Coordinates;
                            for (int j = 0; j < holeCoords.Length; j++)
                            {
                                float x = (float)(holeCoords[j].X - OffsetMundoX);
                                float y = -(float)(holeCoords[j].Y - OffsetMundoY);
                                if (j == 0) holePath.MoveTo(x, y); else holePath.LineTo(x, y);
                            }
                            holePath.Close();
                            polyPath.AddPath(holePath);
                        }
                    }
                }
            }
            // Guarda na placa gráfica as geometrias separadas
            VetoresCategorizados[nomeCamada] = dicCategorias;
        }
        // ======================================================
        // MOTOR DE RAYCASTING (POINT-IN-POLYGON)
        // ======================================================

        public void ConstruirIndiceEspacial(string nomeCamada, FeatureCollection feicoes)
        {
            var arvore = new STRtree<IFeature>();
            foreach (IFeature f in feicoes)
            {
                if (f.Geometry != null)
                {
                    arvore.Insert(f.Geometry.EnvelopeInternal, f);
                }
            }
            arvore.Build();
            ArvoresEspaciais[nomeCamada] = arvore;
        }

        // === MOTOR MATEMÁTICO (C#) ===
        public IFeature? DispararRaycast(double lat, double lng, double toleranciaMetros, out string camadaAtingida)
        {
            camadaAtingida = string.Empty;

            var pontoClique = new NetTopologySuite.Geometries.Point(lng, lat);

            // A MAGIA: Transforma o ponto cego num campo de força magnético (Buffer)
            var areaCaptura = pontoClique.Buffer(toleranciaMetros);

            float skiaX = (float)(lng - OffsetMundoX);
            float skiaY = (float)-(lat - OffsetMundoY);

            for (int i = OrdemCamadas.Count - 1; i >= 0; i--)
            {
                string camada = OrdemCamadas[i];

                if (TemRaster && camada == NomeRasterAtivo)
                {
                    if (LimitesRasterGlobal.Contains(skiaX, skiaY)) return null;
                }

                if (ArvoresEspaciais.TryGetValue(camada, out var arvore))
                {
                    // A Árvore agora é filtrada por toda a área do cilindro e não apenas um ponto
                    var candidatos = arvore.Query(areaCaptura.EnvelopeInternal);

                    IFeature? melhorCandidato = null;
                    double menorDistancia = double.MaxValue;

                    foreach (var candidato in candidatos)
                    {
                        // Verifica se a feição entra dentro do campo de força
                        if (candidato.Geometry.Intersects(areaCaptura))
                        {
                            // Se houver várias coisas empilhadas (ex: um poste em cima de um lote),
                            // a matemática escolhe estritamente aquilo que está mais perto do centro do rato
                            double distancia = candidato.Geometry.Distance(pontoClique);
                            if (distancia < menorDistancia)
                            {
                                menorDistancia = distancia;
                                melhorCandidato = candidato;
                            }
                        }
                    }

                    if (melhorCandidato != null)
                    {
                        camadaAtingida = camada;
                        return melhorCandidato;
                    }
                }
            }
            return null;
        }
        public void DestacarFeicao(IFeature? feicao)
        {
            CaminhoDestaquePoligono?.Dispose(); CaminhoDestaquePoligono = null;
            CaminhoDestaqueLinha?.Dispose(); CaminhoDestaqueLinha = null;

            if (feicao != null && feicao.Geometry != null)
            {
                var polyPath = new SKPath { FillType = SKPathFillType.EvenOdd };
                var linePath = new SKPath();

                for (int i = 0; i < feicao.Geometry.NumGeometries; i++)
                {
                    var subGeom = feicao.Geometry.GetGeometryN(i);

                    if (subGeom is NetTopologySuite.Geometries.Polygon poly)
                    {
                        var extPath = new SKPath();
                        var extCoords = poly.ExteriorRing.Coordinates;
                        for (int j = 0; j < extCoords.Length; j++)
                        {
                            float x = (float)(extCoords[j].X - OffsetMundoX);
                            float y = -(float)(extCoords[j].Y - OffsetMundoY);
                            if (j == 0) extPath.MoveTo(x, y); else extPath.LineTo(x, y);
                        }
                        extPath.Close();
                        polyPath.AddPath(extPath);

                        foreach (var hole in poly.InteriorRings)
                        {
                            var holePath = new SKPath();
                            var holeCoords = hole.Coordinates;
                            for (int j = 0; j < holeCoords.Length; j++)
                            {
                                float x = (float)(holeCoords[j].X - OffsetMundoX);
                                float y = -(float)(holeCoords[j].Y - OffsetMundoY);
                                if (j == 0) holePath.MoveTo(x, y); else holePath.LineTo(x, y);
                            }
                            holePath.Close();
                            polyPath.AddPath(holePath);
                        }
                    }
                    else if (subGeom is NetTopologySuite.Geometries.LineString line)
                    {
                        var lp = new SKPath();
                        var coords = line.Coordinates;
                        for (int j = 0; j < coords.Length; j++)
                        {
                            float x = (float)(coords[j].X - OffsetMundoX);
                            float y = -(float)(coords[j].Y - OffsetMundoY);
                            if (j == 0) lp.MoveTo(x, y); else lp.LineTo(x, y);
                        }
                        linePath.AddPath(lp);
                    }
                    else if (subGeom is NetTopologySuite.Geometries.Point pt)
                    {
                        float x = (float)(pt.Coordinate.X - OffsetMundoX);
                        float y = -(float)(pt.Coordinate.Y - OffsetMundoY);
                        linePath.AddCircle(x, y, 4.0f); // Destaque de ponto
                    }
                }

                if (!polyPath.IsEmpty) CaminhoDestaquePoligono = polyPath;
                if (!linePath.IsEmpty) CaminhoDestaqueLinha = linePath;
            }
            RequestRedraw();
        }
    }
}