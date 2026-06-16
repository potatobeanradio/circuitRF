using System.Collections.Generic;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Analysis;

/// <summary>
/// Appends a list of analyses to the schematic (used when building a parametric-sweep chain).
/// Undo removes all of them.
/// </summary>
internal sealed class AddAnalysesCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly IReadOnlyList<Core.Design.Analysis> _analyses;
    private readonly int _insertAt;

    public string Description => $"Add {_analyses[^1].Name}";

    public AddAnalysesCommand(SchematicEditModel model, IReadOnlyList<Core.Design.Analysis> analyses)
    {
        _model     = model;
        _analyses  = analyses;
        _insertAt  = model.Analyses.Count;
    }

    public void Execute()
    {
        for (int i = 0; i < _analyses.Count; i++)
            _model.Analyses.Insert(_insertAt + i, _analyses[i]);
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var a in _analyses)
            _model.Analyses.Remove(a);
        _model.NotifyChanged();
    }
}
