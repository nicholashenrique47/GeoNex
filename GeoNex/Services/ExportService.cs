using System.Text;
using NetTopologySuite.Geometries;
using OSGeo.OGR;
using OSGeo.OSR;
using NtsEnvelope = NetTopologySuite.Geometries.Envelope;

namespace GeoNex.Services;

public class ExportService
{
    public bool ExportarVetor(
        string nomeCamadaOrigem,
        string caminhoShapefileOrigem,
        string caminhoDestino,
        string formatoDriver,
        string crsDestino,
        IReadOnlySet<long>? fidsFilter = null,
        NtsEnvelope? spatialEnvelope = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(nomeCamadaOrigem);
            ArgumentException.ThrowIfNullOrWhiteSpace(caminhoShapefileOrigem);
            ArgumentException.ThrowIfNullOrWhiteSpace(caminhoDestino);
            ArgumentException.ThrowIfNullOrWhiteSpace(formatoDriver);
            ArgumentException.ThrowIfNullOrWhiteSpace(crsDestino);
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
            using (var srsDestino = SrsFactory.FromUserInput(crsDestino))
            {
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

                // Se houver filtro de extensão espacial ativa na tela
                if (spatialEnvelope != null && !spatialEnvelope.IsNull)
                {
                    layerOrigem.SetSpatialFilterRect(spatialEnvelope.MinX, spatialEnvelope.MinY, spatialEnvelope.MaxX, spatialEnvelope.MaxY);
                }
                else
                {
                    layerOrigem.SetSpatialFilter(null);
                }

                layerOrigem.ResetReading();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using Feature? feat = layerOrigem.GetNextFeature();
                    if (feat is null) break;

                    // Se houver filtro de seleção de feições (FIDs)
                    if (fidsFilter != null && !fidsFilter.Contains(feat.GetFID()))
                    {
                        continue;
                    }

                    using var outFeat = new Feature(defnDestino);
                    using OSGeo.OGR.Geometry? sourceGeometry = feat.GetGeometryRef();
                    using OSGeo.OGR.Geometry? outputGeometry = sourceGeometry?.Clone();
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

            // Se for Shapefile, emitir arquivo .cpg com codificação UTF-8
            if (isShapefile)
            {
                try
                {
                    string stagingCpg = Path.ChangeExtension(publication.StagingPath, ".cpg");
                    File.WriteAllText(stagingCpg, "UTF-8", Encoding.UTF8);
                }
                catch
                {
                    // Falha silenciosa em CPG para não abortar publicação principal
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            publication.Publish(cancellationToken);

            // Garantir que o CPG final também exista no destino publicado
            if (isShapefile)
            {
                try
                {
                    string destCpg = Path.ChangeExtension(caminhoDestino, ".cpg");
                    if (!File.Exists(destCpg))
                    {
                        File.WriteAllText(destCpg, "UTF-8", Encoding.UTF8);
                    }
                }
                catch { }
            }

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
