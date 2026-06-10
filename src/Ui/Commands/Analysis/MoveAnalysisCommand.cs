using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Analysis;

/// <summary>Moves an analysis one position up or down in the list. Undo reverses the move.</summary>
internal sealed class MoveAnalysisCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly Core.Design.Analysis _analysis;
    private readonly bool _moveUp;

    public string Description => _moveUp ? $"Move {_analysis.Name} up" : $"Move {_analysis.Name} down";

    public MoveAnalysisCommand(SchematicEditModel model, Core.Design.Analysis analysis, bool moveUp)
    {
        _model    = model;
        _analysis = analysis;
        _moveUp   = moveUp;
    }

    public void Execute() => Swap(_moveUp);
    public void Undo()    => Swap(!_moveUp);

    private void Swap(bool up)
    {
        int idx = _model.Analyses.IndexOf(_analysis);
        if (idx < 0) return;
        int target = up ? idx - 1 : idx + 1;
        if ((uint)target >= (uint)_model.Analyses.Count) return;
        (_model.Analyses[idx], _model.Analyses[target]) = (_model.Analyses[target], _model.Analyses[idx]);
        _model.NotifyChanged();
    }
}
