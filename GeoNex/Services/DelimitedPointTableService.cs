using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using OSGeo.OGR;
using OSGeo.OSR;

namespace GeoNex.Services;

public static class DelimitedPointTableService
{
    public const int MaximumRows = 1_000_000;
    private const int MaximumDbfTextLength = 254;
    private static readonly char[] SupportedDelimiters = [',', ';', '\t', '|'];

    public sealed record Column(int Index, string Name);
    public sealed record Preview(char Delimiter, IReadOnlyList<Column> Columns, IReadOnlyList<string[]> Rows);
    public sealed record ImportResult(string ShapefilePath, int ImportedCount, int SkippedCount);
    public sealed record CoordinateReferenceSystem(string Authority, string Code, string Name, string Kind)
    {
        public string Identifier => $"{Authority}:{Code}";
    }

    private static readonly Lazy<IReadOnlyList<CoordinateReferenceSystem>> CoordinateReferenceSystems =
        new(LoadCoordinateReferenceSystems, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<CoordinateReferenceSystem> ReadCoordinateReferenceSystems() => CoordinateReferenceSystems.Value;

    public static Preview ReadPreview(string path, char? delimiter = null)
    {
        ValidatePath(path);
        char separator = delimiter ?? DetectDelimiter(path);
        using var reader = OpenReader(path);
        using var records = ReadRecords(reader, separator).GetEnumerator();
        if (!records.MoveNext()) throw new InvalidDataException("O arquivo está vazio.");

        string[] headers = NormalizeHeaders(records.Current);
        if (headers.Length < 2)
            throw new InvalidDataException("A primeira linha precisa conter os nomes das colunas X e Y.");

        var rows = new List<string[]>(6);
        while (rows.Count < 6 && records.MoveNext())
        {
            if (records.Current.All(string.IsNullOrWhiteSpace)) continue;
            rows.Add(records.Current);
        }

        return new Preview(separator,
            headers.Select((name, index) => new Column(index, string.IsNullOrWhiteSpace(name) ? $"Coluna {index + 1}" : name)).ToArray(),
            rows);
    }

    public static ImportResult ImportToShapefile(
        string sourcePath,
        int xColumn,
        int yColumn,
        string sourceCrs,
        char delimiter,
        string destinationDirectory)
    {
        ValidatePath(sourcePath);
        if (xColumn < 0 || yColumn < 0 || xColumn == yColumn)
            throw new ArgumentException("Selecione colunas X e Y diferentes.");
        if (string.IsNullOrWhiteSpace(sourceCrs))
            throw new ArgumentException("Selecione ou informe o SRC das coordenadas.", nameof(sourceCrs));
        if (!SupportedDelimiters.Contains(delimiter))
            throw new ArgumentOutOfRangeException(nameof(delimiter), "Separador não suportado.");

        Directory.CreateDirectory(destinationDirectory);
        string layerName = Path.GetFileNameWithoutExtension(sourcePath);
        string safeName = SanitizeFileName(layerName);
        string outputPath = Path.Combine(destinationDirectory, $"{safeName}_{Guid.NewGuid():N}.shp");
        int imported = 0;
        int skipped = 0;
        try
        {
            using var reader = OpenReader(sourcePath);
            using var records = ReadRecords(reader, delimiter).GetEnumerator();
            if (!records.MoveNext()) throw new InvalidDataException("O arquivo está vazio.");
            string[] headers = NormalizeHeaders(records.Current);
            if (xColumn >= headers.Length || yColumn >= headers.Length)
                throw new InvalidDataException("As colunas selecionadas não existem mais no arquivo.");
            if (headers.Length > 255)
                throw new InvalidDataException("A tabela tem mais de 255 colunas; o formato Shapefile permite no máximo 255.");

            Ogr.RegisterAll();
            Driver driver = Ogr.GetDriverByName("ESRI Shapefile")
                ?? throw new InvalidOperationException("O driver ESRI Shapefile do GDAL não está disponível.");
            using DataSource dataSource = driver.CreateDataSource(outputPath, null)
                ?? throw new IOException("O GDAL não conseguiu criar o arquivo de pontos.");

            using var spatialReference = new SpatialReference("");
            string crsInput = sourceCrs.Trim();
            if (int.TryParse(crsInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out int epsgCode))
                crsInput = $"EPSG:{epsgCode}";
            if (spatialReference.SetFromUserInput(crsInput) != 0 || spatialReference.Validate() != 0)
                throw new ArgumentException($"O GDAL/PROJ não reconheceu o SRC '{sourceCrs}'. Informe um código de autoridade, como EPSG:4326 ou ESRI:102033.", nameof(sourceCrs));
            bool isGeographic = spatialReference.IsGeographic() != 0;
            spatialReference.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            spatialReference.MorphToESRI();
            Layer layer = dataSource.CreateLayer(safeName, spatialReference, wkbGeometryType.wkbPoint, ["ENCODING=UTF-8"])
                ?? throw new IOException("O GDAL não conseguiu criar a camada de pontos.");
            try
            {
                string[] fieldNames = MakeDbfFieldNames(headers);
                for (int i = 0; i < fieldNames.Length; i++)
                {
                    using var field = new FieldDefn(fieldNames[i], FieldType.OFTString);
                    field.SetWidth(MaximumDbfTextLength);
                    if (layer.CreateField(field, 1) != 0)
                        throw new IOException($"Não foi possível criar a coluna '{headers[i]}'. {OSGeo.GDAL.Gdal.GetLastErrorMsg()}");
                }

                using FeatureDefn definition = layer.GetLayerDefn();
                int rowNumber = 1;
                while (records.MoveNext())
                {
                    if (++rowNumber > MaximumRows + 1)
                        throw new InvalidDataException($"O arquivo ultrapassa o limite de {MaximumRows:N0} linhas.");

                    string[] row = records.Current;
                    if (row.All(string.IsNullOrWhiteSpace)) continue;
                    string xText = GetField(row, xColumn).Trim();
                    string yText = GetField(row, yColumn).Trim();
                    if (!TryParseCoordinate(xText, out double x) || !TryParseCoordinate(yText, out double y) ||
                        (isGeographic && (x < -180 || x > 180 || y < -90 || y > 90)))
                    {
                        skipped++;
                        continue;
                    }

                    using var feature = new Feature(definition);
                    for (int fieldIndex = 0; fieldIndex < headers.Length; fieldIndex++)
                    {
                        string value = GetField(row, fieldIndex);
                        if (value.Length > MaximumDbfTextLength) value = value[..MaximumDbfTextLength];
                        feature.SetField(fieldIndex, value);
                    }

                    using var geometry = new Geometry(wkbGeometryType.wkbPoint);
                    geometry.AddPoint_2D(x, y);
                    feature.SetGeometry(geometry);
                    if (layer.CreateFeature(feature) != 0)
                        throw new IOException($"Falha ao gravar o ponto da linha {rowNumber}. {OSGeo.GDAL.Gdal.GetLastErrorMsg()}");
                    imported++;
                }

                if (imported == 0)
                    throw new InvalidDataException("Nenhum ponto válido foi encontrado. Confira as colunas X/Y, o separador e o EPSG.");
                if (layer.SyncToDisk() != 0)
                    throw new IOException($"Falha ao finalizar o arquivo de pontos. {OSGeo.GDAL.Gdal.GetLastErrorMsg()}");
            }
            finally
            {
                layer.Dispose();
            }

            File.WriteAllText(Path.ChangeExtension(outputPath, ".cpg"), "UTF-8", Encoding.ASCII);
            return new ImportResult(outputPath, imported, skipped);
        }
        catch
        {
            DeleteShapefileFamily(outputPath);
            throw;
        }
    }

    private static IReadOnlyList<CoordinateReferenceSystem> LoadCoordinateReferenceSystems()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "gdal", "share", "proj.db"),
            Path.Combine(AppContext.BaseDirectory, "share", "proj", "proj.db"),
            Path.Combine(AppContext.BaseDirectory, "proj.db")
        ];
        string databasePath = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("O banco de sistemas de referência do PROJ (proj.db) não foi encontrado.");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT auth_name, CAST(code AS TEXT), name, 'Projetado' AS kind
              FROM projected_crs WHERE deprecated = 0
            UNION ALL
            SELECT auth_name, CAST(code AS TEXT), name,
                   CASE type WHEN 'geographic 2D' THEN 'Geográfico 2D'
                             WHEN 'geographic 3D' THEN 'Geográfico 3D'
                             ELSE 'Geocêntrico' END AS kind
              FROM geodetic_crs WHERE deprecated = 0
            UNION ALL
            SELECT auth_name, CAST(code AS TEXT), name, 'Local' AS kind
              FROM engineering_crs WHERE deprecated = 0
            UNION ALL
            SELECT auth_name, CAST(code AS TEXT), name, 'Composto' AS kind
              FROM compound_crs WHERE deprecated = 0
            ORDER BY 3 COLLATE NOCASE, 1, 2;
            """;

        using var reader = command.ExecuteReader();
        var coordinateSystems = new List<CoordinateReferenceSystem>();
        while (reader.Read())
        {
            coordinateSystems.Add(new CoordinateReferenceSystem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }
        if (coordinateSystems.Count == 0)
            throw new InvalidDataException("O catálogo PROJ não contém sistemas de referência utilizáveis.");
        return coordinateSystems;
    }

    public static int GuessCoordinateColumn(IReadOnlyList<Column> columns, bool xAxis)
    {
        string[] candidates = xAxis
            ? ["x", "lon", "long", "longitude", "easting", "east", "coordx", "xcoord", "utme", "este"]
            : ["y", "lat", "latitude", "northing", "north", "coordy", "ycoord", "utmn", "norte"];
        foreach (Column column in columns)
        {
            string normalized = NormalizeColumnName(column.Name);
            if (candidates.Contains(normalized, StringComparer.Ordinal)) return column.Index;
        }
        return columns.Count >= 2 ? (xAxis ? 0 : 1) : -1;
    }

    private static char DetectDelimiter(string path)
    {
        using var reader = OpenReader(path);
        string line = reader.ReadLine() ?? throw new InvalidDataException("O arquivo está vazio.");
        var counts = SupportedDelimiters.Select((delimiter, index) =>
            (Delimiter: delimiter, Index: index, Count: CountUnquoted(line, delimiter))).ToArray();
        var best = counts.OrderByDescending(item => item.Count).ThenBy(item => item.Index).First();
        if (best.Count == 0) throw new InvalidDataException("Não identifiquei um separador. Use vírgula, ponto e vírgula, tabulação ou barra vertical.");
        return best.Delimiter;
    }

    private static int CountUnquoted(string line, char delimiter)
    {
        bool quoted = false;
        int count = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { i++; continue; }
                quoted = !quoted;
            }
            else if (!quoted && line[i] == delimiter) count++;
        }
        return count;
    }

    private static IEnumerable<string[]> ReadRecords(TextReader reader, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        bool hasRecordContent = false;
        int value;
        while ((value = reader.Read()) != -1)
        {
            char current = (char)value;
            if (quoted)
            {
                if (current == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else quoted = false;
                }
                else field.Append(current);
                hasRecordContent = true;
                continue;
            }

            if (current == '"' && string.IsNullOrWhiteSpace(field.ToString()))
            {
                field.Clear();
                quoted = true;
                hasRecordContent = true;
            }
            else if (current == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                hasRecordContent = true;
            }
            else if (current is '\r' or '\n')
            {
                if (current == '\r' && reader.Peek() == '\n') reader.Read();
                if (hasRecordContent || fields.Count > 0 || field.Length > 0)
                {
                    fields.Add(field.ToString());
                    yield return fields.ToArray();
                }
                fields.Clear();
                field.Clear();
                hasRecordContent = false;
            }
            else
            {
                field.Append(current);
                if (!char.IsWhiteSpace(current)) hasRecordContent = true;
            }
        }

        if (quoted) throw new InvalidDataException("Há uma aspa aberta sem fechamento no arquivo CSV.");
        if (hasRecordContent || fields.Count > 0 || field.Length > 0)
        {
            fields.Add(field.ToString());
            yield return fields.ToArray();
        }
    }

    private static string[] NormalizeHeaders(string[] headers) =>
        headers.Select((header, index) => index == 0 ? header.Trim().TrimStart('\uFEFF') : header.Trim()).ToArray();

    private static string[] MakeDbfFieldNames(string[] headers)
    {
        var result = new string[headers.Length];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++)
        {
            string name = new string(headers[i].ToUpperInvariant()
                .Select(character => char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_')
                .ToArray()).Trim('_');
            if (name.Length == 0) name = $"FIELD{i + 1}";
            if (char.IsAsciiDigit(name[0])) name = "F" + name;

            string candidate = name[..Math.Min(10, name.Length)];
            int suffix = 2;
            while (!used.Add(candidate))
            {
                string suffixText = suffix++.ToString(CultureInfo.InvariantCulture);
                candidate = name[..Math.Min(name.Length, 10 - suffixText.Length - 1)] + "_" + suffixText;
            }
            result[i] = candidate;
        }
        return result;
    }

    private static string NormalizeColumnName(string name) =>
        new(name.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static bool TryParseCoordinate(string value, out double coordinate) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate) && double.IsFinite(coordinate) ||
        double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out coordinate) && double.IsFinite(coordinate);

    private static string GetField(string[] row, int index) => index < row.Length ? row[index] : string.Empty;

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Selecione um arquivo CSV ou de texto delimitado válido.", path);
        string extension = Path.GetExtension(path);
        if (!new[] { ".csv", ".txt", ".tsv" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Use um arquivo .csv, .txt ou .tsv.");
    }

    private static StreamReader OpenReader(string path) => new(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

    private static string SanitizeFileName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "Pontos" : name;
    }

    private static void DeleteShapefileFamily(string path)
    {
        foreach (string extension in new[] { ".shp", ".shx", ".dbf", ".prj", ".cpg" })
        {
            try { File.Delete(Path.ChangeExtension(path, extension)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
