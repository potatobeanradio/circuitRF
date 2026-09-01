using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Mutual inductance between two inductors (linear-engine §7).
/// Zero-port coupling element — no node connections of its own.
/// Adds off-diagonal −jωM terms to the existing inductor constraint rows.
///
/// Usage order (per frequency):
///   1. All non-mutual components must be stamped first (inductors get their
///      LastBranchIndex set).
///   2. Then Stamp is called for each mutual.
///
/// Resolve() must be called (by the Elaborator post-flatten) before any stamping.
///
/// <para><b>Either end may be an L, an SRLC or a PRLC</b> — anything implementing
/// <see cref="IInductiveBranch"/>, whose contract is that the branch it reports carries the
/// element's inductor current with a −jωL diagonal. That is what makes the −jωM off-diagonal the
/// correct stamp in all three cases: an SRLC's own R and C sit on the same diagonal and a PRLC's sit
/// in the admittance block, and neither is what this touches. Both read their inductance from the
/// same <c>L</c> parameter a plain inductor does, so the k ≥ 1 check below needs no special
/// case.</para>
/// </summary>
public sealed class MutualInductanceModel : ComponentModel
{
    public override int       PortCount => 0;
    public override ModelKind Kind      => ModelKind.Linear;

    private readonly string _ind1Name;
    private readonly string _ind2Name;

    private IInductiveBranch? _i1;
    private IInductiveBranch? _i2;
    private double _l1;
    private double _l2;

    // Deduplication: warn once per component instance, not once per frequency point.
    private bool _warnedOverCoupling;

    public MutualInductanceModel(string inductor1InstanceName, string inductor2InstanceName)
    {
        _ind1Name = inductor1InstanceName;
        _ind2Name = inductor2InstanceName;
    }

    /// <summary>
    /// Called by the Elaborator after all components are flattened.
    /// Resolves the inductor names to the models that carry those branches.
    /// The inductor names are relative to the mutual's parent scope:
    /// "L1" in a top-level mutual → instance path "L1";
    /// "L1" in a mutual inside cell "X1" → path "X1.L1".
    /// </summary>
    public void Resolve(ElaboratedNetlist netlist, ElaboratedComponent selfEc)
    {
        int lastDot    = selfEc.InstancePath.LastIndexOf('.');
        var parentPath = lastDot >= 0 ? selfEc.InstancePath[..lastDot] : "";

        string FullPath(string name) =>
            parentPath.Length > 0 ? $"{parentPath}.{name}" : name;

        var path1 = FullPath(_ind1Name);
        var path2 = FullPath(_ind2Name);

        var ec1 = netlist.Components.FirstOrDefault(c => c.InstancePath == path1)
                  ?? throw new InvalidOperationException(
                      $"Mutual '{selfEc.InstancePath}': inductor '{path1}' not found");
        var ec2 = netlist.Components.FirstOrDefault(c => c.InstancePath == path2)
                  ?? throw new InvalidOperationException(
                      $"Mutual '{selfEc.InstancePath}': inductor '{path2}' not found");

        _i1 = AsInductive(selfEc, ec1, path1);
        _i2 = AsInductive(selfEc, ec2, path2);

        // Cache L1, L2 for the k ≥ 1 coupling check at stamp time. All three referenceable kinds
        // spell their inductance "L", so there is one lookup rather than one per kind.
        _l1 = ec1.Parameters["L"].AsReal();
        _l2 = ec2.Parameters["L"].AsReal();
    }

    /// <summary>
    /// The referenced component as an inductive branch, or a refusal naming what it actually is.
    /// The message lists the kinds that DO work, because "is not an InductorModel" left a user who
    /// had pointed a Mutual at a resistor with nothing to do next.
    /// </summary>
    private static IInductiveBranch AsInductive(
        ElaboratedComponent selfEc, ElaboratedComponent target, string path)
        => target.Model as IInductiveBranch
           ?? throw new InvalidOperationException(
               $"Mutual '{selfEc.InstancePath}': '{path}' is a '{target.ComponentType}', which carries" +
               " no inductor branch to couple to. A Mutual can reference an L, an SRLC or a PRLC.");

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (_i1 is null || _i2 is null)
            throw new InvalidOperationException(
                $"MutualInductanceModel '{c.InstancePath}': Resolve() was not called");

        double m = c.Parameters["M"].AsReal();  // in Henry (unit already applied)

        // Over-coupling diagnostic (warn-and-continue — circuitRF research-tool philosophy).
        // Physical requirement: M² < L1·L2, equivalently |k| < 1.
        // k ≥ 1 is non-physical (or an ideal transformer, not yet modelled), but the stamp
        // proceeds. If the resulting inductance matrix is singular, InductanceRegularization
        // (IfNecessary default) rescues the solve. Warn once per instance.
        // NOTE: negative M (anti-phase coupling) is fully physical and is NOT warned on.
        if (!_warnedOverCoupling && m * m >= _l1 * _l2)
        {
            double k = Math.Abs(m) / Math.Sqrt(_l1 * _l2);
            Console.Error.WriteLine(
                $"[circuitRF] Mutual:{c.InstancePath}: coupling coefficient k={k:G4} ≥ 1 — " +
                $"non-physical (M²={m * m:G4}, L1·L2={_l1 * _l2:G4} H²); proceeding. " +
                "If the matrix becomes singular, InductanceRegularization will rescue the solve.");
            _warnedOverCoupling = true;
        }

        // Adds −jωM to the off-diagonal branch cross-terms in the D block.
        // This extends the inductor constraint rows:
        //   V_a1 − V_b1 − jωL1·i1 − jωM·i2 = 0
        //   V_a2 − V_b2 − jωM·i1 − jωL2·i2 = 0
        var term = new Complex(0.0, -omega * m);
        mna.AddBranchConstraint(_i1.LastBranchIndex, _i2.LastBranchIndex, term);
        mna.AddBranchConstraint(_i2.LastBranchIndex, _i1.LastBranchIndex, term);
    }
}
