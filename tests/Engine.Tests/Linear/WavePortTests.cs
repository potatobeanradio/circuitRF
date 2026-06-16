using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Gate tests for the Z0-terminated (wave) port formulation in SParameterEngine.
///
/// Wave path: ports stamp conductance 1/Z0 (no branch), S extracted via Kurokawa formula.
/// Fixes the parallel-port singularity class (two Ports on same node, port-across-short).
/// Legacy path (Re(Z0) ≤ 0): unchanged ideal-source + Y→S, not exercised here.
/// </summary>
public class WavePortTests
{
    private static DataSet Run(string cnl, double[] freqsHz, AnalysisSettings? settings = null)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return SParameterEngine.Run(nl, freqsHz, settings);
    }

    private static Complex Sij(DataSet ds, int r, int c, int fi = 0) =>
        (Complex)ds["S"][fi, r, c];

    // ── Gate 1: headline bug — two Terms on same node pair → perfect thru ─────

    [Fact]
    public void TwoTermsSameNode_PerfectThru()
    {
        // Term1 n1 0, Term2 n1 0: parallel ideal sources in the legacy path → rank deficiency.
        // Wave path: two conductances on n1, one incident-wave source → solves cleanly.
        var ds = Run(@"
Term:T1  n1 0  Num=1 Z=50 Ohm
Term:T2  n1 0  Num=2 Z=50 Ohm
", [1e6, 1e9, 3e9]);

        for (int fi = 0; fi < 3; fi++)
        {
            Assert.True(Sij(ds, 0, 0, fi).Magnitude < 1e-9,
                $"[fi={fi}] S11={Sij(ds,0,0,fi):G4}, expected ≈ 0 (matched thru)");
            Assert.True((Sij(ds, 1, 0, fi) - Complex.One).Magnitude < 1e-9,
                $"[fi={fi}] S21={Sij(ds,1,0,fi):G4}, expected ≈ 1");
            Assert.True((Sij(ds, 0, 1, fi) - Complex.One).Magnitude < 1e-9,
                $"[fi={fi}] S12={Sij(ds,0,1,fi):G4}, expected ≈ 1");
            Assert.True(Sij(ds, 1, 1, fi).Magnitude < 1e-9,
                $"[fi={fi}] S22={Sij(ds,1,1,fi):G4}, expected ≈ 0");
        }
    }

    // ── Gate 2: port-across-short solves and is a perfect thru ───────────────

    [Fact]
    public void PortAcrossShort_Solves()
    {
        // Port1 at n1, Port2 at n2, Short:Sw ties n1 to n2 (same potential → same as gate 1).
        var ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n2 0  Num=2 Z=50 Ohm
Short:Sw  n1 n2
", [1e9]);

        // No exception; matched thru.
        Assert.True(Sij(ds, 0, 0).Magnitude < 1e-9,  $"S11={Sij(ds,0,0):G4}");
        Assert.True((Sij(ds, 1, 0) - Complex.One).Magnitude < 1e-9, $"S21={Sij(ds,1,0):G4}");
        Assert.True((Sij(ds, 0, 1) - Complex.One).Magnitude < 1e-9, $"S12={Sij(ds,0,1):G4}");
        Assert.True(Sij(ds, 1, 1).Magnitude < 1e-9,  $"S22={Sij(ds,1,1):G4}");
    }

    // ── Gate 3: wave path parity — normal 2-port matches RFNetwork.YToS ──────

    [Fact]
    public void WaveVsLegacy_Parity()
    {
        // Pi-resistor 2-port: R_shunt=100Ω each port, R_series=50Ω between them.
        const string cnl = @"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n2 0  Num=2 Z=50 Ohm
R:R1  n1 0   R=100 Ohm
R:R2  n2 0   R=100 Ohm
R:Rs  n1 n2  R=50 Ohm
";
        var dsWave = Run(cnl, [1e9]);

        // Analytical Y-matrix (purely resistive, frequency-independent).
        double g1 = 1.0 / 100, gs = 1.0 / 50;
        var yMat = new Mat<Complex>(2, 2);
        yMat[0, 0] = new Complex(g1 + gs, 0);
        yMat[0, 1] = new Complex(-gs, 0);
        yMat[1, 0] = new Complex(-gs, 0);
        yMat[1, 1] = new Complex(g1 + gs, 0);
        var sRef = RFNetwork.YToS(yMat, new Complex[] { 50, 50 });

        const double Tol = 1e-9;
        for (int r = 0; r < 2; r++)
        for (int c = 0; c < 2; c++)
            Assert.True((Sij(dsWave, r, c) - sRef[r, c]).Magnitude < Tol,
                $"S[{r+1},{c+1}] wave={Sij(dsWave,r,c):G4} ref={sRef[r,c]:G4}");
    }

    // ── Gate 4: mismatched Z0 thru (50 Ω / 75 Ω) ────────────────────────────

    [Fact]
    public void MismatchedZ0_Thru()
    {
        // Both ports on the same node n1 with different Z0s.
        // Analytical power-wave result for a direct thru with Z0_1=50, Z0_2=75:
        //   S11 = (Z02 - Z01) / (Z02 + Z01)      = 25/125 = 0.2
        //   S22 = (Z01 - Z02) / (Z01 + Z02)      = -25/125 = -0.2
        //   S21 = S12 = 2√(Z01·Z02) / (Z01+Z02) = 2√3750 / 125
        var ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n1 0  Num=2 Z=75 Ohm
", [1e9]);

        const double Tol     = 1e-9;
        const double s11_exp =  0.2;
        const double s22_exp = -0.2;
        double       s21_exp = 2.0 * Math.Sqrt(50.0 * 75.0) / (50.0 + 75.0); // ≈ 0.97980

        Assert.True(Math.Abs(Sij(ds, 0, 0).Real - s11_exp) < Tol,
            $"S11={Sij(ds,0,0):G4}, expected {s11_exp}");
        Assert.True(Math.Abs(Sij(ds, 1, 1).Real - s22_exp) < Tol,
            $"S22={Sij(ds,1,1):G4}, expected {s22_exp}");
        Assert.True(Math.Abs(Sij(ds, 1, 0).Real - s21_exp) < Tol,
            $"S21={Sij(ds,1,0):G4}, expected {s21_exp}");
        Assert.True(Math.Abs(Sij(ds, 0, 1).Real - s21_exp) < Tol,
            $"S12={Sij(ds,0,1):G4}, expected {s21_exp}");
        // Imaginary parts should be zero for a purely resistive network.
        Assert.True(Sij(ds, 0, 0).Imaginary < Tol, $"S11 imag={Sij(ds,0,0).Imaginary}");
        Assert.True(Sij(ds, 1, 0).Imaginary < Tol, $"S21 imag={Sij(ds,1,0).Imaginary}");
    }

    // ── Gate 5: no regularization warning for trivial circuits ───────────────

    [Fact]
    public void WavePath_TrivialShort_NoRegularizationWarning()
    {
        // Two ports on same node: this was the buggy case that triggered a noisy
        // "trying regularization" flood in the legacy path. Wave path must solve
        // silently on the first factorization — no "sparam-regularization" warning.
        var (lib, tb) = new CnlReader().Read(@"
Term:T1  n1 0  Num=1 Z=50 Ohm
Term:T2  n1 0  Num=2 Z=50 Ohm
");
        var nl = new Elaborator(lib).Elaborate(tb);
        _ = SParameterEngine.Run(nl, [1e9]);

        Assert.DoesNotContain(nl.Warnings,
            w => w.Contains("regularization", StringComparison.OrdinalIgnoreCase));
    }

    // ── Gate 6: wave path solves without regularization (RegularizationMode.Never) ──

    [Fact]
    public void WavePath_TrivialShort_NeedsNoRegularization()
    {
        // Port conductances already tie port nodes to ground → no floating-node issue.
        // RegularizationMode.Never must not throw for this topology.
        var settings = new AnalysisSettings
        {
            ConductanceRegularization = RegularizationMode.Never,
            InductanceRegularization  = RegularizationMode.Never,
        };
        var ds = Run(@"
Term:T1  n1 0  Num=1 Z=50 Ohm
Term:T2  n1 0  Num=2 Z=50 Ohm
", [1e9], settings);

        // Clean result, no exception.
        Assert.True((Sij(ds, 1, 0) - Complex.One).Magnitude < 1e-9,
            $"S21={Sij(ds,1,0):G4}, expected 1.0");
    }
}
