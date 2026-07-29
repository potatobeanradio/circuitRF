using System.Numerics;
using CircuitRF.Core;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>Lightweight IMnaContext test double that records every AddBlockAdmittance/AddAdmittance
/// call into a dense dictionary, keyed by (row,col) — enough to directly compare two stamps'
/// resulting admittance entries without a full MnaSystem/solve. Mirrors the shape of
/// Engine.Tests.HarmonicBalance.P1ToneTests's own CaptureMnaContext. Also records branch-current
/// Z-parameter stamps (AddBranchCurrent/AddConstraint/AddBranchConstraint — the pattern
/// <c>ZPortModel</c>/<c>MicrostripBendModel</c> use for a direct Z-matrix) into their own
/// dictionaries, so a model using EITHER stamping style can be exercised without a full
/// MnaSystem/solve.</summary>
public sealed class CapturingMnaContext : IMnaContext
{
    public Dictionary<(int Row, int Col), Complex> Entries { get; } = new();
    public Dictionary<(int Branch, int Node), Complex> NodeConstraints { get; } = new();
    public Dictionary<(int Branch, int OtherBranch), Complex> BranchConstraints { get; } = new();
    public List<(int Branch, int NodeFrom, int NodeTo)> BranchCurrents { get; } = new();
    private int _branchCount;

    public int AddBranch() => _branchCount++;

    public void AddAdmittance(int nodeA, int nodeB, Complex y)
    {
        Add(nodeA, nodeA, y);
        Add(nodeA, nodeB, -y);
        Add(nodeB, nodeA, -y);
        Add(nodeB, nodeB, y);
    }

    public void AddBlockAdmittance(int rowNode, int colNode, Complex y) => Add(rowNode, colNode, y);

    public void AddBranchCurrent(int branch, int nodeFrom, int nodeTo)
        => BranchCurrents.Add((branch, nodeFrom, nodeTo));

    public void AddConstraint(int branch, int node, Complex coeff)
    {
        var key = (branch, node);
        NodeConstraints[key] = NodeConstraints.TryGetValue(key, out var existing) ? existing + coeff : coeff;
    }

    public void AddNodeBranchCoupling(int node, int branch, Complex coeff) { }

    public void AddBranchConstraint(int branch1, int branch2, Complex coeff)
    {
        var key = (branch1, branch2);
        BranchConstraints[key] = BranchConstraints.TryGetValue(key, out var existing) ? existing + coeff : coeff;
    }

    public void AddCurrentInjection(int node, Complex i) { }
    public void AddSourceValue(int branch, Complex v) { }

    private void Add(int row, int col, Complex y)
    {
        if (row == 0 || col == 0) return; // ground rows/cols are dropped, matching the real engine
        var key = (row, col);
        Entries[key] = Entries.TryGetValue(key, out var existing) ? existing + y : y;
    }
}
