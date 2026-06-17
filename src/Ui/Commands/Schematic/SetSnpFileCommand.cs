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

    public SetSnpFileCommand(SchematicEditModel model, EditableComponent comp, string newFile)
    {
        _model         = model;
        _fileParam     = comp.Parameters.First(p => p.Name == "File");
        _numPortsParam = comp.Parameters.FirstOrDefault(p => p.Name == "NumPorts");
        _oldFile       = _fileParam.Expression;
        _newFile       = newFile;
        _oldNumPorts   = _numPortsParam?.Expression ?? "";

        // Resolve relative path and sniff port count from file content.
        string probe = newFile;
        if (!System.IO.Path.IsPathRooted(probe) && model.SchematicDirectory is { } dir)
            probe = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, probe));
        _newNumPorts = TouchstoneIO.TryGetPortCount(probe, out int n, out _)
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
