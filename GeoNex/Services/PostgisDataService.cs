using System.Buffers;
using System.Data;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;
using NpgsqlTypes;
using OSGeo.OSR;
using SkiaSharp;
using NtsPoint = NetTopologySuite.Geometries.Point;

namespace GeoNex.Services;

public sealed class PostgisConnectionOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string SslMode { get; set; } = "Prefer";

    public override string ToString() => $"{Host}:{Port}/{Database} ({Username})";
}

public sealed record PostgisSpatialTable(
    string Schema,
    string Table,
    string GeometryColumn,
    string GeometryType,
    int Srid,
    int CoordinateDimension);

public sealed record PostgisLayerSource(
    PostgisConnectionOptions Connection,
    PostgisSpatialTable Table);

public sealed record PostgisLoadedLayer(
    IReadOnlyList<CompiledFeature> Features,
    FeatureEnvelope Bounds,
    int Srid,
    int FeatureCount);

/// <summary>Read-only discovery and bounded vector loading plus transactional feature inserts for PostGIS.</summary>
public static class PostgisDataService
{
    public const int MaximumFeaturesPerLayer = 100_000;
    public const long MaximumGeometryBytesPerLayer = 256L * 1024 * 1024;

    public static async Task<string> TestConnectionAsync(
        PostgisConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var dataSource = BuildDataSource(options);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT PostGIS_Full_Version()", connection)
        {
            CommandTimeout = 10
        };

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result?.ToString() ?? throw new InvalidOperationException("O servidor não retornou a versão do PostGIS.");
    }

    public static async Task<IReadOnlyList<PostgisSpatialTable>> ListSpatialTablesAsync(
        PostgisConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var dataSource = BuildDataSource(options);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT f_table_schema, f_table_name, f_geometry_column, type, srid, coord_dimension
            FROM public.geometry_columns
            WHERE f_table_schema NOT IN ('pg_catalog', 'information_schema', 'topology')
              AND f_table_schema NOT LIKE 'pg_temp_%'
            ORDER BY f_table_schema, f_table_name, f_geometry_column
            """;

        var tables = new List<PostgisSpatialTable>();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 15 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(new PostgisSpatialTable(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5)));
        }

        return tables;
    }

    public static async Task<PostgisLoadedLayer> LoadLayerAsync(
        PostgisConnectionOptions options,
        PostgisSpatialTable table,
        int targetSrid,
        double offsetX,
        double offsetY,
        CancellationToken cancellationToken = default)
    {
        if (table.CoordinateDimension != 2)
            throw new InvalidOperationException("Esta camada usa coordenadas Z/M. Por segurança, o GeoNex adiciona apenas geometrias 2D nesta versão.");
        if (table.Srid <= 0 || targetSrid <= 0)
            throw new InvalidOperationException("A camada e o projeto precisam ter um SRID EPSG válido para reprojeção.");

        await using var dataSource = BuildDataSource(options);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        string schema = QuoteIdentifier(table.Schema);
        string tableName = QuoteIdentifier(table.Table);
        string geometryColumn = QuoteIdentifier(table.GeometryColumn);
        string geometryExpression = table.Srid == targetSrid
            ? geometryColumn
            : $"ST_Transform({geometryColumn}, @target_srid)";
        string sql = $"SELECT ST_AsEWKB({geometryExpression}) FROM {schema}.{tableName} " +
                     $"WHERE {geometryColumn} IS NOT NULL LIMIT @feature_limit";

        var features = new List<CompiledFeature>(Math.Min(MaximumFeaturesPerLayer, 4096));
        var bounds = FeatureEnvelope.Empty;
        long geometryBytes = 0;
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 90 };
        if (table.Srid != targetSrid)
            command.Parameters.AddWithValue("target_srid", NpgsqlDbType.Integer, targetSrid);
        command.Parameters.AddWithValue("feature_limit", NpgsqlDbType.Integer, MaximumFeaturesPerLayer + 1);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            var wkbReader = new WKBReader();
            long fid = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                long remainingGeometryBudget = MaximumGeometryBytesPerLayer - geometryBytes;
                if (remainingGeometryBudget <= 0)
                    throw new InvalidOperationException($"A camada excede o limite seguro de {MaximumGeometryBytesPerLayer / (1024 * 1024)} MB de geometria por carregamento.");
                byte[] wkb = await ReadBoundedGeometryAsync(reader, 0, remainingGeometryBudget, cancellationToken);
                geometryBytes = checked(geometryBytes + wkb.LongLength);

                if (fid >= MaximumFeaturesPerLayer)
                    throw new InvalidOperationException($"A camada excede o limite seguro de {MaximumFeaturesPerLayer:N0} feições por carregamento.");

                Geometry geometry = wkbReader.Read(wkb);
                if (geometry.IsEmpty) continue;
                geometry.SRID = targetSrid;
                IEnumerable<Geometry> renderGeometries = geometry is MultiPoint multiPoint
                    ? multiPoint.Geometries
                    : new[] { geometry };
                foreach (Geometry renderGeometry in renderGeometries)
                {
                    if (features.Count >= MaximumFeaturesPerLayer)
                        throw new InvalidOperationException($"A camada excede o limite seguro de {MaximumFeaturesPerLayer:N0} feições renderizadas por carregamento.");
                    var compiled = PostgisGeometryCompiler.Compile(renderGeometry, fid++, table.Table, offsetX, offsetY);
                    features.Add(compiled);
                    if (!compiled.EnvelopeWorld.IsNull)
                        bounds = Union(bounds, compiled.EnvelopeWorld);
                }
            }
        }
        catch
        {
            foreach (CompiledFeature feature in features) feature.Path?.Dispose();
            throw;
        }

        return new PostgisLoadedLayer(features, bounds, targetSrid, features.Count);
    }

    public static async Task<int> InsertFeaturesAsync(
        PostgisLayerSource source,
        string projectSrs,
        IReadOnlyCollection<NetTopologySuite.Features.IFeature> features,
        CancellationToken cancellationToken = default)
    {
        if (features.Count == 0) return 0;
        int projectSrid = ResolveEpsg(projectSrs);
        if (source.Table.Srid <= 0)
            throw new InvalidOperationException("A coluna de geometria de destino não possui SRID definido.");

        await using var dataSource = BuildDataSource(source.Connection);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string geometryValue = "ST_Transform(ST_GeomFromWKB(@geometry, @project_srid), @target_srid)";
        if (source.Table.GeometryType.StartsWith("MULTI", StringComparison.OrdinalIgnoreCase))
            geometryValue = $"ST_Multi({geometryValue})";
        string sql = $"INSERT INTO {QuoteIdentifier(source.Table.Schema)}.{QuoteIdentifier(source.Table.Table)} " +
                     $"({QuoteIdentifier(source.Table.GeometryColumn)}) " +
                     $"VALUES ({geometryValue})";

        try
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 60 };
            var geometryParameter = command.Parameters.Add("geometry", NpgsqlDbType.Bytea);
            command.Parameters.AddWithValue("project_srid", NpgsqlDbType.Integer, projectSrid);
            command.Parameters.AddWithValue("target_srid", NpgsqlDbType.Integer, source.Table.Srid);
            var writer = new WKBWriter();
            foreach (NetTopologySuite.Features.IFeature feature in features)
            {
                if (feature.Geometry == null || feature.Geometry.IsEmpty)
                    throw new InvalidOperationException("Há uma feição sem geometria válida na fila de gravação.");
                geometryParameter.Value = writer.Write(feature.Geometry);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("O PostGIS não confirmou a inserção de uma feição.");
            }

            await transaction.CommitAsync(cancellationToken);
            foreach (NetTopologySuite.Features.IFeature feature in features)
                MarkPersisted(feature);
            return features.Count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static void MarkPersisted(NetTopologySuite.Features.IFeature feature)
    {
        if (feature.Attributes == null) return;
        if (feature.Attributes.Exists("_SYS_GRAVADO")) feature.Attributes["_SYS_GRAVADO"] = "SIM";
        else feature.Attributes.Add("_SYS_GRAVADO", "SIM");
    }

    public static int ResolveEpsg(string srs)
    {
        if (string.IsNullOrWhiteSpace(srs))
            throw new InvalidOperationException("Defina o SRC do projeto antes de adicionar uma camada PostGIS.");

        using SpatialReference reference = SrsFactory.FromUserInput(srs);
        reference.AutoIdentifyEPSG();
        string? authority = reference.GetAuthorityName(null);
        string? code = reference.GetAuthorityCode(null);
        if (string.Equals(authority, "EPSG", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(code, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int epsg) && epsg > 0)
            return epsg;

        throw new InvalidOperationException("O PostGIS exige um SRC de projeto identificado por código EPSG. Escolha um SRC EPSG e tente novamente.");
    }

    private static NpgsqlDataSource BuildDataSource(PostgisConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Host) || string.IsNullOrWhiteSpace(options.Database) || string.IsNullOrWhiteSpace(options.Username))
            throw new ArgumentException("Informe servidor, banco de dados e usuário do PostGIS.");
        if (options.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), "A porta deve estar entre 1 e 65535.");

        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host.Trim(),
            Port = options.Port,
            Database = options.Database.Trim(),
            Username = options.Username.Trim(),
            Password = options.Password,
            SslMode = options.SslMode switch
            {
                "Require" => SslMode.Require,
                "Disable" => SslMode.Disable,
                _ => SslMode.Prefer
            },
            Timeout = 8,
            CommandTimeout = 30,
            PersistSecurityInfo = false,
            ApplicationName = "GeoNex"
        };

        var builder = new NpgsqlDataSourceBuilder(connectionString.ConnectionString);
        return builder.Build();
    }

    private static async Task<byte[]> ReadBoundedGeometryAsync(
        NpgsqlDataReader reader,
        int ordinal,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream = reader.GetStream(ordinal);
        using var geometry = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                if (read > maximumBytes - geometry.Length)
                    throw new InvalidOperationException($@"A geometria excede o limite restante de {maximumBytes / (1024 * 1024)} MB deste carregamento.");
                geometry.Write(buffer, 0, read);
            }

            return geometry.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static FeatureEnvelope Union(FeatureEnvelope left, FeatureEnvelope right)
    {
        if (left.IsNull) return right;
        if (right.IsNull) return left;
        return new FeatureEnvelope(
            Math.Min(left.MinX, right.MinX), Math.Max(left.MaxX, right.MaxX),
            Math.Min(left.MinY, right.MinY), Math.Max(left.MaxY, right.MaxY));
    }
}

public static class PostgisGeometryCompiler
{
    public static void Rebase(IReadOnlyList<CompiledFeature> features, double offsetDeltaX, double offsetDeltaY)
    {
        if (offsetDeltaX == 0 && offsetDeltaY == 0) return;
        var translation = SKMatrix.CreateTranslation((float)-offsetDeltaX, (float)offsetDeltaY);
        foreach (CompiledFeature feature in features)
        {
            feature.Path?.Transform(translation);
            feature.SetCentroidLocalPrecise(
                feature.CentroidLocalPrecise.X - offsetDeltaX,
                feature.CentroidLocalPrecise.Y + offsetDeltaY);
        }
    }

    public static CompiledFeature Compile(Geometry geometry, long fid, string layerName, double offsetX, double offsetY)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        GeometryKind kind = ResolveKind(geometry);
        Envelope envelope = geometry.EnvelopeInternal;
        var featureEnvelope = envelope.IsNull
            ? FeatureEnvelope.Empty
            : new FeatureEnvelope(envelope.MinX, envelope.MaxX, envelope.MinY, envelope.MaxY);
        var feature = new CompiledFeature
        {
            FID = fid,
            LayerName = layerName,
            Kind = kind,
            EnvelopeWorld = featureEnvelope,
            CentroidLocal = kind == GeometryKind.Point && geometry is NtsPoint point
                ? new SKPoint((float)(point.X - offsetX), (float)(offsetY - point.Y))
                : new SKPoint((float)(envelope.Centre.X - offsetX), (float)(offsetY - envelope.Centre.Y))
        };

        if (kind == GeometryKind.Point && geometry is NtsPoint precisePoint)
            feature.SetCentroidLocalPrecise(precisePoint.X - offsetX, offsetY - precisePoint.Y);

        if (kind == GeometryKind.Point) return feature;

        var path = new SKPath { FillType = kind == GeometryKind.Polygon ? SKPathFillType.EvenOdd : SKPathFillType.Winding };
        try
        {
            AppendGeometry(path, geometry, offsetX, offsetY);
            feature.Path = path;
            return feature;
        }
        catch
        {
            path.Dispose();
            throw;
        }
    }

    private static GeometryKind ResolveKind(Geometry geometry) => geometry switch
    {
        NtsPoint or MultiPoint => GeometryKind.Point,
        LineString or MultiLineString => GeometryKind.Line,
        Polygon or MultiPolygon => GeometryKind.Polygon,
        GeometryCollection collection when collection.NumGeometries > 0 => ResolveCollectionKind(collection),
        _ => throw new InvalidDataException($"Tipo de geometria PostGIS não suportado: {geometry.GeometryType}.")
    };

    private static GeometryKind ResolveCollectionKind(GeometryCollection collection)
    {
        GeometryKind kind = ResolveKind(collection.GetGeometryN(0));
        for (int i = 1; i < collection.NumGeometries; i++)
        {
            if (ResolveKind(collection.GetGeometryN(i)) != kind)
                throw new InvalidDataException("Uma feição GeometryCollection contém tipos mistos e não pode ser desenhada como uma camada simples.");
        }
        return kind;
    }

    private static void AppendGeometry(SKPath path, Geometry geometry, double offsetX, double offsetY)
    {
        switch (geometry)
        {
            case LineString line:
                AppendLine(path, line.Coordinates, offsetX, offsetY, close: false);
                break;
            case Polygon polygon:
                AppendLine(path, polygon.ExteriorRing.Coordinates, offsetX, offsetY, close: true);
                for (int i = 0; i < polygon.NumInteriorRings; i++)
                    AppendLine(path, polygon.GetInteriorRingN(i).Coordinates, offsetX, offsetY, close: true);
                break;
            case GeometryCollection collection:
                for (int i = 0; i < collection.NumGeometries; i++)
                    AppendGeometry(path, collection.GetGeometryN(i), offsetX, offsetY);
                break;
            default:
                throw new InvalidDataException($"A geometria {geometry.GeometryType} não pode ser compilada como caminho vetorial.");
        }
    }

    private static void AppendLine(SKPath path, Coordinate[] coordinates, double offsetX, double offsetY, bool close)
    {
        if (coordinates.Length == 0) return;
        path.MoveTo((float)(coordinates[0].X - offsetX), (float)(offsetY - coordinates[0].Y));
        for (int i = 1; i < coordinates.Length; i++)
            path.LineTo((float)(coordinates[i].X - offsetX), (float)(offsetY - coordinates[i].Y));
        if (close) path.Close();
    }
}
