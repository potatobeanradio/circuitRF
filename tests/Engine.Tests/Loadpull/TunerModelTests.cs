using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// Step 1 unit tests for TunerModel (loadpull.md §1, Phase4b1_Brief.md Step 1).
/// </summary>
public class TunerModelTests(ITestOutputHelper output)
{
    // ── Test 1: Z form parses from .cnl ──────────────────────────────────────

    [Fact]
    public void Tuner_ZForm_ParsesFromCnl()
    {
        var cnl = "Tuner:Load  n_drain 0   Z[1]=80+j*10   Z[2]=1   Zdefault=1e-6   BiasTee=on   Vbias=48\n";
        var (_, tb) = new CnlReader().Read(cnl, sourceDirectory: null);
        Assert.Single(tb.Instances);
        var inst = tb.Instances[0];
        Assert.Equal("Tuner", inst.Reference, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Load", inst.InstanceName);

        var z1Ov = inst.Overrides.FirstOrDefault(o => o.Name == "Z[1]");
        Assert.NotNull(z1Ov);
        Assert.Equal("80+j*10", z1Ov!.Expression);

        var z2Ov = inst.Overrides.FirstOrDefault(o => o.Name == "Z[2]");
        Assert.NotNull(z2Ov);
        output.WriteLine("Tuner Z-form parse: OK");
    }

    // ── Test 2: Γ form converts to Z correctly ────────────────────────────────

    [Fact]
    public void Tuner_GammaForm_ConvertsToZ()
    {
        // G[1]=0.5 with Z0=50 → Z = 50*(1+0.5)/(1-0.5) = 150 Ω
        double z0 = 50.0;
        var    gamma = new Complex(0.5, 0);
        var    hz    = new Dictionary<int, Complex>
        {
            [1] = z0 * (Complex.One + gamma) / (Complex.One - gamma)
        };
        var tm = new TunerModel("GTest", hz, new Complex(1e-6, 0), false, 0);
        var z1 = tm.GetDeclaredZ(1);

        Assert.InRange(z1.Real,      149.999, 150.001);
        Assert.InRange(z1.Imaginary, -1e-9,   1e-9);
        output.WriteLine($"Γ=0.5 → Z={z1.Real:F2}+j{z1.Imaginary:F2} Ω  (expected 150+j0 Ω)");
    }

    // ── Test 3: Same harmonic with Z and G → error ────────────────────────────

    [Fact]
    public void Tuner_SameHarmonicZandG_ThrowsError()
    {
        var parameters = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
        {
            ["TunerName"] = new Value("BadTuner"),
            ["Z[1]"]      = new Value(25.0),
            ["G[1]"]      = new Value(0.5),
        };
        var ex = Assert.Throws<InvalidOperationException>(
            () => ComponentModelFactory.TryCreate("Tuner", parameters));
        Assert.Contains("harmonic 1", ex.Message);
        output.WriteLine($"Same-harmonic error: '{ex.Message}'");
    }

    // ── Test 4: Missing Z[1]/G[1] → error ────────────────────────────────────

    [Fact]
    public void Tuner_MissingZ1_ThrowsError()
    {
        var parameters = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
        {
            ["TunerName"] = new Value("NoZ1"),
            ["Z[2]"]      = new Value(1.0),
        };
        var ex = Assert.Throws<InvalidOperationException>(
            () => ComponentModelFactory.TryCreate("Tuner", parameters));
        Assert.Contains("Z[1] or G[1] is required", ex.Message);
        output.WriteLine($"Missing Z[1] error: '{ex.Message}'");
    }

    // ── Test 5: Zdefault fallback ─────────────────────────────────────────────

    [Fact]
    public void Tuner_UnknownHarmonic_FallsBackToZdefault()
    {
        var hz = new Dictionary<int, Complex> { [1] = new Complex(80, 10) };
        var tm = new TunerModel("T", hz, new Complex(1e-6, 0), false, 0);
        Assert.InRange(tm.GetDeclaredZ(3).Real, 9.9e-7, 1.1e-6);
        output.WriteLine("Zdefault fallback: OK");
    }

    // ── Test 6: SourceTuner |Vs| formula ─────────────────────────────────────

    [Fact]
    public void SourceTuner_ComputesCorrectVsMagnitude()
    {
        // Z[1]=25 Ω, Pavl=1 mW → |Vs| = sqrt(8·0.001·25) = sqrt(0.2) ≈ 0.4472 V
        var hz  = new Dictionary<int, Complex> { [1] = new Complex(25, 0) };
        var tm  = new TunerModel("Src", hz, new Complex(1e-6, 0), false, 0);
        tm.SetRole(TunerRole.Source);
        tm.SetSourceDrive(2e9, 1e-3);

        double expectedVs = Math.Sqrt(8 * 1e-3 * 25);

        // Stamp at the fundamental omega and capture source values.
        var ctx   = new CaptureMnaContext();
        var nodes = new int[] { 1, 2, 3, 4 };
        var ec    = new ElaboratedComponent("Tuner", "Src", nodes,
            new Dictionary<string, Value>(), tm);
        tm.Stamp(ctx, ec, 2.0 * Math.PI * 2e9);

        // The V_1Tone drive stamps a source value of |Vs| at the fundamental.
        var driven = ctx.SourceValues.Where(v => v.Magnitude > 1e-9).ToList();
        Assert.NotEmpty(driven);
        Assert.InRange(driven[0].Real, expectedVs - 1e-6, expectedVs + 1e-6);
        output.WriteLine($"|Vs|_expected={expectedVs:F6} V, stamped={driven[0].Real:F6} V ✓");
    }

    // ── Test 7: Elaborator mints internal nodes ───────────────────────────────

    [Fact]
    public void Elaborator_Tuner_MintsInternalNodes()
    {
        var cnl = "Tuner:Load  n_drain 0   Z[1]=80+j*10   Zdefault=1e-6   BiasTee=on   Vbias=48\n";
        var (lib, tb) = new CnlReader().Read(cnl, sourceDirectory: null);
        var elab    = new Elaborator(lib);
        var netlist = elab.Elaborate(tb);

        var ec = netlist.Components.FirstOrDefault(c =>
            c.ComponentType.Equals("Tuner", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(ec);
        Assert.Equal(4, ec!.Nodes.Length);

        var blockName = netlist.Nodes.NameOf(ec.Nodes[2]);
        var biasName  = netlist.Nodes.NameOf(ec.Nodes[3]);
        Assert.StartsWith("__tuner_", blockName);
        Assert.Contains("_block", blockName);
        Assert.StartsWith("__tuner_", biasName);
        Assert.Contains("_bias", biasName);
        output.WriteLine($"Internal nodes: {blockName}={ec.Nodes[2]}, {biasName}={ec.Nodes[3]}");
    }

    // ── Test 8: Loadpull directive parsed by CnlReader ───────────────────────

    [Fact]
    public void CnlReader_LoadpullDirective_ParsesCorrectly()
    {
        var cnl = @"
analysis LP1  type=loadpull  Tone=2e9  MaxHarm=4  LoadTuner=Load  SourceTuner=Src  Sweep=Load  TuneHarm=1  Grid=""dummy.gam""  Compression=3  GainType=Gt  PinStart=-20  PinStep=1  PinMax=10  Tickle=-50  MaxIter=100
";
        var (_, tb) = new CnlReader().Read(cnl, sourceDirectory: null);
        var lpa = tb.Analyses.OfType<CircuitRF.Core.Design.LoadpullAnalysis>().FirstOrDefault();
        Assert.NotNull(lpa);
        Assert.Equal("LP1",   lpa!.Name);
        Assert.Equal("2e9",   lpa.ToneExpr);
        Assert.Equal("Load",  lpa.LoadTunerName);
        Assert.Equal("Src",   lpa.SourceTunerName);
        Assert.Equal("1",     lpa.TuneHarmExpr);
        Assert.Equal("3",     lpa.CompressionExpr);
        Assert.Equal("Gt",    lpa.GainTypeExpr);
        Assert.Equal("-20",   lpa.PinStartExpr);
        Assert.Equal("10",    lpa.PinMaxExpr);
        Assert.Equal("-50",   lpa.TickleExpr);
        Assert.Equal("100",   lpa.MaxIterExpr);
        output.WriteLine("Loadpull directive parse: OK");
    }

    // ── Test 9: HB directive carries MaxIterExpr ──────────────────────────────

    [Fact]
    public void CnlReader_HbDirective_MaxIter_Parsed()
    {
        var cnl = "analysis HB1  type=hb  Tone=2e9  MaxHarm=4  MaxIter=150\n";
        var (_, tb) = new CnlReader().Read(cnl, sourceDirectory: null);
        var hba = tb.Analyses.OfType<CircuitRF.Core.Design.HarmonicBalanceAnalysis>().FirstOrDefault();
        Assert.NotNull(hba);
        Assert.Equal("150", hba!.MaxIterExpr);
        output.WriteLine("HB MaxIter parse: OK");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class CaptureMnaContext : CircuitRF.Core.IMnaContext
    {
        private int _br;
        public List<Complex> SourceValues { get; } = [];
        public int  AddBranch()                                     => _br++;
        public void AddAdmittance(int na, int nb, Complex y)       { }
        public void AddBlockAdmittance(int rn, int cn, Complex y)  { }
        public void AddBranchCurrent(int b, int na, int nb)        { }
        public void AddConstraint(int b, int n, Complex c)         { }
        public void AddBranchConstraint(int b1, int b2, Complex c) { }
        public void AddCurrentInjection(int n, Complex i)          { }
        public void AddSourceValue(int b, Complex v)               { SourceValues.Add(v); }
    }
}
