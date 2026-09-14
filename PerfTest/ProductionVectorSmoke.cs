using System.Collections;
using System.Diagnostics;
using System.Reflection;
using GeoNex.Services;
using OSGeo.OGR;
using OSGeo.OSR;
using SkiaSharp;

internal static class ProductionVectorSmoke
{
    // Load the built application: this exercises the actual reader/resource owner, not a copy or stub.
    public static void Run(string assemblyPath, string source)
    {
        GdalRuntimeBootstrap.Configure();
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var reader = assembly.GetType("GeoNex.Services.FastShapeReader", throwOnError: true)!;
        var clock = Stopwatch.StartNew();
        object tuple = reader.GetMethod("ReadAllFeatures")!.Invoke(null,
            new object?[] { source, "smoke", 0d, 0d, null })!;
        var features = (IList)tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
        using var resource = (IDisposable)tuple.GetType().GetField("Item2")!.GetValue(tuple)!;
        clock.Stop();
        using var ogrSource = Ogr.Open(source, 0) ?? throw new InvalidDataException(source);
        using var ogrLayer = ogrSource.GetLayerByIndex(0);
        long expected = ogrLayer.GetFeatureCount(1);
        if (features.Count != expected) throw new InvalidOperationException($"Counts differ: {features.Count}/{expected}");
        foreach (int i in new[] { 0, features.Count - 1 }.Where(i => i >= 0).Distinct())
        {
            object feature = features[i]!;
            long fid = (long)feature.GetType().GetField("FID")!.GetValue(feature)!;
            if (fid != i) throw new InvalidOperationException("Source FID/order changed");
            using var path = (SKPath?)feature.GetType().GetMethod("GetPath")!.Invoke(feature,
                new object?[] { resource, 0d, 0d, null, false });
            if (path == null || path.PointCount == 0) throw new InvalidOperationException("Missing geometry");
            using var original = ogrLayer.GetFeature(fid);
            var attributes = (IDictionary)resource.GetType().GetMethod("LerAtributos")!.Invoke(resource, new object[] { i })!;
            if (attributes.Count != original.GetFieldCount()) throw new InvalidOperationException("Attribute count changed");
        }
        Console.WriteLine($"Production SHP reader: PASS source={Path.GetFileName(source)} features={features.Count} read_ms={clock.Elapsed.TotalMilliseconds:F2}; GDAL count, first/last FIDs, geometry and attribute counts");

        if (features.Count == 0) return;
        using var sourceCrs = new SpatialReference(File.ReadAllText(Path.ChangeExtension(source, ".prj")));
        using var targetCrs = new SpatialReference("");
        targetCrs.ImportFromEPSG(3857);
        sourceCrs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        targetCrs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        using var transform = new CoordinateTransformation(sourceCrs, targetCrs);
        var expectedMetadata = new List<(object Feature, double[] Bounds, SKPoint Center)>();
        foreach (int index in new[] { 0, features.Count - 1 }.Distinct())
        {
            object feature = features[index]!;
            object envelope = feature.GetType().GetField("EnvelopeWorld")!.GetValue(feature)!;
            double Value(string field) => (double)envelope.GetType().GetField(field)!.GetValue(envelope)!;
            var center = (SKPoint)feature.GetType().GetField("CentroidLocal")!.GetValue(feature)!;
            double[] x = { Value("MinX"), Value("MinX"), Value("MaxX"), Value("MaxX"), center.X };
            double[] y = { Value("MinY"), Value("MaxY"), Value("MinY"), Value("MaxY"), -center.Y };
            transform.TransformPoints(5, x, y, new double[5]);
            expectedMetadata.Add((feature, new[] { x.Take(4).Min(), x.Take(4).Max(), y.Take(4).Min(), y.Take(4).Max() },
                new SKPoint((float)x[4], -(float)y[4])));
        }
        assembly.GetType("GeoNex.Services.ProjetoService", true)!
            .GetMethod("ReprojetarMetadadosEmLotes", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { features, File.ReadAllText(Path.ChangeExtension(source, ".prj")), "EPSG:3857", 0d, 0d });
        foreach (var metadata in expectedMetadata)
        {
            object envelope = metadata.Feature.GetType().GetField("EnvelopeWorld")!.GetValue(metadata.Feature)!;
            string[] fields = { "MinX", "MaxX", "MinY", "MaxY" };
            for (int i = 0; i < fields.Length; i++)
                if ((double)envelope.GetType().GetField(fields[i])!.GetValue(envelope)! != metadata.Bounds[i])
                    throw new InvalidOperationException("Production metadata envelope differs");
            if ((SKPoint)metadata.Feature.GetType().GetField("CentroidLocal")!.GetValue(metadata.Feature)! != metadata.Center)
                throw new InvalidOperationException("Production metadata centroid differs");
        }
        Console.WriteLine("Production metadata: PASS (ProjetoService integration, first/last envelopes and centroids vs serial GDAL)");
        var builder = features[0]!.GetType().GetMethod("BuildBatchPathWithTransform")!;
        using (var original = ogrLayer.GetFeature(0))
        using (var projected = original.GetGeometryRef().Clone())
        {
            projected.Transform(transform);
            var coordinates = new List<(double X, double Y)>();
            Collect(projected);
            void Collect(Geometry geometry)
            {
                if (geometry.GetGeometryCount() > 0)
                    for (int i = 0; i < geometry.GetGeometryCount(); i++) Collect(geometry.GetGeometryRef(i));
                else for (int i = 0; i < geometry.GetPointCount(); i++) coordinates.Add((geometry.GetX(i), geometry.GetY(i)));
            }
            double ox = coordinates[0].X, oy = coordinates[0].Y;
            var single = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(features[0]!.GetType()))!;
            single.Add(features[0]);
            using var local = new SKPath();
            builder.Invoke(null, new object[] { local, resource, single, ox, oy, .01f, 100f, transform, CancellationToken.None, false });
            var points = local.Points;
            if (points.Length != coordinates.Count) throw new InvalidOperationException("Rebased vertex count differs from GDAL");
            double worst = 0, oldWorst = 0;
            for (int i = 0; i < points.Length; i++)
            {
                worst = Math.Max(worst, Math.Max(Math.Abs(points[i].X - (coordinates[i].X - ox)), Math.Abs(points[i].Y - (oy - coordinates[i].Y))));
                oldWorst = Math.Max(oldWorst, Math.Max(Math.Abs((float)coordinates[i].X - coordinates[i].X), Math.Abs((float)coordinates[i].Y - coordinates[i].Y)));
            }
            if (worst * 100 > .01) throw new InvalidOperationException($"Rebased vertex error at 100px/m: {worst * 100}");
            Console.WriteLine($"Production high-zoom precision: PASS vertices={points.Length} max_error_m={worst:G5} previous_world_float_error_m={oldWorst:G5}");
            var cacheOrigin = new SKPoint((float)ox, (float)oy);
            resource.GetType().GetMethod("StorePreciseRenderPath")!.Invoke(resource,
                new object[] { local, local.Bounds, cacheOrigin, 100f, false, false, true });
            object?[] preciseArgs = { local.Bounds, cacheOrigin, 125f, false, null, false };
            bool preciseHit = (bool)resource.GetType().GetMethod("TryGetPreciseRenderPath")!.Invoke(resource, preciseArgs)!;
            using var precisePath = (SKPath?)preciseArgs[4];
            if (!preciseHit || !precisePath!.Points.SequenceEqual(points))
                throw new InvalidOperationException("Production precise cache changed vertices");
            Console.WriteLine("Production precise cache: PASS (reuse retains all vertices)");
        }
        using var exact = Build(1);
        if (exact.IsEmpty) throw new InvalidOperationException("Empty transformed geometry");
        float fit = Math.Min(800 / exact.Bounds.Width, 600 / exact.Bounds.Height) * .8f;
        resource.GetType().GetMethod("StoreRenderPath")!.Invoke(resource,
            new object[] { exact, exact.Bounds, fit, false, false, true });
        foreach (float zoom in new[] { fit, fit * 1.5f, fit * 2 })
        {
            object?[] arguments = { exact.Bounds, zoom, false, null, false };
            bool hit = (bool)resource.GetType().GetMethod("TryGetRenderPath")!.Invoke(resource, arguments)!;
            using var cached = (SKPath?)arguments[3];
            using var fresh = Build(zoom);
            if (!hit) throw new InvalidOperationException("Expected production geometry cache hit (requires spare RAM)");
            if (!Pixels(cached!, zoom).SequenceEqual(Pixels(fresh, zoom)))
                throw new InvalidOperationException("Cached/fresh projected pixels differ");
        }
        Console.WriteLine($"Production GDAL projection/cache: PASS vertices={exact.PointCount}; fresh vs reused pixels at three zooms, same alpha/fill/border");

        SKPath Build(float zoom)
        {
            var path = new SKPath { FillType = SKPathFillType.Winding };
            try
            {
                builder.Invoke(null, new object[] { path, resource, features, 0d, 0d, 1 / zoom,
                    zoom, transform, CancellationToken.None, false });
                return path;
            }
            catch { path.Dispose(); throw; }
        }

        byte[] Pixels(SKPath path, float zoom)
        {
            using var bitmap = new SKBitmap(800, 600, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(400, 300);
            canvas.Scale(zoom);
            canvas.Translate(-exact.Bounds.MidX, -exact.Bounds.MidY);
            using var paint = new SKPaint { IsAntialias = true, Color = SKColors.Blue.WithAlpha(89) };
            canvas.DrawPath(path, paint);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 1 / zoom;
            canvas.DrawPath(path, paint);
            return bitmap.Bytes;
        }
    }
}
