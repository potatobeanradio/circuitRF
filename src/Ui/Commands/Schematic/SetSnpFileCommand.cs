using System.Linq;
using CircuitRF.Ui.Schematic;
using RfCore;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Sets the File parameter on an SnP component and atomically re-sniffs NumPorts
/// so the dynamic symbol redraws at the correct port count. Undo restores both fields.
/// </summary>
internal sealed class SetSnpFileCommand : IUiCommand
{
    private readonly SchematicEditModel  _model;
    private readonly EditableParameter   _fileParam;
    private readonly EditableParameter?  _numPortsParam;
    private readonly string _newFile,     _oldFile;
    private readonly string _newNumPorts, _oldNumPorts;

    public string Description => "Set SnP file";

    /// <summary>
    /// <paramref name="workspaceRoot"/> is the base a stored <c>File</c> is relative to — the same
    /// one <see cref="SnpPathPolicy.ToStored"/> wrote it against and the same one the elaborator
    /// resolves it against at Run. <b>It is a parameter rather than something derived here</b>
    /// because the run's own root is <c>WorkspaceViewModel.CurrentWorkspaceRoot</c>, which a
    /// command holding only a <see cref="SchematicEditModel"/> cannot see; deriving it by walking
    /// up to the nearest <c>.cws</c> would give a different answer for a foreign document.
    ///
    /// <para>Null is allowed and means "no workspace" — a scratch schematic, where
    /// <see cref="SnpPathPolicy.Resolve"/> falls back to the schematic's own directory.</para>
    /// </summary>
    public SetSnpFileCommand(
        SchematicEditModel model, EditableComponent comp, string newFile, string? workspaceRoot)
    {
        _model         = model;
        _fileParam     = comp.Parameters.First(p => p.Name == "File");
        _numPortsParam = comp.Parameters.FirstOrDefault(p => p.Name == "NumPorts");
        _oldFile       = _fileParam.Expression;
        _newFile       = newFile;
        _oldNumPorts   = _numPortsParam?.Expression ?? "";

        // Sniff the port count from the file's own content. An unresolvable or unreadable path
        // leaves NumPorts exactly as it was — this command never invents one, because a guessed
        // port count draws pins that are not there.
        string? probe = SnpPathPolicy.Resolve(newFile, workspaceRoot, model.SchematicDirectory);
        _newNumPorts = probe is not null && TouchstoneIO.TryGetPortCount(probe, out int n, out _)
            ? n.ToString() : _oldNumPorts;
    }

    public void Execute()
    {
        _fileParam.Expression = _newFile;
        if (_numPortsParam is not null) _numPortsParam.Expression = _newNumPorts;
        _model.NotifyChanged();
    }

    public void Undo()
    {
        _fileParam.Expression = _oldFile;
        if (_numPortsParam is not null) _numPortsParam.Expression = _oldNumPorts;
        _model.NotifyChanged();
    }
}
