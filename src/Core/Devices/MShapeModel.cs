using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Drawn metal, MSHAPE (brief-pcd-0 §4.3, PCD-3): one terminal per <c>PinX[k]</c>. With <c>Model=open</c> or <c>none</c> it
/// stamps nothing, so its terminals are open in a circuit simulation; with <c>Model=short</c> every terminal is one node, an
/// ideal junction or star pad (a 0 V branch from pin 1 to each other pin). Whether the instance counts as having a circuit
/// model is its <c>Model</c> word, which the EM switch reads (EmModelSwitch.WithoutCircuitModel), not this.
/// </summary>
public sealed class MShapeModel(int pinCount, bool shorted = false) : ComponentModel
{
    public override int       PortCount => pinCount;
    public override ModelKind Kind      => ModelKind.Linear;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (!shorted) return;
        for (int k = 1; k < c.Nodes.Length; k++)
        {
            int br = mna.AddBranch();
            mna.AddBranchCurrent(br, c.Nodes[0], c.Nodes[k]);
            mna.AddConstraint(br, c.Nodes[0], Complex.One);
            mna.AddConstraint(br, c.Nodes[k], -Complex.One);
        }
    }

    /// <summary>The pin count an instance's keys state: k = 1, 2, … while <c>PinX[k]</c> is one of them.</summary>
    public static int PinCountOf(IEnumerable<string> keys)
    {
        var set = keys as IReadOnlySet<string> ?? keys.ToHashSet(StringComparer.Ordinal);
        int k = 0;
        while (set.Contains($"PinX[{k + 1}]")) k++;
        return k;
    }
}
