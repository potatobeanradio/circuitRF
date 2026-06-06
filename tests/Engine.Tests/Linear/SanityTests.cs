using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Small analytically-verified circuits to confirm extraction correctness
/// before running the full Hero 1 gate.
/// </summary>
public class SanityTests
{
    private static DataSet Run(string cnl, double[] freqsHz)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return SParameterEngine.Run(nl, freqsHz);
    }

    // Extract S[fi, r, c] from a DataSet (0-based freq, row, col indices).
    private static Complex Sij(DataSet ds, int r, int c, int fi = 0) =>
        (Complex)ds["S"][fi, r, c];

    // ── 1-port: 50Ω shunt — S11 = 0 (matched) ───────────────────────────────
    [Fact]
    public void OnePort_MatchedLoad_S11IsZero()
    {
        var ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
R:R1  n1 0  R=50 Ohm
", [1e9]);

        var s11 = Sij(ds, 0, 0);
        Assert.True(s11.Magnitude < 1e-8, $"S11={s11:G4}, expected ≈ 0");
    }

    // ── 2-port: pure resistive π-network, exact S-params known ───────────────
    // Topology: R_shunt=100Ω at each port, R_series=50Ω between them.
    // Computed analytically: S11 = S22 = −1/21 ≈ −0.04762, S21 = S12 = 4/21 ≈ 0.38095
    [Fact]
    public void TwoPort_PiResistor_SParamsMatchAnalytic()
    {
        var ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n2 0  Num=2 Z=50 Ohm
R:R1  n1 0   R=100 Ohm
R:R2  n2 0   R=100 Ohm
R:Rs  n1 n2  R=50 Ohm
", [1e9]);

        const double Tol = 1e-6;
        Assert.True(Math.Abs(Sij(ds, 0, 0).Real - (-1.0 / 21)) < Tol, $"S11={Sij(ds,0,0):G4}");
        Assert.True(Math.Abs(Sij(ds, 1, 0).Real - (8.0 / 21))  < Tol, $"S21={Sij(ds,1,0):G4}");
        Assert.True(Math.Abs(Sij(ds, 0, 1).Real - (8.0 / 21))  < Tol, $"S12={Sij(ds,0,1):G4}");
        Assert.True(Math.Abs(Sij(ds, 1, 1).Real - (-1.0 / 21)) < Tol, $"S22={Sij(ds,1,1):G4}");
        Assert.True(Sij(ds, 0, 0).Imaginary < Tol, $"S11 imag={Sij(ds,0,0).Imaginary:G4}");
        Assert.True(Sij(ds, 1, 0).Imaginary < Tol, $"S21 imag={Sij(ds,1,0).Imaginary:G4}");
    }

    // ── 2-port: inductor + shunt resistors — verifies inductive stamp ────────
    // At ω, series L = 10 nH between ports, R_shunt=200Ω at each port, Z0=50Ω.
    // Y11=Y22 = 1/200 + 1/(jωL), Y12=Y21 = -1/(jωL).
    [Fact]
    public void TwoPort_InductorSeries_SParamsMatchAnalytic()
    {
        const double L   = 10e-9;   // 10 nH
        const double R   = 200.0;   // 200 Ω shunt
        const double Z0  = 50.0;
        const double f   = 1e9;
        double omega = 2 * Math.PI * f;

        var ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n2 0  Num=2 Z=50 Ohm
R:R1  n1 0  R=200 Ohm
R:R2  n2 0  R=200 Ohm
L:Ls  n1 n2  L=10 nH
", [f]);

        // Analytical Y-matrix
        var yL   = new Complex(0, -1.0 / (omega * L));  // Y_series = 1/(jωL) = -j/(ωL)
        var yR   = new Complex(1.0 / R, 0);
        var Y11  = yR + yL;
        var Y12  = -yL;

        // Y → S (uniform Z0)
        Complex sqZ = Math.Sqrt(Z0);
        var Y11h = Z0 * Y11;
        var Y12h = Z0 * Y12;
        // S = (I - Ŷ)(I + Ŷ)^{-1}  for 2×2
        var det_sum = (Complex.One + Y11h) * (Complex.One + Y11h) - Y12h * Y12h;
        var s11a    = ((Complex.One - Y11h) * (Complex.One + Y11h) + Y12h * Y12h) / det_sum;
        var s21a    = (-2.0 * Y12h) / det_sum;

        const double Tol = 1e-6;
        Assert.True((Sij(ds, 0, 0) - s11a).Magnitude < Tol, $"S11 sim={Sij(ds,0,0):G4} vs {s11a:G4}");
        Assert.True((Sij(ds, 1, 0) - s21a).Magnitude < Tol, $"S21 sim={Sij(ds,1,0):G4} vs {s21a:G4}");
    }

    // ── Step 1: Inductor series-R correctness ────────────────────────────────
    // An inductor L with series resistance R must satisfy Z(ω) = R + jωL.
    // Test: 1-port with RL series arm to ground. Analytical S11 = (Z − Z0)/(Z + Z0)
    // where Z = R + jωL + Z0 (port source impedance in series), reduced to one port.
    //
    // Simpler formulation: 1-port, RL shunt to ground.
    //   Y_shunt = 1/(R + jωL).  Y_total = Y_port + Y_shunt = 1/Z0 + 1/(R+jωL)
    //   S11 = (Z_in − Z0)/(Z_in + Z0) where Z_in = 1/Y_total ... using Y→S directly.
    //
    // Use 2-port with RL series arm: verified against the lossless inductor at R→0.
    [Fact]
    public void InductorWithSeriesR_ImpedanceMatchesAnalytic()
    {
        const double L   = 10e-9;   // 10 nH
        const double R   = 5.0;     // 5 Ω series loss
        const double Z0  = 50.0;
        const double f   = 1e9;
        double omega = 2 * Math.PI * f;

        // Same topology as TwoPort_InductorSeries but with R= on the inductor.
        // Z_series = R + jωL instead of just jωL.
        var ds = Run($@"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n2 0  Num=2 Z=50 Ohm
R:R1  n1 0  R=200 Ohm
R:R2  n2 0  R=200 Ohm
L:Ls  n1 n2  L=10 nH R=5 Ohm
", [f]);

        // Analytical: Y_series = 1/(R + jωL), same 2×2 Y-matrix structure as before.
        var zSeries = new Complex(R, omega * L);
        var yL_rl   = Complex.One / zSeries;          // admittance of RL series arm
        var yR      = new Complex(1.0 / 200.0, 0);   // shunt resistor admittance
        var Y11     = yR + yL_rl;
        var Y12     = -yL_rl;

        var Y11h    = Z0 * Y11;
        var Y12h    = Z0 * Y12;
        var det_sum = (Complex.One + Y11h) * (Complex.One + Y11h) - Y12h * Y12h;
        var s11a    = ((Complex.One - Y11h) * (Complex.One + Y11h) + Y12h * Y12h) / det_sum;
        var s21a    = (-2.0 * Y12h) / det_sum;

        const double Tol = 1e-6;
        Assert.True((Sij(ds, 0, 0) - s11a).Magnitude < Tol, $"S11 sim={Sij(ds,0,0):G4} vs {s11a:G4}");
        Assert.True((Sij(ds, 1, 0) - s21a).Magnitude < Tol, $"S21 sim={Sij(ds,1,0):G4} vs {s21a:G4}");
    }

    // ── Step 2: Mixed-sign mutual inductance (physical, must solve and be accurate) ──
    // Three inductors with mixed-sign coupling (positive and negative M).
    // The inductance matrix must be positive-definite for a physical coil arrangement.
    // We verify: solves without exception, and S-params are passive and reciprocal.
    [Fact]
    public void ThreeInductors_MixedSignMutual_SolvesCorrectly()
    {
        // L1 = L2 = L3 = 10 nH.  M12 = +3 nH (aids), M23 = +3 nH (aids), M13 = -2 nH (opposes).
        // Inductance matrix eigenvalues are positive → physically realizable → should solve.
        var ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n2 0  Num=2 Z=50 Ohm
Port:P3  n3 0  Num=3 Z=50 Ohm
L:L1  n1 0  L=10 nH
L:L2  n2 0  L=10 nH
L:L3  n3 0  L=10 nH
Mutual:M12  M=3 nH  Inductor1=""L1""  Inductor2=""L2""
Mutual:M23  M=3 nH  Inductor1=""L2""  Inductor2=""L3""
Mutual:M13  M=-2 nH Inductor1=""L1""  Inductor2=""L3""
", [1e9]);

        int N = ds["S"].Axes[1].Length;

        // Reciprocity: S_ij = S_ji for a passive network.
        for (int r = 0; r < N; r++)
        for (int c = 0; c < N; c++)
            Assert.True((Sij(ds, r, c) - Sij(ds, c, r)).Magnitude < 1e-6,
                $"Reciprocity: S[{r+1},{c+1}]={Sij(ds,r,c):G4} ≠ S[{c+1},{r+1}]={Sij(ds,c,r):G4}");

        // Passivity: power out ≤ power in per driven port.
        for (int j = 0; j < N; j++)
        {
            double power = Enumerable.Range(0, N).Sum(k => Sij(ds, k, j).Magnitude * Sij(ds, k, j).Magnitude);
            Assert.True(power <= 1.0 + 1e-6, $"Passivity violation port {j+1}: Σ|S_kj|²={power:G4}");
        }
    }

    // ── Step 3: Short stamp audit ────────────────────────────────────────────
    // Verify that a Short (zero-ohm branch) stamps correctly by comparing a circuit
    // that uses a Short as an internal wire against the same circuit using a direct
    // node connection. Both must produce identical S-parameters.
    [Fact]
    public void Short_AsInternalWire_SameAsDirectConnection()
    {
        // Short:Sw ties n1 to n_int; n_int then drives Rs into Port2.
        // Effective circuit: Port1 — [100Ω shunt] — [50Ω series] — [100Ω shunt] — Port2
        // (same topology as TwoPort_PiResistor, just renames n1 via a Short)
        var dsWithShort = Run(@"
Port:P1  n1    0  Num=1 Z=50 Ohm
Port:P2  n2    0  Num=2 Z=50 Ohm
Short:Sw  n1 n_int
R:Rs    n_int n2   R=50 Ohm
R:Rsh1  n1    0    R=100 Ohm
R:Rsh2  n2    0    R=100 Ohm
", [1e9]);

        // Same circuit, direct connection (no Short)
        var dsDirect = Run(@"
Port:P1  n1  0  Num=1 Z=50 Ohm
Port:P2  n2  0  Num=2 Z=50 Ohm
R:Rs    n1  n2  R=50 Ohm
R:Rsh1  n1   0  R=100 Ohm
R:Rsh2  n2   0  R=100 Ohm
", [1e9]);

        const double Tol = 1e-6;
        for (int r = 0; r < 2; r++)
        for (int c = 0; c < 2; c++)
            Assert.True((Sij(dsWithShort, r, c) - Sij(dsDirect, r, c)).Magnitude < Tol,
                $"S[{r+1},{c+1}] with Short={Sij(dsWithShort,r,c):G4} vs direct={Sij(dsDirect,r,c):G4}");
    }

    // ── Step 4a: Mutual stamp audit — valid coupling (k < 1) ────────────────
    // Two magnetically coupled inductors in shunt; must solve without exception
    // and produce a physically reasonable result (passive, reciprocal).
    [Fact]
    public void Mutual_ValidCoupling_SolvesAndIsReciprocal()
    {
        // L1, L2 = 10 nH; M = 5 nH → k = 0.5 (physical)
        var ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n2 0  Num=2 Z=50 Ohm
L:L1   n1 0  L=10 nH
L:L2   n2 0  L=10 nH
Mutual:M1  M=5 nH Inductor1=""L1"" Inductor2=""L2""
", [1e9]);

        // Reciprocity: S12 = S21 for a passive, symmetric network
        Assert.True((Sij(ds, 0, 1) - Sij(ds, 1, 0)).Magnitude < 1e-6,
            $"S21={Sij(ds,1,0):G4} ≠ S12={Sij(ds,0,1):G4}");

        // Passivity: |S_kj|² summed over k ≤ 1
        for (int j = 0; j < 2; j++)
        {
            double power = Sij(ds, 0, j).Magnitude * Sij(ds, 0, j).Magnitude
                         + Sij(ds, 1, j).Magnitude * Sij(ds, 1, j).Magnitude;
            Assert.True(power <= 1.0 + 1e-6,
                $"Passivity violation for port {j+1}: power out = {power:G4}");
        }
    }

    // ── Change 3: Mutual over-coupling (k ≥ 1) → warn-and-continue ─────────
    // k ≥ 1 is non-physical but no longer a hard error — circuitRF research-tool philosophy.
    // A warning is emitted to stderr; the solve proceeds (result may be non-physical).
    [Fact]
    public void Mutual_OverCoupling_WarnsAndProducesResult()
    {
        // k = 15 nH / sqrt(10 nH × 10 nH) = 1.5 → non-physical, but warn-and-continue.
        var errCapture = new System.IO.StringWriter();
        Console.SetError(errCapture);
        DataSet? ds;
        try
        {
            // Should NOT throw; should warn and return a result.
            ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n2 0  Num=2 Z=50 Ohm
L:L1   n1 0  L=10 nH
L:L2   n2 0  L=10 nH
Mutual:M1  M=15 nH Inductor1=""L1"" Inductor2=""L2""
", [1e9]);
        }
        finally
        {
            Console.SetError(new System.IO.StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }

        string warnings = errCapture.ToString();
        Assert.Contains("k=",  warnings);
        Assert.Contains("≥ 1", warnings);
        Assert.Contains("M1",  warnings);
        Assert.NotNull(ds);  // a result was produced
    }

    // ── Change 1: Resistor with R < 0 — warns and solves ─────────────────────
    [Fact]
    public void Resistor_NegativeR_WarnsAndSolves()
    {
        var errCapture = new System.IO.StringWriter();
        Console.SetError(errCapture);
        DataSet? ds;
        try
        {
            // R < 0: active/negative-resistance element. Should warn, not throw.
            ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
R:Rneg  n1 0  R=-50 Ohm
", [1e9]);
        }
        finally { Console.SetError(new System.IO.StreamWriter(Console.OpenStandardError()) { AutoFlush = true }); }

        string w = errCapture.ToString();
        Assert.Contains("< 0",   w);
        Assert.Contains("Rneg", w);
        Assert.NotNull(ds);
    }

    // ── Change 1: Resistor with R = 0 — uses Gmax, warns ─────────────────────
    [Fact]
    public void Resistor_ZeroR_UsesGmaxAndWarns()
    {
        var errCapture = new System.IO.StringWriter();
        Console.SetError(errCapture);
        DataSet? ds;
        try
        {
            // R = 0: near-short via Gmax. Should warn and return a result.
            ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
R:Rzero  n1 0  R=0 Ohm
", [1e9]);
        }
        finally { Console.SetError(new System.IO.StreamWriter(Console.OpenStandardError()) { AutoFlush = true }); }

        string w = errCapture.ToString();
        Assert.Contains("Gmax", w);
        Assert.Contains("Rzero", w);
        Assert.NotNull(ds);
        // S11 should be very close to -1 (port nearly shorted via Gmax ≫ 1/Z0)
        Assert.True(Sij(ds!, 0, 0).Magnitude > 0.99,
            $"R=0 near-short: expected |S11|≈1, got {Sij(ds!, 0, 0).Magnitude:G4}");
    }

    // ── Change 2: Inductor RLC — AC impedance matches R + jωL + 1/(jωC) ──────
    [Fact]
    public void InductorRLC_AcImpedanceMatchesAnalytic()
    {
        const double L   = 10e-9;   // 10 nH
        const double R   = 2.0;     // 2 Ω
        const double C   = 1e-12;   // 1 pF
        const double Z0  = 50.0;
        const double f   = 1e9;
        double omega = 2 * Math.PI * f;

        var ds = Run($@"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n2 0  Num=2 Z=50 Ohm
R:Rsh1  n1  0  R=200 Ohm
R:Rsh2  n2  0  R=200 Ohm
L:Lc  n1 n2  L=10 nH R=2 Ohm C=1 pF
", [f]);

        // Analytical: Z_series = R + jωL + 1/(jωC) = R + j(ωL − 1/(ωC))
        double imagZ = omega * L - 1.0 / (omega * C);
        var zSeries  = new Complex(R, imagZ);
        var yL_rlc   = Complex.One / zSeries;
        var yR       = new Complex(1.0 / 200.0, 0);
        var Y11      = yR + yL_rlc;
        var Y12      = -yL_rlc;

        var Y11h    = Z0 * Y11;
        var Y12h    = Z0 * Y12;
        var det_sum = (Complex.One + Y11h) * (Complex.One + Y11h) - Y12h * Y12h;
        var s11a    = ((Complex.One - Y11h) * (Complex.One + Y11h) + Y12h * Y12h) / det_sum;
        var s21a    = (-2.0 * Y12h) / det_sum;

        const double Tol = 1e-6;
        Assert.True((Sij(ds, 0, 0) - s11a).Magnitude < Tol, $"S11 sim={Sij(ds,0,0):G4} vs {s11a:G4}");
        Assert.True((Sij(ds, 1, 0) - s21a).Magnitude < Tol, $"S21 sim={Sij(ds,1,0):G4} vs {s21a:G4}");
    }

    // ── Change 2: Inductor with C is a DC open (branch current = 0 at ω=0) ───
    [Fact]
    public void InductorWithC_DcOpen_BranchCurrentIsZero()
    {
        // Stamp the inductor at ω=0 directly and verify the constraint forces i=0.
        // DC-open: constraint row should be "−i = 0" (diagonal = -1, no voltage coefficients).
        var (lib, tb) = new CnlReader().Read(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
L:L1     n1 0  L=10 nH C=1 pF
");
        var nl = new Elaborator(lib).Elaborate(tb);

        var inductorEc = nl.Components.First(ec => ec.ComponentType == "L");
        var mna        = new MnaSystem(nl.Nodes.Count - 1);  // 1 non-ground node

        inductorEc.Model.Stamp(mna, inductorEc, omega: 0.0);

        int nodeCount = nl.Nodes.Count - 1;   // = 1
        int branchRow = nodeCount;             // first branch row = 1

        // Constraint row must have NO voltage-node coefficients.
        Assert.True(mna.GetEntry(branchRow, 0) == Complex.Zero,
            "DC-open: constraint row should have no voltage coefficient");

        // Constraint row diagonal must be -1 (forces i = 0).
        Assert.True(mna.GetEntry(branchRow, branchRow) == new Complex(-1.0, 0.0),
            "DC-open: constraint diagonal must be -1 (forces i=0)");

        // KCL column for this branch: +1 at node 0's row (n1 is matrix row 0).
        Assert.True(mna.GetEntry(0, branchRow) == Complex.One,
            "DC-open: KCL column must have +1 at the node row");
    }

    // ── Diagnostics: print hero1 S-params at 1 GHz so we can see the error ──
    [Fact]
    public void Hero1_DiagnosticPrintAt1GHz()
    {
        var dir     = Path.Combine(AppContext.BaseDirectory, "..");
        string Hero1Dir()
        {
            var d = AppContext.BaseDirectory;
            while (d is not null)
            {
                var c = Path.Combine(d, "testdata", "Hero1");
                if (Directory.Exists(c)) return c;
                d = Path.GetDirectoryName(d);
            }
            throw new DirectoryNotFoundException("testdata/Hero1 not found");
        }

        var cnlPath = Path.Combine(Hero1Dir(), "hero1.cnl");
        var refPath = Path.Combine(Hero1Dir(), "hero1_golden_result.s4p");

        var (lib, tb) = CnlReader.ReadFile(cnlPath);
        var nl = new Elaborator(lib).Elaborate(tb);

        // Single frequency: 1 GHz (on-grid for reference)
        var ds = SParameterEngine.Run(nl, [1e9]);
        var refSnpRaw = RfCore.TouchstoneIO.ReadFile(refPath);
        var refSnp = RfCore.RFNetwork.Interpolate(refSnpRaw, [1e9],
            RfCore.InterpolationMethod.CubicSpline, RfCore.InterpolationFormat.RealImag,
            RfCore.MatrixType.S, RfCore.OutOfRangePolicy.WarnClamp);

        var rm = refSnp.Matrices[0];
        for (int r = 0; r < 4; r++)
        for (int c = 0; c < 4; c++)
        {
            var sv  = Sij(ds, r, c);
            double err = (sv - rm[r, c]).Magnitude;
            Console.WriteLine($"S[{r+1},{c+1}] sim={sv:G4}  ref={rm[r,c]:G4}  err={err:G4}");
        }

        // This test ALWAYS PASSES — it's diagnostic only
        Assert.True(true);
    }
}
