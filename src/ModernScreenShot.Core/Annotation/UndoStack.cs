using System.Text.Json;

namespace ModernScreenShot.Core.Annotation;

/// <summary>Snapshot-based undo/redo over an AnnotationDocument's serialized state.</summary>
public sealed class UndoStack
{
    private readonly List<string> _undo = [];
    private readonly Stack<string> _redo = new();
    private readonly int _limit;

    public UndoStack(int limit = 100) => _limit = limit;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event EventHandler? Changed;

    /// <summary>Call BEFORE mutating the document.</summary>
    public void Push(AnnotationDocument current)
    {
        _undo.Add(Serialize(current));
        if (_undo.Count > _limit) _undo.RemoveAt(0);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public AnnotationDocument? Undo(AnnotationDocument current)
    {
        if (!CanUndo) return null;
        _redo.Push(Serialize(current));
        var s = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        return Deserialize(s);
    }

    public AnnotationDocument? Redo(AnnotationDocument current)
    {
        if (!CanRedo) return null;
        _undo.Add(Serialize(current));
        var doc = Deserialize(_redo.Pop());
        Changed?.Invoke(this, EventArgs.Empty);
        return doc;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static string Serialize(AnnotationDocument d) => JsonSerializer.Serialize(d, JsonDefaults.Options);

    public static AnnotationDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<AnnotationDocument>(json, JsonDefaults.Options) ?? new AnnotationDocument();
}
