namespace GeoNex.Services.Attributes;

public enum AttributeDataType
{
    String,
    Integer,
    Int64,
    Real,
    Boolean,
    Date,
    DateTime
}

public sealed record AttributeColumnDefinition(
    string Name,
    AttributeDataType DataType,
    int Length = 254,
    int Precision = 0,
    bool IsPrimaryKey = false,
    bool IsReadOnly = false,
    string? Alias = null,
    string? DefaultExpression = null
);

public enum RowState
{
    Unchanged,
    Modified,
    Added,
    Deleted
}

public sealed class AttributeRow
{
    public long FID { get; set; }
    public object?[] Values { get; set; }
    public RowState State { get; set; } = RowState.Unchanged;
    public Dictionary<string, object?>? OriginalValues { get; set; }

    public AttributeRow(long fid, object?[] values)
    {
        FID = fid;
        Values = values;
    }

    public AttributeRow Clone()
    {
        var copy = new object?[Values.Length];
        Array.Copy(Values, copy, Values.Length);
        return new AttributeRow(FID, copy)
        {
            State = this.State,
            OriginalValues = this.OriginalValues != null ? new Dictionary<string, object?>(this.OriginalValues) : null
        };
    }
}
