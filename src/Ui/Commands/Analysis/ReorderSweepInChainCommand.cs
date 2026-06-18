using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Analysis;

/// <summary>Reorders a parametric sweep within its chain by one step (inner↔outer), re-linking
/// InnerAnalysisName for the affected sweeps. Undo restores the original sweep instances + order.</summary>
internal sealed class ReorderSweepInChainCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly ParametricSweepAnalysis _sweep;
    private readonly bool _moveInner;
    private List<(int Index, Core.Design.Analysis Old)>? _undo;

    public string Description => _moveInner ? "Move sweep inward" : "Move sweep outward";

    public ReorderSweepInChainCommand(SchematicEditModel model, ParametricSweepAnalysis sweep, bool moveInner)
    { _model = model; _sweep = sweep; _moveInner = moveInner; }

    public void Execute()
    {
        var list = _model.Analyses;
        int idx = list.IndexOf(_sweep);
        if (idx < 0) return;

        // Locate the chain block: base (first non-sweep walking back), then its contiguous sweeps.
        int b = idx;
        while (b > 0 && list[b] is ParametricSweepAnalysis) b--;
        if (list[b] is ParametricSweepAnalysis) return;          // no base found — bail
        int first = b + 1, last = first;
        while (last + 1 < list.Count && list[last + 1] is ParametricSweepAnalysis) last++;

        // Sequence inner→outer (== model order within the block).
        var seq = new List<ParametricSweepAnalysis>();
        for (int i = first; i <= last; i++) seq.Add((ParametricSweepAnalysis)list[i]);

        int k = seq.IndexOf(_sweep);
        int target = _moveInner ? k - 1 : k + 1;
        if (k < 0 || target < 0 || target >= seq.Count) return;  // at an edge — no-op

        (seq[k], seq[target]) = (seq[target], seq[k]);

        // Snapshot for undo, then relink bottom-up and write back.
        _undo = new List<(int, Core.Design.Analysis)>();
        string innerName = list[b].Name;                         // the base
        for (int i = 0; i < seq.Count; i++)
        {
            _undo.Add((first + i, list[first + i]));
            list[first + i] = Relink(seq[i], innerName);
            innerName = list[first + i].Name;
        }
        _model.NotifyChanged();
    }

    public void Undo()
    {
        if (_undo is null) return;
        foreach (var (i, old) in _undo) _model.Analyses[i] = old;
        _model.NotifyChanged();
    }

    private static ParametricSweepAnalysis Relink(ParametricSweepAnalysis s, string inner)
        => s.Spec is { } spec
            ? new ParametricSweepAnalysis(s.Name, s.SweepVarName, spec, inner)        { Enabled = s.Enabled }
            : new ParametricSweepAnalysis(s.Name, s.SweepVarName, s.SweepValues, inner) { Enabled = s.Enabled };
}
