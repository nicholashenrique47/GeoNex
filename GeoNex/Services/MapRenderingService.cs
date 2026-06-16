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
        public List<string> OrdemCamadas { get; set; } = new();
        public List<SkiaSharp.SKPoint> PontosMedicao { get; set; } = new();
        public string NomeRasterAtivo { get; set; } = "";

        // O MOTOR ANALÍTICO ESPACIAL (RAM C#) - Agora utilizando IFeature rigorosamente
        public Dictionary<string, STRtree<IFeature>> ArvoresEspaciais { get; private set; } = new();

        public Dataset DatasetRaster { get; set; }
        public bool TemRaster { get; set; } = false;
        public SkiaSharp.SKRect LimitesRasterGlobal { get; set; }
        // === FERRAMENTAS DE MEDIÇÃO ===
        public List<SkiaSharp.SKPoint> PontosMedicao { get; set; } = new();
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
            if (VetoresPorCamada.ContainsKey(nomeCamada)) VetoresPorCamada[nomeCamada].Dispose();
            if (LinhasPorCamada.ContainsKey(nomeCamada)) LinhasPorCamada[nomeCamada].Dispose();
            if (PontosPorCamada.ContainsKey(nomeCamada)) PontosPorCamada[nomeCamada].Dispose();

            var polyPath = new SKPath { FillType = SKPathFillType.EvenOdd };
            var linePath = new SKPath();
            var pointPath = new SKPath();

            foreach (IFeature feicao in feicoes)
            {
                if (feicao.Geometry == null) continue;

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