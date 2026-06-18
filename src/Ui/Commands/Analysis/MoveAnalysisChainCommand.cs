using System.Collections.Generic;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Analysis;

/// <summary>Moves the whole chain (base + its contiguous parametric sweeps) containing
/// <paramref name="member"/> up or down past the adjacent chain. Undo reverses it.</summary>
internal sealed class MoveAnalysisChainCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly Core.Design.Analysis _member;
    private readonly bool _moveUp;

    public string Description => _moveUp ? "Move analysis up" : "Move analysis down";

    public MoveAnalysisChainCommand(SchematicEditModel model, Core.Design.Analysis member, bool moveUp)
    {
        _model   = model;
        _member  = member;
        _moveUp  = moveUp;
    }

    public void Execute() => Move(_moveUp);
    public void Undo()    => Move(!_moveUp);

    private void Move(bool up)
    {
        var list = _model.Analyses;
        int idx = list.IndexOf(_member);
        if (idx < 0) return;

        // Block containing _member: walk back to its base, forward over its sweeps.
        int start = idx;
        while (start > 0 && list[start] is ParametricSweepAnalysis) start--;
        int end = start;
        while (end + 1 < list.Count && list[end + 1] is ParametricSweepAnalysis) end++;

        if (up)
        {
            if (start == 0) return;
            int prevEnd = start - 1, prevStart = prevEnd;
            while (prevStart > 0 && list[prevStart] is ParametricSweepAnalysis) prevStart--;
            MoveRange(list, start, end, prevStart);
        }
        else
        {
            if (end + 1 >= list.Count) return;
            int nextStart = end + 1, nextEnd = nextStart;
            while (nextEnd + 1 < list.Count && list[nextEnd + 1] is ParametricSweepAnalysis) nextEnd++;
            MoveRange(list, nextStart, nextEnd, start);
        }
        _model.NotifyChanged();
    }

    private static void MoveRange(IList<Core.Design.Analysis> list, int from, int to, int insertAt)
    {
        var block = new List<Core.Design.Analysis>();
        for (int i = from; i <= to; i++) block.Add(list[i]);
        for (int i = to; i >= from; i--) list.RemoveAt(i);
        // insertAt was computed before removal; it is always < from here, so it is unaffected.
        for (int i = 0; i < block.Count; i++) list.Insert(insertAt + i, block[i]);
    }
}
