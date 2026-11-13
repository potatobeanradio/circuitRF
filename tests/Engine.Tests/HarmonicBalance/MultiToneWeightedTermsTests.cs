using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// A device's <c>w ≥ 2</c> weighted terms (<c>NonlinearResult.Terms</c>) in every solver that is not the single-tone
/// <see cref="HbNewton"/>: the two-tone FFT loop (<see cref="HbNewton2D"/>), the lattice loop every run of two or
/// more tones takes by default (<see cref="HbNewtonNd"/>), both envelope engines and the small-signal sweep around a
/// lattice point. Each is held to the single-tone HB, which has always carried the terms, on a circuit both can solve:
/// every tone a multiple of one 10 MHz fundamental, so every mixing product is one of its harmonics and the two
/// spectra can be compared frequency by frequency.
///
/// <para>Two weighted-term devices. The quadrature hybrid with PIM on keeps the imaginary half of its admittance in an
/// <c>H[2] = j·sign(ω)</c> bucket — for an ideal 90° hybrid that is ALL of it, so a solver that drops the bucket sees
/// four open circuits. An SDD with <c>I[1,2]</c> under a low-pass <c>H[2]</c> is the general case: a weight that is
/// genuinely frequency dependent, read at products whose lattice representative sits at a NEGATIVE frequency
/// (<c>(1,−1)</c>, <c>(1,−2)</c>), where it must be the conjugate of <c>H(|ω|)</c>.</para>
/// </summary>
public class MultiToneWeightedTermsTests(ITestOutputHelper output)
{
    private const string Loads = """
        R:R2  thru 0  R=50 Ohm
        R:R3  cpl 0  R=50 Ohm
        R:R4  iso 0  R=50 Ohm
        """;

    private const string Hybrid = "Coupler:H  in 0  thru 0  cpl 0  iso 0  Coupling=3.0103 dB  Phase=90 deg  Z0=50 Ohm  PIM=-60 dBm  PIMPc=30 dBm";

    private static string Tones(double dbm, params double[] mhz) =>
        "PnTone:S  in 0  " + string.Join("  ", mhz.Select((f, i) => $"Freq[{i + 1}]={f} MHz  Pavl[{i + 1}]={dbm} dBm  Phase[{i + 1}]=0 deg")) + "  Z=50 Ohm";

    /// <summary>The reference: single-tone HB at 10 MHz, every product of the tones below one of its harmonics.</summary>
    private static string SingleTone(int maxHarm) => $"analysis HB1 type=hb Tone=\"10e6\" ToneUnit=Hz MaxHarm={maxHarm} Tol=1e-12";

    private static string MultiTone(int order, params double[] mhz) =>
        $"analysis HB1 type=hb NumFreqs={mhz.Length} " + string.Join(" ", mhz.Select((f, i) => $"Tone[{i + 1}]={f}e6"))
      + $" MaxMixOrder={order} MaxHarm={order} Tol=1e-12";

    private static DataSet RunHb(string cnl, AnalysisSettings? settings = null)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().First(), nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        var ds = (DataSet)new HbEngine(nl, tb, settings ?? new AnalysisSettings()).Run(p);
        Assert.True(ds["Converged"].RealValues[0] > 0.5, "HB did not converge");
        return ds;
    }

    private static DataSet RunEnvelope(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
        bool used = false;
        object gate = new();
        ElaboratedNetlist Elab() { lock (gate) { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return nl; } }
        return EnvelopeRunner.Run(plan, Elab);
    }

    /// <summary>
    /// One row of an HB cube as physical frequency (Hz) → phasor. A lattice representative at a negative frequency is
    /// conjugated onto |f| and products that coincide are summed: what the waveform holds at f, and the only form in
    /// which a lattice spectrum and a harmonic one can be compared.
    /// </summary>
    private static Dictionary<long, Complex> Spectrum(DataSet ds, string cube, string row)
    {
        var c = ds[cube];
        int r = Array.IndexOf(c.Axes[0].Labels!, row);
        Assert.True(r >= 0, $"'{row}' is not in the {cube} cube");
        var tones = ds["ToneFreqs"].RealValues;
        var ax = c.Axes[1];
        var s = new Dictionary<long, Complex>();
        for (int k = 0; k < ax.Length; k++)
        {
            double f = ax.Name == "mixIndex"
                ? ax.Labels![k].Trim('(', ')').Split(',').Select((x, t) => int.Parse(x) * tones[t]).Sum()
                : k * tones[0];
            var v = c.ComplexValues[r * ax.Length + k];
            if (f < 0) { f = -f; v = Complex.Conjugate(v); }
            long key = (long)Math.Round(f);
            s[key] = s.GetValueOrDefault(key) + v;
        }
        return s;
    }

    private static double Dbm(Complex v) => 10 * Math.Log10(v.Magnitude * v.Magnitude / 100) + 30;

    /// <summary>Complex agreement, |got − want| ≤ tol·|want|: level AND phase, so a quadrature lost or flipped fails.</summary>
    private void Agrees(string what, double mhz, Complex got, Complex want, double tol)
    {
        double err = (got - want).Magnitude / want.Magnitude;
        output.WriteLine($"{what,-10} {mhz,6} MHz  {Dbm(got),10:F4} dBm {got.Phase * 180 / Math.PI,8:F3}°   "
                       + $"reference {Dbm(want),10:F4} dBm {want.Phase * 180 / Math.PI,8:F3}°   rel {err:E1}");
        Assert.True(err <= tol, $"{what} at {mhz} MHz is {Dbm(got):F4} dBm ∠{got.Phase * 180 / Math.PI:F2}°, the "
                              + $"single-tone reference {Dbm(want):F4} dBm ∠{want.Phase * 180 / Math.PI:F2}° (relative error {err:E2})");
    }

    /// <summary>
    /// Every port's voltage and the through terminal's current, at the carriers and at the products. The isolated
    /// port is held to the product tolerance at every frequency: the ideal hybrid routes no carrier there, so all it
    /// holds, even at a carrier's frequency, is passive intermod.
    /// </summary>
    private void HybridAgrees(DataSet ds, DataSet reference, double[] carriers, double[] products, double productTol)
    {
        foreach (var (row, cube) in new[] { ("in", "V"), ("thru", "V"), ("cpl", "V"), ("iso", "V"), ("H:thru", "I") })
        {
            var got  = Spectrum(ds, cube, row);
            var want = Spectrum(reference, cube, row);
            foreach (double f in carriers)
                Agrees(row, f, got[(long)(f * 1e6)], want[(long)(f * 1e6)], row == "iso" ? productTol : CarrierTol);
            if (row == "in") continue;   // the hybrid sends no product back to its input: both runs hold rounding there
            foreach (double f in products) Agrees(row, f, got[(long)(f * 1e6)], want[(long)(f * 1e6)], productTol);
        }
    }

    /// <summary>A carrier is set by the linear coupling, and two solves of the same circuit agree on it to solver
    /// accuracy (measured: 1e-8 and better on every path here).</summary>
    private const double CarrierTol = 1e-6;

    [Theory]
    [InlineData(true)]    // HbNewtonNd — what two tones take by default
    [InlineData(false)]   // HbNewton2D — the rectangular FFT path, HbTwoToneOnLattice = false
    public void AQuadratureHybridsPim_InTwoToneHb_IsTheSingleToneAnswer(bool onLattice)
    {
        var reference = RunHb($"{Tones(30, 100, 110)}\n{Hybrid}\n{Loads}\n{SingleTone(40)}");
        var ds = RunHb($"{Tones(30, 100, 110)}\n{Hybrid}\n{Loads}\n{MultiTone(5, 100, 110)}",
                       new AnalysisSettings { HbTwoToneOnLattice = onLattice });

        // Measured: 5e-9 on the lattice, 4e-12 on the FFT path. The −87 dBc product is weak enough that the orders a
        // MaxMixOrder = 5 diamond drops, and the reference keeps, are invisible at this tolerance.
        HybridAgrees(ds, reference, carriers: [100, 110], products: [90, 120], productTol: 1e-6);
    }

    [Fact]
    public void AQuadratureHybridsPim_InThreeToneHb_IsTheSingleToneAnswer()
    {
        // Three tones, so every third-order product of every pair and the triple sums — nine of them, none on a carrier.
        var reference = RunHb($"{Tones(30, 100, 110, 140)}\n{Hybrid}\n{Loads}\n{SingleTone(60)}");
        var ds = RunHb($"{Tones(30, 100, 110, 140)}\n{Hybrid}\n{Loads}\n{MultiTone(3, 100, 110, 140)}");

        // At MaxMixOrder = 3 the lattice drops the fifth-order content the reference keeps on these same frequencies
        // (3f1 − 2f2 = 2f2 − f3 = 80 MHz, …): measured 7e-4 at worst (0.006 dB, 0.01°), against a product that is
        // missing altogether when the bucket is.
        HybridAgrees(ds, reference, carriers: [100, 110, 140], products: [60, 70, 80, 90, 120, 130, 150, 170, 180], productTol: 2e-3);
    }

    [Fact]
    public void AQuadratureHybridsPim_InTheLatticeEnvelope_IsTheSingleToneAnswer()
    {
        // Two CW carriers 10 MHz apart, each its own fundamental (EnvelopeEngineMt). A CW envelope is constant, so its
        // last sample IS the periodic steady state, and it must be the harmonic-balance one.
        var reference = RunHb($"{Tones(30, 100, 110)}\n{Hybrid}\n{Loads}\n{SingleTone(40)}");
        var ds = RunEnvelope($"""
            MOD_Source:S1 in 0 Fc=105 MHz Pavg=33.0103 Z=50 Ohm Record=0.5e-3 Carrier[1]="CW;BW=1MHz;Off=-5MHz;P=0" Carrier[2]="CW;BW=1MHz;Off=5MHz;P=0"
            {Hybrid}
            {Loads}
            analysis ENV1 type=env Accuracy=draft MaxHarm=5 MaxMixOrder=5 Envelope=lattice Segments=1 Warmup=0 Stop=2e-6 Probes="thru,cpl,iso"
            """);
        Assert.Equal(2, ds["__EnvFundamentals"].RealValues[0]);

        foreach (var node in new[] { "thru", "cpl", "iso" })
        {
            var want = Spectrum(reference, "V", node);
            // (1,−2) is the lattice's representative of 2f2 − f1, at −120 MHz: its conjugate is the 120 MHz phasor.
            foreach (var (label, mhz, conj) in new[] { ("(1,0)", 100.0, false), ("(0,1)", 110.0, false),
                                                       ("(2,-1)", 90.0, false), ("(1,-2)", 120.0, true) })
            {
                var v = EnvelopeLast(ds, node, label);
                Agrees(node, mhz, conj ? Complex.Conjugate(v) : v, want[(long)(mhz * 1e6)], EnvelopeTol);
            }
        }
    }

    [Fact]
    public void AQuadratureHybridsPim_InTheSingleEnvelope_IsTheSingleToneAnswer()
    {
        // One CW carrier on one envelope (EnvelopeEngine). Its Newton balanced the bucket; the linear state it reports
        // from did not carry it, and read the hybrid as four open circuits.
        var reference = RunHb($"{Tones(30, 100)}\n{Hybrid}\n{Loads}\n{SingleTone(50)}");
        var ds = RunEnvelope($"""
            MOD_Source:S1 in 0 Fc=100 MHz Pavg=30 Z=50 Ohm Record=0.5e-3 Carrier[1]="CW;BW=1MHz;Off=0;P=0"
            {Hybrid}
            {Loads}
            analysis ENV1 type=env Accuracy=draft MaxHarm=5 Envelope=single Segments=1 Warmup=0 Stop=2e-6 Probes="thru,cpl,iso"
            """);
        Assert.Equal(1, ds["__EnvFundamentals"].RealValues[0]);

        foreach (var node in new[] { "thru", "cpl", "iso" })
        {
            var want = Spectrum(reference, "V", node);
            Agrees(node, 100, EnvelopeLast(ds, node, "1"), want[100_000_000], EnvelopeTol);
            // The isolated port's third harmonic sits 180 dB below the carrier, under either run's tolerance.
            if (node != "iso") Agrees(node, 300, EnvelopeLast(ds, node, "3"), want[300_000_000], EnvelopeTol);
        }
    }

    /// <summary>An envelope run solves to a tolerance set by its signal, not HB's 1e-12: measured 2.3e-6 at the
    /// carriers and 7e-6 at the products on both engines, one drive-level offset showing at the first and third power.</summary>
    private const double EnvelopeTol = 1e-4;

    /// <summary>The last sample of an envelope's V cube at one node and product (a lattice label, or a harmonic index).</summary>
    private static Complex EnvelopeLast(DataSet ds, string node, string product)
    {
        var c = ds["V"];
        int nt = c.Axes[0].Length, nn = c.Axes[1].Length, nk = c.Axes[2].Length;
        int n = Array.IndexOf(c.Axes[1].Labels!, node);
        int k = c.Axes[2].Labels is { } l ? Array.IndexOf(l, product) : int.Parse(product);
        Assert.True(n >= 0 && k >= 0, $"{node} / {product} is not in the envelope's V cube");
        return c.ComplexValues[((nt - 1) * nn + n) * nk + k];
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnSddsLowPassWeightedTerm_InTwoToneHb_IsTheSingleToneAnswer(bool onLattice)
    {
        // I[1,2] through H[2] = 1/(1 + j·f/fc): a weight that changes across the band, so it must be read at each
        // product's own frequency, and conjugated where the lattice keeps a product at a negative one — the difference
        // (1,−1) and the upper IM3 (1,−2) here. The quadratic term puts power at the difference and at DC.
        const string sdd = "SDD:X1 in 0 I[1,0]=_v1/200 I[1,2]=g1*_v1+g2*_v1^2+g3*_v1^3 H[2]=1/(1+j*freq/fc) g1=0.01 g2=2e-3 g3=1e-3 fc=60e6";
        var reference = RunHb($"{Tones(0, 100, 110)}\n{sdd}\n{SingleTone(40)}");
        var ds = RunHb($"{Tones(0, 100, 110)}\n{sdd}\n{MultiTone(5, 100, 110)}",
                       new AnalysisSettings { HbTwoToneOnLattice = onLattice });

        var got  = Spectrum(ds, "V", "in");
        var want = Spectrum(reference, "V", "in");
        // Measured 6e-5 at worst (the lattice's upper IM3) and 2e-5 on the FFT path: the orders a MaxMixOrder = 5
        // diamond drops and the reference keeps, which a cubic law at 0 dBm still reaches.
        const double tol = 2e-4;
        foreach (double f in new[] { 100.0, 110.0, 0.0, 10.0, 90.0, 120.0, 210.0 })
            Agrees("in", f, got[(long)(f * 1e6)], want[(long)(f * 1e6)], tol);
    }

    [Fact]
    public void AQuadratureHybridsPim_InATwoToneSmallSignalSweep_IsItsLinearS()
    {
        // The conversion matrix around a two-tone point (HbSmallSignal.LatticeSidebands) is built from the same device
        // spectra as the Newton solve, buckets included. At low drive the PIM law has zero slope, so a WSProbe beside
        // the hybrid reads exactly what the S-parameter engine reads; without the bucket it read an open circuit. The
        // probe frequencies stay off every half mixing frequency (multiples of 5 MHz), where the sweep reports NaN.
        string circuit = $"""
            {Tones(-20, 100, 110)}
            {Hybrid}
            WSProbe:P1 thru t2
            R:R2  t2 0  R=50 Ohm
            R:R3  cpl 0  R=50 Ohm
            R:R4  iso 0  R=50 Ohm
            """;
        var ds = RunHb($"{circuit}\n{MultiTone(3, 100, 110)} SSStart=37 SSStop=97 SSNpts=4 SSUnit=MHz");

        var (lib, tb) = new CnlReader().Read($"{circuit}\nanalysis SP1 type=sparam start=37 stop=97 npts=4 Unit=MHz");
        using var nl = new Elaborator(lib).Elaborate(tb);
        var freqs = tb.Analyses.OfType<SParameterAnalysis>().Single().Expand(nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        var sp = SParameterEngine.Run(nl, freqs);

        var (hb, lin) = (ds["wsp"], sp["wsp"]);
        Assert.Equal(lin.Axes.Select(a => a.Length), hb.Axes.Select(a => a.Length));
        double worst = 0;
        for (int i = 0; i < lin.ComplexValues.Length; i++)
        {
            var (a, b) = (lin.ComplexValues[i], hb.ComplexValues[i]);
            worst = Math.Max(worst, (a - b).Magnitude / Math.Max(1e-30, a.Magnitude));
        }
        output.WriteLine($"two-tone small-signal wsp against the S-parameter wsp: worst relative deviation {worst:E2}");
        Assert.True(worst <= 1e-6, $"the two-tone small-signal wsp differs from the linear one by {worst:E2} relative");
    }

    [Fact]
    public void TheBucketsJacobian_IsWhatFiniteDifferencesOfTheResidualSay_OnBothTwoTonePaths()
    {
        // The analytic Jacobian against central differences of the residual, both now carrying the bucket: the FD
        // oracle each multi-tone path already has (CompareJacobianNumerical2D / Nd), at a point driven hard enough
        // (PIM 40 dB stronger than above, several volts on every product) that the limiter's slope moves. The bucket
        // must also MATTER here — for the ideal hybrid it is the whole linear coupling — or agreement proves nothing.
        var (lib, tb) = new CnlReader().Read($"{Tones(30, 100, 110)}\n{Hybrid.Replace("PIM=-60 dBm", "PIM=-20 dBm")}\n{Loads}\n{MultiTone(3, 100, 110)}");
        using var nl = new Elaborator(lib).Elaborate(tb);
        var ex = new HbLinearExtractor(nl, AnalysisSettings.Default);
        int N = ex.InterfaceCount;
        var ifNodes = ex.InterfaceNodes;
        const double f1 = 100e6, f2 = 110e6;

        var lattice = new MixingLattice(2, 2);   // order 2 keeps it cheap: the FD oracle is 2·dof device passes per path
        var grid    = new MixingGrid(2);
        int M = lattice.MixCount;
        var apft = new HbApft(lattice, AnalysisSettings.Default.HbApftOversample);
        var (n1, n2) = HbFft2D.GridSizes(2, 2, 2);

        var yNN  = new Complex[M][,];
        var iSrc = new Complex[M][];
        for (int m = 0; m < M; m++)
        {
            yNN[m] = new Complex[N, N];
            for (int n = 0; n < N; n++) yNN[m][n, n] = new Complex(0.02, 0.0);
            iSrc[m] = new Complex[N];
        }
        var V = new Complex[N, M];
        for (int n = 0; n < N; n++)
        for (int m = 1; m < M; m++)
            V[n, m] = new Complex(6.0 - 0.7 * n - 0.4 * m, 2.0 + 0.5 * n - 0.3 * m);

        var (_, _, dg, dc, buckets) = HbNewtonNd.EvaluateNonlinearNd(V, apft, N, nl, ifNodes);
        Assert.NotEmpty(buckets);
        var with    = HbNewtonNd.BuildJNd(yNN, dg, dc, apft, N, [f1, f2], buckets: buckets);
        var without = HbNewtonNd.BuildJNd(yNN, dg, dc, apft, N, [f1, f2]);
        double scale = with.Max(Math.Abs), share = with.Zip(without, (a, b) => Math.Abs(a - b)).Max() / scale;
        output.WriteLine($"the bucket's share of the largest Jacobian entry: {share:F3}");
        Assert.True(share > 0.1, $"the bucket moves the Jacobian by only {share:E2} of its scale here");

        var nd  = HbNewtonNd.CompareJacobianNumericalNd(V, yNN, iSrc, apft, N, [f1, f2], nl, ifNodes);
        var two = HbNewton2D.CompareJacobianNumerical2D(V, yNN, iSrc, grid, N, n1, n2, f1, f2, nl, ifNodes);
        // Judged by the worst ABSOLUTE error against the Jacobian's own scale: the relative metric is floored on the
        // near-zero entries (2.5e-4 for an in-phase coupler at this drive, which has no bucket at all), while a
        // missing or misrotated bucket is an error of the scale itself. Measured 6e-10 (lattice) and 4e-10 (FFT path).
        double ndErr = nd.MaxAbsError / scale, twoErr = two.MaxAbsError / scale;
        output.WriteLine($"FD oracle, worst |analytic − FD| / scale: lattice {ndErr:E2}, FFT path {twoErr:E2} (dof {nd.Dof}, scale {scale:E2})");
        Assert.True(ndErr < 1e-7, $"lattice Jacobian vs finite differences: {ndErr:E3} of its scale");
        Assert.True(twoErr < 1e-7, $"two-tone FFT Jacobian vs finite differences: {twoErr:E3} of its scale");
    }
}
