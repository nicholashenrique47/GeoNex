using System.Globalization;
using NetTopologySuite.IO;
using OSGeo.GDAL;
using OSGeo.OGR;

namespace GeoNex.Services.Attributes;

public sealed class OgrAttributeDataProvider : IAttributeDataProvider
{
    private readonly string _filePath;
    private readonly string _layerName;
    private readonly AttributeTableBlockCache _blockCache = new(64);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DataSource? _dataSource;
    private Layer? _layer;
    private List<AttributeColumnDefinition> _columns = new();
    private long _totalRowCount;
    private long _filteredRowCount;
    private bool _isEditing;
    private bool _isWritable;
    private readonly Dictionary<long, Dictionary<string, object?>> _dirtyRows = new();
    private readonly HashSet<long> _deletedRows = new();
    private List<long>? _filteredFids;

    public string LayerName => _layerName;
    public long TotalRowCount => _totalRowCount;
    public long FilteredRowCount => _filteredFids?.Count ?? _filteredRowCount;
    public IReadOnlyList<AttributeColumnDefinition> Columns => _columns;
    public bool IsEditing => _isEditing;
    public bool CanEditSchema => !string.IsNullOrWhiteSpace(_filePath) && File.Exists(_filePath);
    public bool CanEditValues => !string.IsNullOrWhiteSpace(_filePath) && File.Exists(_filePath);
    public bool HasPendingModifications => _dirtyRows.Count > 0 || _deletedRows.Count > 0;
    public string? CurrentFilter { get; private set; }
    public string? SortColumn { get; private set; }
    public bool SortAscending { get; private set; } = true;

    public Func<Task>? BeforeSchemaChangeAsync { get; set; }
    public Func<Task>? AfterSchemaChangeAsync { get; set; }

    public OgrAttributeDataProvider(string filePath, string? layerName = null)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _layerName = layerName ?? Path.GetFileNameWithoutExtension(filePath);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ReopenDataSource(writable: false);
            LoadSchemaAndCounts();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReopenDataSource(bool writable)
    {
        _layer?.Dispose();
        _layer = null;
        _dataSource?.Dispose();
        _dataSource = null;

        GdalRuntimeBootstrap.Configure();
        _dataSource = Ogr.Open(_filePath, writable ? 1 : 0);
        if (_dataSource != null && writable)
        {
            _isWritable = true;
        }
        else if (_dataSource == null && writable)
        {
            // Fallback to read-only if write lock fails
            _dataSource = Ogr.Open(_filePath, 0);
            _isWritable = false;
        }
        else
        {
            _isWritable = false;
        }

        if (_dataSource == null)
            throw new InvalidOperationException($"Não foi possível abrir a fonte de dados: {_filePath}. GDAL: {Gdal.GetLastErrorMsg()}");

        _layer = _dataSource.GetLayerCount() > 0 ? _dataSource.GetLayerByIndex(0) : null;
        if (_layer == null)
            throw new InvalidOperationException($"A camada {_layerName} não contém uma camada OGR válida.");
    }

    private void LoadSchemaAndCounts()
    {
        if (_layer == null) return;

        using FeatureDefn defn = _layer.GetLayerDefn();
        var cols = new List<AttributeColumnDefinition>();

        // Always include FID as pinned primary key
        cols.Add(new AttributeColumnDefinition("FID", AttributeDataType.Int64, 18, 0, IsPrimaryKey: true, IsReadOnly: true));

        int fieldCount = defn.GetFieldCount();
        for (int i = 0; i < fieldCount; i++)
        {
            using FieldDefn fDefn = defn.GetFieldDefn(i);
            string name = fDefn.GetName();
            var type = fDefn.GetFieldType() switch
            {
                FieldType.OFTInteger => AttributeDataType.Integer,
                FieldType.OFTInteger64 => AttributeDataType.Int64,
                FieldType.OFTReal => AttributeDataType.Real,
                FieldType.OFTDate => AttributeDataType.Date,
                FieldType.OFTDateTime => AttributeDataType.DateTime,
                _ => AttributeDataType.String
            };

            cols.Add(new AttributeColumnDefinition(name, type, fDefn.GetWidth(), fDefn.GetPrecision()));
        }

        _columns = cols;
        _totalRowCount = _layer.GetFeatureCount(1);
        _filteredRowCount = _totalRowCount;
        _blockCache.InvalidateAll();
    }

    public async Task<IReadOnlyList<AttributeRow>> GetRowsAsync(int startIndex, int count, CancellationToken ct = default)
    {
        if (count <= 0 || startIndex < 0) return Array.Empty<AttributeRow>();

        await _gate.WaitAsync(ct);
        try
        {
            var result = new List<AttributeRow>(count);
            int remaining = count;
            int currentStart = startIndex;

            while (remaining > 0 && currentStart < FilteredRowCount)
            {
                int bIdx = currentStart / AttributeTableBlockCache.BlockSize;
                int inBOffset = currentStart % AttributeTableBlockCache.BlockSize;

                if (!_blockCache.TryGetBlock(bIdx, out var blockRows) || blockRows == null)
                {
                    blockRows = ReadBlockFromSource(bIdx);
                    _blockCache.PutBlock(bIdx, blockRows);
                }

                for (int i = inBOffset; i < blockRows.Count && remaining > 0; i++)
                {
                    var row = blockRows[i];
                    // Overlay pending edits if any
                    if (_dirtyRows.TryGetValue(row.FID, out var edits))
                    {
                        var merged = row.Clone();
                        for (int c = 1; c < _columns.Count; c++)
                        {
                            string colName = _columns[c].Name;
                            if (edits.TryGetValue(colName, out var editedVal))
                            {
                                merged.Values[c] = editedVal;
                                merged.State = RowState.Modified;
                            }
                        }
                        result.Add(merged);
                    }
                    else
                    {
                        result.Add(row);
                    }

                    remaining--;
                    currentStart++;
                }

                if (blockRows.Count == 0) break;
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<AttributeRow> ReadBlockFromSource(int blockIndex)
    {
        if (_layer == null) return Array.Empty<AttributeRow>();

        int startRow = blockIndex * AttributeTableBlockCache.BlockSize;
        var rows = new List<AttributeRow>(AttributeTableBlockCache.BlockSize);

        if (_filteredFids != null)
        {
            // Reading from filtered FID list
            for (int i = 0; i < AttributeTableBlockCache.BlockSize && (startRow + i) < _filteredFids.Count; i++)
            {
                long fid = _filteredFids[startRow + i];
                using Feature? feat = _layer.GetFeature(fid);
                if (feat != null)
                {
                    rows.Add(ExtractRow(feat));
                }
            }
            return rows;
        }

        // Reading directly sequential
        _layer.SetNextByIndex(startRow);
        for (int i = 0; i < AttributeTableBlockCache.BlockSize; i++)
        {
            using Feature? feat = _layer.GetNextFeature();
            if (feat == null) break;
            rows.Add(ExtractRow(feat));
        }

        return rows;
    }

    private AttributeRow ExtractRow(Feature feat)
    {
        long fid = feat.GetFID();
        var values = new object?[_columns.Count];
        values[0] = fid;

        for (int c = 1; c < _columns.Count; c++)
        {
            var col = _columns[c];
            int fieldIdx = feat.GetFieldIndex(col.Name);
            if (fieldIdx < 0 || !feat.IsFieldSet(fieldIdx))
            {
                values[c] = null;
                continue;
            }

            values[c] = col.DataType switch
            {
                AttributeDataType.Integer => feat.GetFieldAsInteger(fieldIdx),
                AttributeDataType.Int64 => feat.GetFieldAsInteger64(fieldIdx),
                AttributeDataType.Real => feat.GetFieldAsDouble(fieldIdx),
                AttributeDataType.Date or AttributeDataType.DateTime => feat.GetFieldAsString(fieldIdx),
                _ => feat.GetFieldAsString(fieldIdx)
            };
        }

        return new AttributeRow(fid, values);
    }

    public async Task<AttributeRow?> GetRowByFidAsync(long fid, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_layer == null) return null;
            using Feature? feat = _layer.GetFeature(fid);
            if (feat == null) return null;
            var row = ExtractRow(feat);
            if (_dirtyRows.TryGetValue(fid, out var edits))
            {
                for (int c = 1; c < _columns.Count; c++)
                {
                    string colName = _columns[c].Name;
                    if (edits.TryGetValue(colName, out var edVal))
                    {
                        row.Values[c] = edVal;
                        row.State = RowState.Modified;
                    }
                }
            }
            return row;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NetTopologySuite.Geometries.Geometry?> GetGeometryAsync(long fid, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_layer == null) return null;
            using Feature? feat = _layer.GetFeature(fid);
            if (feat == null) return null;
            using var ogrGeom = feat.GetGeometryRef();
            if (ogrGeom == null) return null;
            int size = (int)ogrGeom.WkbSize();
            if (size <= 0) return null;
            byte[] bytes = new byte[size];
            ogrGeom.ExportToWkb(bytes);
            return new WKBReader().Read(bytes);
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetFilterAsync(string? filterQuery, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_layer == null) return;
            CurrentFilter = filterQuery;
            _layer.SetAttributeFilter(filterQuery);
            _totalRowCount = _layer.GetFeatureCount(1);
            _filteredRowCount = _totalRowCount;
            _filteredFids = null;
            _blockCache.InvalidateAll();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetSpatialFilterAsync(IReadOnlySet<long>? matchingFids, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (matchingFids == null)
            {
                _filteredFids = null;
                _filteredRowCount = _totalRowCount;
            }
            else
            {
                _filteredFids = matchingFids.ToList();
                _filteredRowCount = _filteredFids.Count;
            }
            _blockCache.InvalidateAll();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetSortingAsync(string columnName, bool ascending, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_layer == null) return;
            SortColumn = columnName;
            SortAscending = ascending;
            _blockCache.InvalidateAll();

            var colDef = _columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
            if (colDef == null) return;

            // Collect all FIDs with column values for in-memory sort index
            var items = new List<(long FID, object? Value)>((int)_totalRowCount);
            _layer.ResetReading();
            Feature? feat;

            while ((feat = _layer.GetNextFeature()) != null)
            {
                long fid = feat.GetFID();
                object? val = null;

                if (string.Equals(columnName, "FID", StringComparison.OrdinalIgnoreCase))
                {
                    val = fid;
                }
                else
                {
                    int fIdx = feat.GetFieldIndex(columnName);
                    if (fIdx >= 0 && feat.IsFieldSet(fIdx))
                    {
                        val = colDef.DataType switch
                        {
                            AttributeDataType.Integer => feat.GetFieldAsInteger(fIdx),
                            AttributeDataType.Int64 => feat.GetFieldAsInteger64(fIdx),
                            AttributeDataType.Real => feat.GetFieldAsDouble(fIdx),
                            _ => feat.GetFieldAsString(fIdx)
                        };
                    }
                }

                // Check dirty edits
                if (_dirtyRows.TryGetValue(fid, out var edits) && edits.TryGetValue(columnName, out var edVal))
                {
                    val = edVal;
                }

                items.Add((fid, val));
                feat.Dispose();
            }

            if (colDef.DataType is AttributeDataType.Integer or AttributeDataType.Int64 or AttributeDataType.Real)
            {
                items.Sort((a, b) =>
                {
                    double va = double.TryParse(a.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedA) ? parsedA : 0d;
                    double vb = double.TryParse(b.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedB) ? parsedB : 0d;
                    return ascending ? va.CompareTo(vb) : vb.CompareTo(va);
                });
            }
            else
            {
                items.Sort((a, b) =>
                {
                    string sa = a.Value?.ToString() ?? "";
                    string sb = b.Value?.ToString() ?? "";
                    return ascending ? string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase) : string.Compare(sb, sa, StringComparison.OrdinalIgnoreCase);
                });
            }

            _filteredFids = items.Select(x => x.FID).ToList();
            _filteredRowCount = _filteredFids.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearSortingAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            SortColumn = null;
            SortAscending = true;
            _filteredFids = null;
            _filteredRowCount = _totalRowCount;
            _blockCache.InvalidateAll();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SetCellValueAsync(long fid, string columnName, object? newValue)
    {
        if (string.Equals(columnName, "FID", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Não é permitido alterar a coluna primária FID.");

        if (!_dirtyRows.TryGetValue(fid, out var rowEdits))
        {
            rowEdits = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            _dirtyRows[fid] = rowEdits;
        }

        rowEdits[columnName] = newValue;
        _blockCache.InvalidateRow(fid);
        return Task.CompletedTask;
    }

    public Task BeginTransactionAsync(CancellationToken ct = default)
    {
        _isEditing = true;
        return Task.CompletedTask;
    }

    public async Task CommitTransactionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_dirtyRows.Count == 0 && _deletedRows.Count == 0)
            {
                _isEditing = false;
                return;
            }

            if (BeforeSchemaChangeAsync != null)
                await BeforeSchemaChangeAsync();

            ReopenDataSource(writable: true);
            if (_layer == null || !_isWritable)
                throw new InvalidOperationException($"Não foi possível abrir o arquivo da camada para gravação: {_filePath}. GDAL: {Gdal.GetLastErrorMsg()}");

            foreach (var kvp in _dirtyRows)
            {
                long fid = kvp.Key;
                var edits = kvp.Value;
                using Feature? feat = _layer.GetFeature(fid);
                if (feat == null) continue;

                foreach (var edit in edits)
                {
                    int fieldIdx = feat.GetFieldIndex(edit.Key);
                    if (fieldIdx < 0) continue;

                    if (edit.Value == null)
                    {
                        feat.UnsetField(fieldIdx);
                    }
                    else if (edit.Value is int or short or byte)
                    {
                        feat.SetField(fieldIdx, Convert.ToInt32(edit.Value));
                    }
                    else if (edit.Value is long)
                    {
                        feat.SetField(fieldIdx, Convert.ToInt64(edit.Value));
                    }
                    else if (edit.Value is float or double or decimal)
                    {
                        feat.SetField(fieldIdx, Convert.ToDouble(edit.Value));
                    }
                    else
                    {
                        feat.SetField(fieldIdx, edit.Value.ToString());
                    }
                }

                _layer.SetFeature(feat);
            }

            _layer.SyncToDisk();
            _dirtyRows.Clear();
            _deletedRows.Clear();
            _isEditing = false;
            _blockCache.InvalidateAll();

            // Re-read clean
            ReopenDataSource(writable: false);
            LoadSchemaAndCounts();

            if (AfterSchemaChangeAsync != null)
                await AfterSchemaChangeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RollbackTransactionAsync(CancellationToken ct = default)
    {
        _dirtyRows.Clear();
        _deletedRows.Clear();
        _isEditing = false;
        _blockCache.InvalidateAll();
        return Task.CompletedTask;
    }

    public async Task AddColumnAsync(AttributeColumnDefinition column, object? defaultValue = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (BeforeSchemaChangeAsync != null)
                await BeforeSchemaChangeAsync();

            ReopenDataSource(writable: true);
            if (_layer == null || !_isWritable)
                throw new InvalidOperationException($"Não foi possível abrir o arquivo para alteração de esquema: {_filePath}. GDAL: {Gdal.GetLastErrorMsg()}");

            string cleanName = column.Name.Trim().ToUpperInvariant();
            if (_filePath.EndsWith(".shp", StringComparison.OrdinalIgnoreCase) && cleanName.Length > 10)
            {
                cleanName = cleanName[..10];
            }

            var ogrType = column.DataType switch
            {
                AttributeDataType.Integer => FieldType.OFTInteger,
                AttributeDataType.Int64 => FieldType.OFTInteger64,
                AttributeDataType.Real => FieldType.OFTReal,
                AttributeDataType.Date => FieldType.OFTDate,
                AttributeDataType.DateTime => FieldType.OFTDateTime,
                _ => FieldType.OFTString
            };

            using var fDefn = new FieldDefn(cleanName, ogrType);
            if (column.Length > 0) fDefn.SetWidth(column.Length);
            if (column.Precision > 0) fDefn.SetPrecision(column.Precision);

            int err = _layer.CreateField(fDefn, 1);
            if (err != 0)
            {
                string msg = Gdal.GetLastErrorMsg();
                throw new InvalidOperationException($"Erro GDAL ({err}) ao criar o campo '{cleanName}': {msg}");
            }

            _layer.SyncToDisk();
            ReopenDataSource(writable: false);
            LoadSchemaAndCounts();

            if (defaultValue != null)
            {
                _layer.ResetReading();
                Feature? feat;
                while ((feat = _layer.GetNextFeature()) != null)
                {
                    _dirtyRows[feat.GetFID()] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        [cleanName] = defaultValue
                    };
                    feat.Dispose();
                }
            }

            if (AfterSchemaChangeAsync != null)
                await AfterSchemaChangeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DropColumnAsync(string columnName, CancellationToken ct = default)
    {
        if (string.Equals(columnName, "FID", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Não é permitido excluir a coluna FID.");

        await _gate.WaitAsync(ct);
        try
        {
            if (BeforeSchemaChangeAsync != null)
                await BeforeSchemaChangeAsync();

            ReopenDataSource(writable: true);
            if (_layer == null || !_isWritable)
                throw new InvalidOperationException($"Não foi possível abrir o arquivo para exclusão de campo: {_filePath}. GDAL: {Gdal.GetLastErrorMsg()}");

            using var defn = _layer.GetLayerDefn();
            int fieldIdx = defn.GetFieldIndex(columnName);
            if (fieldIdx < 0)
                throw new ArgumentException($"Coluna {columnName} não encontrada.");

            int err = _layer.DeleteField(fieldIdx);
            if (err != 0)
            {
                string msg = Gdal.GetLastErrorMsg();
                throw new InvalidOperationException($"Erro GDAL ({err}) ao excluir o campo {columnName}: {msg}");
            }

            _layer.SyncToDisk();
            ReopenDataSource(writable: false);
            LoadSchemaAndCounts();

            if (AfterSchemaChangeAsync != null)
                await AfterSchemaChangeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenameColumnAsync(string oldName, string newName, CancellationToken ct = default)
    {
        if (string.Equals(oldName, "FID", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Não é permitido renomear a coluna FID.");

        await _gate.WaitAsync(ct);
        try
        {
            if (BeforeSchemaChangeAsync != null)
                await BeforeSchemaChangeAsync();

            ReopenDataSource(writable: true);
            if (_layer == null || !_isWritable)
                throw new InvalidOperationException($"Não foi possível abrir o arquivo para renomear campo: {_filePath}. GDAL: {Gdal.GetLastErrorMsg()}");

            using var defn = _layer.GetLayerDefn();
            int fieldIdx = defn.GetFieldIndex(oldName);
            if (fieldIdx < 0)
                throw new ArgumentException($"Coluna {oldName} não encontrada.");

            string cleanNewName = newName.Trim().ToUpperInvariant();
            if (_filePath.EndsWith(".shp", StringComparison.OrdinalIgnoreCase) && cleanNewName.Length > 10)
            {
                cleanNewName = cleanNewName[..10];
            }

            using var fDefn = defn.GetFieldDefn(fieldIdx);
            using var newFieldDefn = new FieldDefn(cleanNewName, fDefn.GetFieldType());
            newFieldDefn.SetWidth(fDefn.GetWidth());
            newFieldDefn.SetPrecision(fDefn.GetPrecision());

            int err = _layer.AlterFieldDefn(fieldIdx, newFieldDefn, 1);
            if (err != 0)
            {
                string msg = Gdal.GetLastErrorMsg();
                throw new InvalidOperationException($"Erro GDAL ({err}) ao renomear o campo {oldName} para {cleanNewName}: {msg}");
            }

            _layer.SyncToDisk();
            ReopenDataSource(writable: false);
            LoadSchemaAndCounts();

            if (AfterSchemaChangeAsync != null)
                await AfterSchemaChangeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task CalculateColumnAsync(string targetColumn, Func<long, IReadOnlyDictionary<string, object?>, object?> evaluator, IReadOnlySet<long>? targetFids = null, CancellationToken ct = default)
    {
        return CalculateColumnAsync(targetColumn, (fid, dict, geom) => evaluator(fid, dict), targetFids, ct);
    }

    public async Task CalculateColumnAsync(string targetColumn, Func<long, IReadOnlyDictionary<string, object?>, NetTopologySuite.Geometries.Geometry?, object?> evaluator, IReadOnlySet<long>? targetFids = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_layer == null) return;
            _layer.ResetReading();
            Feature? feat;
            var wkbReader = new WKBReader();

            while ((feat = _layer.GetNextFeature()) != null)
            {
                long fid = feat.GetFID();
                if (targetFids != null && !targetFids.Contains(fid))
                {
                    feat.Dispose();
                    continue;
                }

                var rowDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                rowDict["FID"] = fid;
                for (int c = 1; c < _columns.Count; c++)
                {
                    string colName = _columns[c].Name;
                    int fIdx = feat.GetFieldIndex(colName);
                    if (fIdx >= 0 && feat.IsFieldSet(fIdx))
                    {
                        rowDict[colName] = _columns[c].DataType switch
                        {
                            AttributeDataType.Integer => feat.GetFieldAsInteger(fIdx),
                            AttributeDataType.Int64 => feat.GetFieldAsInteger64(fIdx),
                            AttributeDataType.Real => feat.GetFieldAsDouble(fIdx),
                            AttributeDataType.Date or AttributeDataType.DateTime => feat.GetFieldAsString(fIdx),
                            _ => feat.GetFieldAsString(fIdx)
                        };
                    }
                    else
                    {
                        rowDict[colName] = null;
                    }
                }

                // If already dirty, merge
                if (_dirtyRows.TryGetValue(fid, out var existingEdits))
                {
                    foreach (var ed in existingEdits) rowDict[ed.Key] = ed.Value;
                }

                // Read native geometry
                NetTopologySuite.Geometries.Geometry? ntsGeom = null;
                using var ogrGeom = feat.GetGeometryRef();
                if (ogrGeom != null)
                {
                    try
                    {
                        int size = (int)ogrGeom.WkbSize();
                        if (size > 0)
                        {
                            byte[] bytes = new byte[size];
                            ogrGeom.ExportToWkb(bytes);
                            ntsGeom = wkbReader.Read(bytes);
                        }
                    }
                    catch
                    {
                        // Ignore geometry parse failures gracefully
                    }
                }

                object? calculatedValue = evaluator(fid, rowDict, ntsGeom);

                if (!_dirtyRows.TryGetValue(fid, out var targetRowEdits))
                {
                    targetRowEdits = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    _dirtyRows[fid] = targetRowEdits;
                }

                targetRowEdits[targetColumn] = calculatedValue;
                feat.Dispose();
            }

            _isEditing = true;
            _blockCache.InvalidateAll();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ColumnStatistics> GetColumnStatisticsAsync(string columnName, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_layer == null)
                return new ColumnStatistics(columnName, AttributeDataType.String, 0, 0, 0, null, null, null, null, null, Array.Empty<KeyValuePair<string, int>>());

            var colDef = _columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase))
                ?? new AttributeColumnDefinition(columnName, AttributeDataType.String);

            long totalCount = 0;
            long nullCount = 0;
            double? min = null;
            double? max = null;
            double sum = 0;
            double mean = 0;
            double m2 = 0; // Welford variance accumulator

            var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            _layer.ResetReading();
            Feature? feat;
            while ((feat = _layer.GetNextFeature()) != null)
            {
                long fid = feat.GetFID();
                object? val = null;

                if (string.Equals(columnName, "FID", StringComparison.OrdinalIgnoreCase))
                {
                    val = fid;
                }
                else
                {
                    int fIdx = feat.GetFieldIndex(columnName);
                    if (fIdx >= 0 && feat.IsFieldSet(fIdx))
                        val = feat.GetFieldAsString(fIdx);
                }

                if (_dirtyRows.TryGetValue(fid, out var edits) && edits.TryGetValue(columnName, out var edVal))
                    val = edVal;

                totalCount++;

                if (val == null || (val is string s && string.IsNullOrWhiteSpace(s)))
                {
                    nullCount++;
                }
                else
                {
                    string strVal = val.ToString()!;
                    categoryCounts[strVal] = categoryCounts.TryGetValue(strVal, out int cCount) ? cCount + 1 : 1;

                    if (colDef.DataType is AttributeDataType.Integer or AttributeDataType.Int64 or AttributeDataType.Real)
                    {
                        if (double.TryParse(strVal, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                        {
                            min = min.HasValue ? Math.Min(min.Value, num) : num;
                            max = max.HasValue ? Math.Max(max.Value, num) : num;
                            sum += num;

                            // Welford single-pass algorithm
                            long validCount = totalCount - nullCount;
                            double delta = num - mean;
                            mean += delta / validCount;
                            double delta2 = num - mean;
                            m2 += delta * delta2;
                        }
                    }
                }

                feat.Dispose();
            }

            long filledCount = totalCount - nullCount;
            double? stdDev = filledCount > 1 ? Math.Sqrt(m2 / (filledCount - 1)) : null;

            var top5 = categoryCounts
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .ToList();

            return new ColumnStatistics(
                columnName,
                colDef.DataType,
                totalCount,
                nullCount,
                categoryCounts.Count,
                min,
                max,
                filledCount > 0 ? sum : null,
                filledCount > 0 ? mean : null,
                stdDev,
                top5
            );
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _layer?.Dispose();
        _layer = null;
        _dataSource?.Dispose();
        _dataSource = null;
        _gate.Dispose();
    }
}
