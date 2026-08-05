using System.Numerics;

namespace CircuitRF.Core.Elaboration;

/// <summary>
/// The device multiplier — how many identical copies of a component sit in parallel — applied ONCE,
/// here, rather than by each model.
///
/// <para><b>Why it cannot live in the models.</b> It means the same thing for every device: a
/// resistor's conductance, a capacitor's admittance, a diode's current, charge and both Jacobians
/// all scale by the same factor for the same reason. Written per model it would be a parameter every
/// model has to remember to read, a rule every future model has to be told about, and — because
/// forgetting it produces a working circuit with the wrong number of devices in it — a silent
/// failure the first time somebody forgets.</para>
///
/// <para><b>What it scales, generically.</b> Admittance contributions and current injections. That
/// is the whole of what "the same thing again, in parallel" does to a stamp, and stating it this way
/// needs no list of which model is which.</para>
///
/// <para><b>What it REFUSES, and why that is the safe direction.</b> Anything that allocates a
/// branch-current unknown. Two ideal voltage sources in parallel is not a circuit — they constrain
/// the same node pair twice, and the honest answers are "one source" or "a singular matrix", neither
/// of which is what a multiplier means. The same goes for every other Group-2 contribution. Refusing
/// by NAME at the moment the branch is asked for catches every such model, present and future,
/// without a list of them.</para>
/// </summary>
public sealed class MultipliedMnaContext(IMnaContext inner, double multiplier, string instancePath)
    : IMnaContext
{
    private readonly IMnaContext _inner       = inner;
    private readonly double      _m           = multiplier;
    private readonly string      _instancePath = instancePath;

    // ── Group 1: scaled ───────────────────────────────────────────────────────

    public void AddAdmittance(int nodeA, int nodeB, Complex y)
        => _inner.AddAdmittance(nodeA, nodeB, y * _m);

    public void AddBlockAdmittance(int rowNode, int colNode, Complex y)
        => _inner.AddBlockAdmittance(rowNode, colNode, y * _m);

    public void AddCurrentInjection(int node, Complex j)
        => _inner.AddCurrentInjection(node, j * _m);

    // ── Group 2: refused ──────────────────────────────────────────────────────

    public int AddBranch()                                                   => throw Refuse();
    public void AddBranchCurrent(int b, int from, int to)                    => throw Refuse();
    public void AddConstraint(int b, int node, Complex coeff)                => throw Refuse();
    public void AddNodeBranchCoupling(int node, int b, Complex coeff)        => throw Refuse();
    public void AddBranchConstraint(int b, int other, Complex coeff)         => throw Refuse();
    public void AddSourceValue(int b, Complex value)                         => throw Refuse();

    private InvalidOperationException Refuse()
        => new($"'{_instancePath}' states a device multiplier (m={_m:G6}), but this kind of " +
                "component contributes a branch-current unknown rather than an admittance — and " +
                "several of those in parallel is not a circuit, it is the same constraint written " +
                "more than once. Remove the multiplier, or place the copies explicitly.");
}
