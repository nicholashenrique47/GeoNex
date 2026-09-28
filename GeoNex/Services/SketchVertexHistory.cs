namespace GeoNex.Services;

/// <summary>Undo/redo for the current sketch; adding after undo starts a new branch.</summary>
public sealed class SketchVertexHistory<T>
{
    private readonly List<T> vertices;
    private readonly Stack<T> redo = new();

    public SketchVertexHistory(List<T> vertices) => this.vertices = vertices;
    public bool CanUndo => vertices.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Add(T vertex)
    {
        vertices.Add(vertex);
        redo.Clear();
    }

    public bool Undo()
    {
        if (vertices.Count == 0) return false;
        redo.Push(vertices[^1]);
        vertices.RemoveAt(vertices.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (!redo.TryPop(out T? vertex)) return false;
        vertices.Add(vertex);
        return true;
    }

    public void Clear()
    {
        vertices.Clear();
        redo.Clear();
    }
}
