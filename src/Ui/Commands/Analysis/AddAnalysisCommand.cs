using System;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Analysis;

/// <summary>Appends an analysis to the schematic's list. Undo removes it.</summary>
internal sealed class AddAnalysisCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly Core.Design.Analysis _analysis;
    private readonly int _insertAt;

    public string Description => $"Add {_analysis.Name}";

    public AddAnalysisCommand(SchematicEditModel model, Core.Design.Analysis analysis)
    {
        _model    = model;
        _analysis = analysis;
        _insertAt = model.Analyses.Count;
    }

    public void Execute()
    {
        _model.Analyses.Insert(_insertAt, _analysis);
        _model.NotifyChanged();
    }

    public void Undo()
    {
        _model.Analyses.Remove(_analysis);
        _model.NotifyChanged();
    }
}
