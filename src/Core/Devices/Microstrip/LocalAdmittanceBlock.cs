using System.Numerics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// A dense nodal admittance block on local nodes 1..N (0 is ground, dropped as the engine's own matrix
/// drops it). Only the admittance groups are meaningful here: nothing assembled into it allocates a
/// branch. Shared by a junction's arms (<see cref="JunctionArms"/>) and the planned passives
/// (<see cref="MicrostripPlanModel"/>): each assembles its sections here, eliminates the internal nodes
/// and stamps the pins' block alone.
/// </summary>
internal sealed class LocalAdmittanceBlock(int size) : IMnaContext
{
    private readonly Complex[,] _y = new Complex[size, size];
    private int _size = size;

    public Complex this[int r, int c] => _y[r, c];

    public void AddAdmittance(int nodeA, int nodeB, Complex y)
    {
        int a = nodeA - 1, b = nodeB - 1;
        if (a >= 0) _y[a, a] += y;
        if (b >= 0) _y[b, b] += y;
        if (a >= 0 && b >= 0) { _y[a, b] -= y; _y[b, a] -= y; }
    }

    public void AddBlockAdmittance(int rowNode, int colNode, Complex y)
    {
        int r = rowNode - 1, c = colNode - 1;
        if (r >= 0 && c >= 0) _y[r, c] += y;
    }

    /// <summary>Kron-reduces away every node at index ≥ <paramref name="keep"/>, last first; the
    /// remaining leading block is the port admittance seen with those nodes left floating.</summary>
    public void EliminateFrom(int keep)
    {
        for (int k = _size - 1; k >= keep; k--)
        {
            var pivot = _y[k, k];
            for (int i = 0; i < k; i++)
            {
                if (_y[i, k] == Complex.Zero) continue;
                var f = _y[i, k] / pivot;
                for (int j = 0; j < k; j++)
                    _y[i, j] -= f * _y[k, j];
            }
        }
        _size = keep;
    }

    /// <summary>Eliminates every node past the first <paramref name="pins"/>.Count and adds the reduced
    /// block onto those pins of <paramref name="mna"/>.</summary>
    public void ReduceOnto(IMnaContext mna, IReadOnlyList<int> pins)
    {
        int p = pins.Count;
        EliminateFrom(p);
        for (int r = 0; r < p; r++)
            for (int c = 0; c < p; c++)
                mna.AddBlockAdmittance(pins[r], pins[c], _y[r, c]);
    }

    public int AddBranch() => throw Unsupported();
    public void AddBranchCurrent(int branch, int nodeFrom, int nodeTo) => throw Unsupported();
    public void AddConstraint(int branch, int node, Complex coeff) => throw Unsupported();
    public void AddNodeBranchCoupling(int node, int branch, Complex coeff) => throw Unsupported();
    public void AddBranchConstraint(int branch, int otherBranch, Complex coeff) => throw Unsupported();
    public void AddCurrentInjection(int node, Complex j) => throw Unsupported();
    public void AddSourceValue(int branch, Complex value) => throw Unsupported();

    private static NotSupportedException Unsupported()
        => new("A local admittance block holds admittances only.");
}
