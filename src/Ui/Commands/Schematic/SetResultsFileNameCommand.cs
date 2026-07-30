using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Sets the schematic-level results-file override (SchematicEditModel.ResultsFileName).
/// Undoable; fires NotifyChanged so the document is marked dirty and round-trips through .csch.
/// </summary>
internal sealed class SetResultsFileNameCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly string?            _oldValue;
    private readonly string?            _newValue;

    public string Description => "Set results file name";

    public SetResultsFileNameCommand(SchematicEditModel model, string? newValue)
    {
        _model    = model;
        _oldValue = model.ResultsFileName;
        _newValue = newValue;
    }

    public void Execute() => Apply(_newValue);
    public void Undo()    => Apply(_oldValue);

    private void Apply(string? value)
    {
        _model.ResultsFileName = value;
        _model.NotifyChanged();
    }
}
