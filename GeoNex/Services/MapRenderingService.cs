using SkiaSharp;
using OSGeo.GDAL;
using System;
using System.Collections.Generic;

namespace GeoNex.Services
{
    public class MapRenderingService
    {
        // Variáveis de Câmara
        public float CameraZoom { get; set; } = 1.0f;
        public float CameraPanX { get; set; } = 0f;
        public float CameraPanY { get; set; } = 0f;

        // Memória da Placa Gráfica (Vetores)
        public List<SKPath> PathsCompilados { get; private set; } = new();
        public SKPaint PincelFill { get; private set; }
        public SKPaint PincelBorda { get; private set; }

        // === ARQUITETURA NATIVA (Ponteiros C++) ===
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

        public void PreCompilarPoligonos(List<float[]> poligonos)
        {
            foreach (var p in PathsCompilados) p.Dispose();
            PathsCompilados.Clear();

            foreach (var poli in poligonos)
            {
                var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                for (int i = 0; i < poli.Length; i += 2)
                {
                    // A MÁGICA: Guardamos os vetores na sua coordenada geográfica real (ex: 700000, 7150000)
                    // Invertemos apenas o eixo Y, porque a placa gráfica lê de cima para baixo
                    float x = poli[i];
                    float y = -poli[i + 1];

                    if (i == 0) path.MoveTo(x, y);
                    else path.LineTo(x, y);
                }
                path.Close();
                PathsCompilados.Add(path);
            }

            RequestRedraw();
        }
    }
}