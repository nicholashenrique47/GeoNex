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
        public SKPaint PincelDestaqueFill { get; private set; } = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Yellow.WithAlpha(100), IsAntialias = true };
        public SKPaint PincelDestaqueBorda { get; private set; } = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Yellow, StrokeWidth = 2, IsAntialias = true };
        public Dictionary<string, SKPath> VetoresPorCamada { get; private set; } = new();
        public List<string> OrdemCamadas { get; set; } = new();
        public string NomeRasterAtivo { get; set; } = "";

        // O MOTOR ANALÍTICO ESPACIAL (RAM C#) - Agora utilizando IFeature rigorosamente
        public Dictionary<string, STRtree<IFeature>> ArvoresEspaciais { get; private set; } = new();

        public Dataset DatasetRaster { get; set; }
        public bool TemRaster { get; set; } = false;
        public SkiaSharp.SKRect LimitesRasterGlobal { get; set; }

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

            // O EvenOdd garante que os pátios interiores fiquem ocos e não pintados
            var superPath = new SKPath { FillType = SKPathFillType.EvenOdd };

            foreach (IFeature feicao in feicoes)
            {
                if (feicao.Geometry == null) continue;

                // Ancoramos o Mundo Inteiro na primeira coordenada para zerar os eixos
                if (!OffsetMundoDefinido)
                {
                    OffsetMundoX = feicao.Geometry.Coordinates[0].X;
                    OffsetMundoY = feicao.Geometry.Coordinates[0].Y;
                    OffsetMundoDefinido = true;
                }

                for (int i = 0; i < feicao.Geometry.NumGeometries; i++)
                {
                    if (feicao.Geometry.GetGeometryN(i) is NetTopologySuite.Geometries.Polygon poly)
                    {
                        // 1. Desenha a Parede Externa do Polígono
                        var extPath = new SKPath();
                        var extCoords = poly.ExteriorRing.Coordinates;
                        for (int j = 0; j < extCoords.Length; j++)
                        {
                            float x = (float)(extCoords[j].X - OffsetMundoX);
                            float y = -(float)(extCoords[j].Y - OffsetMundoY);
                            if (j == 0) extPath.MoveTo(x, y); else extPath.LineTo(x, y);
                        }
                        extPath.Close();
                        superPath.AddPath(extPath);

                        // 2. Desenha os Furos Interiores (Isolados da Parede Externa)
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
                            superPath.AddPath(holePath);
                        }
                    }
                }
            }
            VetoresPorCamada[nomeCamada] = superPath;
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
        public IFeature? DispararRaycast(double lat, double lng, out string camadaAtingida)
        {
            camadaAtingida = string.Empty;

            var pontoClique = new NetTopologySuite.Geometries.Point(lng, lat);

            for (int i = OrdemCamadas.Count - 1; i >= 0; i--)
            {
                string camada = OrdemCamadas[i];
                if (ArvoresEspaciais.TryGetValue(camada, out var arvore))
                {
                    var candidatos = arvore.Query(pontoClique.EnvelopeInternal);

                    foreach (var candidato in candidatos)
                    {
                        if (candidato.Geometry.Intersects(pontoClique))
                        {
                            camadaAtingida = camada;
                            return candidato;
                        }
                    }
                }
            }
            return null;
        }
        public void DestacarFeicao(IFeature? feicao)
        {
            CaminhoDestaque?.Dispose();
            CaminhoDestaque = null;

            if (feicao != null && feicao.Geometry != null)
            {
                CaminhoDestaque = new SKPath { FillType = SKPathFillType.EvenOdd };
                for (int i = 0; i < feicao.Geometry.NumGeometries; i++)
                {
                    if (feicao.Geometry.GetGeometryN(i) is NetTopologySuite.Geometries.Polygon poly)
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
                        CaminhoDestaque.AddPath(extPath);

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
                            CaminhoDestaque.AddPath(holePath);
                        }
                    }
                }
            }
            RequestRedraw();
        }
    }
}