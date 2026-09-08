using GeoNex.Services;
using OSGeo.OGR;

internal static class ExportContracts
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), $"geonex-export-contracts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            ReplacesSingleFile(root);
            RejectsConcurrentPublication(root);
            RollsBackShapefileSet(root);
            RecoversInterruptedShapefileSet(root);
            RejectsIncompleteShapefile(root);
            CancellationPreservesDestination(root);
            ExportsOverSourceSafely(root);
            Console.WriteLine("Export contracts: PASS (staging, lock, rollback, crash recovery, sidecars, same-path source)");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void ReplacesSingleFile(string root)
    {
        string destination = Path.Combine(root, "single.geojson");
        File.WriteAllText(destination, "old");

        using (var transaction = ExportPublicationTransaction.Begin(destination, isShapefile: false))
        {
            File.WriteAllText(transaction.StagingPath, "new");
            transaction.Publish();
        }

        Assert(File.ReadAllText(destination) == "new", "single-file destination replaced only after staging");
    }

    private static void RollsBackShapefileSet(string root)
    {
        string destination = Path.Combine(root, "rollback.shp");
        foreach (string suffix in new[] { ".shp", ".shx", ".dbf" })
            File.WriteAllText(Path.Combine(root, "rollback" + suffix), "old" + suffix);

        Directory.CreateDirectory(Path.Combine(root, "rollback.prj"));
        bool failed = false;
        try
        {
            using var transaction = ExportPublicationTransaction.Begin(destination, isShapefile: true);
            foreach (string suffix in new[] { ".shp", ".shx", ".dbf", ".prj" })
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(transaction.StagingPath)!, "rollback" + suffix), "new" + suffix);
            transaction.Publish();
        }
        catch (IOException)
        {
            failed = true;
        }

        Assert(failed, "injected sidecar publication failure observed");
        foreach (string suffix in new[] { ".shp", ".shx", ".dbf" })
            Assert(File.ReadAllText(Path.Combine(root, "rollback" + suffix)) == "old" + suffix,
                $"old {suffix} restored after failure");
        Directory.Delete(Path.Combine(root, "rollback.prj"));
    }

    private static void RejectsConcurrentPublication(string root)
    {
        string destination = Path.Combine(root, "locked.geojson");
        bool rejected = false;
        using var first = ExportPublicationTransaction.Begin(destination, isShapefile: false);
        try
        {
            using var second = ExportPublicationTransaction.Begin(destination, isShapefile: false);
        }
        catch (IOException)
        {
            rejected = true;
        }

        Assert(rejected, "concurrent publication to the same destination rejected");
    }

    private static void RejectsIncompleteShapefile(string root)
    {
        string destination = Path.Combine(root, "incomplete.shp");
        File.WriteAllText(destination, "old");
        bool failed = false;

        try
        {
            using var transaction = ExportPublicationTransaction.Begin(destination, isShapefile: true);
            File.WriteAllText(transaction.StagingPath, "new");
            transaction.Publish();
        }
        catch (InvalidDataException)
        {
            failed = true;
        }

        Assert(failed, "incomplete Shapefile rejected");
        Assert(File.ReadAllText(destination) == "old", "validation failure preserves destination");
    }

    private static void RecoversInterruptedShapefileSet(string root)
    {
        string destination = Path.Combine(root, "interrupted.shp");
        string transactionRoot = Path.Combine(
            root,
            $".interrupted.shp.geonex-export-{Guid.NewGuid():N}.tmp");
        string staging = Path.Combine(transactionRoot, "staging");
        string backup = Path.Combine(transactionRoot, "backup");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(backup);

        string[] names = ["interrupted.shp", "interrupted.shx", "interrupted.dbf"];
        File.WriteAllLines(Path.Combine(transactionRoot, "manifest.txt"), names);
        File.WriteAllText(Path.Combine(transactionRoot, "backup-complete"), "1");
        foreach (string name in names)
            File.WriteAllText(Path.Combine(backup, name), "old-" + name);

        // Simula queda depois de publicar apenas o primeiro componente.
        File.WriteAllText(destination, "new-partial");
        File.WriteAllText(Path.Combine(staging, "interrupted.shx"), "new-shx");
        File.WriteAllText(Path.Combine(staging, "interrupted.dbf"), "new-dbf");

        using (ExportPublicationTransaction.Begin(destination, isShapefile: true))
        {
            // Begin recupera journals interrompidos antes de abrir uma nova transação.
        }

        foreach (string name in names)
            Assert(File.ReadAllText(Path.Combine(root, name)) == "old-" + name,
                $"interrupted publication restored {name}");
        Assert(!Directory.Exists(transactionRoot), "recovered journal removed");
    }

    private static void ExportsOverSourceSafely(string root)
    {
        GdalRuntimeBootstrap.Configure();

        string source = Path.Combine(root, "same-path.geojson");
        File.WriteAllText(source,
            """
            {"type":"FeatureCollection","name":"same-path","crs":{"type":"name","properties":{"name":"urn:ogc:def:crs:OGC:1.3:CRS84"}},"features":[{"type":"Feature","properties":{"id":7},"geometry":{"type":"Point","coordinates":[-48.5,-25.8]}}]}
            """);

        var service = new ExportService();
        string shapefile = Path.Combine(root, "actual-output.shp");
        Assert(service.ExportarVetor("actual-output", source, shapefile, "ESRI Shapefile", 4326),
            "real GDAL Shapefile export succeeds through staging");
        foreach (string suffix in new[] { ".shp", ".shx", ".dbf" })
            Assert(File.Exists(Path.ChangeExtension(shapefile, suffix)), $"real Shapefile contains {suffix}");
        Assert(service.ExportarVetor("actual-output", shapefile, shapefile, "ESRI Shapefile", 4326),
            "same-path Shapefile export succeeds through staging");
        using (var reopenedShape = Ogr.Open(shapefile, 0)
            ?? throw new InvalidOperationException("same-path Shapefile did not reopen"))
        using (var shapeLayer = reopenedShape.GetLayerByIndex(0)
            ?? throw new InvalidOperationException("same-path Shapefile layer missing"))
        {
            Assert(shapeLayer.GetFeatureCount(1) == 1, "same-path Shapefile preserves feature count");
        }

        Assert(service.ExportarVetor("same-path", source, source, "GeoJSON", 4326),
            "same-path export succeeds through staging");

        using var reopened = Ogr.Open(source, 0) ?? throw new InvalidOperationException("same-path output did not reopen");
        using var layer = reopened.GetLayerByIndex(0) ?? throw new InvalidOperationException("same-path layer missing");
        Assert(layer.GetFeatureCount(1) == 1, "same-path export preserves feature count");
    }

    private static void CancellationPreservesDestination(string root)
    {
        string destination = Path.Combine(root, "cancelled.geojson");
        File.WriteAllText(destination, "old");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancelled = false;

        try
        {
            using var transaction = ExportPublicationTransaction.Begin(destination, isShapefile: false);
            File.WriteAllText(transaction.StagingPath, "new");
            transaction.Publish(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert(cancelled, "publication observes cancellation");
        Assert(File.ReadAllText(destination) == "old", "cancellation preserves destination");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Export contract failed: {message}");
    }
}
