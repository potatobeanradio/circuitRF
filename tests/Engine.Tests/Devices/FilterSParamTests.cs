using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Systems;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// The ideal filter and the duplexer end to end (brief-sys-6): a one-block netlist terminated in
/// ideal ports, swept, returns EXACTLY the rational S its parameters state — and the properties
/// only a solve can show, because they are statements about a NETWORK rather than about a matrix.
///
/// <list type="bullet">
/// <item><description><b>The three degenerate DC limits SOLVE.</b> A lowpass at ω = 0 is an ideal
/// through, which has no Y matrix; a highpass and a bandpass at ω = 0 are an open at one end and a
/// short at the other, which has neither a Y nor a Z. A matrix comparison cannot show that those
/// assemble and factorise — only a solve can.</description></item>
/// <item><description><b>Group delay is measured off the SIMULATED phase</b>, by differencing it,
/// which is the only gate the Bessel family has and the only one that would catch a magnitude-only
/// response.</description></item>
/// <item><description><b>An unequal-impedance filter is measured in a UNIFORM 50 Ω system</b> and
/// renormalised here, by this file's own arithmetic, back to the pair it was designed
/// against.</description></item>
/// <item><description><b>The duplexer's arms are compared with the standalone filters</b>, and its
/// TX-to-RX isolation is MEASURED and reported rather than asserted against a number pulled from
/// the air — it is a consequence of the two responses and the junction, which is exactly why the
/// component has no isolation parameter.</description></item>
/// </list>
/// </summary>
public class FilterSParamTests(ITestOutputHelper output)
{
    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

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

    private static Complex[,] SAt(string cnl, double freqHz) => Sweep(cnl, [freqHz])[0];

    private static double Db(Complex z) => 20.0 * Math.Log10(z.Magnitude);

    private static void Near(Complex expected, Complex actual, double tol = 1e-12)
        => Assert.True((expected - actual).Magnitude < tol,
                       $"expected {expected}, got {actual} (|Δ| = {(expected - actual).Magnitude:G6})");

    private static string TwoPorts(double z1 = 50, double z2 = 50) => $@"
Port:P1  n1 0  Num=1  Z={N(z1)} Ohm
Port:P2  n2 0  Num=2  Z={N(z2)} Ohm";

    private static string FilterNet(string response, string form, int order,
                                    double fcGHz = 1.0, double f1GHz = 0.9, double f2GHz = 1.1,
                                    double ripple = 0.1, double astop = 40.0,
                                    double zIn = 50, double zOut = 50, double il = 0.0,
                                    double portZ1 = 50, double portZ2 = 50) => $@"
{TwoPorts(portZ1, portZ2)}
Filter:F1  n1 0 n2 0  Response={response} Form={form} Order={order} \
  Fc={N(fcGHz)} GHz F1={N(f1GHz)} GHz F2={N(f2GHz)} GHz \
  Ripple={N(ripple)} Astop={N(astop)} Zin={N(zIn)} Ohm Zout={N(zOut)} Ohm IL={N(il)}
";

    private static FilterModel Model(FilterResponse response, NetworkForm form, int order,
                                     double fcGHz = 1.0, double f1GHz = 0.9, double f2GHz = 1.1,
                                     double ripple = 0.1, double astop = 40.0,
                                     double zIn = 50, double zOut = 50, double il = 0.0)
        => new(response, form, order, fcGHz * 1e9, f1GHz * 1e9, f2GHz * 1e9,
               ripple, astop, zIn, zOut, il);

    // ══ The measured S is the stated S ════════════════════════════════════════

    [Theory]
    [InlineData("Butterworth",  "Lowpass",  3)]
    [InlineData("Butterworth",  "Highpass", 5)]
    [InlineData("Butterworth",  "Bandpass", 2)]
    [InlineData("Chebyshev",    "Bandpass", 3)]
    [InlineData("Chebyshev",    "Lowpass",  6)]
    [InlineData("InvChebyshev", "Bandpass", 4)]
    [InlineData("InvChebyshev", "Highpass", 5)]
    [InlineData("Bessel",       "Lowpass",  4)]
    [InlineData("Bessel",       "Bandpass", 3)]
    [InlineData("Elliptic",     "Bandpass", 5)]
    [InlineData("Elliptic",     "Lowpass",  3)]
    public void AFilterMeasuresTheRationalResponseItsParametersDescribe(
        string response, string form, int order)
    {
        var model = Model(Enum.Parse<FilterResponse>(response), Enum.Parse<NetworkForm>(form), order);

        double[] freqs = [0.2e9, 0.5e9, 0.85e9, 0.95e9, 1.0e9, 1.05e9, 1.15e9, 1.6e9, 4.0e9];
        var swept = Sweep(FilterNet(response, form, order), freqs);

        for (int k = 0; k < freqs.Length; k++)
        {
            var expected = model.SAt(2 * Math.PI * freqs[k]);
            for (int p = 0; p < 2; p++)
            for (int q = 0; q < 2; q++)
                Near(expected[p, q], swept[k][p, q]);
        }
    }

    /// <summary>
    /// <c>|S11|² + |S21|² = 1</c> to 1e-12 across the whole sweep and every form, at <c>IL = 0</c>.
    /// A lossless network is what the response family promises, and this is the gate that would
    /// catch a dropped √Z0 anywhere in the stamp.
    /// </summary>
    [Theory]
    [InlineData("Lowpass")] [InlineData("Highpass")] [InlineData("Bandpass")]
    public void ALosslessFilterIsMeasuredLossless(string form)
    {
        foreach (string response in (string[])["Butterworth", "Chebyshev", "InvChebyshev", "Bessel", "Elliptic"])
        {
            double[] freqs = [.. Enumerable.Range(1, 40).Select(k => k * 0.08e9)];
            var swept = Sweep(FilterNet(response, form, 4), freqs);

            for (int k = 0; k < freqs.Length; k++)
            {
                var s = swept[k];
                double power = s[0, 0].Magnitude * s[0, 0].Magnitude + s[1, 0].Magnitude * s[1, 0].Magnitude;
                Assert.True(Math.Abs(power - 1.0) < 1e-12,
                    $"{response} {form} at {freqs[k] / 1e9:F2} GHz: |S11|² + |S21|² = {power:G17}");
                Near(s[1, 0], s[0, 1]);                       // reciprocal
            }
        }
    }

    [Theory]
    [InlineData(0.5)] [InlineData(3.0)]
    public void AnInsertionLossIsMeasuredAsDissipationRatherThanReflection(double ilDb)
    {
        var lossy = SAt(FilterNet("Chebyshev", "Bandpass", 3, il: ilDb), 1.0e9);
        var ideal = SAt(FilterNet("Chebyshev", "Bandpass", 3, il: 0.0),  1.0e9);

        Near(ideal[1, 0] * Math.Pow(10.0, -ilDb / 20.0), lossy[1, 0]);
        Near(ideal[0, 0], lossy[0, 0]);                        // S11 untouched: it dissipates
        Assert.True(lossy[0, 0].Magnitude * lossy[0, 0].Magnitude
                  + lossy[1, 0].Magnitude * lossy[1, 0].Magnitude < 1.0 - 1e-9);
    }

    // ══ The degenerate DC limits ══════════════════════════════════════════════

    /// <summary>
    /// All three DC limits SOLVE, and each is exactly the network it should be. These are the cases
    /// <c>IdealSBlockModel</c>'s wave constraint exists for: a through has no Y, an open-and-short
    /// pair has neither a Y nor a Z, and a Z- or Y-based stamp would have nothing to write down.
    /// </summary>
    [Theory]
    [InlineData("Butterworth", 3)] [InlineData("Chebyshev", 3)]
    [InlineData("Bessel", 4)]      [InlineData("Elliptic", 5)]
    public void ALowpassAtDcSolvesAndIsAnExactThrough(string response, int order)
    {
        var s = SAt(FilterNet(response, "Lowpass", order), 0.0);
        Near(Complex.Zero, s[0, 0]);
        Near(Complex.Zero, s[1, 1]);
        Near(Complex.One,  s[1, 0]);
        Near(Complex.One,  s[0, 1]);
    }

    [Theory]
    [InlineData("Butterworth", "Highpass", 3)] [InlineData("Butterworth", "Bandpass", 3)]
    [InlineData("Chebyshev",   "Highpass", 4)] [InlineData("Chebyshev",   "Bandpass", 5)]
    [InlineData("Bessel",      "Highpass", 2)] [InlineData("Bessel",      "Bandpass", 3)]
    public void AFormThatBlocksDcSolvesAndPassesNothing(string response, string form, int order)
    {
        var s = SAt(FilterNet(response, form, order), 0.0);
        Near(Complex.Zero, s[1, 0]);
        Near(Complex.One,  s[0, 0]);                 // an OPEN at port 1, exactly
        Assert.Equal(1.0, s[1, 1].Magnitude, 12);    // an open or a short at port 2, by parity
    }

    // ══ The transformations, measured ═════════════════════════════════════════

    [Fact]
    public void AHighpassMirrorsItsLowpassAboutTheCutoff()
    {
        const double Fc = 1.4;
        double[] ratios = [0.15, 0.4, 0.7, 1.0, 1.6, 3.0];

        var lo = Sweep(FilterNet("Chebyshev", "Lowpass",  4, fcGHz: Fc, ripple: 0.2),
                       [.. ratios.Select(r => Fc * r * 1e9)]);
        var hi = Sweep(FilterNet("Chebyshev", "Highpass", 4, fcGHz: Fc, ripple: 0.2),
                       [.. ratios.Select(r => Fc / r * 1e9)]);

        for (int k = 0; k < ratios.Length; k++)
        {
            Assert.Equal(lo[k][1, 0].Magnitude, hi[k][1, 0].Magnitude, 12);
            Assert.Equal(lo[k][0, 0].Magnitude, hi[k][0, 0].Magnitude, 12);
        }
    }

    [Fact]
    public void ABandpassIsGeometricallySymmetricAboutTheGeometricMeanOfItsEdges()
    {
        const double F1 = 1.7, F2 = 2.3;
        double f0 = Math.Sqrt(F1 * F2);
        string net = FilterNet("Chebyshev", "Bandpass", 3, f1GHz: F1, f2GHz: F2);

        foreach (double f in new[] { 1.2, 1.8, 2.0, 3.1, 6.0 })
        {
            var a = SAt(net, f * 1e9);
            var b = SAt(net, f0 * f0 / f * 1e9);
            Assert.Equal(a[1, 0].Magnitude, b[1, 0].Magnitude, 12);
        }

        // …and the two stated edges sit at the same level, which the arithmetic mirror would not give.
        Assert.Equal(SAt(net, F1 * 1e9)[1, 0].Magnitude, SAt(net, F2 * 1e9)[1, 0].Magnitude, 12);
    }

    // ══ Bessel: the group delay off the simulated phase ═══════════════════════

    /// <summary>
    /// The one gate this family has, and the one a magnitude-only response would fail outright:
    /// group delay, differenced from the phase a SOLVE returned, is flat at DC and monotone after.
    /// </summary>
    [Theory]
    [InlineData(3)] [InlineData(5)] [InlineData(7)]
    public void ABesselLowpassHasTheGroupDelayItsOrderPromises(int order)
    {
        const double Fc = 1.0;                                     // GHz
        double expected = 1.0 / (2 * Math.PI * Fc * 1e9);          // the delay convention: τ(0) = 1/ω_c

        double[] freqs = [.. Enumerable.Range(0, 61).Select(k => k * 0.05e9)];
        var swept = Sweep(FilterNet("Bessel", "Lowpass", order, fcGHz: Fc), freqs);

        // Unwrap and difference: τ = −dφ/dω.
        var phase = new double[freqs.Length];
        double turns = 0.0;
        for (int k = 0; k < freqs.Length; k++)
        {
            double p = swept[k][1, 0].Phase;
            if (k > 0 && p - phase[k - 1] + turns > Math.PI) turns -= 2 * Math.PI;
            if (k > 0 && p - phase[k - 1] + turns < -Math.PI) turns += 2 * Math.PI;
            phase[k] = p + turns;
        }

        var tau = new double[freqs.Length - 1];
        for (int k = 0; k < tau.Length; k++)
            tau[k] = -(phase[k + 1] - phase[k]) / (2 * Math.PI * (freqs[k + 1] - freqs[k]));

        // Flat at DC to the order's own tolerance — the second difference of the phase is what
        // "maximally flat delay" means, so the first few samples must agree with each other.
        Assert.Equal(expected, tau[0], 12);
        Assert.Equal(expected, tau[1], 12);

        // …and monotone thereafter. The first sample is a half-step above DC, so the comparison
        // starts there rather than at an extrapolated τ(0).
        for (int k = 1; k < tau.Length; k++)
            Assert.True(tau[k] <= tau[k - 1] + 1e-9 * expected,
                $"order {order}: group delay rose between {freqs[k] / 1e9:F2} and {freqs[k + 1] / 1e9:F2} GHz");

        output.WriteLine($"Bessel order {order}: τ(DC) = {tau[0] * 1e12:F4} ps, " +
                         $"τ(Fc) = {tau[19] * 1e12:F4} ps, τ(3·Fc) = {tau[59] * 1e12:F4} ps");
    }

    // ══ Unequal port impedances ═══════════════════════════════════════════════

    /// <summary>
    /// A filter designed against <c>Zin = 50</c>, <c>Zout = 25</c> is matched at BOTH its ports in
    /// its passband — which a doubly-terminated ladder could not have been, since its termination
    /// ratio is fixed by the family and the order. That is the whole reason this component stamps an
    /// S-matrix.
    /// </summary>
    [Fact]
    public void AFilterWithUnequalPortImpedancesIsMatchedAtBothPortsInItsPassband()
    {
        // Ports referenced to the filter's OWN impedances: this is the system it was designed for.
        string net = FilterNet("Chebyshev", "Bandpass", 3, zIn: 50, zOut: 25, portZ1: 50, portZ2: 25);

        foreach (double f in new[] { 0.92e9, 0.95e9, 1.0e9, 1.05e9, 1.08e9 })
        {
            var s = SAt(net, f);
            Assert.True(Db(s[0, 0]) < -16.0, $"S11 at {f / 1e9:F2} GHz is {Db(s[0, 0]):F2} dB");
            Assert.True(Db(s[1, 1]) < -16.0, $"S22 at {f / 1e9:F2} GHz is {Db(s[1, 1]):F2} dB");
            Assert.True(Db(s[1, 0]) > -0.11, $"S21 at {f / 1e9:F2} GHz is {Db(s[1, 0]):F2} dB");
        }
    }

    /// <summary>
    /// …and measured in a UNIFORM 50 Ω system it is the same network seen from a different
    /// reference: renormalising the 50 Ω measurement back onto (50, 25) reproduces the design
    /// exactly.
    /// </summary>
    /// <remarks>
    /// The renormalisation is written here, from the definition of a travelling wave against a real
    /// reference impedance, rather than taken from the library the model is built on. It is two
    /// 2×2 inversions:
    /// <code>
    ///   M  = D S D⁻¹        Z = (I − M)⁻¹ (I + M) Z0        D = diag(√Z0)
    /// </code>
    /// and back again at the new reference. A filter that was secretly renormalising internally, or
    /// stamping against the wrong √Z0, fails here and passes everything else in this file.
    /// </remarks>
    [Fact]
    public void TheUnequalImpedanceFilterRenormalisesOntoItsDesignReference()
    {
        string uniform = FilterNet("Chebyshev", "Bandpass", 3, zIn: 50, zOut: 25, portZ1: 50, portZ2: 50);
        var model = Model(FilterResponse.Chebyshev, NetworkForm.Bandpass, 3, zIn: 50, zOut: 25);

        foreach (double f in new[] { 0.7e9, 0.95e9, 1.0e9, 1.3e9 })
        {
            var measured = SAt(uniform, f);
            var z = ZFromS(measured, [50.0, 50.0]);
            var back = SFromZ(z, [50.0, 25.0]);
            var expected = model.SAt(2 * Math.PI * f);

            for (int p = 0; p < 2; p++)
            for (int q = 0; q < 2; q++)
                Near(expected[p, q], back[p, q], 1e-9);
        }
    }

    private static Complex[,] ZFromS(Complex[,] s, double[] z0)
    {
        var d = new[] { Math.Sqrt(z0[0]), Math.Sqrt(z0[1]) };
        var m = new Complex[2, 2];
        for (int p = 0; p < 2; p++)
        for (int q = 0; q < 2; q++)
            m[p, q] = d[p] * s[p, q] / d[q];

        var imM = new Complex[2, 2] { { 1 - m[0, 0], -m[0, 1] }, { -m[1, 0], 1 - m[1, 1] } };
        var ipM = new Complex[2, 2] { { 1 + m[0, 0],  m[0, 1] }, {  m[1, 0], 1 + m[1, 1] } };
        var zd  = new Complex[2, 2] { { z0[0], 0 }, { 0, z0[1] } };
        return Mul(Mul(Inv(imM), ipM), zd);
    }

    private static Complex[,] SFromZ(Complex[,] z, double[] z0)
    {
        var zd = new Complex[2, 2] { { z0[0], 0 }, { 0, z0[1] } };
        var minus = new Complex[2, 2] { { z[0, 0] - zd[0, 0], z[0, 1] }, { z[1, 0], z[1, 1] - zd[1, 1] } };
        var plus  = new Complex[2, 2] { { z[0, 0] + zd[0, 0], z[0, 1] }, { z[1, 0], z[1, 1] + zd[1, 1] } };
        var m = Mul(minus, Inv(plus));

        var d = new[] { Math.Sqrt(z0[0]), Math.Sqrt(z0[1]) };
        var s = new Complex[2, 2];
        for (int p = 0; p < 2; p++)
        for (int q = 0; q < 2; q++)
            s[p, q] = m[p, q] * d[q] / d[p];
        return s;
    }

    private static Complex[,] Mul(Complex[,] a, Complex[,] b)
    {
        var r = new Complex[2, 2];
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
            r[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j];
        return r;
    }

    private static Complex[,] Inv(Complex[,] a)
    {
        Complex det = a[0, 0] * a[1, 1] - a[0, 1] * a[1, 0];
        return new Complex[2, 2] { { a[1, 1] / det, -a[0, 1] / det }, { -a[1, 0] / det, a[0, 0] / det } };
    }

    // ══ The duplexer ══════════════════════════════════════════════════════════

    private const double TxLo = 0.90, TxHi = 1.00, RxLo = 1.10, RxHi = 1.20;

    private static string DuplexerNet(int order = 5, double zAnt = 50) => $@"
Port:P1  n1 0  Num=1  Z=50 Ohm
Port:P2  n2 0  Num=2  Z=50 Ohm
Port:P3  n3 0  Num=3  Z=50 Ohm
Duplexer:D1  n1 0 n2 0 n3 0  Zant={N(zAnt)} Ohm \
  TxResponse=Chebyshev TxForm=Bandpass TxOrder={order} TxF1={N(TxLo)} GHz TxF2={N(TxHi)} GHz TxRipple=0.1 \
  RxResponse=Chebyshev RxForm=Bandpass RxOrder={order} RxF1={N(RxLo)} GHz RxF2={N(RxHi)} GHz RxRipple=0.1
";

    private static string ArmNet(double f1, double f2, int order) => $@"
{TwoPorts()}
Filter:F1  n1 0 n2 0  Response=Chebyshev Form=Bandpass Order={order} \
  F1={N(f1)} GHz F2={N(f2)} GHz Ripple=0.1
";

    /// <summary>
    /// <b>The duplexer IS two filter stamps sharing one node — bit for bit.</b> Two separate
    /// <c>Filter</c> instances wired onto the same antenna net produce the identical 3-port S, at
    /// every frequency, to the last bit. That is the executable form of "no new mathematics at all",
    /// and it is the gate the component's whole design rests on.
    /// </summary>
    /// <remarks>
    /// This replaces brief-sys-6's "each arm reproduces the standalone filter's S21 to 1e-9 in its
    /// own passband", which is not true and cannot be — see the next test, which measures how far
    /// out it is and why. Comparing against two filters on a net is the comparison the brief was
    /// reaching for, and unlike the other it is exact.
    /// </remarks>
    [Fact]
    public void TheDuplexerIsExactlyTwoFilterStampsSharingOneNode()
    {
        const int Order = 5;
        string pair = $@"
Port:P1  n1 0  Num=1  Z=50 Ohm
Port:P2  n2 0  Num=2  Z=50 Ohm
Port:P3  n3 0  Num=3  Z=50 Ohm
Filter:FTX  n1 0 n2 0  Response=Chebyshev Form=Bandpass Order={Order} \
  F1={N(TxLo)} GHz F2={N(TxHi)} GHz Ripple=0.1
Filter:FRX  n1 0 n3 0  Response=Chebyshev Form=Bandpass Order={Order} \
  F1={N(RxLo)} GHz F2={N(RxHi)} GHz Ripple=0.1
";
        foreach (double f in new[] { 0.4, 0.9, 0.95, 1.0, 1.05, 1.15, 1.2, 2.5 })
        {
            var dup  = SAt(DuplexerNet(Order), f * 1e9);
            var pairS = SAt(pair, f * 1e9);
            for (int p = 0; p < 3; p++)
            for (int q = 0; q < 3; q++)
                Assert.Equal(pairS[p, q], dup[p, q]);
        }
    }

    /// <summary>
    /// Each arm TRACKS its standalone filter in its own passband, and does not equal it — because
    /// the far arm loads the junction.
    /// </summary>
    /// <remarks>
    /// <b>brief-sys-6 asks for agreement to 1e-9, and the measured disagreement is up to 0.16 in
    /// amplitude.</b> The mechanism is the one the brief names in its own phasing-line paragraph and
    /// then draws the opposite conclusion from. Out of its band an ideal filter's <c>|S11|</c> is 1
    /// to nine or more decimal places — nothing is dissipated, which is exactly what "ideal" buys —
    /// but its ANGLE is not zero: measured here, an adjacent-band arm reflects at about −22° to −26°
    /// at the near band's centre, and a unit-magnitude reflection at a non-zero angle is a REACTANCE,
    /// not an open. A reactance across the junction loads the near arm, so its transmission is not
    /// the standalone one. Widening the gap between the two bands walks the angle towards zero
    /// (−8° at 0.80–0.90 against 1.30–1.40 GHz) and the disagreement down with it — which is the
    /// same statement as "a real duplexer needs a phasing line", and is why placing a TLIN in the arm
    /// is the answer rather than a hidden length inside this component.
    ///
    /// <para>So what is asserted here is the part that IS a property of the ideal filter — a
    /// unit-magnitude out-of-band reflection — plus the direction of the effect, measured at two band
    /// separations. The exact form of "the duplexer is two filters" is the test above.</para>
    /// </remarks>
    [Fact]
    public void AnArmIsLoadedByTheFarArmsReactance_NotByAnIdealOpen()
    {
        const int Order = 5;
        double fc = Math.Sqrt(TxLo * TxHi);

        // Out of its own band the far arm reflects everything — but not at zero degrees. "Everything"
        // to within its own out-of-band transmission and not one part more: what does not come back
        // went THROUGH, and nothing at all was dissipated, which is the sense in which an ideal
        // filter's stopband reflection is total.
        var far = SAt(ArmNet(RxLo, RxHi, Order), fc * 1e9);
        Assert.Equal(1.0 - far[1, 0].Magnitude * far[1, 0].Magnitude,
                     far[0, 0].Magnitude * far[0, 0].Magnitude, 12);
        Assert.True(far[0, 0].Magnitude > 1.0 - 1e-6,
            $"the far arm reflects only {far[0, 0].Magnitude:F12} of the wave at {fc:F4} GHz");
        double angle = far[0, 0].Phase * 180.0 / Math.PI;
        Assert.True(Math.Abs(angle) > 5.0,
            $"the far arm reflects at {angle:F2}°, which is close enough to an open that this test " +
            "no longer measures anything — the disagreement below would then need another cause");

        double WorstDeviation(double txLo, double txHi, double rxLo, double rxHi)
        {
            string net = $@"
Port:P1  n1 0  Num=1  Z=50 Ohm
Port:P2  n2 0  Num=2  Z=50 Ohm
Port:P3  n3 0  Num=3  Z=50 Ohm
Duplexer:D1  n1 0 n2 0 n3 0  Zant=50 Ohm   TxResponse=Chebyshev TxForm=Bandpass TxOrder={Order} TxF1={N(txLo)} GHz TxF2={N(txHi)} GHz TxRipple=0.1   RxResponse=Chebyshev RxForm=Bandpass RxOrder={Order} RxF1={N(rxLo)} GHz RxF2={N(rxHi)} GHz RxRipple=0.1
";
            double worst = 0.0;
            for (double f = txLo; f <= txHi + 1e-12; f += (txHi - txLo) / 20.0)
                worst = Math.Max(worst, (SAt(net, f * 1e9)[1, 0] - SAt(ArmNet(txLo, txHi, Order), f * 1e9)[1, 0]).Magnitude);
            return worst;
        }

        double adjacent  = WorstDeviation(0.90, 1.00, 1.10, 1.20);
        double separated = WorstDeviation(0.80, 0.90, 1.30, 1.40);

        output.WriteLine($"Far arm at the near band centre: |S11| = {far[0, 0].Magnitude:F12} ∠{angle:F3}°");
        output.WriteLine($"Worst |ΔS21| against the standalone arm — adjacent bands: {adjacent:F6}");
        output.WriteLine($"                                        — separated bands: {separated:F6}");

        Assert.True(adjacent > 1e-3, "the arms are not being loaded at all, which cannot be right");
        Assert.True(separated < adjacent,
            "widening the band separation must walk the far arm's reflection towards an open, and " +
            "the loading down with it");
    }

    /// <summary>
    /// The TX-to-RX isolation, MEASURED and reported. There is deliberately no parameter for it, and
    /// therefore no number in this test to compare against — what is asserted is the only thing that
    /// is genuinely derivable: the leakage into an arm cannot exceed that arm's own standalone
    /// rejection at the same frequency, because everything reaching RX passed the RX filter and the
    /// junction can only have divided the drive on the way in.
    /// </summary>
    [Fact]
    public void TheDuplexersTxToRxIsolationIsWhatTheTwoResponsesProduce()
    {
        const int Order = 5;
        double worstInBand = double.NegativeInfinity;

        foreach (double f in new[] { TxLo, 0.925, 0.95, 0.975, TxHi, RxLo, 1.125, 1.15, 1.175, RxHi })
        {
            var dup = SAt(DuplexerNet(Order), f * 1e9);
            double iso = Db(dup[2, 1]);
            bool inTxBand = f <= TxHi + 1e-12;

            // The bound: the arm the leakage must cross to arrive.
            double armRejection = Db(SAt(ArmNet(inTxBand ? RxLo : TxLo, inTxBand ? RxHi : TxHi, Order),
                                         f * 1e9)[1, 0]);
            Assert.True(iso <= armRejection + 1e-9,
                $"at {f:F3} GHz the isolation is {iso:F2} dB, better than the {armRejection:F2} dB " +
                "the far arm rejects on its own — which would mean energy arrived without crossing it");

            worstInBand = Math.Max(worstInBand, iso);
            output.WriteLine($"{f:F3} GHz: TX→RX isolation {iso:F2} dB " +
                             $"(far arm alone rejects {armRejection:F2} dB)");
        }

        output.WriteLine($"Worst in-band TX→RX isolation across both bands: {worstInBand:F2} dB");
    }

    /// <summary>
    /// The antenna port's return loss is good in BOTH bands — the property that makes it a duplexer
    /// rather than two filters on a wire. It comes out of the shared node: in the TX band the RX arm
    /// presents its own out-of-band reflection to the junction and vice versa.
    /// </summary>
    [Fact]
    public void TheAntennaPortIsMatchedInBothBands()
    {
        const int Order = 5;
        foreach (var (lo, hi) in new[] { (TxLo, TxHi), (RxLo, RxHi) })
        {
            double worst = double.NegativeInfinity;
            for (double f = lo; f <= hi + 1e-12; f += (hi - lo) / 20.0)
                worst = Math.Max(worst, Db(SAt(DuplexerNet(Order), f * 1e9)[0, 0]));

            output.WriteLine($"ANT return loss across {lo:F2}–{hi:F2} GHz: worst {worst:F2} dB");
            Assert.True(worst < -10.0, $"ANT return loss across {lo:F2}–{hi:F2} GHz is only {worst:F2} dB");
        }
    }

    /// <summary>
    /// The duplexer is lossless: at any frequency, the three columns of its measured S each carry
    /// unit power. Nothing about the junction is allowed to create or destroy any.
    /// </summary>
    [Fact]
    public void TheDuplexerIsMeasuredLossless()
    {
        foreach (double f in new[] { 0.5, 0.9, 0.95, 1.05, 1.15, 1.5, 3.0 })
        {
            var s = SAt(DuplexerNet(), f * 1e9);
            for (int q = 0; q < 3; q++)
            {
                double power = 0.0;
                for (int p = 0; p < 3; p++) power += s[p, q].Magnitude * s[p, q].Magnitude;
                Assert.True(Math.Abs(power - 1.0) < 1e-10,
                    $"column {q} at {f:F2} GHz carries {power:G17} of unit power");
            }

            // …and reciprocal, which a junction of two reciprocal arms must be.
            Near(s[1, 0], s[0, 1], 1e-12);
            Near(s[2, 0], s[0, 2], 1e-12);
            Near(s[2, 1], s[1, 2], 1e-12);
        }
    }

    [Fact]
    public void TheDuplexerAtDcSolves()
    {
        // Both arms are bandpasses, so both are open at DC: the shared node sees two opens and the
        // whole component is an open at all three ports. It has neither a Y nor a Z there.
        var s = SAt(DuplexerNet(), 0.0);
        for (int p = 0; p < 3; p++)
        for (int q = 0; q < 3; q++)
            Near(p == q ? Complex.One : Complex.Zero, s[p, q]);
    }

    // ══ Refusals ══════════════════════════════════════════════════════════════

    [Fact]
    public void AFilterWithTheWrongNetCountIsRefusedByName()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SAt($@"
{TwoPorts()}
Filter:F1  n1 0 n2  Response=Butterworth Form=Lowpass Order=3 Fc=1 GHz
", 1e9));
        Assert.Contains("Filter 'F1'", ex.Message);
        Assert.Contains("expected 4 nets", ex.Message);
    }

    [Fact]
    public void ADuplexerWithTheWrongNetCountIsRefusedByName()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SAt(@"
Port:P1  n1 0  Num=1  Z=50 Ohm
Port:P2  n2 0  Num=2  Z=50 Ohm
Port:P3  n3 0  Num=3  Z=50 Ohm
Duplexer:D1  n1 0 n2 0 n3  Zant=50 Ohm
", 1e9));
        Assert.Contains("Duplexer 'D1'", ex.Message);
        Assert.Contains("ant+, ant−, tx+, tx−, rx+, rx−", ex.Message);
    }

    /// <summary>
    /// A passive intermod on either of these two is refused BY NAME, and the refusal names the
    /// alternative. A memoryless nonlinearity cannot attach to a rational transfer function inside
    /// one component; an attenuator at a small loss carrying the PIM specification, placed in the
    /// path, generates the same product into the same signal.
    /// </summary>
    [Theory]
    [InlineData("Filter:F1  n1 0 n2 0  Response=Chebyshev Form=Bandpass Order=3 PIM=-110 dBm")]
    [InlineData("Duplexer:D1  n1 0 n2 0 n1 0  Zant=50 Ohm PIM=-110 dBm")]
    public void APassiveIntermodOnAFrequencyDependentBlockIsRefusedByName(string line)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SAt($"{TwoPorts()}\n{line}\n", 1e9));
        Assert.Contains("cannot carry a passive intermod", ex.Message);
        Assert.Contains("attenuator", ex.Message);
    }
}
