using System.Collections.Specialized;
using GeoNex.Services;
using SkiaSharp;
using PrintScale;

GdalConfiguration.ConfigureGdal();
GdalConfiguration.ConfigureOgr();
int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
void Near(double a, double b, double tolerance, string message) => Check(Math.Abs(a-b) < tolerance, $"{message}: {a} != {b}");

var camera = new MapRenderingService { ProjetoSRS = "EPSG:31983", CameraZoom = 2, ViewportEscalaAutoFit = .5f, CameraPanX = 20, OffsetMundoX=500000, OffsetMundoY=7400000 };
var metric = PrintMapContext.Capture(camera);
Near(metric.OriginX ?? double.NaN, 500000, 1e-9, "absolute grid origin X");
Near(metric.OriginY ?? double.NaN, 7400000, 1e-9, "absolute grid origin Y");
Near(metric.MetersPerUnit, 1, 1e-9, "UTM meters");
Near(metric.CenterX, -20, 1e-5, "capture includes camera pan");
Near(camera.CameraZoom, 2, 1e-9, "capture doesn't mutate camera");
camera.ProjetoSRS = "EPSG:4326";
Check(PrintMapContext.Capture(camera).MetersPerUnit == 0, "degrees must not have a metric scale");
camera.ProjetoSRS = "EPSG:2227";
var feet = PrintMapContext.Capture(camera);
Near(feet.MetersPerUnit, 1200.0/3937, 1e-10, "US survey feet");

foreach (var units in new[] { 1.0, feet.MetersPerUnit })
foreach (var dpi in new[] { 1f, 150f/96, 300f/96 })
foreach (var rotation in new[] { 0f, 30f, 90f })
{
    const double width = 200 * 96 / 25.4, height = 113.7 * 96 / 25.4;
    var viewport = MapViewportMetrics.Create((int)Math.Round(width), (int)Math.Round(height), dpi);
    var zoom = (float)PrintMapContext.ScaleToZoom(5000, units);
    Check(MapCoordinateFrame.TryCreate(viewport, new SKPoint(10,20), zoom, rotation, out var frame, width, height), "print frame");
    var a = frame.LocalToCssMatrix.MapPoint(new SKPoint(10,20));
    var b = frame.LocalToCssMatrix.MapPoint(new SKPoint(10+(float)(50/units),20));
    double dx=(b.X-a.X)*width/viewport.CssWidth, dy=(b.Y-a.Y)*height/viewport.CssHeight;
    Near(Math.Sqrt(dx*dx+dy*dy)*25.4/96, 10, .001, "50 m at 1:5000 = 10 mm, invariant to rotation/DPI/fractional frame");
    if(rotation==0) Near(frame.LocalViewportBounds.Width*units, 1000, .002, "200 mm frame at 1:5000 covers 1000 m");
}

var query = new NameValueCollection { ["ct"]=metric.Token, ["cs"]="5000", ["cx"]="100", ["cy"]="200", ["cw"]="755.9055", ["ch"]="429.7323" };
Check(PrintMapContext.TryRead(query,metric,out var center,out var zoomValue), "valid request");
Near(zoomValue, 96/.0254/5000, 1e-7, "request zoom");
Check(PrintMapContext.TryReadLayoutSize(query,out _,out _), "fractional dimensions");
foreach(var invalid in new[]{"NaN","Infinity","-1","0","1e15","abc"})
{ query["cs"]=invalid; Check(!PrintMapContext.TryRead(query,metric,out _,out _), "reject invalid scale"); }
query["cs"]="5000";query["ct"]="stale";
Check(!PrintMapContext.TryRead(query,metric,out _,out _), "reject stale CRS/origin");
query["cw"]="NaN";
Check(!PrintMapContext.TryReadLayoutSize(query,out _,out _), "reject invalid dimensions");
Console.WriteLine($"{checks} print scale checks passed.");

// Minimal source for the contract test; production uses the real MapRenderingService.
namespace GeoNex.Services
{
    public sealed class MapRenderingService
    {
        public string ProjetoSRS { get; set; } = "EPSG:4326";
        public float CameraZoom { get; set; } = 1;
        public float CameraPanX { get; set; }
        public float CameraPanY { get; set; }
        public float ViewportEscalaAutoFit { get; set; } = 1;
        public float ViewportMidX { get; set; }
        public float ViewportMidY { get; set; }
        public double OffsetMundoX { get; set; }
        public double OffsetMundoY { get; set; }
    }
}
