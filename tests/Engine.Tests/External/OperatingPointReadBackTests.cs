using System.Globalization;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.External;

/// <summary>
/// Reading a compact model's own operating-point variables back — the <c>OP</c> cube, its
/// provenance twin, the <c>DC1.OP(…)</c> accessor, and the per-sample large-signal form.
///
/// <para><b>Every value asserted here comes from <see cref="SquareLawFetProvider.Channel"/>'s closed
/// form, evaluated at the operating point the engine reports.</b> That is the point of the fixture:
/// a real compiled model has no closed form, so a test written against one can assert only that
/// nothing crashed — which passes just as happily on a read-back that returns the PREVIOUS bias.
/// That off-by-one is the whole failure class this feature has, and it is invisible to any check
/// that does not know the right answer independently.</para>
/// </summary>
[Collection("ExternalDeviceRegistry")]
public sealed class OperatingPointReadBackTests : IDisposable
{
    private const string Provider = "synthetic";

    private readonly SquareLawFetProvider _provider = new(Provider);

    public OperatingPointReadBackTests()
    {
        ExternalDeviceRegistry.Clear();
        ExternalDeviceRegistry.Register(_provider);
    }

    public void Dispose() => ExternalDeviceRegistry.Clear();

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static readonly SquareLawFetProvider.Params P =
        new(Beta: 0.05, Vth0: 1.0, Lambda: 0.02, Rg: 10.0, Rs: 1.0, Rth: 5.0, Ktv: 0.0);

    private static string Inv(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A gate-driven, drain-biased FET with its thermal pin on its own net.</summary>
    private static string Netlist(string vg, string vdd, string extra = "", string analyses = "")
        => $"""
            Vdc:VG   g 0  Vdc={vg}
            Vdc:VD   d 0  Vdc={vdd}
            ExtDevice:X1  g d 0 tj  Provider={Provider} Type={SquareLawFetProvider.TypeName} Beta={Inv(P.Beta)} Vth0={Inv(P.Vth0)} Lambda={Inv(P.Lambda)} Rg={Inv(P.Rg)} Rs={Inv(P.Rs)} Rth={Inv(P.Rth)} Ktv={Inv(P.Ktv)} {extra}
            {analyses}
            """;

    private static (NonlinearDcEngine.DcResult Dc, DataSet Ds) RunDc(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var dc  = NonlinearDcEngine.Run(nl);
        return (dc, DcResultPacker.Pack(dc, nl));
    }

    /// <summary>
    /// The op-vars the fixture MUST have reported at a solved operating point, computed from the
    /// engine's own converged node voltages through the fixture's closed form — the independent
    /// answer, not a second reading of the same storage.
    /// </summary>
    private static (double Id, double Gm, double Gds, double Tj) Expected(
        NonlinearDcEngine.DcResult dc, ElaboratedNetlist nl)
    {
        double V(string net)
        {
            for (int c = 1; c < nl.Nodes.Count; c++)
                if (nl.Nodes.NameOf(c) == net) return dc.NodeVoltages[c - 1];
            throw new Xunit.Sdk.XunitException($"no net '{net}' in the elaborated netlist");
        }

        // The internal nodes the elaborator minted for this instance, in descriptor order.
        string[] minted = [.. Enumerable.Range(1, nl.Nodes.Count - 1)
                                        .Select(c => nl.Nodes.NameOf(c))
                                        .Where(n => n.StartsWith("__extdev_", StringComparison.Ordinal))];
        Assert.Equal(2, minted.Length);

        double vgi = V(minted[0]), vsi = V(minted[1]);
        double vd = V("d"), t = V("tj");

        var (id, gm, gds, _) = SquareLawFetProvider.Channel(P, vgi - vsi, vd - vsi, t);
        return (id, gm, gds, t);
    }

    private static void Close(double expected, double actual, string what)
        => Assert.True(Math.Abs(actual - expected) <= 1e-9 + 1e-9 * Math.Abs(expected),
                       $"{what}: expected {expected:G17}, got {actual:G17}");

    private static double Real(DataCube cube, params object[] at) => cube[at];
    private static Complex Cplx(DataCube cube, params object[] at) => cube[at];

    // ── P3: the DC cube ───────────────────────────────────────────────────────

    /// <summary>
    /// The read-back matches arithmetic at the converged bias — the only assertion that can tell a
    /// correct read from one taken a call too late.
    /// </summary>
    [Fact]
    public void Dc_OpVarsMatchClosedForm_AtTheConvergedBias()
    {
        var (lib, tb) = new CnlReader().Read(Netlist("3.0", "5.0"));
        var nl = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl);

        Assert.True(dc.Converged);
        var (id, gm, gds, tj) = Expected(dc, nl);
        Assert.True(id > 1e-4, "the fixture must actually be conducting, or this proves nothing");

        Close(id,  dc.OperatingPointVars["X1.Id"],  "Id");
        Close(gm,  dc.OperatingPointVars["X1.Gm"],  "Gm");
        Close(gds, dc.OperatingPointVars["X1.Gds"], "Gds");
        Close(tj,  dc.OperatingPointVars["X1.Tj"],  "Tj");

        // An int op-var is a real once it is a number in a cube.
        Assert.Equal(1.0, dc.OperatingPointVars["X1.Region"]);

        // A STRING op-var is declared and is never read back — a single-kind numeric cube has
        // nowhere to put it. Declared-and-unreadable, not absent: the descriptor still names it.
        Assert.DoesNotContain("X1.Regime", dc.OperatingPointVars.Keys);
        Assert.Contains(SquareLawFetProvider.TypeDescriptor.OpVars, o => o.Name == "Regime");
    }

    /// <summary>One cube on a labelled axis, plus the provenance twin the picker filters on.</summary>
    [Fact]
    public void Dc_PacksOneCubeOnALabelledAxis_WithProvenance()
    {
        var (_, ds) = RunDc(Netlist("3.0", "5.0"));

        Assert.True(ds.Contains("OP"));
        Assert.True(ds.Contains("__OpVars"));

        var op = ds["OP"];
        Assert.Equal(1, op.Rank);
        Assert.Equal("opvar", op.Axes[0].Name);
        Assert.Equal(DataKind.Real, op.DataKind);

        // Not one cube per quantity — the shape a model declaring tens of them has to survive.
        Assert.Equal(5, op.Axes[0].Labels!.Length);
        Assert.All(op.Axes[0].Labels!, l => Assert.StartsWith("X1.", l, StringComparison.Ordinal));
        Assert.DoesNotContain(ds.Cubes.Keys, n => n.StartsWith("OP:", StringComparison.Ordinal));

        // Stable order, so a picker's rows do not move between two runs of the same design.
        Assert.Equal([.. op.Axes[0].Labels!.OrderBy(x => x, StringComparer.Ordinal)], op.Axes[0].Labels!);

        Assert.Equal(op.Axes[0].Labels!, ds["__OpVars"].Axes[0].Labels!);
    }

    /// <summary>A circuit with no external device adds no cubes and asks no provider anything.</summary>
    [Fact]
    public void Dc_NoExternalDevice_AddsNoCubesAtAll()
    {
        _provider.ResetCounters();

        var (_, ds) = RunDc("""
            Vdc:V1  a 0  Vdc=1
            R:R1    a 0  R=1k
            Diode:D1 a 0  Is=1e-14
            """);

        Assert.False(ds.Contains("OP"));
        Assert.False(ds.Contains("__OpVars"));
        Assert.Equal(0, _provider.OperatingPointReads);
    }

    // ── P3: correspondence under a sweep ──────────────────────────────────────

    /// <summary>
    /// <b>The trap this feature has.</b> After stacking, the value at sweep index k must be the one
    /// read at k's own converged point. Two points at genuinely different biases, in the right
    /// order — a read taken outside the solve's scope gives two copies of one, or the two swapped,
    /// and nothing in the residual objects.
    /// </summary>
    [Fact]
    public void Sweep_EachPointCarriesItsOwnReadBack_InOrder()
    {
        var (lib, tb) = new CnlReader().Read(Netlist("VG_val", "5.0", analyses: """
            VG_val = 2.5
            analysis DC1  type=dc
            analysis SW1  type=parametric_sweep  Var=VG_val  Values=2.5,4.0  Inner=DC1
            """));

        var sweep = (ParametricSweepAnalysis)tb.Analyses.First(a => a.Name == "SW1");
        var ds    = ParametricSweepEngine.Run(sweep, lib, tb);

        var op = ds["OP"];
        Assert.Equal(2, op.Rank);
        Assert.Equal("VG_val", op.Axes[0].Name);
        Assert.Equal("opvar",  op.Axes[1].Name);

        // `__`-prefixed, so StackSweepAxis passes it through sweep-invariantly rather than
        // prepending an axis to what is only a list of names.
        Assert.True(ds.Contains("__OpVars"));
        Assert.Equal(1, ds["__OpVars"].Rank);

        int gm = Array.IndexOf(op.Axes[1].Labels!, "X1.Gm");
        double gmAtLow  = Real(op, 0, gm);
        double gmAtHigh = Real(op, 1, gm);

        // Both real, both different, and in the direction the physics demands: a square-law channel
        // has more gm at more overdrive. Two equal values is what a read outside the solve's own
        // scope produces, and "different" alone would not catch them arriving swapped.
        Assert.True(gmAtLow  > 1e-6, $"gm at VG=2.5 was {gmAtLow}");
        Assert.True(gmAtHigh > gmAtLow * 1.2,
                    $"gm should rise with overdrive: {gmAtLow:G6} → {gmAtHigh:G6}");

        // And each one is the closed form at ITS OWN converged point, not merely a rising pair.
        foreach (var (k, vg) in new[] { (0, 2.5), (1, 4.0) })
        {
            var (lib2, tb2) = new CnlReader().Read(Netlist(Inv(vg), "5.0"));
            var nl2 = new Elaborator(lib2).Elaborate(tb2);
            var dc2 = NonlinearDcEngine.Run(nl2);
            Close(Expected(dc2, nl2).Gm, Real(op, k, gm), $"gm at sweep index {k}");
        }
    }

    // ── D3: what a read-back costs ────────────────────────────────────────────

    /// <summary>
    /// <b>A counter, deliberately, not a timing test.</b> The structural property is "one evaluation
    /// per device per converged point" — a read-back accidentally wired into the Newton loop would
    /// still pass any wall-clock budget on a fast machine and would still be wrong.
    /// </summary>
    [Fact]
    public void ReadBack_CostsOneEvaluationAndOneReadPerDevicePerPoint()
    {
        var (lib, tb) = new CnlReader().Read(Netlist("3.0", "5.0"));

        // Elaboration itself evaluates the device — it probes for unwritten nodes — so the counter
        // is reset AFTER it, or the solve's own cost is not what is being counted.
        var nl = new Elaborator(lib).Elaborate(tb);

        _provider.ResetCounters();
        var dc = NonlinearDcEngine.Run(nl);
        Assert.True(dc.Converged);

        // ONE read for the whole solve. The solve took many Newton iterations, each of which
        // evaluated the device — a read-back wired into that loop instead of taken once at the
        // answer would be a count in the dozens, and would still pass any wall-clock budget.
        Assert.True(dc.Iterations > 3, $"the fixture must actually iterate, got {dc.Iterations}");
        Assert.Equal(1, _provider.OperatingPointReads);

        // And the read-back's own share of the evaluations is exactly one: the deliberate one at
        // the converged bias. Measured directly on the model, which is the only way to separate it
        // from the solve's own evaluations rather than inferring it from a difference.
        var model = nl.Components.Select(c => c.Model).OfType<ExternalDeviceModel>().Single();
        _provider.ResetCounters();
        Assert.NotNull(model.ReadOperatingPointAt(new double[SquareLawFetProvider.NodeCount]));
        Assert.Equal(1, _provider.PointsEvaluated);
        Assert.Equal(1, _provider.OperatingPointReads);
    }

    /// <summary>
    /// The per-instance switch. Off removes the round trip as well as the cube — which is the whole
    /// answer to what a read-back costs a user who never plots one.
    /// </summary>
    [Fact]
    public void SwitchedOff_PublishesNothingAndAsksNothing()
    {
        var (lib, tb) = new CnlReader().Read(Netlist("3.0", "5.0"));
        var nl = new Elaborator(lib).Elaborate(tb);

        // The design-layer switch is a VerilogA parameter; on an ExtDevice the same state is the
        // model's own init-only flag, which is what the engine actually reads.
        foreach (var ec in nl.Components)
            if (ec.Model is ExternalDeviceModel ed)
                Assert.True(ed.ReportsOperatingPoint);

        _provider.ResetCounters();
        var on = NonlinearDcEngine.Run(nl);
        Assert.NotEmpty(on.OperatingPointVars);
        Assert.Equal(1, _provider.OperatingPointReads);

        var off = new ExternalDeviceModel(
            _provider.Create(SquareLawFetProvider.TypeName, new Dictionary<string, string>()),
            Provider, "X1") { ReportOperatingPoint = false };
        Assert.False(off.ReportsOperatingPoint);

        _provider.ResetCounters();
        Assert.Null(off.ReadOperatingPointAt(new double[SquareLawFetProvider.NodeCount]));
        Assert.Null(off.ReadOperatingPointOver([new double[SquareLawFetProvider.NodeCount]]));
        Assert.Equal(0, _provider.OperatingPointReads);
        Assert.Equal(0, _provider.PointsEvaluated);
    }

    // ── P4: naming one ────────────────────────────────────────────────────────

    /// <summary>
    /// <c>DC1.OP("X1.Gm")</c>. Qualified only — there is no unqualified spelling, so a testbench
    /// running two analyses never has to be guessed about.
    /// </summary>
    [Fact]
    public void Measurement_ResolvesAQualifiedOpVarByName()
    {
        var (lib, tb) = new CnlReader().Read(Netlist("3.0", "5.0", analyses: """
            analysis DC1  type=dc
            measure gm_S  = DC1.OP("X1.Gm")
            measure gm_mS = DC1.OP("X1.Gm") * 1000
            """));

        var nl  = new Elaborator(lib).Elaborate(tb);
        var dc  = NonlinearDcEngine.Run(nl);
        var ds  = DcResultPacker.Pack(dc, nl);

        var me     = new MeasurementEvaluator(tb, nl, new Dictionary<string, DataSet> { ["DC1"] = ds });
        var errors = me.EvaluateInto(ds);
        Assert.Empty(errors);

        Close(dc.OperatingPointVars["X1.Gm"], ds["gm_S"].RealValues[0], "gm through the accessor");
        Close(dc.OperatingPointVars["X1.Gm"] * 1000.0, ds["gm_mS"].RealValues[0], "gm in mS");
    }

    /// <summary>
    /// A name the model does not declare is refused BY NAME. A silently dropped reference is a wrong
    /// answer that converges.
    /// </summary>
    [Fact]
    public void Measurement_RefusesAnUndeclaredOpVar_ByName()
    {
        var (lib, tb) = new CnlReader().Read(Netlist("3.0", "5.0", analyses: """
            analysis DC1  type=dc
            measure oops = DC1.OP("X1.NotAThing")
            """));

        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = DcResultPacker.Pack(NonlinearDcEngine.Run(nl), nl);

        var errors = new MeasurementEvaluator(tb, nl, new Dictionary<string, DataSet> { ["DC1"] = ds })
                     .EvaluateInto(ds);

        string all = string.Join("\n", errors);
        Assert.Contains("X1.NotAThing", all, StringComparison.Ordinal);
        Assert.Contains("X1.Gm", all, StringComparison.Ordinal);   // and it says what IS available
        Assert.False(ds.Contains("oops"));
    }

    // ── P5: the large-signal form ─────────────────────────────────────────────

    /// <summary>
    /// <b>At a large-signal point an op-var is a waveform, not a scalar.</b> The cube is Complex on
    /// the same harmonic axis <c>V</c> and <c>INl</c> use, and k=0 is the cycle average — which is
    /// what a designer means by "gm at this drive". A drive that swings the device must move that
    /// average away from its own small-signal value, and must put energy in the harmonics; a
    /// read-back that reported one arbitrary sample would do neither reliably.
    /// </summary>
    [Fact]
    public void Hb_OpVarsAreWaveformsOnTheHarmonicAxis()
    {
        const string cnl = $"""
            V_1Tone:VS   n_gbias 0  Vdc=3  Freq=2e9  V=0.8  Phase=0
            L:Lbias_g    n_gbias g  L=1  R=0
            V:Vdd        n_dbias 0  V=5
            L:Lbias_d    n_dbias d  L=1  R=0
            R:Rload      d 0  R=50
            ExtDevice:X1  g d 0 tj  Provider=synthetic Type=SquareLawFet Beta=0.05 Vth0=1 Lambda=0.02 Rg=10 Rs=1 Rth=5 Ktv=0
            analysis HB1  type=hb  Tone=2e9  MaxHarm=5  Tol=1e-8
            """;

        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        DataSet res = new HbEngine(nl, tb).Run(HbEngine.Resolve(hba, nl.ResolvedGlobals));

        var op = res["OP"];
        Assert.Equal(2, op.Rank);
        Assert.Equal("opvar",    op.Axes[0].Name);
        Assert.Equal("harmonic", op.Axes[1].Name);
        Assert.Equal(DataKind.Complex, op.DataKind);
        Assert.True(res.Contains("__OpVars"));

        int gm = Array.IndexOf(op.Axes[0].Labels!, "X1.Gm");
        Assert.True(gm >= 0, string.Join(", ", op.Axes[0].Labels!));

        Complex dc  = Cplx(op, gm, 0);
        Complex h1  = Cplx(op, gm, 1);

        // DC of a real waveform is real.
        Assert.True(Math.Abs(dc.Imaginary) < 1e-9 * (1.0 + Math.Abs(dc.Real)),
                    $"the cycle average of a real quantity must be real, got {dc}");
        Assert.True(dc.Real > 0, $"a conducting device must have a positive average gm, got {dc.Real}");

        // A driven device swings gm over the cycle, so the fundamental is not negligible beside the
        // average — this is the assertion a per-sample read makes possible and a scalar cannot.
        Assert.True(h1.Magnitude > 0.01 * dc.Real,
                    $"gm should swing under drive: |gm(f0)| = {h1.Magnitude:G6} vs mean {dc.Real:G6}");
    }

    /// <summary>An HB run with the read-back switched off publishes no OP cube.</summary>
    [Fact]
    public void Hb_NoReportingDevice_AddsNoCubes()
    {
        const string cnl = """
            V_1Tone:VS  a 0  Vdc=0  Freq=2e9  V=0.5  Phase=0
            R:R1        a b  R=50
            Diode:D1    b 0  Is=1e-14
            analysis HB1  type=hb  Tone=2e9  MaxHarm=5  Tol=1e-8
            """;

        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        DataSet res = new HbEngine(nl, tb).Run(HbEngine.Resolve(hba, nl.ResolvedGlobals));

        Assert.False(res.Contains("OP"));
        Assert.False(res.Contains("__OpVars"));
    }
}
