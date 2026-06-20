using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Gate tests for the SDD control-current HB Jacobian coupling J_cc (brief #3) —
/// FD-oracle anchored.
///
/// T1 SensitivityRowIdentity  §3.1: c0 + Σ rRef·iNl == SolveFullNetwork[branchIdx].
/// T2 FdJacobian_SingleRef     decisive gate: I[1,0]=g*_v1+beta*_c1, C[1]=L1 → MaxRelError ≤ 1e-5.
/// T3 FdJacobian_ChargePath    I[1,1]=beta*_c1 (control current weighted by jω) → oracle passes.
/// T4 CrossDevice_Coupling     two SDDs sharing a network, B senses A's branch → oracle passes.
/// T5 ConvergenceImprovement   J_cc restores quadratic convergence (fewer iters).
/// </summary>
public class SddControlCurrentHbJacobianTests(ITestOutputHelper output)
{
    private static (ElaboratedNetlist nl, TestBench tb, HarmonicBalanceAnalysis hba) Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        return (nl, tb, hba);
    }

    // ── T1: sensitivity-row identity (§3.1) ──────────────────────────────────
    [Fact]
    public void SensitivityRowIdentity()
    {
        const string cnl = @"
V_1Tone:VS   n_in 0   Vdc=0  Freq=1e9  V=1.0
R:R_src      n_in n_d   R=50
L:L1         n_d  n_s    L=1e-9  R=1
R:R_s        n_s  0      R=10
SDD:X1       n_d 0   Ports=1  I[1,0]=0.02*_v1  C[1]=L1

analysis HB1  type=hb  Tone=1e9  MaxHarm=3  Tol=1e-7
";
        var (nl, _, _) = Elaborate(cnl);
        var ext = new HbLinearExtractor(nl, AnalysisSettings.Default);
        double omega = 2.0 * Math.PI * 1e9;

        // Populate the LU cache + resolve L1's branch index via a stamp pass.
        ext.Extract(omega);
        int branchIdx = -1;
        foreach (var ec in nl.Components)
            if (ec.Model is InductorModel im) branchIdx = im.LastBranchIndex;
        Assert.True(branchIdx >= 0, "L1 branch index not resolved");

        var bSrc = ext.BuildSourceRhs(omega);
        int N = ext.InterfaceCount;

        // c0 = source-only branch current.
        var c0 = ext.SolveFullNetwork(omega, new Complex[N], bSrc)[branchIdx];
        var rRef = ext.ControlSensitivityRow(omega, branchIdx);

        // Random iNl injection.
        var rng = new Random(1234);
        var iNl = new Complex[N];
        for (int j = 0; j < N; j++)
            iNl[j] = new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);

        var direct = ext.SolveFullNetwork(omega, iNl, bSrc)[branchIdx];
        Complex viaRow = c0;
        for (int j = 0; j < N; j++) viaRow += rRef[j] * iNl[j];

        output.WriteLine($"direct = {direct},  viaRow = {viaRow}");
        Assert.True((direct - viaRow).Magnitude < 1e-9 * (1 + direct.Magnitude),
            $"Sensitivity-row identity failed: direct={direct}, viaRow={viaRow}");
    }

    // ── T2: decisive FD Jacobian gate — single control ref ───────────────────
    [Fact]
    public void FdJacobian_SingleRef()
    {
        const string cnl = @"
g = 0.05
a = 0.1
beta = 0.5

V_1Tone:VS   n_in 0   Vdc=0  Freq=1e9  V=0.3
R:R_src      n_in n_d   R=50
L:L1         n_d  n_s    L=1e-9  R=1
R:R_s        n_s  0      R=10
SDD:X1       n_d 0   Ports=1  I[1,0]=g*_v1 + a*_v1*_v1 + beta*_c1  C[1]=L1

analysis HB1  type=hb  Tone=1e9  MaxHarm=2  Tol=1e-9
";
        RunFdGate(cnl, gate: 1e-5);
    }

    // ── T3: FD Jacobian — control current in a charge (w=1) path ──────────────
    [Fact]
    public void FdJacobian_ChargePath()
    {
        const string cnl = @"
g = 0.05
a = 0.1
beta = 1e-12

V_1Tone:VS   n_in 0   Vdc=0  Freq=1e9  V=0.3
R:R_src      n_in n_d   R=50
L:L1         n_d  n_s    L=1e-9  R=1
R:R_s        n_s  0      R=10
SDD:X1       n_d 0   Ports=1  I[1,0]=g*_v1 + a*_v1*_v1  Q[1]=beta*_c1  C[1]=L1

analysis HB1  type=hb  Tone=1e9  MaxHarm=2  Tol=1e-9
";
        RunFdGate(cnl, gate: 1e-5);
    }

    // ── T4: cross-device coupling (the sharp edge) ───────────────────────────
    [Fact]
    public void CrossDevice_Coupling()
    {
        const string cnl = @"
g = 0.05
a = 0.1
beta = 0.5

V_1Tone:VS   n_in 0   Vdc=0  Freq=1e9  V=0.3
R:R_src      n_in n_a   R=50
L:L1         n_a  n_s    L=1e-9  R=1
R:R_s        n_s  0      R=10
SDD:XA       n_a 0   Ports=1  I[1,0]=g*_v1 + a*_v1*_v1             C[1]=L1
SDD:XB       n_a 0   Ports=1  I[1,0]=g*_v1 + a*_v1*_v1 + beta*_c1  C[1]=L1

analysis HB1  type=hb  Tone=1e9  MaxHarm=2  Tol=1e-9
";
        RunFdGate(cnl, gate: 1e-5);
    }

    // ── All five referenceable kinds (FD oracle ≤ 1e-5 with a control term) ──
    //
    // Each circuit puts the referenced device in the SDD's current path so the
    // control coupling ∂_c_ref/∂V is genuinely non-zero (exercises J_cc, not J_cc=0).

    [Fact]
    public void FdJacobian_VdcKind()
    {
        // Vdc branch current = (V(n_v) − V(n_d))/Rv; SDD injection at n_d moves it.
        const string cnl = @"
g = 0.05
a = 0.1
beta = 0.5

V_1Tone:VS   n_in 0   Vdc=0  Freq=1e9  V=0.3
R:R_src      n_in n_d   R=50
Vdc:VB       n_v 0      Vdc=1
R:Rv         n_v n_d     R=50
C:Cd         n_d 0       C=2e-12
SDD:X1       n_d 0   Ports=1  I[1,0]=g*_v1 + a*_v1*_v1 + beta*_c1  C[1]=VB

analysis HB1  type=hb  Tone=1e9  MaxHarm=2  Tol=1e-9
";
        RunFdGate(cnl, gate: 1e-5);
    }

    [Fact]
    public void FdJacobian_IProbeKind()
    {
        // IProbe carries the current into n_d = I(Rd) + I(SDD) → coupled to V_sdd.
        const string cnl = @"
g = 0.05
a = 0.1
beta = 0.5

V_1Tone:VS   n_in 0   Vdc=0  Freq=1e9  V=0.3
R:R_src      n_in n_p   R=50
IProbe:IP1   n_p n_d
R:Rd         n_d 0       R=50
C:Cd         n_d 0       C=2e-12
SDD:X1       n_d 0   Ports=1  I[1,0]=g*_v1 + a*_v1*_v1 + beta*_c1  C[1]=IP1

analysis HB1  type=hb  Tone=1e9  MaxHarm=2  Tol=1e-9
";
        RunFdGate(cnl, gate: 1e-5);
    }

    [Fact]
    public void FdJacobian_ZPortKind()
    {
        // Z_Port port-1 current depends on V(n_d); SDD injection at n_d moves it.
        const string cnl = @"
g = 0.05
a = 0.1
beta = 0.5

V_1Tone:VS   n_in 0   Vdc=0  Freq=1e9  V=0.3
R:R_src      n_in n_d   R=50
Z_Port:ZP1   n_d 0   Z[1,1]=50
C:Cd         n_d 0       C=2e-12
SDD:X1       n_d 0   Ports=1  I[1,0]=g*_v1 + a*_v1*_v1 + beta*_c1  C[1]=ZP1  Cport[1]=1

analysis HB1  type=hb  Tone=1e9  MaxHarm=2  Tol=1e-9
";
        RunFdGate(cnl, gate: 1e-5);
    }

    [Fact]
    public void FdJacobian_SnpKind()
    {
        // 1-port SnP (resistive, Γ=0 ⇒ Z=50Ω) in the SDD's path; its port branch
        // current depends on V(n_d). Written to a temp .s1p and referenced absolutely.
        string dir = Path.Combine(Path.GetTempPath(), $"snp_cc_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(dir);
        string s1p = Path.Combine(dir, "match.s1p");
        File.WriteAllText(s1p, "# GHz S MA R 50\n0   0.0 0\n1.0 0.0 0\n2.0 0.0 0\n");
        try
        {
            string cnl = $@"
g = 0.05
a = 0.1
beta = 0.5

V_1Tone:VS   n_in 0   Vdc=0  Freq=1e9  V=0.3
R:R_src      n_in n_d   R=50
SnP:S1       n_d 0   NumPorts=1 File=""{s1p.Replace("\\", "/")}""
C:Cd         n_d 0       C=2e-12
SDD:X1       n_d 0   Ports=1  I[1,0]=g*_v1 + a*_v1*_v1 + beta*_c1  C[1]=S1  Cport[1]=1

analysis HB1  type=hb  Tone=1e9  MaxHarm=2  Tol=1e-9
";
            RunFdGate(cnl, gate: 1e-5);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best-effort */ } }
    }

    // ── Convergence improvement: J_cc restores quadratic convergence ──────────
    [Fact]
    public void ConvergenceImprovement_JccBeatsQuasiNewton()
    {
        // Strong control coupling (beta=0.8): the missing ∂_c/∂V term in the brief-#2
        // quasi-Newton path slows it; J_cc converges in fewer iterations. The computation
        // is fully deterministic, so the iteration counts are reproducible across platforms.
        const string cnl = @"
g = 0.05
a = 0.2
beta = 0.8

V_1Tone:VS   n_in 0   Vdc=0  Freq=1e9  V=0.4
R:R_src      n_in n_d   R=50
L:L1         n_d  n_s    L=1e-9  R=1
R:R_s        n_s  0      R=10
SDD:X1       n_d 0   Ports=1  I[1,0]=g*_v1 + a*_v1*_v1 + beta*_c1  C[1]=L1

analysis HB1  type=hb  Tone=1e9  MaxHarm=2  Tol=1e-12
";
        var (full, fc)  = SolveCounting(cnl, useJcc: true);
        var (quasi, qc) = SolveCounting(cnl, useJcc: false);
        output.WriteLine($"iters: J_cc={full} (conv={fc})  quasi-Newton={quasi} (conv={qc})");
        Assert.True(fc, "J_cc path did not converge");
        Assert.True(full < quasi,
            $"J_cc ({full} iters) should converge in fewer iterations than quasi-Newton ({quasi})");
    }

    // Build the full Newton system from a CNL and run HbNewton.Solve directly,
    // returning the iteration count (J_cc on or off).
    private (int iters, bool converged) SolveCounting(string cnl, bool useJcc)
    {
        var (nl, tb, hba) = Elaborate(cnl);
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);

        // Resolve SDD control branch indices (side effect on the SddModels) via a real run.
        new HbEngine(nl, tb).Run(p);

        int    K      = p.MaxHarmonic;
        double f0     = p.ToneHz;
        int    gridN  = HbFft.GridSize(K, p.FFTOverSample);
        double omega0 = 2.0 * Math.PI * f0;

        var ext     = new HbLinearExtractor(nl, AnalysisSettings.Default);
        int N       = ext.InterfaceCount;
        int[] ifN   = ext.InterfaceNodes;

        var yNN  = new Complex[K + 1][,];
        var iSrc = new Complex[K + 1][];
        (yNN[0], iSrc[0]) = ext.ExtractDC();
        for (int k = 1; k <= K; k++) (yNN[k], iSrc[k]) = ext.Extract(k * omega0);

        var bSrc = new Complex[K + 1][];
        for (int k = 0; k <= K; k++) bSrc[k] = ext.BuildSourceRhs(k == 0 ? 0.0 : k * omega0);
        var cc = new ControlCurrentContext(ext, bSrc, f0, K);

        // Cold-start guess: DC operating point + small AC seed.
        var dc = NonlinearDcEngine.Run(nl, AnalysisSettings.Default);
        var V = new Complex[N, K + 1];
        for (int n = 0; n < N; n++)
        {
            int c = ifN[n];
            double vdc = c > 0 && c - 1 < dc.NodeVoltages.Length ? dc.NodeVoltages[c - 1] : 0.0;
            V[n, 0] = new Complex(vdc, 0);
            for (int k = 1; k <= K; k++) V[n, k] = new Complex(1e-3, 1e-3);
        }

        var sr = HbNewton.Solve(V, yNN, iSrc, f0, K, N, nl, ifN, gridN,
            AnalysisSettings.Default, p.Tol, 1.0, 0, cc, useControlJacobian: useJcc);
        return (sr.Iterations, sr.Converged);
    }

    // ── §3.2 tripwire: oracle SEES a large error WITHOUT J_cc (proves J_cc is real) ──
    [Fact]
    public void OracleSeesError_WhenJccDisabled()
    {
        // Same strong-coupling circuit as the single-ref gate. With J_cc the oracle is
        // ≤ 1e-5; without it the analytic Jacobian misses the whole control term while
        // the two-pass residual still moves _c_ref with V → a large, structured mismatch.
        const string cnl = @"
g = 0.05
a = 0.1
beta = 0.5

V_1Tone:VS   n_in 0   Vdc=0  Freq=1e9  V=0.3
R:R_src      n_in n_d   R=50
L:L1         n_d  n_s    L=1e-9  R=1
R:R_s        n_s  0      R=10
SDD:X1       n_d 0   Ports=1  I[1,0]=g*_v1 + a*_v1*_v1 + beta*_c1  C[1]=L1

analysis HB1  type=hb  Tone=1e9  MaxHarm=2  Tol=1e-9
";
        var (with, without) = RunFdGate(cnl, gate: 1e-5);
        output.WriteLine($"MaxRelError: with J_cc={with:E3}  without J_cc={without:E3}");
        Assert.True(without > 1e-3,
            $"Without J_cc the oracle should report a large control-row error; got {without:E3}");
    }

    // ── Shared FD-oracle runner ───────────────────────────────────────────────
    // Returns (MaxRelError with J_cc, MaxRelError without J_cc).
    private (double withJcc, double withoutJcc) RunFdGate(string cnl, double gate)
    {
        var (nl, tb, hba) = Elaborate(cnl);
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        var eng = new HbEngine(nl, tb);
        var run = eng.Run(p);
        var ds  = (DataSet)run;

        Assert.True(ds["Converged"].RealValues[0] > 0.5, "HB did not converge");

        // Converged interface voltages → Vstar (interface-only, per RunJacobianDiagnostic).
        var ext = new HbLinearExtractor(nl, AnalysisSettings.Default);
        int N = ext.InterfaceCount;
        int K = p.MaxHarmonic;
        int[] ifNodes = ext.InterfaceNodes;

        var vCube = ds["V"];
        string[] nodeLabels = vCube.Axes[0].Labels!;
        var Vstar = new Complex[N, K + 1];
        for (int n = 0; n < N; n++)
        {
            int circNode = ifNodes[n];
            string nm = circNode < nl.Nodes.Count ? nl.Nodes.NameOf(circNode) : $"node{circNode}";
            int row = Array.IndexOf(nodeLabels, nm);
            Assert.True(row >= 0, $"interface node {nm} not found in V cube");
            for (int k = 0; k <= K; k++) Vstar[n, k] = (Complex)vCube[row, k];
        }

        var cmp = eng.RunJacobianDiagnostic(p, Vstar, 0.0);
        output.WriteLine($"MaxRelError = {cmp.MaxRelError:E4}  at row {cmp.MaxRelRow} col {cmp.MaxRelCol}");
        output.WriteLine($"MaxAbsError = {cmp.MaxAbsError:E4}");
        foreach (var d in cmp.TopDiscrepancies.Take(8))
            output.WriteLine($"  {d.BlockDesc}: analytic={d.AnalyticVal:E4} fd={d.FdVal:E4} rel={d.RelError:E3}");

        Assert.True(cmp.MaxRelError <= gate,
            $"FD Jacobian MaxRelError {cmp.MaxRelError:E4} exceeds gate {gate:E1}");

        var without = eng.RunJacobianDiagnostic(p, Vstar, 0.0, useControlJacobian: false);
        return (cmp.MaxRelError, without.MaxRelError);
    }
}
