using SkiaSharp;

namespace GeoNex.Services
{
    public class MapRenderingService
    {
        // Variáveis de Câmara
        public float CameraZoom { get; set; } = 1.0f;
        public float CameraPanX { get; set; } = 0f;
        public float CameraPanY { get; set; } = 0f;

        // Memória da Placa Gráfica
        public List<SKPath> PathsCompilados { get; private set; } = new();
        public SKPaint PincelFill { get; private set; }
        public SKPaint PincelBorda { get; private set; }

        // Evento que avisa a GPU para pintar um novo frame
        public event Action? OnMapInvalidated;

        public MapRenderingService()
        {
            // Criados apenas uma vez no arranque do sistema
            PincelBorda = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Cyan.WithAlpha(200), StrokeWidth = 0, IsAntialias = true };
            PincelFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Cyan.WithAlpha(25), IsAntialias = true };
        }

        // Método chamado pelo rato no Blazor para acionar a GPU
        public void RequestRedraw() => OnMapInvalidated?.Invoke();

        public void PreCompilarPoligonos(List<float[]> poligonos)
        {
            foreach (var p in PathsCompilados) p.Dispose();
            PathsCompilados.Clear();

            // Descobre os limites do mapa
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;

            foreach (var p in poligonos)
            {
                for (int i = 0; i < p.Length; i += 2)
                {
                    if (p[i] < minX) minX = p[i];
                    if (p[i] > maxX) maxX = p[i];
                    if (p[i + 1] < minY) minY = p[i + 1];
                    if (p[i + 1] > maxY) maxY = p[i + 1];
                }
            }

            float larguraGraus = maxX - minX;
            float alturaGraus = maxY - minY;
            if (larguraGraus <= 0) larguraGraus = 1;
            if (alturaGraus <= 0) alturaGraus = 1;

            float escala = Math.Min(1920f / larguraGraus, 1080f / alturaGraus) * 0.9f;
            float offX = -(minX + larguraGraus / 2f) * escala;
            float offY = -(minY + alturaGraus / 2f) * escala;

            // Compila para a RAM
            foreach (var poli in poligonos)
            {
                var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                for (int i = 0; i < poli.Length; i += 2)
                {
                    float x = poli[i] * escala + offX;
                    float y = -poli[i + 1] * escala - offY; // Inverte o Y nativamente
                    if (i == 0) path.MoveTo(x, y);
                    else path.LineTo(x, y);
                }
                path.Close();
                PathsCompilados.Add(path);
            }

            RequestRedraw(); // Requisita o primeiro frame
        }
        // Variáveis para suportar o Raster (Ortofoto)
        public SkiaSharp.SKBitmap RasterAtivo { get; set; }
        public SkiaSharp.SKRect LimitesRaster { get; set; }
        public bool TemRaster => RasterAtivo != null;

        // Método para limpar a memória RAM quando trocar de imagem
        public void CarregarRaster(byte[] imageBytes, float minX, float minY, float maxX, float maxY)
        {
            if (RasterAtivo != null)
            {
                RasterAtivo.Dispose(); // Liberta a RAM da imagem anterior
            }

            RasterAtivo = SkiaSharp.SKBitmap.Decode(imageBytes);
            LimitesRaster = new SkiaSharp.SKRect(minX, minY, maxX, maxY);
        }
    }
}