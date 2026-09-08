using System;
using System.IO;
using GeoNex.Services;

namespace PerfNTS
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Lendo LOTES...");
            string path = @"C:\Users\Windows 10\Desktop\SHP S\LOTEAMENTOS_LOTES.shp";
            var result = FastShapeReader.ReadAllFeatures(path, "LOTES", 0.0, 0.0, null);
            var features = result.Features;
            var data = result.ShpData;
            Console.WriteLine($"Total: {features.Count}");
            if (features.Count > 0)
            {
                var f = features[0];
                Console.WriteLine($"Kind: {f.Kind}, Offset: {f.DataOffset}, Env: {f.EnvelopeWorld}");
                var p = f.GetPath(data, 0, 0);
                Console.WriteLine($"Path: {p != null}, PointCount: {p?.PointCount}");
            }
        }
    }
}
