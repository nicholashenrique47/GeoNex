using GeoNex.Services;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using OSGeo.OGR;
using OSGeo.OSR;
using System.IO.MemoryMappedFiles;

internal static class VectorEditPersistenceContracts
{
    public static void RunOnShapefileCopy(string sourcePath)
    {
        GdalRuntimeBootstrap.Configure();
        string source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("Source Shapefile not found.", source);

        string directory = Path.Combine(Path.GetTempPath(), $"geonex-save-copy-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "roundtrip.shp");
        string[] extensions = [".shp", ".shx", ".dbf", ".prj", ".cpg"];
        var originalState = extensions
            .Select(extension => Path.ChangeExtension(source, extension))
            .Where(File.Exists)
            .ToDictionary(path => path, path => (Length: new FileInfo(path).Length, LastWriteTimeUtc: File.GetLastWriteTimeUtc(path)));

        try
        {
            foreach (string extension in extensions)
            {
                string sourceSidecar = Path.ChangeExtension(source, extension);
                if (!File.Exists(sourceSidecar)) continue;
                string targetSidecar = Path.ChangeExtension(target, extension);
                File.Copy(sourceSidecar, targetSidecar, overwrite: true);
                FileAttributes attributes = File.GetAttributes(targetSidecar);
                File.SetAttributes(targetSidecar, attributes & ~FileAttributes.ReadOnly);
            }

            long originalCount;
            string geometryWkt;
            using (DataSource sourceData = Ogr.Open(target, 0)
                ?? throw new IOException("Could not open the temporary Shapefile copy."))
            using (Layer sourceLayer = sourceData.GetLayerByIndex(0)
                ?? throw new IOException("The temporary Shapefile copy has no vector layer."))
            {
                originalCount = sourceLayer.GetFeatureCount(1);
                using OSGeo.OGR.Feature sample = sourceLayer.GetNextFeature()
                    ?? throw new InvalidDataException("The temporary Shapefile has no feature to use in the write test.");
                using Geometry sampleGeometry = sample.GetGeometryRef()
                    ?? throw new InvalidDataException("The sample feature has no geometry.");
                sampleGeometry.ExportToWkt(out geometryWkt);
            }

            NetTopologySuite.Features.Feature feature = CreateFeature("ROUNDTRIP", geometryWkt);
            int saved;
            // Keep the production-style mapped SHP open during update, as it is while the layer renders.
            using (var sharedStream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sharedMap = MemoryMappedFile.CreateFromFile(
                sharedStream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false))
            {
                saved = VectorEditPersistenceService.SaveToOgr(target, [feature]);
            }
            Require(saved == 1, $"Expected one new feature on the copied dataset, got {saved}.");
            Require(VectorEditPersistenceService.IsPersisted(feature), "The copied-dataset feature was not marked persisted.");

            using (DataSource verification = Ogr.Open(target, 0)
                ?? throw new IOException("Could not reopen the temporary Shapefile after writing."))
            using (Layer verificationLayer = verification.GetLayerByIndex(0)
                ?? throw new IOException("Could not reopen the temporary Shapefile layer after writing."))
            {
                long actualCount = verificationLayer.GetFeatureCount(1);
                Require(actualCount == originalCount + 1,
                    $"Expected {originalCount + 1} features in the copied dataset after save, got {actualCount}.");
            }

            Require(originalState.All(entry =>
            {
                var current = new FileInfo(entry.Key);
                return current.Exists && current.Length == entry.Value.Length && File.GetLastWriteTimeUtc(entry.Key) == entry.Value.LastWriteTimeUtc;
            }), "The source Shapefile changed during the copy-only roundtrip test.");
            Console.WriteLine($"PASS vector-edit persistence on copy: source={Path.GetFileName(source)} original={originalCount:N0} saved={saved} source-unchanged=true.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static void Run()
    {
        GdalRuntimeBootstrap.Configure();
        string directory = Path.Combine(Path.GetTempPath(), $"geonex-save-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "drawn.shp");

        try
        {
            CreateTargetShapefile(path);
            NetTopologySuite.Features.Feature[] features =
            [
                CreateFeature("A", "POLYGON ((0 0, 3 0, 3 2, 0 2, 0 0))"),
                CreateFeature("B", "POLYGON ((5 5, 7 5, 7 8, 5 8, 5 5))")
            ];

            // Reproduce the live renderer's read map. The SHP must be mapped with write sharing,
            // otherwise Windows prevents GDAL from opening the same file for update.
            if (OperatingSystem.IsWindows())
            {
                using var legacyMap = MemoryMappedFile.CreateFromFile(
                    path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                using DataSource? updateProbe = Ogr.Open(path, 1);
                Require(updateProbe is null, "The legacy non-shared map no longer reproduces the Windows write lock.");
            }

            using (var sharedStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sharedMap = MemoryMappedFile.CreateFromFile(
                sharedStream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false))
            {
                int saved = VectorEditPersistenceService.SaveToOgr(path, features);
                Require(saved == 2, $"Expected 2 new features, got {saved}.");
            }
            Require(features.All(VectorEditPersistenceService.IsPersisted), "Persisted features were not marked after disk sync.");
            ValidateSavedAttributes(path);

            int repeatedSave = VectorEditPersistenceService.SaveToOgr(path, features);
            Require(repeatedSave == 0, "A repeated save duplicated already persisted features.");
            Require(CountFeatures(path) == 2, "The Shapefile record count changed after a repeated save.");

            VerifyProjectGeometryReprojection(directory);

            NetTopologySuite.Features.Feature unsaved = CreateFeature("C", "POLYGON ((10 10, 12 10, 12 12, 10 12, 10 10))");
            bool failed = false;
            try
            {
                VectorEditPersistenceService.SaveToOgr(Path.Combine(directory, "missing", "missing.shp"), [unsaved]);
            }
            catch (IOException)
            {
                failed = true;
            }
            Require(failed, "An unavailable destination did not report a write failure.");
            Require(!VectorEditPersistenceService.IsPersisted(unsaved), "A failed write was marked as persisted.");

            Console.WriteLine("PASS vector-edit persistence: write, duplicate guard, and failure state.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyProjectGeometryReprojection(string directory)
    {
        string path = Path.Combine(directory, "project-crs.shp");
        Driver driver = Ogr.GetDriverByName("ESRI Shapefile")
            ?? throw new InvalidOperationException("ESRI Shapefile driver is unavailable.");
        using (DataSource dataSource = driver.CreateDataSource(path, null)
            ?? throw new IOException("Could not create the CRS transformation test layer."))
        using (OSGeo.OSR.SpatialReference targetSrs = SrsFactory.FromEPSG(31982))
        using (Layer layer = dataSource.CreateLayer("project_crs", targetSrs, wkbGeometryType.wkbPoint, null)
            ?? throw new IOException("Could not create the CRS transformation test layer."))
        {
        }

        const double utmX = 725_590.82;
        const double utmY = 7_136_808.03;
        double[] projectPoint = [utmX, utmY, 0];
        using (var toProject = SrsFactory.CreateTransform("EPSG:31982", "EPSG:3857"))
            toProject.TransformPoint(projectPoint);

        var projectGeometry = new NetTopologySuite.Geometries.Point(projectPoint[0], projectPoint[1]);
        var feature = new NetTopologySuite.Features.Feature(
            projectGeometry,
            new NetTopologySuite.Features.AttributesTable());
        int saved = VectorEditPersistenceService.SaveToOgr(path, [feature], "EPSG:3857");
        Require(saved == 1, "The projected feature was not saved to the target layer.");
        Require(Math.Abs(feature.Geometry!.Coordinate.X - projectPoint[0]) < 0.001 &&
                Math.Abs(feature.Geometry.Coordinate.Y - projectPoint[1]) < 0.001,
            "Saving to another CRS mutated the project-coordinate geometry in memory.");

        using DataSource verification = Ogr.Open(path, 0)
            ?? throw new IOException("Could not reopen the CRS transformation layer.");
        using Layer verificationLayer = verification.GetLayerByIndex(0)
            ?? throw new IOException("Could not reopen the CRS transformation layer.");
        using OSGeo.OGR.Feature savedFeature = verificationLayer.GetNextFeature()
            ?? throw new InvalidDataException("The transformed feature was not written.");
        using Geometry storedGeometry = savedFeature.GetGeometryRef()
            ?? throw new InvalidDataException("The saved feature has no geometry.");
        Require(Math.Abs(storedGeometry.GetX(0) - utmX) < 0.05 &&
                Math.Abs(storedGeometry.GetY(0) - utmY) < 0.05,
            $"The saved feature is not in the layer CRS: expected ({utmX}, {utmY}), got ({storedGeometry.GetX(0)}, {storedGeometry.GetY(0)}).");
    }

    private static void CreateTargetShapefile(string path)
    {
        Driver driver = Ogr.GetDriverByName("ESRI Shapefile")
            ?? throw new InvalidOperationException("ESRI Shapefile driver is unavailable.");
        using DataSource dataSource = driver.CreateDataSource(path, null)
            ?? throw new IOException("Could not create test Shapefile.");
        using SpatialReference reference = SrsFactory.FromEPSG(4326);
        using Layer layer = dataSource.CreateLayer("drawn", reference, wkbGeometryType.wkbPolygon, null)
            ?? throw new IOException("Could not create test Shapefile layer.");
        using (var id = new FieldDefn("ID", FieldType.OFTInteger))
            Require(layer.CreateField(id, 1) == 0, "Could not create ID field.");
        using (var name = new FieldDefn("NOME", FieldType.OFTString))
        {
            name.SetWidth(40);
            Require(layer.CreateField(name, 1) == 0, "Could not create NOME field.");
        }
        using (var area = new FieldDefn("AREA_M2", FieldType.OFTReal))
            Require(layer.CreateField(area, 1) == 0, "Could not create AREA_M2 field.");
    }

    private static NetTopologySuite.Features.Feature CreateFeature(string name, string wkt)
    {
        var geometry = new WKTReader().Read(wkt);
        var attributes = new AttributesTable
        {
            { "ID", "" },
            { "NOME", name }
        };
        return new NetTopologySuite.Features.Feature(geometry, attributes);
    }

    private static void ValidateSavedAttributes(string path)
    {
        using DataSource dataSource = Ogr.Open(path, 0)
            ?? throw new IOException("Could not reopen test Shapefile to verify attributes.");
        using Layer layer = dataSource.GetLayerByIndex(0)
            ?? throw new IOException("Could not reopen test Shapefile layer to verify attributes.");
        long expectedId = 1;
        while (true)
        {
            using OSGeo.OGR.Feature? feature = layer.GetNextFeature();
            if (feature is null) break;
            Require(feature.GetFieldAsInteger("ID") == expectedId, "The generated feature ID was not written.");
            Require(feature.GetFieldAsDouble("AREA_M2") == 6d, "The computed polygon area was not written.");
            Require(feature.GetFieldAsString("NOME") == (expectedId == 1 ? "A" : "B"), "The feature name was not written.");
            expectedId++;
        }
        Require(expectedId == 3, "The expected records were not found while validating attributes.");
    }

    private static long CountFeatures(string path)
    {
        using DataSource dataSource = Ogr.Open(path, 0)
            ?? throw new IOException("Could not reopen test Shapefile.");
        using Layer layer = dataSource.GetLayerByIndex(0)
            ?? throw new IOException("Could not reopen test Shapefile layer.");
        return layer.GetFeatureCount(1);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
