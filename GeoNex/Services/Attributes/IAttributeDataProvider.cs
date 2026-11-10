using NetTopologySuite.Geometries;
using SkiaSharp;

namespace GeoNex.Services.Attributes;

public sealed record ColumnStatistics(
    string ColumnName,
    AttributeDataType DataType,
    long TotalCount,
    long NullCount,
    long DistinctCount,
    double? MinValue,
    double? MaxValue,
    double? SumValue,
    double? MeanValue,
    double? StdDevValue,
    IReadOnlyList<KeyValuePair<string, int>> TopCategories
);

public interface IAttributeDataProvider : IDisposable
{
    string LayerName { get; }
    long TotalRowCount { get; }
    long FilteredRowCount { get; }
    IReadOnlyList<AttributeColumnDefinition> Columns { get; }
    bool IsEditing { get; }
    bool CanEditSchema { get; }
    bool CanEditValues { get; }
    bool HasPendingModifications { get; }
    string? CurrentFilter { get; }
    string? SortColumn { get; }
    bool SortAscending { get; }

    Func<Task>? BeforeSchemaChangeAsync { get; set; }
    Func<Task>? AfterSchemaChangeAsync { get; set; }

    Task InitializeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AttributeRow>> GetRowsAsync(int startIndex, int count, CancellationToken ct = default);
    Task<AttributeRow?> GetRowByFidAsync(long fid, CancellationToken ct = default);
    Task<NetTopologySuite.Geometries.Geometry?> GetGeometryAsync(long fid, CancellationToken ct = default);

    Task SetFilterAsync(string? filterQuery, CancellationToken ct = default);
    Task SetSpatialFilterAsync(IReadOnlySet<long>? matchingFids, CancellationToken ct = default);
    Task SetSortingAsync(string columnName, bool ascending, CancellationToken ct = default);
    Task ClearSortingAsync(CancellationToken ct = default);

    Task SetCellValueAsync(long fid, string columnName, object? newValue);
    Task BeginTransactionAsync(CancellationToken ct = default);
    Task CommitTransactionAsync(CancellationToken ct = default);
    Task RollbackTransactionAsync(CancellationToken ct = default);

    Task AddColumnAsync(AttributeColumnDefinition column, object? defaultValue = null, CancellationToken ct = default);
    Task DropColumnAsync(string columnName, CancellationToken ct = default);
    Task RenameColumnAsync(string oldName, string newName, CancellationToken ct = default);

    Task CalculateColumnAsync(string targetColumn, Func<long, IReadOnlyDictionary<string, object?>, object?> evaluator, IReadOnlySet<long>? targetFids = null, CancellationToken ct = default);
    Task CalculateColumnAsync(string targetColumn, Func<long, IReadOnlyDictionary<string, object?>, NetTopologySuite.Geometries.Geometry?, object?> evaluator, IReadOnlySet<long>? targetFids = null, CancellationToken ct = default);
    Task<ColumnStatistics> GetColumnStatisticsAsync(string columnName, CancellationToken ct = default);
}
