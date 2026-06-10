using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Analysis;

/// <summary>Removes an analysis from the list. Undo re-inserts at the original index.</summary>
internal sealed class RemoveAnalysisCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly Core.Design.Analysis _analysis;
    private int _savedIndex;

    public string Description => $"Remove analysis {_analysis.Name}";

    public RemoveAnalysisCommand(SchematicEditModel model, Core.Design.Analysis analysis)
    {
        _model    = model;
        _analysis = analysis;
    }

    public void Execute()
    {
        _savedIndex = _model.Analyses.IndexOf(_analysis);
        if (_savedIndex >= 0)
            _model.Analyses.RemoveAt(_savedIndex);
        _model.NotifyChanged();
    }

    public void Undo()
    {
        int insertAt = Math.Clamp(_savedIndex, 0, _model.Analyses.Count);
        _model.Analyses.Insert(insertAt, _analysis);
        _model.NotifyChanged();
    }
}
