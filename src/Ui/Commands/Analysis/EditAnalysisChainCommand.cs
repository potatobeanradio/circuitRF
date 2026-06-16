using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Analysis;

/// <summary>
/// Replaces an analysis chain (inner + all wrapping sweeps) with a new chain.
/// Undo restores the original chain at the original position.
/// </summary>
internal sealed class EditAnalysisChainCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly IReadOnlyList<Core.Design.Analysis> _oldChain;
    private readonly IReadOnlyList<Core.Design.Analysis> _newChain;
    private readonly int _insertAt;

    public string Description => $"Edit {_newChain[^1].Name}";

    public EditAnalysisChainCommand(
        SchematicEditModel model,
        IReadOnlyList<Core.Design.Analysis> oldChain,
        IReadOnlyList<Core.Design.Analysis> newChain)
    {
        _model    = model;
        _oldChain = oldChain;
        _newChain = newChain;
        // Insert position = index of the first old-chain member.
        int idx = oldChain.Count > 0 ? model.Analyses.IndexOf(oldChain[0]) : model.Analyses.Count;
        _insertAt = idx < 0 ? model.Analyses.Count : idx;
    }

    public void Execute()
    {
        // Remove all old chain members (from list, not relying on order).
        foreach (var a in _oldChain)
            _model.Analyses.Remove(a);
        // Insert new chain at the original position.
        for (int i = 0; i < _newChain.Count; i++)
            _model.Analyses.Insert(Math.Min(_insertAt + i, _model.Analyses.Count), _newChain[i]);
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var a in _newChain)
            _model.Analyses.Remove(a);
        for (int i = 0; i < _oldChain.Count; i++)
            _model.Analyses.Insert(Math.Min(_insertAt + i, _model.Analyses.Count), _oldChain[i]);
        _model.NotifyChanged();
    }
}
