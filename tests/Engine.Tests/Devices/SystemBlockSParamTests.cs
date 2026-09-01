using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// The circulator, the directional coupler (which is also both hybrids) and the balun end to end
/// (brief-sys-3): a one-block netlist terminated in ideal ports, swept, returns EXACTLY the S its
/// parameters state — and the four properties that only a solve can show, because they are
/// statements about the network rather than about the matrix.
///
/// <list type="bullet">
/// <item><description><b>Non-reciprocity is MEASURED</b>, not assumed: the circulator's simulated
/// <c>S21</c> and <c>S12</c> differ by the full isolation, and reversing <c>Direction</c> exchanges
/// them exactly. No other component in the repository can fail this test.</description></item>
/// <item><description><b>The circulator's Y is computed from the SIMULATED S</b> and compared with
/// the antisymmetric closed form, which is the executable version of "this block has a Y but no
/// Z".</description></item>
/// <item><description><b>Two hybrids back to back reproduce the input</b> — the classic quadrature
/// identity, and the one gate here that catches a sign error no single-block measurement
/// can.</description></item>
/// <item><description><b>A differential load across the balun's balanced pair</b> is transformed by
/// the ratio the port impedances state.</description></item>
/// </list>
///
/// <para>Every expected number is computed here from the dB and degree values on the netlist line.
/// The tolerance is 1e-12: a wave constraint row solved exactly returns the matrix it was built
/// from to machine precision, and anything looser would hide a dropped √Z0.</para>
/// </summary>
public class SystemBlockSParamTests(ITestOutputHelper output)
{
    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    private static Complex[,] SAt(string cnl, double freqHz = 1e9) => Sweep(cnl, [freqHz])[0];

    private static Complex[][,] Sweep(string cnl, double[] freqs)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, freqs);
        var c  = ds["S"];
        int n  = c.Axes[1].Length;

        var all = new Complex[freqs.Length][,];
        for (int f = 0; f < freqs.Length; f++)
        {
            var s = new Complex[n, n];
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                s[i, j] = (Complex)c[f, i, j];
            all[f] = s;
        }
        return all;
    }

    private static double Amp(double db)  => Math.Pow(10.0, -db / 20.0);
    private static double Supp(double db) => db >= 150.0 ? 0.0 : Amp(db);

    private static void Near(Complex expected, Complex actual, double tol = 1e-12)
        => Assert.True((expected - actual).Magnitude < tol,
                       $"expected {expected}, got {actual} (|Δ| = {(expected - actual).Magnitude:G6})");

    private static string Ports(int n)
        => string.Join("\n", Enumerable.Range(1, n).Select(k => $"Port:P{k}  n{k} 0  Num={k}  Z=50 Ohm"));

    private static string Nets(int n) => string.Join(" ", Enumerable.Range(1, n).Select(k => $"n{k} 0"));

    // ══ Circulator ════════════════════════════════════════════════════════════

    private static string CirculatorNet(string dir, double il, double iso, double rl) => $@"
{Ports(3)}
Circulator:C1  {Nets(3)}  Direction={dir} IL={N(il)} Isolation={N(iso)} RL={N(rl)} Z0=50
";

    [Theory]
    [InlineData("CW",  0.0, 200.0, 200.0)]
    [InlineData("CCW", 0.0, 200.0, 200.0)]
    [InlineData("CW",  0.4,  20.0,  18.0)]
    [InlineData("CCW", 0.4,  20.0,  18.0)]
    [InlineData("CW",  1.2,  35.0,  25.0)]
    public void Circulator_MeasuresTheMatrixItsParametersDescribe(
        string dir, double il, double iso, double rl)
    {
        var s = SAt(CirculatorNet(dir, il, iso, rl));

        double fwd = Amp(il), rev = Supp(iso), refl = Supp(rl);
        bool   cw  = dir == "CW";

        for (int p = 0; p < 3; p++)
        {
            Near(new Complex(refl, 0), s[p, p]);
            int next = (p + 1) % 3;                      // CW carries port p to port p+1
            Near(new Complex(cw ? fwd : rev, 0), s[next, p]);
            Near(new Complex(cw ? rev : fwd, 0), s[p, next]);
        }
    }

    [Theory]
    [InlineData(20.0)]
    [InlineData(35.0)]
    public void Circulator_NonReciprocityIsMeasured_NotAssumed(double isolationDb)
    {
        // S21 and S12 must differ by the FULL isolation, on all three port pairs. This is the only
        // component in the repository with S ≠ Sᵀ, so it is also the only test here that a
        // symmetric stamp could not pass by accident.
        var s = SAt(CirculatorNet("CW", 0.0, isolationDb, 200.0));

        foreach (var (fwd, rev) in new[] { ((1, 0), (0, 1)), ((2, 1), (1, 2)), ((0, 2), (2, 0)) })
        {
            double db = 20.0 * Math.Log10(s[fwd.Item1, fwd.Item2].Magnitude
                                        / s[rev.Item1, rev.Item2].Magnitude);
            output.WriteLine($"S[{fwd}]/S[{rev}] = {db:F9} dB against a stated {isolationDb} dB");
            Assert.Equal(isolationDb, db, 10);
        }
    }

    [Fact]
    public void Circulator_AtItsDefaults_LeaksNothingAtAll_AndReversingExchangesThem()
    {
        // "Ideal" means the reverse entry is ABSENT, not 200 dB down, so the measured reverse
        // transmission is zero to machine precision rather than 1e-10.
        var cw  = SAt(CirculatorNet("CW",  0, 200, 200));
        var ccw = SAt(CirculatorNet("CCW", 0, 200, 200));

        Near(Complex.One,  cw[1, 0]);
        Near(Complex.Zero, cw[0, 1], 1e-14);

        for (int p = 0; p < 3; p++)
        for (int q = 0; q < 3; q++)
            Near(cw[q, p], ccw[p, q]);
    }

    [Fact]
    public void Circulator_TheSimulatedIdealYIsTheAntisymmetricOne_AndItHasNoZAtAll()
    {
        // Computed from the SIMULATED S rather than from the model's own buffer: det(I − S) = 0
        // exactly, so Z = Z0(I+S)(I−S)⁻¹ does not exist, while Y = (1/Z0)(I−S)(I+S)⁻¹ does and is
        // antisymmetric with a zero diagonal — itself singular, because every row and column sums
        // to zero as a floating network's must. SYS-4's memoryless overlay needs exactly this
        // matrix, and this is where the claim in CirculatorModel's doc comment is checked against a
        // real solve.
        var s = SAt(CirculatorNet("CW", 0, 200, 200));

        Assert.True(Det3(Add(Eye(), s, -1)).Magnitude < 1e-14,
                    $"det(I − S) = {Det3(Add(Eye(), s, -1))}, which should be identically zero");
        Assert.Equal(2.0, Det3(Add(Eye(), s, +1)).Real, 12);

        var y = YFromS(s, 50.0);
        var expected = new[,] { { 0.0, 1.0, -1.0 }, { -1.0, 0.0, 1.0 }, { 1.0, -1.0, 0.0 } };
        for (int p = 0; p < 3; p++)
        {
            Complex rowSum = Complex.Zero, colSum = Complex.Zero;
            for (int q = 0; q < 3; q++)
            {
                Near(new Complex(expected[p, q] / 50.0, 0), y[p, q], 1e-13);
                rowSum += y[p, q];
                colSum += y[q, p];
            }
            Assert.True(rowSum.Magnitude < 1e-13 && colSum.Magnitude < 1e-13);
        }
    }

    // ══ Coupler, and both hybrids ═════════════════════════════════════════════

    private static string CouplerNet(double coupling, double phase, double directivity,
                                     double il, double rl) => $@"
{Ports(4)}
Coupler:CPL1  {Nets(4)}  Coupling={N(coupling)} Phase={N(phase)} deg Directivity={N(directivity)} IL={N(il)} RL={N(rl)} Z0=50
";

    [Theory]
    [InlineData(20.0,    90.0, 200.0, 0.0, 200.0)]
    [InlineData(3.0103,  90.0, 200.0, 0.0, 200.0)]
    [InlineData(3.0103, 180.0, 200.0, 0.0, 200.0)]
    [InlineData(10.0,     0.0,  25.0, 0.5,  20.0)]
    [InlineData(6.0,     90.0,  18.0, 1.0,  15.0)]
    [InlineData(30.0,   180.0,  30.0, 0.2,  22.0)]
    public void Coupler_MeasuresTheMatrixItsParametersDescribe(
        double coupling, double phase, double directivity, double il, double rl)
    {
        var s = SAt(CouplerNet(coupling, phase, directivity, il, rl), 2e9);

        double  c    = Amp(coupling);
        double  loss = Amp(il);
        Complex thru = new(Math.Sqrt(1.0 - c * c) * loss, 0);
        Complex cpl  = Complex.FromPolarCoordinates(c * loss, -phase * Math.PI / 180.0);   // the netlist says "deg"
        Complex iso  = new(c * Supp(directivity) * loss, 0);
        Complex refl = new(Supp(rl), 0);

        var expected = new Complex[4, 4];
        for (int p = 0; p < 4; p++) expected[p, p] = refl;
        expected[0, 1] = expected[1, 0] = expected[2, 3] = expected[3, 2] = thru;
        expected[0, 2] = expected[2, 0] = expected[1, 3] = expected[3, 1] = cpl;
        expected[0, 3] = expected[3, 0] = expected[1, 2] = expected[2, 1] = iso;

        for (int p = 0; p < 4; p++)
        for (int q = 0; q < 4; q++)
            Near(expected[p, q], s[p, q]);
    }

    [Theory]
    [InlineData(3.0103)]
    [InlineData(10.0)]
    [InlineData(20.0)]
    public void Coupler_EnergyBalancesAndTheIsolatedPortIsExactlyZero_AcrossTheSweep(double coupling)
    {
        double[] freqs = [0.0, 1e6, 1e9, 5e9, 2e10];
        foreach (var s in Sweep(CouplerNet(coupling, 90, 200, 0, 200), freqs))
        {
            double thru = s[1, 0].Magnitude, cpl = s[2, 0].Magnitude;
            Assert.Equal(1.0, thru * thru + cpl * cpl, 12);
            Assert.Equal(Amp(coupling), cpl, 12);

            // With the directivity off, NO entry is stamped — the exactness of that is gated on the
            // matrix itself in SystemBlockSMatrixTests; what a solve returns is that absence plus
            // its own roundoff, which is ~1e-15 here and would be ~1e-10 if a 200 dB term had been
            // stamped instead of skipped. The two numbers are five orders apart, so this DOES
            // separate "absent" from "small".
            Assert.True(s[3, 0].Magnitude < 1e-14, $"isolated port holds {s[3, 0]}");
            Assert.True(s[2, 1].Magnitude < 1e-14, $"the other isolated pair holds {s[2, 1]}");
        }
    }

    [Fact]
    public void Coupler_HoldsItsQuadratureAtEveryFrequency()
    {
        // arg(S31) − arg(S21) = −90° everywhere, DC included. That is the idealisation the doc
        // comment names: a quadrature relationship held at every frequency is a Hilbert transform
        // rather than a network, which costs nothing in a frequency-domain simulator and is
        // exactly what a system block diagram wants.
        double[] freqs = [0.0, 1e6, 1e9, 5e9, 2e10];
        var sweep = Sweep(CouplerNet(3.0103, 90, 200, 0, 200), freqs);

        for (int f = 0; f < freqs.Length; f++)
        {
            var s = sweep[f];
            double deg = (s[2, 0].Phase - s[1, 0].Phase) * 180.0 / Math.PI;
            output.WriteLine($"{freqs[f]:G4} Hz: arg(S31) − arg(S21) = {deg:F12}°");
            Assert.Equal(-90.0, deg, 11);
        }
    }

    /// <summary>
    /// Two hybrids back to back, thru to thru and coupled to coupled — the classic quadrature
    /// combiner identity, and the one gate here that catches a sign error no single-block
    /// measurement can. Everything arrives at the second hybrid's ISOLATED port and nothing at its
    /// IN port, because the two paths add in one and cancel in the other:
    /// <code>
    ///   S(iso)  = c·t + t·c = 2·c·t·e^(−j90°) = −j   at an exactly equal split
    ///   S(in)   = t·t + (−jc)(−jc) = t² − c² = 0     ditto
    /// </code>
    /// <para>The cancellation at the IN port is what makes this a sign gate: it is a DIFFERENCE of
    /// two terms of the same size, so it holds only if the −90° went onto the right arms.</para>
    /// </summary>
    [Theory]
    [InlineData("3.0103")]                    // what the Hybrid90 TILE places
    [InlineData("3.0102999566398121")]        // and an exactly equal split
    public void TwoHybridsBackToBack_ReproduceTheInput(string couplingDb)
    {
        var s = SAt($@"
Port:P1  a 0  Num=1  Z=50 Ohm
Port:P2  z 0  Num=2  Z=50 Ohm
Port:P3  t2 0 Num=3  Z=50 Ohm
Coupler:H1  a 0  m 0  n 0  t1 0   Coupling={couplingDb} Phase=90 deg Directivity=200 IL=0 RL=200 Z0=50
Coupler:H2  t2 0 m 0  n 0  z  0   Coupling={couplingDb} Phase=90 deg Directivity=200 IL=0 RL=200 Z0=50
R:R1  t1 0  R=50
");
        double c = Amp(double.Parse(couplingDb, CultureInfo.InvariantCulture));
        double t = Math.Sqrt(1.0 - c * c);

        // Everything out of the second hybrid's ISO port, at −90°, with unit magnitude.
        Near(new Complex(0, -2.0 * c * t), s[1, 0]);
        Assert.Equal(1.0, s[1, 0].Magnitude, 12);
        Assert.Equal(-90.0, s[1, 0].Phase * 180.0 / Math.PI, 12);

        // and the cancellation at its IN port, which is exact only when the split is exact — at the
        // tile's own 3.0103 dB the residue is t² − c² ≈ 1e−8, and the test says so rather than
        // hiding it behind a tolerance.
        Near(new Complex(t * t - c * c, 0), s[2, 0]);
        output.WriteLine($"coupling {couplingDb} dB: leakage back to the second hybrid's IN port "
                       + $"= {s[2, 0].Magnitude:E3} (t² − c² = {t * t - c * c:E3})");

        Near(Complex.Zero, s[0, 0], 1e-11);   // and the input port stays matched
    }

    // ══ Balun ═════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(1.5, 0.8, 6.0)]
    [InlineData(0.5, -1.2, -4.0)]
    public void Balun_MeasuresTheMatrixItsParametersDescribe(double il, double ampImb, double phaseImb)
    {
        var s = SAt($@"
{Ports(3)}
Balun:B1  {Nets(3)}  Zunb=50 Zbal=50 IL={N(il)} AmpImb={N(ampImb)} PhaseImb={N(phaseImb)} deg
");
        double  loss = Amp(il);
        double  k    = Math.Pow(10.0, ampImb / 40.0);
        double  half = 1.0 / Math.Sqrt(2.0);
        Complex plus  = new(half * k * loss, 0);
        Complex minus = -Complex.FromPolarCoordinates(half / k * loss, -phaseImb * Math.PI / 180.0);

        Near(plus,  s[1, 0]); Near(plus,  s[0, 1]);
        Near(minus, s[2, 0]); Near(minus, s[0, 2]);
        Near(Complex.Zero, s[0, 0]);
        foreach (var (p, q) in new[] { (1, 1), (2, 2), (1, 2), (2, 1) })
            Near(new Complex(0.5, 0), s[p, q]);
    }

    [Fact]
    public void Balun_AtZeroImbalance_GivesExactlyAntiphaseOutputsOfEqualMagnitude()
    {
        var s = SAt($@"
{Ports(3)}
Balun:B1  {Nets(3)}  Zunb=50 Zbal=50
");
        Assert.Equal(s[1, 0].Magnitude, s[2, 0].Magnitude, 14);
        Assert.Equal(1.0 / Math.Sqrt(2.0), s[1, 0].Magnitude, 12);
        Assert.Equal(180.0, Math.Abs(s[2, 0].Phase - s[1, 0].Phase) * 180.0 / Math.PI, 11);
        Near(Complex.Zero, s[0, 0]);
    }

    /// <summary>
    /// A differential load <c>R</c> across BAL+/BAL− is seen at the unbalanced port as
    /// <c>R·Zunb/(2·Zbal)</c> — the ideal transformer of turns ratio <c>n = √(2·Zbal/Zunb)</c> that
    /// the block's modal form is. The test computes the expected reflection itself.
    ///
    /// <para>The load is CENTRE-TAPPED to ground for a reason worth knowing: a purely floating
    /// resistor between the two balanced nodes leaves the common-mode potential undetermined,
    /// because the ideal balun's common mode is an OPEN (<c>S_cc = +1</c>, i.e. <c>i₂ + i₃ = 0</c>)
    /// and the floating resistor says the same thing — two identical rows, and a rank-deficient
    /// matrix. See <see cref="Balun_AFloatingDifferentialLoadIsAGenuineFloatingNode"/>, which
    /// measures exactly that. Two R/2 halves with the tap grounded is the same differential load
    /// and pins the common mode, and since the unbalanced port does not couple to the common mode
    /// at all the answer is unchanged.</para>
    /// </summary>
    [Theory]
    [InlineData(100.0, 50.0, 50.0)]     // the ordinary 1:2 balun, matched
    [InlineData(200.0, 50.0, 50.0)]
    [InlineData( 50.0, 50.0, 50.0)]
    [InlineData( 50.0, 50.0, 25.0)]     // 50 Ω differential, still matched
    [InlineData(100.0, 75.0, 50.0)]
    public void Balun_ADifferentialLoadSeesTheStatedImpedanceTransformation(
        double rDiff, double zUnb, double zBal)
    {
        var s = SAt($@"
Port:P1  a 0  Num=1  Z={N(zUnb)} Ohm
Balun:B1 a 0 p 0 n 0  Zunb={N(zUnb)} Zbal={N(zBal)}
R:Rd1    p m  R={N(rDiff / 2)}
R:Rd2    m n  R={N(rDiff / 2)}
R:Rct    m 0  R=1e-9
");
        double zin = rDiff * zUnb / (2.0 * zBal);
        output.WriteLine($"R_diff {rDiff} Ω through {zUnb}/{zBal} → Zin {zin} Ω, S11 = {s[0, 0]}");
        Near(new Complex((zin - zUnb) / (zin + zUnb), 0), s[0, 0], 1e-11);
    }

    /// <summary>
    /// The same measurement with the load left FLOATING, recorded rather than avoided: it is a
    /// genuine floating-node case, the engine says so in its own warning, and its gmin
    /// regularisation still lands within ~1e-10 of the right answer instead of on it. Nothing here
    /// is wrong — an ideal balun really does leave the common-mode potential of a floating
    /// differential load undefined — but a reader who writes that netlist and sees 5e-11 where the
    /// rest of this file holds 1e-15 should find the reason here.
    /// </summary>
    [Fact]
    public void Balun_AFloatingDifferentialLoadIsAGenuineFloatingNode()
    {
        const string cnl = @"
Port:P1  a 0  Num=1  Z=50 Ohm
Balun:B1 a 0 p 0 n 0  Zunb=50 Zbal=50
R:Rd     p n  R=100
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, [1e9]);
        var s11 = (Complex)ds["S"][0, 0, 0];

        output.WriteLine($"floating differential load: S11 = {s11}");
        foreach (var w in nl.Warnings) output.WriteLine($"warning: {w}");

        Assert.Contains(nl.Warnings, w => w.Contains("regularization", StringComparison.OrdinalIgnoreCase));
        Assert.True(s11.Magnitude < 1e-9,  "the regularised answer is still the matched one");
        Assert.True(s11.Magnitude > 1e-15, "and it is NOT machine-precision — that is the point");
    }

    // ── small dense helpers, so the gate does its own arithmetic ──────────────

    private static Complex[,] Eye()
    {
        var m = new Complex[3, 3];
        for (int i = 0; i < 3; i++) m[i, i] = Complex.One;
        return m;
    }

    private static Complex[,] Add(Complex[,] a, Complex[,] b, double sign)
    {
        var m = new Complex[3, 3];
        for (int p = 0; p < 3; p++)
        for (int q = 0; q < 3; q++)
            m[p, q] = a[p, q] + sign * b[p, q];
        return m;
    }

    private static Complex Det3(Complex[,] m)
        => m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
         - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
         + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

    /// <summary>Y = (1/Z0)·(I − S)(I + S)⁻¹ for a uniform real reference impedance.</summary>
    private static Complex[,] YFromS(Complex[,] s, double z0)
    {
        var plus = Add(Eye(), s, +1);
        Complex det = Det3(plus);
        var inv = new Complex[3, 3];
        for (int p = 0; p < 3; p++)
        for (int q = 0; q < 3; q++)
        {
            int r0 = (q + 1) % 3, r1 = (q + 2) % 3;
            int c0 = (p + 1) % 3, c1 = (p + 2) % 3;
            inv[p, q] = (plus[r0, c0] * plus[r1, c1] - plus[r0, c1] * plus[r1, c0]) / det;
        }

        var num = Add(Eye(), s, -1);
        var y   = new Complex[3, 3];
        for (int p = 0; p < 3; p++)
        for (int q = 0; q < 3; q++)
        {
            Complex acc = Complex.Zero;
            for (int k = 0; k < 3; k++) acc += num[p, k] * inv[k, q];
            y[p, q] = acc / z0;
        }
        return y;
    }
}
