// brief-paa-8 R-paa8-2 — what a Feed-mode Phased Antenna Array presents at its pin F once the fast path has closed it: a one-port with the
// reflection Γ_F(f) the array, its channels and its beamformer give together. The circuit outside the array is solved with this in the
// array's place (PhasedArrayFastPath), so every wave the full solve would report at the circuit's ports comes out of the same engine.

using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine.AntennaArray;

/// <summary>R-paa8-2 — a one-port of reflection Γ(f) in a real Z0, its current a branch flowing into it (as an SnP block's).</summary>
internal sealed class ArrayFeedLoadModel(IReadOnlyDictionary<double, Complex> reflection, double z0) : ComponentModel
{
    public override int PortCount => 1;
    public override ModelKind Kind => ModelKind.Linear;

    /// <summary>The branch of the port, set by each <see cref="Stamp"/>.</summary>
    public int[] PortBranchIndices { get; } = [-1];

    /// <summary>The reflection, matched to the frequency to a part in 10⁹.</summary>
    public Complex At(double hz)
    {
        if (reflection.TryGetValue(hz, out var g)) return g;
        foreach (var (f, v) in reflection)
            if (Math.Abs(f - hz) <= 1e-9 * Math.Max(f, hz)) return v;
        throw new InvalidOperationException($"The array's closed reflection was not formed at {hz:G9} Hz.");
    }

    // b = Γ·a with a = (v + Z0·i)/(2√Z0), b = (v − Z0·i)/(2√Z0): (1 − Γ)·v − Z0·(1 + Γ)·i = 0, finite for every |Γ| ≤ 1.
    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        Complex g = At(Math.Abs(omega) / (2 * Math.PI));
        if (omega < 0) g = Complex.Conjugate(g);
        int br = mna.AddBranch();
        PortBranchIndices[0] = br;
        mna.AddBranchCurrent(br, c.Nodes[0], c.Nodes.Length > 1 ? c.Nodes[1] : 0);
        mna.AddConstraint(br, c.Nodes[0], 1 - g);
        if (c.Nodes.Length > 1 && c.Nodes[1] > 0) mna.AddConstraint(br, c.Nodes[1], g - 1);
        mna.AddBranchConstraint(br, br, -z0 * (1 + g));
    }
}
