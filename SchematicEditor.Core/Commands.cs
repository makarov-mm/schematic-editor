namespace SchematicEditor.Core;

/// <summary>An undoable edit of a document.</summary>
public interface IEditCommand
{
    /// <summary>What the command does, as a short phrase: "Move", "Delete".</summary>
    string Name { get; }

    void Execute(SchematicDocument doc);
    void Undo(SchematicDocument doc);
}

/// <summary>Undo and redo history. Every edit of the document goes through <see cref="Push"/>.</summary>
public sealed class UndoStack(SchematicDocument doc)
{
    private readonly Stack<IEditCommand> _undo = [];
    private readonly Stack<IEditCommand> _redo = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.TryPeek(out IEditCommand? command) ? command.Name : null;
    public string? RedoName => _redo.TryPeek(out IEditCommand? command) ? command.Name : null;

    /// <summary>Executes and records a command; the redo history is dropped.</summary>
    public void Push(IEditCommand command)
    {
        command.Execute(doc);
        _undo.Push(command);
        _redo.Clear();
        doc.NotifyChanged();
    }

    public void Undo()
    {
        if (!_undo.TryPop(out IEditCommand? command))
            return;
        command.Undo(doc);
        _redo.Push(command);
        doc.NotifyChanged();
    }

    public void Redo()
    {
        if (!_redo.TryPop(out IEditCommand? command))
            return;
        command.Execute(doc);
        _undo.Push(command);
        doc.NotifyChanged();
    }
}

/// <summary>Adds an element on top; a symbol whose reference designator ends in '?' gets the next free number.</summary>
public sealed class AddElementCommand(SchematicElement element) : IEditCommand
{
    public string Name => element is SymbolInstance s ? "Place " + s.Definition.Name : "Draw wire";

    public void Execute(SchematicDocument doc)
    {
        doc.Add(element);
        if (element is SymbolInstance symbol && symbol.RefDes.EndsWith('?'))
            symbol.RefDes = doc.NextRefDes(symbol.Definition.RefPrefix);
    }

    public void Undo(SchematicDocument doc) => doc.Remove(element);
}

/// <summary>Deletes elements; undo puts each back where it was in the stacking order.</summary>
public sealed class DeleteElementsCommand(IEnumerable<SchematicElement> elements) : IEditCommand
{
    private readonly SchematicElement[] _elements = [.. elements];
    private readonly List<(int Index, SchematicElement Element)> _removed = [];

    public string Name => "Delete";

    public void Execute(SchematicDocument doc)
    {
        _removed.Clear();
        foreach (SchematicElement element in _elements)
        {
            int index = doc.Remove(element);
            if (index >= 0)
                _removed.Add((index, element));
        }
    }

    public void Undo(SchematicDocument doc)
    {
        for (int i = _removed.Count - 1; i >= 0; i--)
            doc.Add(_removed[i].Element, _removed[i].Index);
    }
}

public sealed class MoveElementsCommand(IEnumerable<SchematicElement> elements, Vec2 delta) : IEditCommand
{
    private readonly SchematicElement[] _elements = [.. elements];

    public string Name => "Move";

    public void Execute(SchematicDocument doc) => Shift(delta);
    public void Undo(SchematicDocument doc) => Shift(-delta);

    private void Shift(Vec2 d)
    {
        foreach (SchematicElement element in _elements)
        {
            switch (element)
            {
                case SymbolInstance s:
                    s.Position += d;
                    break;
                case Wire w:
                    for (int i = 0; i < w.Points.Count; i++)
                        w.Points[i] += d;
                    break;
            }
        }
    }
}

/// <summary>Turns each symbol a quarter turn clockwise about its own origin.</summary>
public sealed class RotateSymbolsCommand(IEnumerable<SymbolInstance> symbols) : IEditCommand
{
    private readonly SymbolInstance[] _symbols = [.. symbols];

    public string Name => "Rotate";

    public void Execute(SchematicDocument doc)
    {
        foreach (SymbolInstance s in _symbols)
            s.Rotation = s.Rotation.Next;
    }

    public void Undo(SchematicDocument doc)
    {
        foreach (SymbolInstance s in _symbols)
            s.Rotation = s.Rotation.Previous;
    }
}

public sealed class MirrorSymbolsCommand(IEnumerable<SymbolInstance> symbols) : IEditCommand
{
    private readonly SymbolInstance[] _symbols = [.. symbols];

    public string Name => "Mirror";

    public void Execute(SchematicDocument doc) => Flip();
    public void Undo(SchematicDocument doc) => Flip();

    private void Flip()
    {
        foreach (SymbolInstance s in _symbols)
            s.Mirror = !s.Mirror;
    }
}

/// <summary>Sets a symbol's reference designator and value.</summary>
public sealed class EditSymbolCommand(SymbolInstance symbol, string refDes, string value) : IEditCommand
{
    private readonly string _oldRefDes = symbol.RefDes;
    private readonly string _oldValue = symbol.Value;

    public string Name => "Edit properties";

    public void Execute(SchematicDocument doc) => (symbol.RefDes, symbol.Value) = (refDes, value);
    public void Undo(SchematicDocument doc) => (symbol.RefDes, symbol.Value) = (_oldRefDes, _oldValue);
}

/// <summary>Several commands as one undo step (e.g. paste).</summary>
public sealed class CompositeCommand(string name, IEnumerable<IEditCommand> commands) : IEditCommand
{
    private readonly IEditCommand[] _commands = [.. commands];

    public string Name { get; } = name;

    public void Execute(SchematicDocument doc)
    {
        foreach (IEditCommand command in _commands)
            command.Execute(doc);
    }

    public void Undo(SchematicDocument doc)
    {
        for (int i = _commands.Length - 1; i >= 0; i--)
            _commands[i].Undo(doc);
    }
}
