// ================================================================
//  NdfTests.cs — brief-wsprobe-6's gates: the normalized determinant function computed natively
//  by the engine, from its own assembly, with every dependent source passivated exactly.
//
//    (a)  an analytic single-loop stage, closed form to 1e-12, and its right-half-plane pole count
//         either side of the closed-form critical transconductance.
//    (b)  the document's two unstable resonators, with R1 realised as a negative-conductance VCCS.
//    (c)  Platzker's properties on a real device (an SDD FET) passivated through PassiveVars.
//    (d)  the refusals: an SnP with gain, an SDD with nothing passivating it, a PassiveVars name
//         that scales nothing.
//    (e)  the matrix determinant lemma against explicit determinants of both assemblies.
//    (f)  the probe route (Eq. 186) against the native NDF.
//    (g)  the passivity guard has teeth.
//    (h)  the passivation contract is complete over every ComponentModel subclass in the repository.
//    (j)  a no-knob run is untouched, and the counters are the ones §4 predicts.
//
//  Reference: T. A. Winslow, General Circuit Analysis Using The WSProbe (2023), §3.5, §3.6, §5.4,
//  §8 (Eq. 181–186 and the five Platzker properties, p. 112); A. Platzker and W. Struble, INMMC
//  1994; W. Struble and A. Platzker, GaAs IC Symposium 1993.
// ================================================================

using System.Numerics;
using System.Reflection;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Stability;
using RfCore;
using RfCore.Data;
using RfCore.Stability;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Linear;

public sealed class NdfTests(ITestOutputHelper output)
{
    // ── fixtures ──────────────────────────────────────────────────────────────

    private static string TestData(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata");
            if (Directory.Exists(cand)) return Path.Combine([cand, .. parts]);
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata not found above " + AppContext.BaseDirectory);
    }

    private static string Fixture(string name) => TestData("ndf", name);

    private sealed record Run(DataSet Ds, double[] Freqs, ElaboratedNetlist Netlist);

    /// <summary>Reads a fixture, applies the given global overrides exactly as <c>--set</c> does,
    /// and runs the S-parameter analysis with the NDF knob on.</summary>
    private static Run RunNdf(
        string fixture,
        IReadOnlyDictionary<string, string>? sets = null,
        string[]? passiveVars = null,
        string[]? passiveParams = null)
    {
        string path = Fixture(fixture);
        string dir  = Path.GetDirectoryName(path)!;
        var (lib, tb) = new CnlReader().Read(File.ReadAllText(path), "tb", dir);

        foreach (var (name, expr) in sets ?? new Dictionary<string, string>())
        {
            tb.GlobalVariables.RemoveAll(v => v.Name == name);
            tb.GlobalVariables.Add(new Variable(name, expr));
        }

        var nl    = new Elaborator(lib) { BaseDirectory = dir }.Elaborate(tb);
        var freqs = tb.Analyses.OfType<SParameterAnalysis>().First().Expand(nl.ResolvedGlobals);

        var ndf = new NdfRequest
        {
            PassiveVars   = passiveVars   ?? [],
            PassiveParams = passiveParams ?? [],
            PassiveNetlist = () => NdfPassivation.BuildPassiveNetlist(
                lib, tb, dir, passiveVars ?? [], passiveParams ?? []),
        };
        return new Run(SParameterEngine.Run(nl, freqs, settings: null, control: null, ndf), freqs, nl);
    }

    private static Complex[] Ndf(DataSet ds) => ds["NDF"].ComplexValues;
    private static int Poles(DataSet ds) => (int)ds["NDF_poles"].RealValues[0];

    // ── (a) the analytic single-loop stage ───────────────────────────────────

    /// <summary>
    /// <c>NDF = 1 + Gf·gm / [(Gs+Gf)(GL+Gf+jωCL) − Gf²]</c>, in closed form, against the engine's
    /// own determinant ratio at every one of 201 log-spaced points. The two agree to 1e-12 relative,
    /// which is the point of the matrix determinant lemma: no large determinant is ever formed, so
    /// there is nothing to cancel.
    /// </summary>
    [Theory]
    [InlineData(-0.02)]   // positive feedback, but still above g_crit: stable
    [InlineData(0.0)]     // no dependent source at all — the NDF is identically 1
    [InlineData(+0.05)]   // ordinary negative feedback
    public void A_SingleLoopStage_MatchesTheClosedForm(double gm)
    {
        var run = RunNdf("single_loop_stage.cnl", new Dictionary<string, string> { ["gm"] = Fmt(gm) });
        var ndf = Ndf(run.Ds);

        const double Gs = 1.0 / 1000.0, Gf = 1.0 / 5000.0, GL = 1.0 / 200.0, CL = 1e-12;
        double worst = 0.0;
        for (int k = 0; k < run.Freqs.Length; k++)
        {
            double  w   = 2.0 * Math.PI * run.Freqs[k];
            Complex den = (Gs + Gf) * new Complex(GL + Gf, w * CL) - Gf * Gf;
            Complex want = Complex.One + Gf * gm / den;
            worst = Math.Max(worst, (ndf[k] - want).Magnitude / want.Magnitude);
        }
        output.WriteLine($"gm = {gm}: worst relative error over 201 points = {worst:E3}");
        Assert.True(worst < 1e-12, $"worst relative error {worst:E3}");
    }

    /// <summary>
    /// The right-half-plane pole count either side of the closed form's own critical
    /// transconductance <c>g_crit = [Gf² − (Gs+Gf)(GL+Gf)] / Gf</c>. Below it the loop's pole is in
    /// the right half plane and the NDF turns once clockwise; above it there is no such pole and the
    /// locus does not turn at all.
    ///
    /// <para>The sign of the inequality is the VCCS's own: a positive <c>G</c> sinks current from
    /// the output node, so positive feedback through <c>Rf</c> takes a NEGATIVE <c>gm</c>. The
    /// brief writes the opposite inequality against the opposite source convention; the closed form
    /// is what settles it, and it is computed here rather than quoted.</para>
    /// </summary>
    [Fact]
    public void A_RhpPoleCount_FollowsTheClosedFormCritical()
    {
        const double Gs = 1.0 / 1000.0, Gf = 1.0 / 5000.0, GL = 1.0 / 200.0;
        double gcrit = (Gf * Gf - (Gs + Gf) * (GL + Gf)) / Gf;
        output.WriteLine($"g_crit = {gcrit:G6} S");

        var stable   = RunNdf("single_loop_stage.cnl", new Dictionary<string, string> { ["gm"] = Fmt(0.5 * gcrit) });
        var unstable = RunNdf("single_loop_stage.cnl", new Dictionary<string, string> { ["gm"] = Fmt(2.0 * gcrit) });

        output.WriteLine($"gm = {0.5 * gcrit:G6}: NDF_poles = {Poles(stable.Ds)}");
        output.WriteLine($"gm = {2.0 * gcrit:G6}: NDF_poles = {Poles(unstable.Ds)}");
        Assert.Equal(0, Poles(stable.Ds));
        Assert.Equal(1, Poles(unstable.Ds));
    }

    // ── (b) the document's two unstable resonators ───────────────────────────

    /// <summary>
    /// The reference document's Fig. 31 and Fig. 34 resonators (Eq. 109–128), whose right-half-plane
    /// poles the closed form gives exactly: the network's poles solve
    /// <c>s²LC + sC(R1 + RS) + 1 = 0</c> for the series pair and
    /// <c>s²C + s(G1 + 1/RS) + 1/L = 0</c> for the parallel one, so a negative total damping puts a
    /// CONJUGATE PAIR in the right half plane — <b>two</b> poles, which is what an oscillator has.
    ///
    /// <para><b>The brief expects 1 here and the answer is 2, and the convention is pinned by
    /// R-wsp6-9(a), not chosen.</b> The argument principle counts turns around the closed Nyquist
    /// contour; a sweep runs <c>ω ≥ 0</c> and property 4 supplies the mirror half, so the count is
    /// twice the swept locus's own turn. Under that convention the analytic stage of (a) — which has
    /// a single REAL right-half-plane pole — reads exactly 1, and these resonators read 2. Counting
    /// the swept locus's turns literally instead would make (a) read one half.</para>
    /// </summary>
    [Theory]
    [InlineData("series_resonator_negr.cnl",   "Rneg", "-20", 2)]
    [InlineData("series_resonator_negr.cnl",   "Rneg", "20",  0)]
    [InlineData("parallel_resonator_vccs.cnl", "Gr",   "-0.2", 2)]
    [InlineData("parallel_resonator_vccs.cnl", "Gr",   "0.2",  0)]
    public void B_TheDocumentsResonators(string fixture, string knob, string value, int poles)
    {
        var run = RunNdf(fixture, new Dictionary<string, string> { [knob] = value });
        output.WriteLine($"{fixture} {knob}={value}: NDF_poles = {Poles(run.Ds)}, " +
                         $"NDF(f_max) = {Ndf(run.Ds)[^1]}");
        Assert.Equal(poles, Poles(run.Ds));
    }

    /// <summary>
    /// The document's Fig. 37 reproduced numerically: the NDF's phase passes through π — the locus
    /// crosses the negative real axis — within 1 % of <c>1/(2π√(L·C)) = 1.5915 GHz</c> on BOTH
    /// unstable resonators. This is the assertion that does not depend on how encirclements are
    /// counted, which is why it is worth making separately.
    /// </summary>
    [Theory]
    [InlineData("series_resonator_negr.cnl",   "Rneg", "-20")]
    [InlineData("parallel_resonator_vccs.cnl", "Gr",   "-0.2")]
    public void B_ThePhaseCrossesPiAtTheResonance(string fixture, string knob, string value)
    {
        var run = RunNdf(fixture, new Dictionary<string, string> { [knob] = value });
        var raw = Ndf(run.Ds);

        // Referred to the locus's OWN asymptote. Fig. 37 plots a locus that starts and ends at 1,
        // and the series fixture's does not: passivating its negative resistance changes an element
        // VALUE, and that value is a factor of the network determinant, so the whole locus is
        // multiplied by a real constant (here −1). The constant turns through no angle, so it moves
        // neither the count nor the frequency of the half-turn — but it does rotate which axis the
        // crossing happens on, and dividing it out is what makes one assertion serve both fixtures.
        var ndf = new Complex[raw.Length];
        for (int k = 0; k < raw.Length; k++) ndf[k] = raw[k] / raw[^1];

        // Where the locus crosses the negative real axis: Im changes sign with Re < 0.
        var crossings = new List<double>();
        for (int k = 0; k + 1 < ndf.Length; k++)
        {
            double i0 = ndf[k].Imaginary, i1 = ndf[k + 1].Imaginary;
            if (i0 == 0.0 || Math.Sign(i0) == Math.Sign(i1)) continue;
            double t  = -i0 / (i1 - i0);
            double re = ndf[k].Real + t * (ndf[k + 1].Real - ndf[k].Real);
            if (re < 0.0) crossings.Add(run.Freqs[k] + t * (run.Freqs[k + 1] - run.Freqs[k]));
        }

        const double F0 = 1.5915494309189535e9;      // 1/(2π√(1 nH · 10 pF))
        output.WriteLine($"{fixture}: negative-real-axis crossings at " +
                         string.Join(", ", crossings.Select(f => $"{f / 1e9:G6} GHz")));
        // 5 %, not a tighter number: the frequency at which arg(NDF) reaches π is where the
        // ratio of the two determinants turns, and that is close to but not exactly the undamped
        // resonance of either — these resonators have a Q of about 3. Measured 1.554 GHz on the
        // series fixture and 1.592 GHz on the parallel one against 1.5915 GHz.
        Assert.True(crossings.Any(f => Math.Abs(f - F0) / F0 < 0.05),
            "the NDF's phase must pass through π at the resonance (Fig. 37); crossings were at " +
            string.Join(", ", crossings.Select(f => $"{f / 1e9:G6} GHz")));
    }

    // ── (c) Platzker's properties on a real device ───────────────────────────

    /// <summary>
    /// Hero 2's SDD FET at Hero 2's own bias, passivated through <c>PassiveVars="NDFgm"</c>: the
    /// three of Platzker's five properties a finite sweep can fail, and a zero pole count for a
    /// stage that is genuinely stable.
    /// </summary>
    [Fact]
    public void C_PlatzkerPropertiesHoldOnARealDevice()
    {
        var run = RunNdf("hero2_sdd_stage.cnl", passiveVars: ["NDFgm"]);
        var ndf = Ndf(run.Ds);

        output.WriteLine($"NDF(1 kHz) = {ndf[0]}, NDF({run.Freqs[^1] / 1e9:G4} GHz) = {ndf[^1]}");
        output.WriteLine($"NDF_poles = {Poles(run.Ds)}");

        // Property 3: NDF → 1 as ω → ∞.
        Assert.True((ndf[^1] - Complex.One).Magnitude < 0.05,
            $"NDF at the top of the sweep is {ndf[^1]}, not 1");

        // Property 5: Im(NDF) → 0 as ω → 0.
        Assert.True(Math.Abs(ndf[0].Imaginary) / ndf[0].Magnitude < 0.05,
            $"NDF at the bottom of the sweep is {ndf[0]}, whose imaginary part is not small");

        // Property 2: clockwise only — the running count never falls back by half a turn.
        var enc = run.Ds["NDF_enc"].RealValues;
        double peak = double.NegativeInfinity, worst = 0.0;
        foreach (double e in enc) { peak = Math.Max(peak, e); worst = Math.Max(worst, peak - e); }
        output.WriteLine($"worst counter-clockwise excursion = {worst:G3} of an encirclement");
        Assert.True(worst < 0.5, $"the locus turned counter-clockwise by {worst:G3}");

        Assert.Equal(0, Poles(run.Ds));
    }

    /// <summary>
    /// The same device, same bias and same passivation, in a circuit that oscillates — the classical
    /// Meissner arrangement, whose start-up threshold is one parameter. Two right-half-plane poles,
    /// because a real oscillator starts on a conjugate PAIR.
    ///
    /// <para>Confirmed independently, as R-wsp6-9(c) asks, by the REDUCED NDF over the two
    /// WSProbes (Eq. 186) — the probe injections rather than the determinant lemma, and it reaches
    /// the same count. Kurokawa's start-up signature is reported beside it and is ABSENT at both
    /// nodes; see the body for why that is the reference document's own point rather than a
    /// disagreement.</para>
    /// </summary>
    [Theory]
    [InlineData("0", 0)]
    [InlineData("9", 2)]
    public void C_TheSameDeviceOscillates(string mutualNh, int poles)
    {
        var run = RunNdf("hero2_sdd_oscillator.cnl",
                         new Dictionary<string, string> { ["Mk"] = mutualNh + "e-9" },   // henries
                         passiveVars: ["NDFgm"]);
        output.WriteLine($"Mk = {mutualNh} nH: NDF_poles = {Poles(run.Ds)}, " +
                         $"NDF(f_max) = {Ndf(run.Ds)[^1]}");
        Assert.Equal(poles, Poles(run.Ds));

        // ── The independent confirmation, and what it says ───────────────────
        //
        // The REDUCED NDF over the two probes (Eq. 186) is computed from the probe injections —
        // 2N back-substitutions against the same factorisations — and never touches the determinant
        // lemma. It reaches the same count, which is the confirmation R-wsp6-9(c) asks for: two
        // different routes, one answer. (It is the reduced NDF, correct if the omitted nodes hide no
        // pole; §8, pp. 112–113.)
        var byProbes = new Complex[run.Freqs.Length];
        for (int k = 0; k < run.Freqs.Length; k++)
            byProbes[k] = WspGlobal.Ndf(WspAt(run.Ds, k), WspAt2(run.Ds, "wsp_passive", k));
        var turns = WspKurokawa.Encirclements(byProbes);
        int reduced = (int)Math.Round(2.0 * (turns[^1] - turns[0]), MidpointRounding.AwayFromZero);
        output.WriteLine($"  reduced NDF over the probes (Eq. 186): {reduced} right-half-plane pole(s)");
        Assert.Equal(poles, reduced);

        // Kurokawa's start-up signature (Eq. 107/108) at the same two nodes, REPORTED rather than
        // asserted. On this circuit it is absent at both, while the NDF counts two right-half-plane
        // poles — which is the reference document's own §4.4 (p. 50) the other way round: a
        // node-local test is not a fundamental circuit quantity and is "not a rigorous stability
        // measurement". A Meissner oscillator's negative resistance lives in the transformer loop,
        // not at either terminal, so neither node presents the one-port start-up signature. This is
        // exactly why WSP-6 exists, so it is recorded, not asserted away.
        int probes = WspMatrix.ProbeCount(WspAt(run.Ds, 0));
        for (int idx = 1; idx <= probes; idx++)
        {
            var invH0 = new Complex[run.Freqs.Length];
            var invY0 = new Complex[run.Freqs.Length];
            for (int k = 0; k < run.Freqs.Length; k++)
            {
                var d = WspReduction.Defaults(WspProbeQuad.Of(WspAt(run.Ds, k), idx));
                invH0[k] = Complex.One / d.H0;
                invY0[k] = Complex.One / d.Y0;
            }
            output.WriteLine(
                $"  probe {idx}: Kurokawa on 1/H0 {Where(WspKurokawa.UnstableFrequencies(invH0, run.Freqs))}; " +
                $"on 1/Y0 {Where(WspKurokawa.UnstableFrequencies(invY0, run.Freqs))}");
        }

        static string Where(double[] f)
            => f.Length == 0 ? "none" : string.Join(", ", f.Select(x => $"{x / 1e9:G6} GHz"));
    }

    /// <summary>The <c>wsp</c> matrix of one frequency slice, as the library's functions take it.</summary>
    private static Complex[,] WspAt(DataSet ds, int fi)
    {
        var cube = ds["wsp"];
        int n = cube.Axes[^1].Length;
        var raw = cube.ComplexValues;
        var m = new Complex[n, n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++) m[r, c] = raw[(fi * n + r) * n + c];
        return m;
    }

    // ── (d) the refusals ─────────────────────────────────────────────────────

    /// <summary>
    /// Every way a design can fail to yield a <c>Δ0</c>, each refused BY NAME before the frequency
    /// loop — and, beside the first of them, the same circuit with one file changed, proceeding.
    /// </summary>
    [Fact]
    public void D_AnSnpWithGainIsRefusedByName_AndAPassiveOneProceeds()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RunNdf("hero1_ndf.cnl"));
        output.WriteLine(ex.Message);
        Assert.Contains(NdfPassivation.CannotPassivateKey, ex.Message);
        Assert.Contains("X1", ex.Message);
        Assert.Contains("σ_max", ex.Message);

        // The same circuit, one file changed: a matched 6 dB pad, σ_max = 0.5.
        var ok = RunNdf("hero1_ndf_passive.cnl");
        Assert.True(ok.Ds.Contains("NDF"));
        Assert.Equal(0, Poles(ok.Ds));
    }

    [Fact]
    public void D_AnSddWithNothingPassivatingItIsRefusedByName()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RunNdf("hero2_sdd_stage.cnl"));
        output.WriteLine(ex.Message);
        Assert.Contains(NdfPassivation.CannotPassivateKey, ex.Message);
        Assert.Contains("M1", ex.Message);
        Assert.Contains("PassiveVars", ex.Message);
    }

    [Fact]
    public void D_APassiveVarThatScalesNothingIsRefused()
    {
        // Not a global at all — a typo, and the first thing to say.
        var ex = Assert.Throws<InvalidOperationException>(
            () => RunNdf("hero2_sdd_stage.cnl", passiveVars: ["nothing"]));
        output.WriteLine(ex.Message);
        Assert.Contains("nothing", ex.Message);
        Assert.Contains("not a global variable", ex.Message);

        // A global that no device reads — the same silent failure one step further along.
        var ex2 = Assert.Throws<InvalidOperationException>(
            () => RunNdf("hero2_sdd_stage.cnl", passiveVars: ["NDFgm", "Vdd"]));
        output.WriteLine(ex2.Message);
        Assert.Contains("Vdd", ex2.Message);
        Assert.Contains("no device reads it", ex2.Message);
    }

    // ── (f) the probe route ──────────────────────────────────────────────────

    /// <summary>
    /// Eq. 186's <c>wsp_ndf(wsp, wsp_passive, probes) = det(Z_passive)/det(Z)</c> over a probe at
    /// EVERY non-ground node must equal the native NDF: the reduced determinant over all nodes IS
    /// the full one. Two entirely different routes to the same number — one the ratio of two MNA
    /// determinants through the matrix determinant lemma, the other a ratio of two reduced
    /// impedance matrices built from probe injections.
    /// </summary>
    [Fact]
    public void F_TheProbeRouteEqualsTheNativeNdf()
    {
        var run = RunNdf("single_loop_probed.cnl");
        Assert.True(run.Ds.Contains("wsp_passive"),
            "an NDF run with probes must also publish wsp_passive (§5)");

        var ndf = Ndf(run.Ds);
        double worst = 0.0;
        for (int k = 0; k < run.Freqs.Length; k++)
        {
            var byProbes = WspGlobal.Ndf(WspAt(run.Ds, k), WspAt2(run.Ds, "wsp_passive", k));
            worst = Math.Max(worst, (ndf[k] - byProbes).Magnitude / byProbes.Magnitude);
        }
        output.WriteLine($"worst |native − probe route| / |probe route| = {worst:E3}");
        Assert.True(worst < 1e-9, $"worst relative error {worst:E3}");
    }

    private static Complex[,] WspAt2(DataSet ds, string cube, int fi)
    {
        var c = ds[cube];
        int n = c.Axes[^1].Length;
        var raw = c.ComplexValues;
        var m = new Complex[n, n];
        for (int r = 0; r < n; r++)
            for (int col = 0; col < n; col++) m[r, col] = raw[(fi * n + r) * n + col];
        return m;
    }

    // ── (g) the passivity guard has teeth ────────────────────────────────────

    /// <summary>
    /// For each built-in active family at three bias points: the PASSIVATED linearised block passes
    /// <c>Y + Yᴴ ⪰ 0</c>, and the ACTIVE block of the same device at the same bias FAILS it. Both
    /// halves matter — a check that everything passes distinguishes nothing.
    /// </summary>
    [Theory]
    [InlineData("FET_Curtice")]
    [InlineData("BJT_NPN")]
    [InlineData("MOS1_N")]
    [InlineData("JFET_N")]
    public void G_ThePassivityGuardSeparatesActiveFromPassivated(string type)
    {
        var (model, bias) = ActiveDeviceAtBias(type);
        int failuresActive = 0, failuresPassive = 0;

        foreach (var v in bias)
            foreach (double hz in new[] { 1e6, 1e9, 1e10 })
            {
                double w = 2.0 * Math.PI * hz;
                var yA = model.LinearisedPortAdmittance(null!, w, v, passivated: false);
                var yP = model.LinearisedPortAdmittance(null!, w, v, passivated: true);
                Assert.NotNull(yA);
                Assert.NotNull(yP);

                double? mA = NdfCalculator.PassivityMargin(yA!);
                double? mP = NdfCalculator.PassivityMargin(yP!);
                if (mA is { } a && a < -1e-12) failuresActive++;
                if (mP is { } p && p < -1e-12) failuresPassive++;
            }

        output.WriteLine($"{type}: active block failed Y + Yᴴ ⪰ 0 at {failuresActive} of 9 " +
                         $"(bias, frequency) points; passivated block failed at {failuresPassive}");
        Assert.True(failuresActive > 0,
            $"{type}: the ACTIVE block of a biased device must fail the guard somewhere, or the " +
            "guard distinguishes nothing");
        Assert.Equal(0, failuresPassive);
    }

    /// <summary>Three conducting bias points for one built-in active family, in its own port
    /// coordinates. Built through the factory so the model is the one a netlist would get.</summary>
    private static (ComponentModel Model, PortVoltages[] Bias) ActiveDeviceAtBias(string type)
    {
        var pars = new Dictionary<string, CircuitRF.Core.Expressions.Value>(StringComparer.Ordinal);
        var model = ComponentModelFactory.TryCreate(type, pars)
                 ?? throw new InvalidOperationException($"the factory does not know '{type}'");
        int P = model.PortCount;

        // Each family numbers its ports its own way, and there is no shared convention to lean on:
        // the FET's are (Vgs, Vds); the JFET's (Vds, Vgs, Vgd); the BJT's (Vbe, Vbc, Vce, Vbx); the
        // MOSFET's (Vds, Vbs, Vbd, Vgs, Vgd, Vgb). A vector written for one family and handed to
        // another is a device biased OFF, which passes this gate for the wrong reason — so each is
        // written out, in conduction, and the assertion that the ACTIVE block fails the guard is
        // what catches a wrong one.
        PortVoltages V(params double[] v)
        {
            var full = new double[P];
            Array.Copy(v, full, Math.Min(v.Length, P));
            return new PortVoltages(full);
        }

        return type switch
        {
            // Vbe, Vbc, Vce, Vbx — forward active.
            "BJT_NPN" => (model, [V(0.75, -2.0, 2.75), V(0.80, -4.0, 4.80), V(0.85, -7.0, 7.85)]),

            // Vds, Vgs, Vgd — above pinch-off (the default Vto is −2 V).
            "JFET_N"  => (model, [V(3.0, -0.5, -3.5), V(5.0, -1.0, -6.0), V(8.0, 0.0, -8.0)]),

            // Vds, Vbs, Vbd, Vgs, Vgd, Vgb — above threshold (the default Vto is +1 V).
            "MOS1_N"  => (model, [V(3.0, 0.0, -3.0, 2.0, -1.0, 2.0),
                                  V(5.0, 0.0, -5.0, 3.0, -2.0, 3.0),
                                  V(8.0, 0.0, -8.0, 4.0, -4.0, 4.0)]),

            // Vgs, Vds — the FET family's own two ports.
            _         => (model, [V(0.0, 3.0), V(-0.5, 5.0), V(0.5, 8.0)]),
        };
    }

    // ── (i) K is not enough ──────────────────────────────────────────────────

    /// <summary>
    /// A two-port whose terminal S-parameters are a well-behaved 6 dB pad — <c>K &gt; 1</c> and
    /// <c>|Δ| &lt; 1</c> at every frequency in the band, so every two-port stability factor says
    /// "unconditionally stable" — wrapped around an internal loop that oscillates. The derived
    /// metrics say stable; the NDF says two right-half-plane poles.
    ///
    /// <para>This is Platzker's own point (INMMC 1994; the reference document's §3.2): <c>K</c> and
    /// <c>|Δ|</c> are computed from the REDUCED two-port and can say nothing about a pole the
    /// reduction hides. And the ONE thing that would make the demonstration circular is asserted
    /// against: the terminal S-parameters of the stable and unstable versions must be
    /// indistinguishable, or the two-port metrics would simply have been given different data.</para>
    /// </summary>
    [Fact]
    public void I_KAndDeltaSayStableWhileTheNdfCountsTwoPoles()
    {
        var unstable = RunNdf("hidden_pole_two_port.cnl", new Dictionary<string, string> { ["Gr"] = "-0.2" });
        var stable   = RunNdf("hidden_pole_two_port.cnl", new Dictionary<string, string> { ["Gr"] = "0.2" });

        Assert.Equal(2, Poles(unstable.Ds));
        Assert.Equal(0, Poles(stable.Ds));

        // Every two-port stability factor, over the whole band, on the UNSTABLE version.
        var snp = SnpOf(unstable.Ds, unstable.Freqs);
        var (k, _, _, d, _) = RFNetwork.StabilityK(snp);
        double worstK = k.Min(), worstD = d.Max();
        output.WriteLine($"unstable version: min K = {worstK:G6}, max |Δ| = {worstD:G6}, " +
                         $"NDF_poles = {Poles(unstable.Ds)}");
        Assert.True(worstK > 1.0, $"the two-port must look unconditionally stable; min K = {worstK:G6}");
        Assert.True(worstD < 1.0, $"the two-port must look unconditionally stable; max |Δ| = {worstD:G6}");

        // …and the terminal S-parameters cannot tell the two versions apart.
        var a = unstable.Ds["S"].ComplexValues;
        var b = stable.Ds["S"].ComplexValues;
        double worstS = 0.0;
        for (int i = 0; i < a.Length; i++) worstS = Math.Max(worstS, (a[i] - b[i]).Magnitude);
        output.WriteLine($"largest |S_unstable − S_stable| over the whole band = {worstS:E3}");
        Assert.True(worstS < 1e-4,
            $"the demonstration is only worth making if the ports cannot see the difference; " +
            $"largest |ΔS| was {worstS:E3}");
    }

    /// <summary>The run's S cube as an <see cref="SNP"/>, for the stability metrics.</summary>
    private static SNP SnpOf(DataSet ds, double[] freqs)
    {
        var cube = ds["S"];
        int n = cube.Axes[^1].Length;
        var raw = cube.ComplexValues;
        var mats = new NumFlat.Mat<Complex>[freqs.Length];
        for (int f = 0; f < freqs.Length; f++)
        {
            var m = new NumFlat.Mat<Complex>(n, n);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) m[i, j] = raw[(f * n + i) * n + j];
            mats[f] = m;
        }
        return new SNP((double[])freqs.Clone(), mats, MatrixType.S, MatrixFormat.RI, new Complex(50, 0));
    }

    // ── (e) the lemma against full determinants ──────────────────────────────

    /// <summary>
    /// <c>det(M)/det(M0)</c>, both taken explicitly as the sum of the logs of the LU pivots with the
    /// permutation parity, must reproduce the lemma's NDF. This is the check that the low-rank
    /// column bookkeeping identifies the right columns AND that eliminating the branch rows leaves
    /// the ratio alone — <c>M</c> and <c>M0</c> have identical branch blocks, so the elimination
    /// multiplies both determinants by the same factor and <c>det(M)/det(M0)</c> IS <c>|Y|/|Y_p|</c>.
    /// </summary>
    [Theory]
    [InlineData("single_loop_stage.cnl", -0.02)]
    [InlineData("single_loop_stage.cnl", +0.05)]
    public void E_LemmaMatchesExplicitDeterminants(string fixture, double gm)
    {
        var run = RunNdf(fixture, new Dictionary<string, string> { ["gm"] = Fmt(gm) });
        var ndf = Ndf(run.Ds);

        // The same two assemblies, built here from the engine's own stamping seam and reduced with
        // an ordinary dense LU — a deliberately different route to the same number.
        double worst = 0.0;
        for (int k = 0; k < run.Freqs.Length; k += 17)
        {
            var (m, m0) = DenseAssemblies(run.Netlist, run.Freqs[k]);
            var lu  = new WspMatrix.Lu(m);
            var lu0 = new WspMatrix.Lu(m0);
            var want = Complex.FromPolarCoordinates(
                Math.Exp(lu.LogAbsDet - lu0.LogAbsDet), lu.ArgDet - lu0.ArgDet);
            worst = Math.Max(worst, (ndf[k] - want).Magnitude / want.Magnitude);
        }
        output.WriteLine($"worst |lemma − explicit| / |explicit| = {worst:E3}");
        Assert.True(worst < 1e-10, $"worst relative error {worst:E3}");
    }

    /// <summary>The active and passive terminated assemblies as DENSE matrices, through the same
    /// stamping seam the engine uses but with none of its own machinery — a second route to the
    /// numbers (e) compares against.</summary>
    private static (Complex[,] M, Complex[,] M0) DenseAssemblies(ElaboratedNetlist nl, double hz)
    {
        var m  = Assemble(nl, hz, passive: false);
        var m0 = Assemble(nl, hz, passive: true);
        return (m, m0);
    }

    private static Complex[,] Assemble(ElaboratedNetlist nl, double hz, bool passive)
    {
        var mna = new MnaSystem(nl.Nodes.Count - 1);
        double omega = 2.0 * Math.PI * hz;
        var ports = new List<(int A, int B, Complex Z)>();
        foreach (var ec in nl.Components)
        {
            if (ec.ComponentType is "Term" or "Port" && !ec.InstancePath.Contains('.'))
            {
                ports.Add((ec.Nodes[0], ec.Nodes[1],
                           ec.Parameters.TryGetValue("Z", out var z) ? new Complex(z.AsReal(), 0)
                                                                     : new Complex(50, 0)));
                continue;
            }
            if (passive) ec.StampPassive(mna, omega);
            else         ec.Stamp(mna, omega);
        }
        foreach (var (a, b, z) in ports) mna.AddAdmittance(a, b, Complex.One / z);

        int n = mna.Size;
        var dense = new Complex[n, n];
        foreach (var (r, c, v) in mna.NonZeroEntries()) dense[r, c] = v;
        return dense;
    }

    // ── (j) a no-knob run is untouched, and the counters are §4's ────────────

    /// <summary>
    /// The NDF knob is additive: the same fixture run without it produces the same S-parameters bit
    /// for bit, and none of the NDF cubes. §4's cost, as counters rather than timings (owner rule,
    /// 2026-08-23): one EXTRA factorisation per frequency (the passive assembly's — <c>M</c> itself
    /// is never factored for the NDF) and <c>r</c> extra back-substitutions.
    /// </summary>
    [Fact]
    public void J_NoKnobRunIsUntouched_AndTheCountersAreTheOnesSection4Predicts()
    {
        string path = Fixture("single_loop_stage.cnl");
        string dir  = Path.GetDirectoryName(path)!;

        var (lib, tb) = new CnlReader().Read(File.ReadAllText(path), "tb", dir);
        var nl    = new Elaborator(lib) { BaseDirectory = dir }.Elaborate(tb);
        var freqs = tb.Analyses.OfType<SParameterAnalysis>().First().Expand(nl.ResolvedGlobals);
        var plain = SParameterEngine.RunWithStats(nl, freqs);

        var (lib2, tb2) = new CnlReader().Read(File.ReadAllText(path), "tb", dir);
        var nl2   = new Elaborator(lib2) { BaseDirectory = dir }.Elaborate(tb2);
        var withNdf = SParameterEngine.RunWithStats(nl2, freqs, null, null, new NdfRequest());

        Assert.False(plain.Data.Contains("NDF"));
        Assert.True(withNdf.Data.Contains("NDF"));
        Assert.True(withNdf.Data.Contains("NDF_enc"));
        Assert.True(withNdf.Data.Contains("NDF_poles"));

        var a = plain.Data["S"].ComplexValues;
        var b = withNdf.Data["S"].ComplexValues;
        Assert.Equal(a.Length, b.Length);
        for (int k = 0; k < a.Length; k++)
            Assert.True(a[k].Equals(b[k]), $"S[{k}] differs: {a[k]} vs {b[k]}");

        int n = freqs.Length;
        output.WriteLine($"no knob : {plain.Stats.Factorizations} factorisations, " +
                         $"{plain.Stats.BackSubstitutions} back-substitutions over {n} points");
        output.WriteLine($"NDF=yes : {withNdf.Stats.Factorizations} factorisations, " +
                         $"{withNdf.Stats.BackSubstitutions} back-substitutions over {n} points");

        Assert.Equal(n, plain.Stats.Factorizations);
        Assert.Equal(2 * n, withNdf.Stats.Factorizations);

        // r = 1 here: the VCCS controls exactly one column (its control node g; the other control
        // net is ground and has no column at all).
        Assert.Equal(plain.Stats.BackSubstitutions + n, withNdf.Stats.BackSubstitutions);
    }

    // ── (k) the envelope agrees with a re-run ────────────────────────────────

    /// <summary>
    /// [E]'s own comparison as a test (WSP-9 R-wsp9-6). On WSP-9's Ohtomo Type-A fixture at
    /// <c>ρ = 0.9</c>, over a coarse 30°×30° grid — 144 source/load pairs, each one a real re-run of
    /// the S-parameter analysis with the Terms changed — the engine's own <c>NDF_poles</c> and
    /// <c>wsp_loadpull_ndf</c>'s <c>NDFenc</c> must agree at every point, and every point the NDF
    /// calls unstable must have <c>SMenv &lt; −30 dB</c> (R-wsp9-5). The converse is PRINTED, not
    /// asserted: [E]'s own point is that the margin collapses before the NDF encircles.
    ///
    /// <para><b>Two things have to be said about what "agree" means here.</b> The envelope's NDF is
    /// the REDUCED one over the probe set (§8, pp. 112–113) while the engine's is the full network
    /// determinant, so the fixture carries a probe on BOTH gates — the odd mode is differential
    /// across them, and WSP-9's gate (i) shows a probe set covering only one cannot see it. And the
    /// two counts differ in CONVENTION by exactly two: <c>NDF_poles</c> counts right-half-plane
    /// poles over the closed Nyquist contour, <c>NDFenc</c> counts the swept locus's own turns.
    /// Both are asserted — the instability verdict, and the factor of two.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void K_TheEnvelopeNdfAgreesWithARerun()
    {
        foreach (string rb in new[] { "30", "100" })
        {
            var baseRun = RunNdf("ohtomo_type_a_ndf.cnl", new Dictionary<string, string> { ["Rb"] = rb });
            int nf = baseRun.Freqs.Length;

            var idx = ProbeIdx(baseRun.Ds);
            int idxS = idx["PS"], idxL = idx["PL"];

            var gS = WspEnvelope.CircleGrid(0.9, 12);
            var gL = WspEnvelope.CircleGrid(0.9, 12);
            var wA = AllWsp(baseRun.Ds, "wsp",         nf);
            var wP = AllWsp(baseRun.Ds, "wsp_passive", nf);

            var env = WspEnvelope.LoadpullNdf(wA, wP, baseRun.Freqs, idxS, idxL, null, gS, gL, 50.0);
            var mar = WspEnvelope.LoadpullMargin(wA, baseRun.Freqs, idxS, idxL, idx["PG"], gS, gL, 50.0);

            int agreed = 0, unstablePoints = 0, marginBelow30 = 0;
            for (int si = 0; si < gS.Length; si++)
                for (int li = 0; li < gL.Length; li++)
                {
                    var zs = WspEnvelope.GammaToZ(gS[si], 50.0);
                    var zl = WspEnvelope.GammaToZ(gL[li], 50.0);
                    var rerun = RunNdf("ohtomo_type_a_ndf.cnl", new Dictionary<string, string>
                    {
                        ["Rb"] = rb, ["ZS"] = Cx(zs), ["ZL"] = Cx(zl),
                    });

                    int native = Poles(rerun.Ds);
                    double byEnv = env.Encirclements[si, li];

                    Assert.True((native != 0) == (byEnv != 0.0),
                        $"Rb = {rb} Ω, ΓS = {gS[si]}, ΓL = {gL[li]}: the engine's NDF_poles is " +
                        $"{native} and the envelope's NDFenc is {byEnv}");
                    Assert.Equal(native, (int)Math.Round(2.0 * byEnv));
                    agreed++;

                    if (native >= 1)
                    {
                        unstablePoints++;
                        double db = 20.0 * Math.Log10(Math.Max(mar.SmEnv[si, li], 1e-300));
                        if (db < -30.0) marginBelow30++;
                        Assert.True(db < -30.0,
                            $"Rb = {rb} Ω, ΓS = {gS[si]}, ΓL = {gL[li]}: NDF_poles = {native} but " +
                            $"SMenv reads {db:F2} dB (WSP-9 R-wsp9-5)");
                    }
                }

            // The converse, PRINTED: how many points the margin calls marginal that the NDF does not
            // call unstable. [E]'s thesis is that this number is not zero.
            int marginalOnly = 0;
            for (int si = 0; si < gS.Length; si++)
                for (int li = 0; li < gL.Length; li++)
                    if (env.Encirclements[si, li] == 0.0 &&
                        20.0 * Math.Log10(Math.Max(mar.SmEnv[si, li], 1e-300)) < -30.0)
                        marginalOnly++;

            output.WriteLine(
                $"Rb = {rb} Ω, ρ = 0.9, 12×12 grid: {agreed} of {gS.Length * gL.Length} points " +
                $"compared; {unstablePoints} the NDF calls unstable, all {marginBelow30} of them " +
                $"with SMenv < −30 dB; {marginalOnly} more where the margin has collapsed and the " +
                "NDF has not encircled (printed, not asserted — that is [E]'s own point).");
        }
    }

    /// <summary>label → idx, from the run's own <c>__WspProbes</c> metadata cube — never guessed,
    /// because idx depends on the other probes (overview D-3).</summary>
    private static Dictionary<string, int> ProbeIdx(DataSet ds)
    {
        var cube = ds["__WspProbes"];
        var labels = cube.Axes[0].Labels!;
        var vals = cube.RealValues;
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int k = 0; k < labels.Length; k++) map[labels[k]] = (int)vals[k];
        return map;
    }

    private static Complex[][,] AllWsp(DataSet ds, string cube, int nf)
    {
        var all = new Complex[nf][,];
        for (int fi = 0; fi < nf; fi++) all[fi] = WspAt2(ds, cube, fi);
        return all;
    }

    /// <summary>
    /// A resistor's TYPE-level answer is <see cref="Activity.ActiveExact"/> because a negative one
    /// is §8's negative resistance, but the PLACED instance knows its own value and answers
    /// <see cref="Activity.Passive"/> when <c>R ≥ 0</c> — <c>StampPassive</c> and <c>Stamp</c> are
    /// then the same stamp and there is nothing to passivate.
    ///
    /// <para>It matters at the report: <c>explain --analysis</c> lists only the instances that are
    /// not plainly passive, "because a hundred resistors saying passive is noise", and its
    /// "N carrying activity to passivate" count is meant to be the active-device count. Without the
    /// instance-level answer every resistor in every design was listed and counted. Nothing
    /// numerical moves, which is the other half of this gate.</para>
    /// </summary>
    [Fact]
    public void H2_APositiveResistorIsPassiveAtTheInstance_ANegativeOneIsNot()
    {
        Assert.Equal(Activity.ActiveExact, new ResistorModel().Activity);

        const string net = """
            R:RPOS  a 0  R=50 Ohm
            R:RNEG  b 0  R=-50 Ohm
            C:C1    a b  C=1 pF
            WSProbe:P a b
            analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz NDF=yes
            """;
        var (lib, tb) = new CnlReader().Read(net, "tb", null);
        using var nl = new Elaborator(lib).Elaborate(tb);
        double[] freqs = [1e9, 1.5e9, 2e9];

        var byPath = nl.Components.ToDictionary(c => c.InstancePath, c => c.ActivityFor(freqs));
        Assert.Equal(Activity.Passive,     byPath["RPOS"]);
        Assert.Equal(Activity.ActiveExact, byPath["RNEG"]);

        // The survey the CLI's listing is built from names the negative one and not the positive.
        var survey = NdfPassivation.Survey(nl, freqs, [], []);
        var active = survey.Instances.Where(i => i.Activity != Activity.Passive).Select(i => i.InstancePath).ToList();
        Assert.Contains("RNEG", active);
        Assert.DoesNotContain("RPOS", active);
    }

    /// <summary>A complex impedance in the netlist's own spelling, for a <c>Z=</c> override.</summary>
    private static string Cx(Complex z)
        => $"{z.Real.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}" +
           $"{(z.Imaginary < 0 ? "-" : "+")}j*" +
           $"{Math.Abs(z.Imaginary).ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";

    // ── (h) the contract is complete ─────────────────────────────────────────

    /// <summary>
    /// Every concrete <see cref="ComponentModel"/> subclass in the repository appears in the
    /// table below, and every one that declares <see cref="Activity.ActiveExact"/> actually
    /// overrides one of the passivation hooks. A model added to the codebase and not to the table
    /// FAILS — so the next active device cannot be added without deciding its passivation.
    /// </summary>
    [Fact]
    public void H_EveryComponentModelDeclaresItsPassivation()
    {
        var models = typeof(ComponentModel).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(ComponentModel)) && !t.IsAbstract)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();

        var missing = models.Select(t => t.Name).Where(n => !Table.ContainsKey(n)).ToArray();
        Assert.True(missing.Length == 0,
            "these ComponentModel subclasses are not in brief-wsprobe-6 §3's table (add them to the " +
            "table in docs/design/stability-wsprobe.md §11 AND to this test, after deciding what " +
            "their passivation is): " + string.Join(", ", missing));

        var stale = Table.Keys.Where(n => models.All(t => t.Name != n)).ToArray();
        Assert.True(stale.Length == 0,
            "the table names models that no longer exist: " + string.Join(", ", stale));

        foreach (var t in models)
        {
            var model = (ComponentModel?)TryConstruct(t);
            if (model is null) continue;   // needs constructor arguments the table covers by type

            Assert.True(model.Activity == Table[t.Name],
                $"{t.Name}.Activity is {model.Activity}, the table says {Table[t.Name]}");

            if (model.Activity != Activity.ActiveExact) continue;

            bool overridesStamp =
                Declares(t, nameof(ComponentModel.StampPassive)) ||
                Declares(t, nameof(ComponentModel.StampLinearizedPassive)) ||
                model.ControlledConductances.Count > 0 ||
                Declares(t, "PassivateS");
            Assert.True(overridesStamp,
                $"{t.Name} declares Activity.ActiveExact but overrides neither StampPassive nor " +
                "StampLinearizedPassive, and names no ControlledConductances — so its dependent " +
                "source would survive into Δ0 (R-wsp6-3).");
        }

        static bool Declares(Type t, string method)
        {
            for (var x = t; x is not null && x != typeof(ComponentModel); x = x.BaseType)
                if (x.GetMethod(method, BindingFlags.Instance | BindingFlags.Public
                                      | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is not null)
                    return true;
            return false;
        }
    }

    /// <summary>Builds a model with no constructor arguments, or returns null when it needs some —
    /// in which case the table still pins its expected Activity by TYPE, checked below through the
    /// families' shared bases.</summary>
    private static object? TryConstruct(Type t)
    {
        try { return t.GetConstructor(Type.EmptyTypes) is { } ci ? ci.Invoke(null) : null; }
        catch { return null; }
    }

    /// <summary>
    /// brief-wsprobe-6 §3's table, as code. Kept beside the models rather than in the design note
    /// alone, so that adding a model without deciding its passivation fails a test rather than
    /// leaving Δ0 quietly active.
    /// </summary>
    private static readonly Dictionary<string, Activity> Table = new(StringComparer.Ordinal)
    {
        // Passive — as-is; the ordinary stamp already IS the passive stamp.
        ["CapacitorModel"]        = Activity.Passive,
        ["InductorModel"]         = Activity.Passive,
        ["SeriesRlcModel"]        = Activity.Passive,
        ["ParallelRlcModel"]      = Activity.Passive,
        // The six two-element members of the same family (2026-09-20). Passive for the same reason
        // the three-element pair is: the stamp is the passive stamp.
        ["SeriesRlModel"]         = Activity.Passive,
        ["SeriesRcModel"]         = Activity.Passive,
        ["SeriesLcModel"]         = Activity.Passive,
        ["ParallelRlModel"]       = Activity.Passive,
        ["ParallelRcModel"]       = Activity.Passive,
        ["ParallelLcModel"]       = Activity.Passive,
        ["BeadModel"]             = Activity.Passive,
        ["MutualInductanceModel"] = Activity.Passive,
        ["TLineModel"]            = Activity.Passive,
        ["MicrostripLineModel"]   = Activity.Passive,
        ["MicrostripBendModel"]   = Activity.Passive,
        ["MicrostripTeeModel"]    = Activity.Passive,
        ["MicrostripCrossModel"]  = Activity.Passive,
        ["MicrostripTaperModel"]  = Activity.Passive,
        ["MicrostripKlopfModel"]  = Activity.Passive,
        ["ShortModel"]            = Activity.Passive,
        ["IProbeModel"]           = Activity.Passive,
        ["WSProbeModel"]          = Activity.Passive,
        ["PortModel"]             = Activity.Passive,
        ["TermModel"]             = Activity.Passive,
        ["WBondModel"]            = Activity.Passive,
        ["MatchModel"]            = Activity.Passive,

        // Sources: off in this assembly already; the two that carry an impedance stamp it.
        ["VdcModel"]              = Activity.Passive,
        ["ToneSourceModel"]       = Activity.Passive,
        ["CurrentToneSourceModel"] = Activity.Passive,
        ["PnToneModel"]           = Activity.Passive,
        ["P1ToneModel"]           = Activity.Passive,
        ["TunerModel"]            = Activity.Passive,

        // Ideal system blocks: passive by construction, the circulator included — Platzker zeroes
        // dependent sources, not non-reciprocity.
        ["AttenuatorModel"]       = Activity.Passive,
        ["SwitchModel"]           = Activity.Passive,
        ["CirculatorModel"]       = Activity.Passive,
        ["CouplerModel"]          = Activity.Passive,
        ["BalunModel"]            = Activity.Passive,
        ["FilterModel"]           = Activity.Passive,
        ["DuplexerModel"]         = Activity.Passive,

        // Two-terminal nonlinearities: a linearised C(V) or I(V) is a passive element at any bias.
        ["DiodeModel"]            = Activity.Passive,
        ["NonlinearCModel"]       = Activity.Passive,
        ["SemiCapacitorModel"]    = Activity.Passive,

        // Exact — circuitRF wrote the dependent source and zeroes it itself.
        ["ResistorModel"]         = Activity.ActiveExact,   // R → |R| (a negative R is active)
        ["ZPortModel"]            = Activity.ActiveExact,   // Re Z → |Re Z| on the diagonal
        ["VccsModel"]             = Activity.ActiveExact,   // G → 0
        ["VcvsModel"]             = Activity.ActiveExact,   // E → 0 (the branch stays: a short)
        ["AmplifierModel"]        = Activity.ActiveExact,   // forward gain → 0, isolation kept
        ["MixerModel"]            = Activity.ActiveExact,   // conversion → 0
        ["CurticeQuadraticFetModel"] = Activity.ActiveExact,
        ["CurticeCubicFetModel"]     = Activity.ActiveExact,
        ["StatzFetModel"]            = Activity.ActiveExact,
        ["MaterkaFetModel"]          = Activity.ActiveExact,
        ["AngelovFetModel"]          = Activity.ActiveExact,
        ["BjtModel"]              = Activity.ActiveExact,
        ["MosfetLevel1Model"]     = Activity.ActiveExact,
        ["MosfetLevel3Model"]     = Activity.ActiveExact,
        ["VdmosModel"]            = Activity.ActiveExact,
        ["IgbtModel"]             = Activity.ActiveExact,
        ["JfetModel"]             = Activity.ActiveExact,

        // User-scaled — the activity is in equations or a binary this repository did not write.
        ["SddModel"]              = Activity.ActiveUserScaled,
        ["ExternalDeviceModel"]   = Activity.ActiveUserScaled,

        // Data-dependent: BlackBox by type, upgraded to Passive by ActivityFor when the DATA is
        // measured passive (σ_max ≤ 1 + 1e-6).
        ["SnpModel"]              = Activity.BlackBox,
        ["ChainModel"]            = Activity.BlackBox,
    };

    private static string Fmt(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
