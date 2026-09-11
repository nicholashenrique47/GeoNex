using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GeoNex.Services;

/// <summary>Local-only PDF/image reference and optional OCR. Never executes document scripts.</summary>
public static class LayoutReferenceImporter
{
    public sealed record TextBox(string Text, double X, double Y, double Width, double Height);
    public sealed record Result(string DataUrl, uint Width, uint Height, uint Pages, List<TextBox> Texts, string Warning);
    public const int MaxBytes = 20_000_000;

    public static async Task<Result> ReadAsync(byte[] bytes, bool pdf, int pageNumber, bool recognize)
    {
        if (bytes.Length is 0 or > MaxBytes) throw new InvalidDataException("Limite de arquivo: 20 MB.");
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
        input.Seek(0);
        using var rendered = new InMemoryRandomAccessStream();
        uint pages = 1;
        IRandomAccessStream source = input;
        if (pdf)
        {
            var document = await PdfDocument.LoadFromStreamAsync(input);
            pages = document.PageCount;
            if (pageNumber < 1 || pageNumber > pages) throw new InvalidDataException($"PDF possui {pages} página(s). Escolha uma página válida.");
            using var page = document.GetPage((uint)pageNumber - 1);
            var factor = Math.Min(2, 2400 / Math.Max(page.Size.Width, page.Size.Height));
            if (!double.IsFinite(factor) || factor <= 0) throw new InvalidDataException("Página PDF inválida.");
            await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, page.Size.Width * factor), DestinationHeight = (uint)Math.Max(1, page.Size.Height * factor) });
            rendered.Seek(0); source = rendered;
        }
        var decoder = await BitmapDecoder.CreateAsync(source);
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || (ulong)decoder.PixelWidth * decoder.PixelHeight > 100_000_000)
            throw new InvalidDataException("Imagem inválida ou maior que 100 megapixels.");
        var maxDimension = recognize ? Math.Min(2400u, OcrEngine.MaxImageDimension) : 2400u;
        var ratio = Math.Min(1, maxDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform { ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * ratio), ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * ratio) };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var texts = new List<TextBox>();
        var warning = "Referência visual: não recupera camadas, escala ou vetores.";
        if (recognize)
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null) warning = "OCR indisponível: instale um idioma de reconhecimento no Windows. Referência preservada.";
            else
            {
                var result = await engine.RecognizeAsync(bitmap);
                // Bounding boxes stay in source-image coordinates; typography/rotation require review.
                var angle = (((result.TextAngle ?? 0) + 180) % 360 + 360) % 360 - 180;
                {
                    foreach (var line in result.Lines.Take(150))
                    {
                        if (line.Words.Count == 0) continue;
                        var left = line.Words.Min(w => w.BoundingRect.Left); var top = line.Words.Min(w => w.BoundingRect.Top);
                        var right = line.Words.Max(w => w.BoundingRect.Right); var bottom = line.Words.Max(w => w.BoundingRect.Bottom);
                        texts.Add(new(line.Text, left, top, right - left, bottom - top));
                    }
                    warning = texts.Count == 0 ? "Nenhum texto reconhecido; referência preservada." : $"{texts.Count} linhas OCR recuperadas para revisão. Referência original oculta na lista de itens; sem reconstrução de vetores ou fontes originais.";
                    if (result.Lines.Count > 150) warning += " Limite de 150 linhas atingido.";
                    if (Math.Abs(angle) > 2) warning += $" OCR detectou inclinação de {angle:F1}°; revise a orientação dos textos.";
                }
            }
        }
        using var png = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, png);
        encoder.SetSoftwareBitmap(bitmap); await encoder.FlushAsync();
        if (png.Size > 12_000_000) throw new InvalidDataException("Referência codificada muito grande. Reduza a imagem.");
        using var reader = new DataReader(png.GetInputStreamAt(0));
        await reader.LoadAsync((uint)png.Size); var data = new byte[(int)png.Size]; reader.ReadBytes(data);
        return new("data:image/png;base64," + Convert.ToBase64String(data), (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, pages, texts, warning);
    }
}
