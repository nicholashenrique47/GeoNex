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

        public void PreCompilarPoligonos(string nomeCamada, List<float[]> poligonos)
        {
            if (VetoresPorCamada.ContainsKey(nomeCamada))
            {
                VetoresPorCamada[nomeCamada].Dispose();
            }

            var superPath = new SKPath { FillType = SKPathFillType.Winding };

            foreach (var poli in poligonos)
            {
                var subPath = new SKPath();
                for (int i = 0; i < poli.Length; i += 2)
                {
                    float x = poli[i];
                    float y = -poli[i + 1];
                    if (i == 0) subPath.MoveTo(x, y);
                    else subPath.LineTo(x, y);
                }
                subPath.Close();
                superPath.AddPath(subPath);
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

        public IFeature? DispararRaycast(double lat, double lng)
        {
            // Forçamos o compilador a usar a geometria cartográfica e não o ponto de desenho da UI
            var pontoClique = new NetTopologySuite.Geometries.Point(lng, lat);

            for (int i = OrdemCamadas.Count - 1; i >= 0; i--)
            {
                string camada = OrdemCamadas[i];
                if (ArvoresEspaciais.TryGetValue(camada, out var arvore))
                {
                    // Filtro de Caixa
                    var candidatos = arvore.Query(pontoClique.EnvelopeInternal);

                    // Matemática Exata
                    foreach (var candidato in candidatos)
                    {
                        if (candidato.Geometry.Intersects(pontoClique))
                        {
                            return candidato;
                        }
                    }
                }
            }
            return null;
        }
    }
}