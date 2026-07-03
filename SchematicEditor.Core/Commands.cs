namespace SchematicEditor.Core;

/// <summary>An undoable document mutation (command pattern).</summary>
public interface IEditCommand
{
    string Name { get; }
    void Execute(SchematicDocument doc);
    void Undo(SchematicDocument doc);
}

/// <summary>Classic undo/redo stack. Every mutation must go through Push.</summary>
public sealed class UndoStack(SchematicDocument doc)
{
    private readonly Stack<IEditCommand> _undo = [];
    private readonly Stack<IEditCommand> _redo = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.Count > 0 ? _undo.Peek().Name : null;

    /// <summary>Execute a command and record it. Clears the redo stack.</summary>
    public void Push(IEditCommand cmd)
    {
        cmd.Execute(doc);
        _undo.Push(cmd);
        _redo.Clear();
        doc.NotifyChanged();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var cmd = _undo.Pop();
        cmd.Undo(doc);
        _redo.Push(cmd);
        doc.NotifyChanged();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var cmd = _redo.Pop();
        cmd.Execute(doc);
        _undo.Push(cmd);
        doc.NotifyChanged();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}

public sealed class AddElementCommand(SchematicElement element, string? name = null) : IEditCommand
{
    public string Name { get; } = name ?? "Add " + element.GetType().Name;

    public void Execute(SchematicDocument doc)
    {
        doc.AddElement(element);
        if (element is SymbolInstance s && s.RefDes.EndsWith('?'))
            s.RefDes = doc.NextRefDes(s.Definition.RefPrefix);
    }

    public void Undo(SchematicDocument doc) => doc.RemoveElement(element);
}

public sealed class DeleteElementsCommand(IEnumerable<SchematicElement> elements) : IEditCommand
{
    private readonly List<SchematicElement> _elements = [.. elements];
    public string Name => "Delete";

    public void Execute(SchematicDocument doc)
    {
        foreach (var e in _elements) doc.RemoveElement(e);
    }

    public void Undo(SchematicDocument doc)
    {
        foreach (var e in _elements) doc.AddElement(e);
    }
}

public sealed class MoveElementsCommand(IEnumerable<SchematicElement> elements, Vec2 delta) : IEditCommand
{
    private readonly List<SchematicElement> _elements = [.. elements];
    public string Name => "Move";

    public void Execute(SchematicDocument doc) => Shift(delta);
    public void Undo(SchematicDocument doc) => Shift(new Vec2(-delta.X, -delta.Y));

    private void Shift(Vec2 d)
    {
        foreach (var e in _elements)
        {
            switch (e)
            {
                case SymbolInstance s:
                    s.Position += d;
                    break;
                case Wire w:
                    for (int i = 0; i < w.Points.Count; i++) w.Points[i] += d;
                    break;
            }
        }
    }
}

public sealed class RotateSymbolCommand(SymbolInstance symbol) : IEditCommand
{
    public string Name => "Rotate";

    public void Execute(SchematicDocument doc) =>
        symbol.Rotation = (Rotation)(((int)symbol.Rotation + 1) & 3);

    public void Undo(SchematicDocument doc) =>
        symbol.Rotation = (Rotation)(((int)symbol.Rotation + 3) & 3);
}

public sealed class MirrorSymbolCommand(SymbolInstance symbol) : IEditCommand
{
    public string Name => "Mirror";

    public void Execute(SchematicDocument doc) => symbol.Mirror = !symbol.Mirror;
    public void Undo(SchematicDocument doc) => symbol.Mirror = !symbol.Mirror;
}

public sealed class SetPropertyCommand(SymbolInstance symbol, bool isValue, string newText) : IEditCommand
{
    private string _oldText = "";
    public string Name { get; } = isValue ? "Set value" : "Set refdes";

    public void Execute(SchematicDocument doc)
    {
        if (isValue) { _oldText = symbol.Value; symbol.Value = newText; }
        else { _oldText = symbol.RefDes; symbol.RefDes = newText; }
    }

    public void Undo(SchematicDocument doc)
    {
        if (isValue) symbol.Value = _oldText;
        else symbol.RefDes = _oldText;
    }
}

/// <summary>Groups several commands into one undo step (e.g. paste).</summary>
public sealed class CompositeCommand(string name, IEnumerable<IEditCommand> commands) : IEditCommand
{
    private readonly List<IEditCommand> _commands = [.. commands];
    public string Name { get; } = name;

    public void Execute(SchematicDocument doc)
    {
        foreach (var c in _commands) c.Execute(doc);
    }

    public void Undo(SchematicDocument doc)
    {
        for (int i = _commands.Count - 1; i >= 0; i--) _commands[i].Undo(doc);
    }
}
