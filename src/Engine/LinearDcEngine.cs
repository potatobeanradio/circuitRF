// The ω = 0 LINEAR solve, on the sparse path every other analysis already uses
// (docs/design/railrf.md §4.4; docs/sonnet-briefs/brief-railrf-5-dc-solve.md R-rail5-1).
//
// ── WHY THIS IS NOT A SECOND SOLVER ────────────────────────────────────────────────────────────
//
// §4.4: "Assemble one sparse MNA system and solve via CSparse's LU — the same numerical layer every
// other circuitRF analysis uses. At ω = 0 the system is real, symmetric and positive-definite and
// THE SAME CODE PATH SOLVES IT FAR FASTER." That sentence is this file. Nothing here implements a
// matrix, an ordering or a factorisation: MnaSystem assembles, AMD orders and CSparse's SparseLU
// factorises, exactly as they do for an S-parameter point.
//
// ── WHY NonlinearDcEngine IS NOT WHAT railRF CALLS ─────────────────────────────────────────────
//
// It is the right engine for a circuit and the wrong one for a mesh, for one reason that is about
// storage rather than about physics: it materialises the augmented system as a DENSE double[n,n]
// before assembling a CSC out of it, which is what makes its thermal measurement and its
// per-unknown residual report cheap to write. A PDN mesh is tens of thousands of nodes and the
// extractor's own ceiling is 400,000 cells — 1.28 TB at that size — so the dense array is the whole
// of the difference. Newton is the other half: a resistive mesh is LINEAR, so there is one
// assembly, one factorisation and one back-substitution, and the iteration, the source stepping and
// the convergence trace have nothing to do.
//
// So this is NonlinearDcEngine's linear pass with the dense array removed, and it deliberately
// keeps every convention that pass sets: Port and Term are inert at DC, a gmin shunt sits on every
// voltage row, mutual inductance is stamped after everything else, and a branch current flows from
// its element's FIRST node to its SECOND.

using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine;

/// <summary>
/// The whole solution vector of an ω = 0 linear solve: every node voltage, every branch current,
/// and which component owns which branch.
///
/// <para><b>Branch currents are here because they cannot be derived.</b> A resistor's current is
/// <c>ΔV/R</c> and needs nothing; an ideal source's current is a solution UNKNOWN, and it is
/// precisely the number "which source carried what share of the load" is made of
/// (railrf.md §2.2). A result that reported only node voltages could not answer it at all.</para>
/// </summary>
public sealed class LinearDcSolution
{
    /// <summary>Non-ground node voltages. Index <c>i</c> is circuit node <c>i + 1</c>, matching
    /// <see cref="NonlinearDcEngine.DcResult.NodeVoltages"/>.</summary>
    public required double[] NodeVoltages { get; init; }

    /// <summary>Branch-current unknowns, in allocation order. Positive current flows from the
    /// owning element's FIRST node to its SECOND — the engine's fixed convention.</summary>
    public required double[] BranchCurrents { get; init; }

    /// <summary>Index into <see cref="ElaboratedNetlist.Components"/> → the FIRST branch that
    /// component allocated, as an index into <see cref="BranchCurrents"/>. Absent for every
    /// component that owns no branch.</summary>
    public required IReadOnlyDictionary<int, int> ComponentBranches { get; init; }

    /// <summary>The voltage at a circuit node, with ground answering exactly zero.</summary>
    public double VoltageAt(int node) =>
        node <= 0 || node - 1 >= NodeVoltages.Length ? 0.0 : NodeVoltages[node - 1];

    /// <summary>The current through <paramref name="componentIndex"/>'s own branch, or null where
    /// that component owns none.</summary>
    public double? BranchCurrentOf(int componentIndex) =>
        ComponentBranches.TryGetValue(componentIndex, out int b) && b < BranchCurrents.Length
            ? BranchCurrents[b] : null;

    /// <summary>
    /// The same solution in the shape <see cref="DcResultPacker"/> packs, so an ω = 0 linear run
    /// yields the <c>DataSet</c> every other analysis yields and nothing downstream has to learn a
    /// second result type.
    /// </summary>
    public NonlinearDcEngine.DcResult AsDcResult() =>
        new(NodeVoltages, converged: true, iters: 1, residual: 0.0,
            new NonlinearDcEngine.ConvergenceTrace(), new Dictionary<string, double>());
}

/// <summary>What one linear DC solve produced, or why nothing was produced.</summary>
/// <param name="Solution">The answer. Null exactly when <paramref name="Refusal"/> is not.</param>
/// <param name="Refusal">Why there is no answer, as a sentence a caller can print.</param>
public sealed record LinearDcResult(LinearDcSolution? Solution, string? Refusal);

/// <summary>A linear netlist at ω = 0, solved once on the sparse path.</summary>
public static class LinearDcEngine
{
    /// <summary>
    /// The shunt conductance to ground on every voltage row, in siemens — the same
    /// <see cref="NonlinearDcEngine.DefaultGmin"/> the nonlinear pass uses.
    ///
    /// <para><b>It is what lets a decoupling capacitor be in the netlist.</b> A capacitor is an
    /// EXACT open at ω = 0 (railrf.md §2.8, and brief 3's R-rail3-4 says it stays in the netlist
    /// anyway), so a node reached only through one has no equation of its own and the factorisation
    /// reports a zero row. gmin gives it one — it settles at zero volts, which is the honest answer
    /// for copper with no DC path — and at 1e-12 S it is 4e-7 S across a 400,000-cell mesh, against
    /// milliohms of copper.</para>
    /// </summary>
    public const double DefaultGmin = NonlinearDcEngine.DefaultGmin;

    /// <summary>Solves <paramref name="netlist"/> at ω = 0, or refuses and says why.</summary>
    public static LinearDcResult Run(ElaboratedNetlist netlist, double gmin = DefaultGmin)
    {
        int nodeCount = netlist.Nodes.Count - 1;
        if (nodeCount <= 0)
            return new LinearDcResult(null, "The netlist has no node other than ground, so there is nothing to solve.");

        foreach (var ec in netlist.Components)
            if (ec.Model.Kind == ModelKind.Nonlinear)
                return new LinearDcResult(null,
                    $"'{ec.InstancePath}' is a {ec.ComponentType}, which is a nonlinear device. This solve " +
                    "is the linear one — run the operating point through NonlinearDcEngine instead.");

        var mna = new MnaSystem(nodeCount);
        mna.Reset();

        var componentBranches = new Dictionary<int, int>();

        // Two passes, mutual inductance last, exactly as SParameterEngine stamps: a mutual reads the
        // branch index its coupled inductors allocated, so it cannot be stamped before them.
        for (int pass = 0; pass < 2; pass++)
        for (int i = 0; i < netlist.Components.Count; i++)
        {
            var ec = netlist.Components[i];
            bool mutual = ec.Model is MutualInductanceModel;
            if (mutual != (pass == 1)) continue;

            // A Term or a Port is a DRIVEN port for S-parameter analysis and a 0 V source anywhere
            // else — which at DC would short the very drop being measured. Inert here, as it is in
            // NonlinearDcEngine's own linear pass.
            if (ec.Model is PortModel or TermModel) continue;

            int before = mna.BranchCount;
            try { ec.Stamp(mna, omega: 0.0); }
            catch (NotImplementedException) { }
            if (mna.BranchCount > before) componentBranches[i] = before;

            netlist.DrainModelWarnings(ec.Model);
        }

        if (gmin > 0)
            for (int n = 1; n <= nodeCount; n++)
                mna.AddAdmittance(n, 0, new Complex(gmin, 0));

        Complex[] rhs;
        var x = new Complex[mna.Size];
        try
        {
            var lu = mna.Factorize(nodeNamer: r => netlist.Nodes.NameOf(r + 1));
            rhs = mna.BuildRhs();
            lu.Solve(rhs, x);
        }
        catch (SingularMatrixException ex)
        {
            return new LinearDcResult(null, ex.Message);
        }

        var v = new double[nodeCount];
        for (int i = 0; i < nodeCount; i++) v[i] = x[i].Real;

        var ib = new double[mna.BranchCount];
        for (int b = 0; b < ib.Length; b++) ib[b] = x[nodeCount + b].Real;

        return new LinearDcResult(new LinearDcSolution
        {
            NodeVoltages      = v,
            BranchCurrents    = ib,
            ComponentBranches = componentBranches,
        }, null);
    }
}
