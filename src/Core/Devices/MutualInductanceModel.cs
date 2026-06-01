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
/// </summary>
public sealed class MutualInductanceModel : ComponentModel
{
    public override int       PortCount => 0;
    public override ModelKind Kind      => ModelKind.Linear;

    private readonly string _ind1Name;
    private readonly string _ind2Name;

    private InductorModel? _i1;
    private InductorModel? _i2;

    public MutualInductanceModel(string inductor1InstanceName, string inductor2InstanceName)
    {
        _ind1Name = inductor1InstanceName;
        _ind2Name = inductor2InstanceName;
    }

    /// <summary>
    /// Called by the Elaborator after all components are flattened.
    /// Resolves the inductor names to InductorModel instances.
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

        _i1 = (ec1.Model as InductorModel)
              ?? throw new InvalidOperationException(
                  $"Mutual '{selfEc.InstancePath}': '{path1}' is not an InductorModel");
        _i2 = (ec2.Model as InductorModel)
              ?? throw new InvalidOperationException(
                  $"Mutual '{selfEc.InstancePath}': '{path2}' is not an InductorModel");
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (_i1 is null || _i2 is null)
            throw new InvalidOperationException(
                $"MutualInductanceModel '{c.InstancePath}': Resolve() was not called");

        double m = c.Parameters["M"].AsReal();  // in Henry (unit already applied)

        // Adds −jωM to the off-diagonal branch cross-terms in the D block.
        // This extends the inductor constraint rows:
        //   V_a1 − V_b1 − jωL1·i1 − jωM·i2 = 0
        //   V_a2 − V_b2 − jωM·i1 − jωL2·i2 = 0
        var term = new Complex(0.0, -omega * m);
        mna.AddBranchConstraint(_i1.LastBranchIndex, _i2.LastBranchIndex, term);
        mna.AddBranchConstraint(_i2.LastBranchIndex, _i1.LastBranchIndex, term);
    }
}
