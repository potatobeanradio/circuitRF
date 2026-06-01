using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Small analytically-verified circuits to confirm extraction correctness
/// before running the full Hero 1 gate.
/// </summary>
public class SanityTests
{
    private static RfCore.SNP Run(string cnl, double[] freqsHz)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return SParameterEngine.Run(nl, freqsHz);
    }

    // ── 1-port: 50Ω shunt — S11 = 0 (matched) ───────────────────────────────
    [Fact]
    public void OnePort_MatchedLoad_S11IsZero()
    {
        var snp = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
R:R1  n1 0  R=50 Ohm
", [1e9]);

        var s11 = snp.Matrices[0][0, 0];
        Assert.True(s11.Magnitude < 1e-8, $"S11={s11:G4}, expected ≈ 0");
    }

    // ── 2-port: pure resistive π-network, exact S-params known ───────────────
    // Topology: R_shunt=100Ω at each port, R_series=50Ω between them.
    // Computed analytically: S11 = S22 = −1/21 ≈ −0.04762, S21 = S12 = 4/21 ≈ 0.38095
    [Fact]
    public void TwoPort_PiResistor_SParamsMatchAnalytic()
    {
        var snp = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n2 0  Num=2 Z=50 Ohm
R:R1  n1 0   R=100 Ohm
R:R2  n2 0   R=100 Ohm
R:Rs  n1 n2  R=50 Ohm
", [1e9]);

        var s = snp.Matrices[0];
        const double Tol = 1e-6;
        Assert.True(Math.Abs(s[0, 0].Real - (-1.0 / 21)) < Tol, $"S11={s[0,0]:G4}");
        Assert.True(Math.Abs(s[1, 0].Real - (8.0 / 21)) < Tol, $"S21={s[1,0]:G4}");
        Assert.True(Math.Abs(s[0, 1].Real - (8.0 / 21)) < Tol, $"S12={s[0,1]:G4}");
        Assert.True(Math.Abs(s[1, 1].Real - (-1.0 / 21)) < Tol, $"S22={s[1,1]:G4}");
        Assert.True(s[0, 0].Imaginary < Tol, $"S11 imag={s[0,0].Imaginary:G4}");
        Assert.True(s[1, 0].Imaginary < Tol, $"S21 imag={s[1,0].Imaginary:G4}");
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

        var snp = Run(@"
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

        var s = snp.Matrices[0];
        const double Tol = 1e-6;
        Assert.True((s[0, 0] - s11a).Magnitude < Tol, $"S11 sim={s[0,0]:G4} vs {s11a:G4}");
        Assert.True((s[1, 0] - s21a).Magnitude < Tol, $"S21 sim={s[1,0]:G4} vs {s21a:G4}");
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
        var simSnp = SParameterEngine.Run(nl, [1e9]);
        var refSnpRaw = RfCore.TouchstoneIO.ReadFile(refPath);
        var refSnp = RfCore.RFNetwork.Interpolate(refSnpRaw, [1e9],
            RfCore.InterpolationMethod.CubicSpline, RfCore.InterpolationFormat.RealImag,
            RfCore.MatrixType.S, RfCore.OutOfRangePolicy.WarnClamp);

        var sm = simSnp.Matrices[0];
        var rm = refSnp.Matrices[0];
        for (int r = 0; r < 4; r++)
        for (int c = 0; c < 4; c++)
        {
            double err = (sm[r, c] - rm[r, c]).Magnitude;
            Console.WriteLine($"S[{r+1},{c+1}] sim={sm[r,c]:G4}  ref={rm[r,c]:G4}  err={err:G4}");
        }

        // This test ALWAYS PASSES — it's diagnostic only
        Assert.True(true);
    }
}
