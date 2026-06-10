using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Analysis;

/// <summary>
/// Toggles the Enabled state of one analysis in the list.
/// Undo restores the previous state.
/// </summary>
internal sealed class EnableAnalysisCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly Core.Design.Analysis _analysis;
    private readonly bool _newEnabled;

    public string Description => _newEnabled ? $"Enable {_analysis.Name}" : $"Disable {_analysis.Name}";

    public EnableAnalysisCommand(SchematicEditModel model, Core.Design.Analysis analysis, bool enabled)
    {
        _model    = model;
        _analysis = analysis;
        _newEnabled = enabled;
    }

    public void Execute()
    {
        _analysis.Enabled = _newEnabled;
        _model.NotifyChanged();
    }

    public void Undo()
    {
        _analysis.Enabled = !_newEnabled;
        _model.NotifyChanged();
    }
}
