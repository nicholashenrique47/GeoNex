using SkiaSharp;
using OSGeo.GDAL;
using System;
using System.Collections.Generic;

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