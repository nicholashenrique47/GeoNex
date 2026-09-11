using GeoNex.Services;
using System.Text.Json;

if(args.Length!=3)throw new ArgumentException("Usage: LayoutImport image.png document.pdf output-directory");
Directory.CreateDirectory(args[2]);
int checks=0;
void Check(bool result,string message){if(!result)throw new Exception(message);checks++;}
var image=await LayoutReferenceImporter.ReadAsync(await File.ReadAllBytesAsync(args[0]),false,1,true);
Console.WriteLine(image.Warning);
Console.WriteLine("OCR languages: "+string.Join(",",Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Select(l=>l.LanguageTag)));
Check(image.Width<=2400&&image.Height<=2400,"bounded image decoding");
Check(image.DataUrl.StartsWith("data:image/png;base64,"),"normalized image");
Check(image.Texts.Count>0,"native OCR returns editable lines");
Check(image.Texts.All(t=>t.X>=0&&t.Y>=0&&t.Width>0&&t.Height>0),"OCR bounds valid");
await File.WriteAllTextAsync(Path.Combine(args[2],"import-result.json"),JsonSerializer.Serialize(image,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
var pdf=await LayoutReferenceImporter.ReadAsync(await File.ReadAllBytesAsync(args[1]),true,1,true);
Check(pdf.Pages>=1&&pdf.Width<=2400&&pdf.Height<=2400,"native PDF rendering");
Check(pdf.Texts.Count>0,"PDF OCR returns text");
await File.WriteAllBytesAsync(Path.Combine(args[2],"normalized-pdf.png"),Convert.FromBase64String(pdf.DataUrl.Split(',')[1]));
try {await LayoutReferenceImporter.ReadAsync(await File.ReadAllBytesAsync(args[1]),true,10000,false);throw new Exception("invalid page accepted");}catch(InvalidDataException){checks++;}
try {await LayoutReferenceImporter.ReadAsync([],false,1,false);throw new Exception("empty input accepted");}catch(InvalidDataException){checks++;}
Console.WriteLine($"{checks} native import checks passed; image OCR: {image.Texts.Count} lines; PDF OCR: {pdf.Texts.Count} lines. Artifacts: {args[2]}");
