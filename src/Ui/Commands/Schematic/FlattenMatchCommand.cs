using System.Text;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// <b>Flatten to Cell</b> as ONE undoable operation (match.md §11.2): write the cell folder, and —
/// when the dialog's checkbox is on — replace the <c>Match</c> with an instance of it.
///
/// <h3>Why the file writes are inside the command</h3>
/// <para>The brief asks for one undo that reverses everything, "the instance, the cell reference,
/// the files". Splitting the writes out would give a user an undo that removed the instance and left
/// the cell behind, so the next Flatten would refuse the name it had just been given back. That is
/// the same reasoning Layout's Group into Cell used to reach the OPPOSITE conclusion (R-L3c-6, which
/// deliberately keeps the folder): there the grouped cell is the deliverable and the instance is
/// incidental, and a grouped cell may already have been opened and edited. Here the deliverable is
/// the replacement, and the cell is written from a design that is still sitting in the schematic —
/// so it can be rewritten byte-identically by a Redo.</para>
///
/// <h3>Undo never deletes work</h3>
/// <para>The folder is removed only when it still holds <b>exactly</b> the three files this command
/// wrote, byte for byte. Anything else — a symbol the user edited, a layout view added, a second
/// schematic — and the folder stays and the caller is told why. An undo stack is not allowed to
/// destroy something it did not create.</para>
/// </summary>
internal sealed class FlattenMatchCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableComponent _match;
    private readonly EditableComponent? _replacement;
    private readonly SchematicEditModel _cellSchematic;
    private readonly MatchDesign _design;
    private readonly string _parentDir;
    private readonly IMessageSink? _messages;

    private List<(string Path, string Content)> _written = [];

    /// <param name="model">The schematic that owns the <c>Match</c>.</param>
    /// <param name="match">The instance being flattened.</param>
    /// <param name="replacement">
    /// The pre-built cell instance, or null when the user cleared "Replace …". Its position,
    /// rotation and mirror are the <c>Match</c>'s own and its symbol is a copy of the same glyph, so
    /// every wire endpoint already sits on a pin of the replacement — nothing has to be moved, which
    /// is exactly why the symbol is copied rather than generated.
    /// </param>
    public FlattenMatchCommand(
        SchematicEditModel model,
        EditableComponent match,
        EditableComponent? replacement,
        string parentDir,
        string cellName,
        SchematicEditModel cellSchematic,
        MatchDesign design,
        IMessageSink? messages = null)
    {
        _model = model;
        _match = match;
        _replacement = replacement;
        _parentDir = parentDir;
        CellName = cellName;
        _cellSchematic = cellSchematic;
        _design = design;
        _messages = messages;
    }

    /// <summary>The cell this command writes.</summary>
    public string CellName { get; }

    /// <summary>Its folder, whether or not it exists at this moment.</summary>
    public string CellDir => Path.Combine(_parentDir, CellName);

    /// <inheritdoc/>
    public string Description => $"Flatten {_match.InstanceName} to cell {CellName}";

    /// <inheritdoc/>
    public void Execute()
    {
        // Redo after an undo that could NOT delete the folder (because the user had edited it)
        // rewrites nothing: the cell is already there, and it is theirs now.
        if (!Directory.Exists(CellDir))
        {
            var result = MatchFlatten.Write(_parentDir, CellName, _cellSchematic, _design);
            _written = [.. result.Files.Select(p => (p, File.ReadAllText(p)))];
        }

        if (_replacement is not null)
        {
            _model.Components.Remove(_match);
            _model.Components.Add(_replacement);
            _model.NotifyChanged();
        }
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_replacement is not null)
        {
            _model.Components.Remove(_replacement);
            _model.Components.Add(_match);
            _model.NotifyChanged();
        }

        if (!Directory.Exists(CellDir)) return;

        if (!FolderIsExactlyWhatWeWrote())
        {
            _messages?.Warning(
                $"Undo restored {_match.InstanceName}, but the cell folder '{CellName}' has changed "
                + "since it was written and was left in place.");
            return;
        }

        MatchFlatten.TryDeleteFolder(CellDir);
    }

    private bool FolderIsExactlyWhatWeWrote()
    {
        if (_written.Count == 0) return false;

        try
        {
            var present = Directory.GetFiles(CellDir, "*", SearchOption.AllDirectories);
            if (present.Length != _written.Count) return false;

            foreach (var (path, content) in _written)
            {
                if (!File.Exists(path)) return false;
                if (!string.Equals(File.ReadAllText(path, Encoding.UTF8), content, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
