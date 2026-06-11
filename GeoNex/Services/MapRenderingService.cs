using SkiaSharp;
using OSGeo.GDAL;
using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Features;

namespace GeoNex.Services
{
    public class MapRenderingService
    {
        public float CameraZoom { get; set; } = 1.0f;
        public float CameraPanX { get; set; } = 0f;
        public float CameraPanY { get; set; } = 0f;

        public SKPaint PincelFill { get; private set; }
        public SKPaint PincelBorda { get; private set; }

        // === OTIMIZAÇÃO: UM ÚNICO OBJETO POR CAMADA ===
        public Dictionary<string, SKPath> VetoresPorCamada { get; private set; } = new();
        public List<string> OrdemCamadas { get; set; } = new();
        public string NomeRasterAtivo { get; set; } = "";
        // === O MOTOR ANALÍTICO (RAM C#) ===
        // STRtree: O índice espacial que pesquisa 100.000 polígonos em 1 milissegundo
        public Dictionary<string, STRtree<Feature>> ArvoresEspaciais { get; private set; } = new();

        public Dataset DatasetRaster { get; set; }
        public bool TemRaster { get; set; } = false;
        public SkiaSharp.SKRect LimitesRasterGlobal { get; set; }

        public event Action? OnMapInvalidated;

        public MapRenderingService()
        {
            PincelBorda = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Cyan.WithAlpha(200), StrokeWidth = 0, IsAntialias = true };
            PincelFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Cyan.WithAlpha(25), IsAntialias = true };
        }

        // ======================================================
        // MOTOR DE RAYCASTING (POINT-IN-POLYGON)
        // ======================================================

        // Este método será chamado quando carregar o ficheiro .shp para a memória
        public void ConstruirIndiceEspacial(string nomeCamada, FeatureCollection feicoes)
        {
            var arvore = new STRtree<Feature>();
            foreach (var f in feicoes)
            {
                if (f.Geometry != null)
                {
                    // Insere o polígono na "caixa" correta da árvore
                    arvore.Insert(f.Geometry.EnvelopeInternal, f);
                }
            }
            // Trava a árvore para leitura ultra-rápida
            arvore.Build();
            ArvoresEspaciais[nomeCamada] = arvore;
        }

        public Feature? DispararRaycast(double lat, double lng)
        {
            // O Leaflet envia sempre EPSG:4326. No plano cartesiano, Longitude é o X e Latitude é o Y.
            var pontoClique = new Point(lng, lat);

            // Procura de cima para baixo (a camada que está visualmente no topo é a que recebe o clique)
            for (int i = OrdemCamadas.Count - 1; i >= 0; i--)
            {
                string camada = OrdemCamadas[i];
                if (ArvoresEspaciais.TryGetValue(camada, out var arvore))
                {
                    // 1. FILTRO DE CAIXA (Brutalmente rápido: reduz de 50.000 para ~3 candidatos)
                    var candidatos = arvore.Query(pontoClique.EnvelopeInternal);

                    // 2. MATEMÁTICA EXATA (Raycasting real nos 3 candidatos)
                    foreach (var candidato in candidatos)
                    {
                        if (candidato.Geometry.Intersects(pontoClique))
                        {
                            return candidato; // Alvo Abatido!
                        }
                    }
                }
            }
            return null; // O clique atingiu uma área vazia
        }
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

            // O Mega-Path: Funde todos os lotes e ruas numa única entidade matemática
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
    }
}