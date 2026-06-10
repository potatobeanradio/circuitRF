using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Analysis;

/// <summary>Replaces one analysis in the schematic's list with another. Undo restores the original.</summary>
internal sealed class EditAnalysisCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly Core.Design.Analysis _original;
    private readonly Core.Design.Analysis _replacement;
    private readonly int _index;

    public string Description => $"Edit {_original.Name}";

    public EditAnalysisCommand(SchematicEditModel model, Core.Design.Analysis original, Core.Design.Analysis replacement)
    {
        _model       = model;
        _original    = original;
        _replacement = replacement;
        _index       = model.Analyses.IndexOf(original);
    }

    public void Execute()
    {
        if (_index >= 0) _model.Analyses[_index] = _replacement;
        _model.NotifyChanged();
    }

    public void Undo()
    {
        if (_index >= 0) _model.Analyses[_index] = _original;
        _model.NotifyChanged();
    }
}
