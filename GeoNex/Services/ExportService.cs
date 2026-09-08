using OSGeo.OGR;
using OSGeo.OSR;

namespace GeoNex.Services;

public class ExportService
{
    public bool ExportarVetor(
        string nomeCamadaOrigem,
        string caminhoShapefileOrigem,
        string caminhoDestino,
        string formatoDriver,
        int epsgDestino,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(nomeCamadaOrigem);
            ArgumentException.ThrowIfNullOrWhiteSpace(caminhoShapefileOrigem);
            ArgumentException.ThrowIfNullOrWhiteSpace(caminhoDestino);
            ArgumentException.ThrowIfNullOrWhiteSpace(formatoDriver);
            if (epsgDestino <= 0) throw new ArgumentOutOfRangeException(nameof(epsgDestino));
            cancellationToken.ThrowIfCancellationRequested();

            Ogr.RegisterAll();
            bool isShapefile = string.Equals(formatoDriver, "ESRI Shapefile", StringComparison.OrdinalIgnoreCase);
            if (isShapefile && !string.Equals(Path.GetExtension(caminhoDestino), ".shp", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Exportações Shapefile precisam usar a extensão .shp.", nameof(caminhoDestino));

            using var publication = ExportPublicationTransaction.Begin(caminhoDestino, isShapefile);

            // Todos os handles fecham antes da publicação, inclusive quando origem e destino coincidem.
            using (var dsOrigem = Ogr.Open(Path.GetFullPath(caminhoShapefileOrigem), 0)
                ?? throw new InvalidOperationException("Falha ao abrir a fonte original."))
            using (var layerOrigem = dsOrigem.GetLayerByIndex(0)
                ?? throw new InvalidOperationException("Camada vetorial nula no arquivo."))
            using (var srsDestino = new SpatialReference(""))
            {
                EnsureSuccess(srsDestino.ImportFromEPSG(epsgDestino), $"EPSG:{epsgDestino} inválido");
                srsDestino.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

                var driver = Ogr.GetDriverByName(formatoDriver)
                    ?? throw new InvalidOperationException($"Driver {formatoDriver} não encontrado no GDAL.");

                using var dsDestino = driver.CreateDataSource(publication.StagingPath, [])
                    ?? throw new InvalidOperationException("Não foi possível criar a exportação temporária.");
                using var layerDestino = dsDestino.CreateLayer(
                    nomeCamadaOrigem,
                    srsDestino,
                    layerOrigem.GetGeomType(),
                    []) ?? throw new InvalidOperationException("Não foi possível criar a camada de destino.");

                using var defnOrigem = layerOrigem.GetLayerDefn();
                for (int i = 0; i < defnOrigem.GetFieldCount(); i++)
                {
                    using var fieldDefn = defnOrigem.GetFieldDefn(i);
                    EnsureSuccess(layerDestino.CreateField(fieldDefn, 1), $"Falha ao criar o campo {fieldDefn.GetName()}");
                }

                using var srsOrigem = layerOrigem.GetSpatialRef()
                    ?? throw new InvalidDataException(
                        "A camada de origem não possui SRC. Defina o SRC antes de exportar; EPSG:4326 não será presumido.");
                srsOrigem.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
                using var coordTrans = new CoordinateTransformation(srsOrigem, srsDestino);
                using var defnDestino = layerDestino.GetLayerDefn();

                layerOrigem.ResetReading();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using Feature? feat = layerOrigem.GetNextFeature();
                    if (feat is null) break;

                    using var outFeat = new Feature(defnDestino);
                    using Geometry? sourceGeometry = feat.GetGeometryRef();
                    using Geometry? outputGeometry = sourceGeometry?.Clone();
                    if (outputGeometry is not null)
                    {
                        EnsureSuccess(outputGeometry.Transform(coordTrans), "Falha ao reprojetar uma geometria");
                        EnsureSuccess(outFeat.SetGeometry(outputGeometry), "Falha ao copiar uma geometria");
                    }

                    for (int i = 0; i < defnOrigem.GetFieldCount(); i++)
                    {
                        if (feat.IsFieldSetAndNotNull(i))
                            outFeat.SetField(i, feat.GetFieldAsString(i));
                        else
                            outFeat.SetFieldNull(i);
                    }

                    EnsureSuccess(layerDestino.CreateFeature(outFeat), "Falha ao gravar uma feição");
                }

                EnsureSuccess(layerDestino.SyncToDisk(), "Falha ao sincronizar a camada de destino");
                EnsureSuccess(dsDestino.SyncToDisk(), "Falha ao sincronizar o dataset de destino");
            }

            cancellationToken.ThrowIfCancellationRequested();
            publication.Publish(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"[EXPORT EXCEPTION] {ex}");
            return false;
        }
    }

    private static void EnsureSuccess(int status, string operation)
    {
        if (status != 0) throw new InvalidOperationException($"{operation} (OGR status {status}).");
    }
}
