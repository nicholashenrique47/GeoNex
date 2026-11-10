namespace GeoNex.Services.Attributes;

public sealed record CellEditRecord(long FID, string ColumnName, object? OldValue, object? NewValue);

public sealed class AttributeTransactionManager
{
    private readonly Stack<IReadOnlyList<CellEditRecord>> _undoStack = new();
    private readonly Stack<IReadOnlyList<CellEditRecord>> _redoStack = new();
    private readonly int _maxHistory;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public int UndoCount => _undoStack.Count;
    public int RedoCount => _redoStack.Count;

    public AttributeTransactionManager(int maxHistory = 100)
    {
        _maxHistory = Math.Max(10, maxHistory);
    }

    public void RecordEdit(long fid, string columnName, object? oldValue, object? newValue)
    {
        RecordEdits(new[] { new CellEditRecord(fid, columnName, oldValue, newValue) });
    }

    public void RecordEdits(IReadOnlyList<CellEditRecord> edits)
    {
        if (edits == null || edits.Count == 0) return;

        if (_undoStack.Count >= _maxHistory)
        {
            var list = _undoStack.ToList();
            list.RemoveAt(list.Count - 1);
            _undoStack.Clear();
            for (int i = list.Count - 1; i >= 0; i--)
                _undoStack.Push(list[i]);
        }

        _undoStack.Push(edits);
        _redoStack.Clear();
    }

    public bool Undo(IAttributeDataProvider provider)
    {
        if (!CanUndo) return false;

        var edits = _undoStack.Pop();
        var reverseList = new List<CellEditRecord>(edits.Count);

        foreach (var edit in edits)
        {
            provider.SetCellValueAsync(edit.FID, edit.ColumnName, edit.OldValue);
            reverseList.Add(new CellEditRecord(edit.FID, edit.ColumnName, edit.NewValue, edit.OldValue));
        }

        _redoStack.Push(reverseList);
        return true;
    }

    public bool Redo(IAttributeDataProvider provider)
    {
        if (!CanRedo) return false;

        var edits = _redoStack.Pop();
        var reverseList = new List<CellEditRecord>(edits.Count);

        foreach (var edit in edits)
        {
            provider.SetCellValueAsync(edit.FID, edit.ColumnName, edit.OldValue); // here OldValue holds the next forward value
            reverseList.Add(new CellEditRecord(edit.FID, edit.ColumnName, edit.NewValue, edit.OldValue));
        }

        _undoStack.Push(reverseList);
        return true;
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }
}
