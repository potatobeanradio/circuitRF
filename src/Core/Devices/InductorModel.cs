using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Two-terminal inductor with optional series resistance R= and/or series capacitance C=.
/// Implements a series-RLC branch on a single Group-2 branch-current unknown.
///
/// Constraint: V_a − V_b − Z_total(ω)·i = 0
///   where Z_total = R + jωL + 1/(jωC)   (terms present only when the parameter is supplied).
///
/// DC behaviour (ω = 0):
///   Without C: Z_dc = R (pure resistor if R>0, exact short if R=0).
///   With C:    1/(jωC) → ∞ as ω→0. The branch is a DC OPEN: constraint is stamped as −i = 0
///              (forces branch current to zero; node voltages are unconstrained by this branch).
///
/// The optional R= parameter is required for physically-lossy inductors (Hero 1B carries R per
/// inductor). Stamping it correctly is a correctness requirement, not a workaround.
/// </summary>
public sealed class InductorModel : ComponentModel, IInductiveBranch
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    /// <summary>
    /// Branch index assigned on the most recent Stamp call.
    /// Set during each frequency pass; stable across frequencies for a fixed topology.
    /// Used by MutualInductanceModel to stamp off-diagonal coupling terms — through
    /// <see cref="IInductiveBranch"/>, which SRLC and PRLC implement too.
    /// </summary>
    public int LastBranchIndex { get; private set; } = -1;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double l = c.Parameters["L"].AsReal();
        // R= and C= are optional. Units already applied by the elaborator.
        double r      = c.Parameters.TryGetValue("R", out var rv) ? rv.AsReal() : 0.0;
        bool   hasC   = c.Parameters.TryGetValue("C", out var cv);
        double capVal = hasC ? cv.AsReal() : 0.0;

        int br = mna.AddBranch();
        LastBranchIndex = br;

        if (hasC && omega == 0.0)
        {
            // DC with series capacitor: the branch is a DC open circuit.
            // The capacitive impedance 1/(jωC) diverges at DC, forcing branch current to zero.
            // Stamp: KCL column (so the branch column is non-zero) + constraint −i = 0.
            // Node voltages are unconstrained by this branch (correct for an open circuit).
            mna.AddBranchCurrent(br, c.Nodes[0], c.Nodes[1]); // KCL: current i flows a→b (=0)
            mna.AddBranchConstraint(br, br, new Complex(-1.0, 0.0)); // −i = 0 → i = 0
            return;
        }

        // Standard case: AC (ω > 0) or DC without C.
        // KCL: branch current i flows from Nodes[0] to Nodes[1].
        mna.AddBranchCurrent(br, c.Nodes[0], c.Nodes[1]);

        // Constraint: V_a − V_b − Z_total·i = 0
        mna.AddConstraint(br, c.Nodes[0], +Complex.One);
        mna.AddConstraint(br, c.Nodes[1], -Complex.One);

        // Diagonal term −Z_total = −R − jωL − 1/(jωC) = −R + j(1/(ωC) − ωL)
        // The capacitive contribution −1/(jωC) = j/(ωC) → adds +1/(ωC) to the imaginary part.
        double realPart = -r;
        double imagPart = -omega * l;
        if (hasC && omega != 0.0)
            imagPart += 1.0 / (omega * capVal);  // −1/(jωC) = +j/(ωC)

        var diag = new Complex(realPart, imagPart);
        if (diag != Complex.Zero)
            mna.AddBranchConstraint(br, br, diag);
    }
}
