using System.Globalization;
using NetTopologySuite.Features;
using OSGeo.GDAL;
using OSGeo.OGR;
using OSGeo.OSR;
using OgrGeometry = OSGeo.OGR.Geometry;

namespace GeoNex.Services;

/// <summary>Persists newly digitized NTS features to an OGR vector dataset with explicit error handling.</summary>
public static class VectorEditPersistenceService
{
    private const string TransactionsCapability = "Transactions";
    public const string ExistingFeatureIdAttribute = "_SYS_EDIT_FID";
    public const string DeleteFeatureAttribute = "_SYS_EDIT_DELETE";

    public static int SaveToOgr(
        string path,
        IReadOnlyCollection<NetTopologySuite.Features.IFeature> features,
        string? sourceSrs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(features);
        if (features.Count == 0) return 0;

        Ogr.RegisterAll();
        using DataSource? dataSource = Ogr.Open(path, 1);
        if (dataSource == null)
        {
            string gdalError = Gdal.GetLastErrorMsg();
            string details = string.IsNullOrWhiteSpace(gdalError)
                ? "Verifique se os arquivos .shp, .shx e .dbf permitem gravação e se outro processo não está mantendo um bloqueio incompatível."
                : gdalError;
            throw new IOException($"O GDAL não abriu a camada '{Path.GetFullPath(path)}' para gravação. {details}");
        }

        using Layer? layer = dataSource.GetLayerByIndex(0);
        if (layer == null)
            throw new IOException($"O arquivo '{Path.GetFileName(path)}' não contém uma camada vetorial gravável. {Gdal.GetLastErrorMsg()}");

        using SpatialReference? destinationSrs = layer.GetSpatialRef();
        using SpatialReference? featureSrs = string.IsNullOrWhiteSpace(sourceSrs)
            ? null
            : SrsFactory.FromUserInput(sourceSrs);
        if (featureSrs is not null && destinationSrs is null)
            throw new InvalidDataException(
                $"A camada '{Path.GetFileName(path)}' não possui SRC definido. Defina o SRC da camada antes de salvar geometrias do projeto.");

        bool hasPendingDeletes = features.Any(feature => !IsPersisted(feature) && IsDeletionPending(feature));
        if (hasPendingDeletes && !layer.TestCapability("DeleteFeature"))
            throw new NotSupportedException($"O formato da camada '{Path.GetFileName(path)}' não permite excluir feições por identificador.");

        destinationSrs?.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        using CoordinateTransformation? toDestination = featureSrs is not null && destinationSrs is not null &&
            featureSrs.IsSame(destinationSrs, null) != 1
                ? new CoordinateTransformation(featureSrs, destinationSrs)
                : null;

        bool supportsTransactions = layer.TestCapability(TransactionsCapability);
        bool transactionStarted = false;
        var persisted = new List<NetTopologySuite.Features.IFeature>(features.Count);
        try
        {
            if (supportsTransactions)
            {
                EnsureSuccess(layer.StartTransaction(), "iniciar transação");
                transactionStarted = true;
            }

            long nextId = Math.Max(1, layer.GetFeatureCount(1) + 1);
            IEnumerable<NetTopologySuite.Features.IFeature> pendingFeatures = features
                .Where(feature => !IsPersisted(feature));
            foreach (NetTopologySuite.Features.IFeature ntsFeature in pendingFeatures
                .Where(feature => !IsDeletionPending(feature))
                .Concat(pendingFeatures.Where(IsDeletionPending)
                    .OrderByDescending(feature => TryGetExistingFeatureId(feature, out long fid) ? fid : long.MinValue)))
            {
                if (TryGetExistingFeatureId(ntsFeature, out long deletedFeatureId) && IsDeletionPending(ntsFeature))
                {
                    EnsureSuccess(layer.DeleteFeature(deletedFeatureId), $"excluir a feição {deletedFeatureId}");
                    if (!supportsTransactions)
                    {
                        EnsureSuccess(layer.SyncToDisk(), $"sincronizar a exclusão da feição {deletedFeatureId}");
                        MarkPersisted(ntsFeature);
                    }
                    persisted.Add(ntsFeature);
                    continue;
                }
                if (ntsFeature.Geometry == null || ntsFeature.Geometry.IsEmpty)
                    throw new InvalidDataException("Uma feição da fila não possui geometria válida.");

                using var geometry = OgrGeometry.CreateFromWkt(ntsFeature.Geometry.AsText());
                if (geometry == null)
                    throw new IOException($"O GDAL não converteu a geometria para '{Path.GetFileName(path)}'. {Gdal.GetLastErrorMsg()}");
                if (toDestination is not null)
                    EnsureSuccess(geometry.Transform(toDestination), $"reprojetar a feição {nextId} para o SRC da camada");

                if (TryGetExistingFeatureId(ntsFeature, out long existingFeatureId))
                {
                    using OSGeo.OGR.Feature? existingFeature = layer.GetFeature(existingFeatureId);
                    if (existingFeature is null)
                        throw new InvalidDataException($"A feição {existingFeatureId} não existe mais em '{Path.GetFileName(path)}'.");
                    EnsureSuccess(existingFeature.SetGeometry(geometry), $"atualizar a geometria da feição {existingFeatureId}");
                    EnsureSuccess(layer.SetFeature(existingFeature), $"gravar a geometria da feição {existingFeatureId}");
                    if (!supportsTransactions)
                    {
                        EnsureSuccess(layer.SyncToDisk(), $"sincronizar a feição {existingFeatureId} no disco");
                        MarkPersisted(ntsFeature);
                    }
                    persisted.Add(ntsFeature);
                    continue;
                }

                using FeatureDefn definition = layer.GetLayerDefn();
                using var ogrFeature = new OSGeo.OGR.Feature(definition);
                ogrFeature.SetGeometry(geometry);
                CopyAttributes(ntsFeature, ogrFeature, definition, nextId);

                EnsureSuccess(layer.CreateFeature(ogrFeature), $"gravar feição {nextId}");
                if (!supportsTransactions)
                {
                    EnsureSuccess(layer.SyncToDisk(), $"sincronizar feição {nextId} no disco");
                    MarkPersisted(ntsFeature);
                }
                persisted.Add(ntsFeature);
                nextId++;
            }

            if (supportsTransactions)
            {
                EnsureSuccess(layer.CommitTransaction(), "confirmar transação");
                transactionStarted = false;
            }

            foreach (NetTopologySuite.Features.IFeature feature in persisted)
                MarkPersisted(feature);
            return persisted.Count;
        }
        catch
        {
            if (transactionStarted)
            {
                try { layer.RollbackTransaction(); }
                catch (Exception rollbackError) { Console.Error.WriteLine($"Falha ao reverter a transação OGR: {rollbackError.Message}"); }
            }
            throw;
        }
    }

    public static bool IsPersisted(NetTopologySuite.Features.IFeature feature) =>
        feature.Attributes != null && feature.Attributes.Exists("_SYS_GRAVADO") &&
        string.Equals(feature.Attributes["_SYS_GRAVADO"]?.ToString(), "SIM", StringComparison.Ordinal);

    public static bool TryGetExistingFeatureId(NetTopologySuite.Features.IFeature feature, out long featureId)
    {
        featureId = -1;
        if (feature.Attributes == null || !feature.Attributes.Exists(ExistingFeatureIdAttribute)) return false;
        return long.TryParse(
            Convert.ToString(feature.Attributes[ExistingFeatureIdAttribute], CultureInfo.InvariantCulture),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out featureId) && featureId >= 0;
    }

    public static bool IsDeletionPending(NetTopologySuite.Features.IFeature feature) =>
        feature.Attributes != null && feature.Attributes.Exists(DeleteFeatureAttribute) &&
        string.Equals(Convert.ToString(feature.Attributes[DeleteFeatureAttribute], CultureInfo.InvariantCulture), "SIM", StringComparison.Ordinal);

    private static void MarkPersisted(NetTopologySuite.Features.IFeature feature)
    {
        if (feature.Attributes == null) return;
        if (feature.Attributes.Exists("_SYS_GRAVADO")) feature.Attributes["_SYS_GRAVADO"] = "SIM";
        else feature.Attributes.Add("_SYS_GRAVADO", "SIM");
    }

    private static void CopyAttributes(
        NetTopologySuite.Features.IFeature ntsFeature,
        OSGeo.OGR.Feature ogrFeature,
        FeatureDefn definition,
        long id)
    {
        // Field values are copied by schema name; generated layers also receive sensible defaults.
        for (int i = 0; i < definition.GetFieldCount(); i++)
        {
            using FieldDefn field = definition.GetFieldDefn(i);
            string name = field.GetNameRef();
            string normalizedName = name.ToUpperInvariant();
            if (normalizedName.StartsWith("_SYS_", StringComparison.Ordinal)) continue;
            if (ntsFeature.Attributes != null && ntsFeature.Attributes.Exists(name))
            {
                object? value = ntsFeature.Attributes[name];
                if (value != null && value != DBNull.Value && normalizedName != "_SYS_GRAVADO")
                {
                    bool blank = value is string text && string.IsNullOrWhiteSpace(text);
                    if (!blank)
                    {
                        SetFieldValue(ogrFeature, i, field.GetFieldType(), value);
                        continue;
                    }
                }
            }

            if (normalizedName == "ID") SetFieldValue(ogrFeature, i, field.GetFieldType(), id);
            else if (normalizedName == "NOME") ogrFeature.SetField(i, $"Feição {id.ToString(CultureInfo.InvariantCulture)}");
            else if (normalizedName == "DATA_CRIAC") ogrFeature.SetField(i, DateTime.Now.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
            else if (normalizedName == "AREA_M2" &&
                     (ntsFeature.Geometry is NetTopologySuite.Geometries.Polygon || ntsFeature.Geometry is NetTopologySuite.Geometries.MultiPolygon))
                SetFieldValue(ogrFeature, i, field.GetFieldType(), ntsFeature.Geometry.Area);
            else if (normalizedName == "COMPRIM_M" &&
                     (ntsFeature.Geometry is NetTopologySuite.Geometries.LineString || ntsFeature.Geometry is NetTopologySuite.Geometries.MultiLineString))
                SetFieldValue(ogrFeature, i, field.GetFieldType(), ntsFeature.Geometry.Length);
            else if (normalizedName == "COORD_X" && ntsFeature.Geometry is NetTopologySuite.Geometries.Point point)
                SetFieldValue(ogrFeature, i, field.GetFieldType(), point.X);
            else if (normalizedName == "COORD_Y" && ntsFeature.Geometry is NetTopologySuite.Geometries.Point pointY)
                SetFieldValue(ogrFeature, i, field.GetFieldType(), pointY.Y);
        }
    }

    private static void SetFieldValue(OSGeo.OGR.Feature feature, int index, FieldType type, object value)
    {
        switch (type)
        {
            case FieldType.OFTInteger:
                feature.SetField(index, Convert.ToInt32(value, CultureInfo.InvariantCulture));
                break;
            case FieldType.OFTInteger64:
                feature.SetFieldInteger64(index, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case FieldType.OFTReal:
                feature.SetField(index, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            default:
                feature.SetField(index, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                break;
        }
    }

    private static void EnsureSuccess(int result, string operation)
    {
        if (result != 0)
            throw new IOException($"O GDAL falhou ao {operation}. {Gdal.GetLastErrorMsg()}");
    }
}
