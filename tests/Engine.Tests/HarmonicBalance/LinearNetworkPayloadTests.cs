// ================================================================
//  LinearNetworkPayloadTests.cs — Phase 5-7 round-trip oracle
//
//  Validates that the ILinearNetworkPayload data exported from
//  HbLinearNetworkPayload (via HbRunResult.LinearPayload) is a
//  complete and correct description of the linear MNA system.
//
//  The oracle: for each (k, si) pair, the exported (G, bSrc, iNl,
//  interfaceNodes) must satisfy G·x = b where b = bSrc - iNl corrections
//  and x = IBackSolverProvider.GetFullSolution(k, si).
//
//  Residual check:  ‖G·x - b‖₂ < 1e-10
//  (LU back-solve precision — far tighter than Newton convergence.)
//
//  Condition 2 of the Phase 5-7 approval: k=0 (DC) is explicitly
//  tested, not just k=1.  DC is where matrix construction has special
//  cases (real-only admittances, inductance regularization).
//
//  See docs/design/data-export.md §9.
// ================================================================

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Export;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Round-trip oracle for <see cref="ILinearNetworkPayload"/> data.
/// Uses Hero 2 (single-FET PA sweep) as the reference circuit.
/// </summary>
public class LinearNetworkPayloadTests(ITestOutputHelper output)
{
    private static string Hero2Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero2");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero2 not found");
    }

    // ── Shared HB run (lazy, shared across tests in this class) ─────────────

    private static HbRunResult? _cachedResult;
    private static readonly Lock _lock = new();

    private static HbRunResult GetHero2Result()
    {
        lock (_lock)
        {
            if (_cachedResult is not null) return _cachedResult;

            var dir     = Hero2Dir();
            var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero2.cnl"));
            var netlist = new Elaborator(lib).Elaborate(tb);
            var hba     = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
            // Run a short sweep (3 points) — enough to test multiple si indices.
            var p       = HbEngine.Resolve(hba, netlist.ResolvedGlobals)
                         with { SweepStop = -14.0, SweepStep = 1.0 };
            _cachedResult = new HbEngine(netlist, tb).Run(p);
            return _cachedResult;
        }
    }

    // ── Helper: sparse matrix-vector product ─────────────────────────────────

    /// <summary>
    /// Compute G·x from the COO triplet representation.
    /// Rows, Cols are 0-based; Data are the complex matrix entries.
    /// </summary>
    private static Complex[] SparseMv(
        int[]     rows,
        int[]     cols,
        Complex[] data,
        Complex[] x,
        int       mnaSize)
    {
        var y = new Complex[mnaSize];
        for (int j = 0; j < rows.Length; j++)
            y[rows[j]] += data[j] * x[cols[j]];
        return y;
    }

    // ── Helper: compute adjusted RHS b = bSrc - iNl correction ──────────────

    private static Complex[] BuildRhs(
        ILinearNetworkPayload payload,
        int                   si,
        int                   k)
    {
        int M = payload.MnaSize;
        int N = payload.InterfaceCount;
        int[] ifaceNodes = payload.InterfaceNodes;

        var b = new Complex[M];
        for (int m = 0; m < M; m++)
            b[m] = payload.GetBSrc(si, k, m);

        // Subtract NL interface currents (same operation as SolveFullNetwork).
        // interfaceNodes[n] is 1-based (circuit node index), MNA index = circNode - 1.
        for (int n = 0; n < N; n++)
            b[ifaceNodes[n] - 1] -= payload.GetINl(si, n, k);

        return b;
    }

    // ── Oracle helper: assert ‖G·x - b‖₂ < tol ──────────────────────────────

    private void AssertLinearResidue(
        ILinearNetworkPayload payload,
        IBackSolverProvider   bsp,
        int                   si,
        int                   k,
        double                absTol = 1e-10)
    {
        int M = payload.MnaSize;

        var (rows, cols, gData) = payload.GetSparseG(k);
        var x = bsp.GetFullSolution(k, si);
        var b = BuildRhs(payload, si, k);

        var Gx    = SparseMv(rows, cols, gData, x, M);
        double resid = 0;
        for (int m = 0; m < M; m++)
            resid += (Gx[m] - b[m]).Magnitude;

        output.WriteLine(
            $"  k={k} si={si}: ‖G·x - b‖₁ = {resid:E3}  (nnz={rows.Length}, M={M})");

        Assert.True(resid < absTol,
            $"Residual ‖G·x - b‖₁ = {resid:E3} > {absTol:E0} at k={k}, si={si}. " +
            "Exported G or bSrc/iNl does not match the back-solver's linear system.");
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Phase 5-7 condition: LinearPayload is non-null after a single-tone HB run.
    /// </summary>
    [Fact]
    public void LinearPayload_IsPopulated_AfterHbRun()
    {
        var result = GetHero2Result();
        Assert.NotNull(result.LinearPayload);
        Assert.IsAssignableFrom<IBackSolverProvider>(result.LinearPayload);
    }

    /// <summary>
    /// Payload dimensions are self-consistent and plausible for Hero 2.
    /// </summary>
    [Fact]
    public void LinearPayload_Dimensions_ArePlausible()
    {
        var result  = GetHero2Result();
        var payload = result.LinearPayload!;

        int K1 = payload.HarmonicCount;
        int S  = payload.SweepCount;
        int M  = payload.MnaSize;
        int N  = payload.NonGroundCount;
        int Ni = payload.InterfaceCount;

        output.WriteLine(
            $"K+1={K1}  SweepCount={S}  MnaSize={M}  NonGnd={N}  Interface={Ni}");
        output.WriteLine($"NodeNames: [{string.Join(", ", payload.NodeNames)}]");
        output.WriteLine($"BranchNames: [{string.Join(", ", payload.BranchNames)}]");
        output.WriteLine($"InterfaceNodes: [{string.Join(", ", payload.InterfaceNodes)}]");

        Assert.True(K1 >= 2, "HarmonicCount must be at least 2 (DC + fundamental)");
        Assert.True(S  >= 1, "SweepCount must be at least 1");
        Assert.True(M  >= N, "MnaSize >= NonGroundCount");
        Assert.True(N  >= 1, "Must have at least one non-ground node");
        Assert.True(Ni >= 1, "Must have at least one interface node (the FET)");

        // Node names must match NonGroundCount
        Assert.Equal(N, payload.NodeNames.Length);
        // Branch names: one per branch (MnaSize - NonGroundCount)
        Assert.Equal(M - N, payload.BranchNames.Length);
        // InterfaceNodes length matches InterfaceCount
        Assert.Equal(Ni, payload.InterfaceNodes.Length);

        // AC harmonics (k≥1) share the same sparsity pattern (topology-invariant for AC).
        // k=0 (DC) may differ because zero-admittance stamps (e.g. capacitor in bias tee
        // with jωC=0 at DC) can create different effective nonzero patterns vs AC.
        // The NpyWriter/MatWriter handle this correctly by calling GetSparseG(k) per harmonic.
        if (K1 >= 3)
        {
            var (rows1, cols1, _) = payload.GetSparseG(1);
            var (rows2, cols2, _) = payload.GetSparseG(2);
            Assert.Equal(rows1, rows2);
            Assert.Equal(cols1, cols2);
        }
    }

    /// <summary>
    /// Round-trip oracle — k=0 (DC harmonic).
    ///
    /// Condition 2 of the Phase 5-7 approval: DC is where the linear MNA has
    /// special cases (real-only admittances at ω=0; inductance regularization).
    /// The exported G(k=0) must be the SAME G the back-solver used for GetSolution(0, si).
    ///
    /// Proof: ‖G(0) · x(0, si) - b(0, si)‖₁ < 1e-10 for all sweep points si.
    /// </summary>
    [Fact]
    public void RoundTrip_Oracle_K0_DC_GTimesX_EqualsB()
    {
        var result  = GetHero2Result();
        var payload = result.LinearPayload!;
        var bsp     = (IBackSolverProvider)payload;

        int S = payload.SweepCount;
        output.WriteLine("k=0 (DC) round-trip oracle:");

        for (int si = 0; si < S; si++)
            AssertLinearResidue(payload, bsp, si, k: 0);

        output.WriteLine("PASS: ‖G(0)·x - b‖ < 1e-10 for all sweep points.");
    }

    /// <summary>
    /// Round-trip oracle — k=1 (fundamental harmonic).
    ///
    /// The fundamental is the primary operating-point harmonic and the one most
    /// exercised by HB Newton convergence; its residual should be tightest.
    ///
    /// Proof: ‖G(1) · x(1, si) - b(1, si)‖₁ < 1e-10 for all sweep points si.
    /// </summary>
    [Fact]
    public void RoundTrip_Oracle_K1_Fundamental_GTimesX_EqualsB()
    {
        var result  = GetHero2Result();
        var payload = result.LinearPayload!;
        var bsp     = (IBackSolverProvider)payload;

        int S = payload.SweepCount;
        output.WriteLine("k=1 (fundamental) round-trip oracle:");

        for (int si = 0; si < S; si++)
            AssertLinearResidue(payload, bsp, si, k: 1);

        output.WriteLine("PASS: ‖G(1)·x - b‖ < 1e-10 for all sweep points.");
    }

    /// <summary>
    /// Round-trip oracle — all harmonics including k=2..K.
    ///
    /// The higher harmonics use the same G pattern but different admittance values (ω = k·2π·f0).
    /// This confirms the pattern/data split is correct for every harmonic.
    /// </summary>
    [Fact]
    public void RoundTrip_Oracle_AllHarmonics_GTimesX_EqualsB()
    {
        var result  = GetHero2Result();
        var payload = result.LinearPayload!;
        var bsp     = (IBackSolverProvider)payload;

        int K1 = payload.HarmonicCount;
        int S  = payload.SweepCount;
        output.WriteLine($"All-harmonic round-trip oracle (K+1={K1}, S={S}):");

        for (int k  = 0; k  < K1; k++)
        for (int si = 0; si < S;  si++)
            AssertLinearResidue(payload, bsp, si, k);

        output.WriteLine("PASS: ‖G(k)·x - b‖ < 1e-10 for all k, si.");
    }

    /// <summary>
    /// Node names resolve to correct circuit node indices via the payload.
    /// The drain node voltage from the solution vector must match the HB cube.
    /// </summary>
    [Fact]
    public void RoundTrip_Oracle_DrainNodeVoltage_MatchesCubeAndBackSolver()
    {
        var result  = GetHero2Result();
        var payload = result.LinearPayload!;
        var bsp     = (IBackSolverProvider)payload;
        var bs      = result.BackSolver!;

        // Find drain node — it has the highest DC voltage in the Hero 2 circuit.
        string[] nodeNames = payload.NodeNames;
        string   drainName = "";
        double   maxDcV    = 0;

        for (int ni = 0; ni < nodeNames.Length; ni++)
        {
            if (!bs.TryGetNodeNumber(nodeNames[ni], out int cn)) continue;
            double vDc = bs.GetNodeVoltage(cn, 0, 0).Magnitude;
            if (vDc > maxDcV)
            {
                maxDcV    = vDc;
                drainName = nodeNames[ni];
            }
        }
        Assert.False(string.IsNullOrEmpty(drainName), "Could not identify drain node by DC voltage.");
        output.WriteLine($"Identified drain node: '{drainName}' (V_DC = {maxDcV:G4} V)");

        // Find the 0-based MNA index of the drain node.
        int drainMnaIdx = Array.IndexOf(nodeNames, drainName);
        Assert.True(drainMnaIdx >= 0, $"drainName '{drainName}' not in NodeNames array.");

        int S = payload.SweepCount;
        const double Tol = 1e-10;

        for (int k  = 0; k  < payload.HarmonicCount; k++)
        for (int si = 0; si < S;                     si++)
        {
            // Voltage from the cached full solution vector.
            Complex xDrain = bsp.GetFullSolution(k, si)[drainMnaIdx];

            // Cross-check via HbLinearBackSolver.GetNodeVoltage (different code path).
            if (!bs.TryGetNodeNumber(drainName, out int circNode)) continue;
            Complex bsDrain = bs.GetNodeVoltage(circNode, k, si);

            double err = (xDrain - bsDrain).Magnitude;
            if (err > Tol)
            {
                output.WriteLine(
                    $"FAIL drain k={k} si={si}: payload={xDrain:G6}  backSolver={bsDrain:G6}  err={err:E3}");
                Assert.Fail(
                    $"Drain voltage mismatch: payload={xDrain:G6} vs backSolver={bsDrain:G6} " +
                    $"at k={k} si={si}  err={err:E3} > {Tol:E0}");
            }
        }

        output.WriteLine($"PASS: drain node voltages match back-solver to {Tol:E0} " +
                         $"across all {payload.HarmonicCount} harmonics × {S} sweep points.");
    }

    /// <summary>
    /// Omegas array: DC at index 0 (ω=0), harmonic k at ω=k·2π·f0.
    /// </summary>
    [Fact]
    public void LinearPayload_Omegas_AreCorrect()
    {
        var result  = GetHero2Result();
        var payload = result.LinearPayload!;

        double[] omegas = payload.Omegas;
        Assert.Equal(payload.HarmonicCount, omegas.Length);
        Assert.Equal(0.0, omegas[0], precision: 15);     // DC must be exactly zero

        // f0 from Hero 2 is 2 GHz; ω_1 = 2π × 2e9 ≈ 12.566e9 rad/s
        // Verify ratio: ω_k = k·ω_1
        double omega1 = omegas[1];
        Assert.True(omega1 > 1e9, "Fundamental angular frequency must be > 1 GHz");
        for (int k = 2; k < omegas.Length; k++)
        {
            double expected = k * omega1;
            double actual   = omegas[k];
            Assert.Equal(expected, actual, precision: 10);
        }
    }
}
