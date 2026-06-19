using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Gate tests for the unified HB I [branch, harmonic] cube (brief-unify-i-cube-engine).
///
/// T1 — Hb_IProbe_CurrentCube_Present        I cube exists, rank=2, branch+harmonic axes.
/// T2 — Hb_IProbe_DcComponent_MatchesDc      HB k=0 ≈ DC operating-point current.
/// T3 — Hb_IProbe_Provenance                 __ProbeBranches still lists IProbe names.
/// T4 — Hb_DevicePortCurrents_InUnifiedCube  Device-port branches are in the I cube.
/// T5 — Dc_ProbeBranches_Provenance          DC I cube + __ProbeBranches still correct.
/// </summary>
public class HbIProbeCurrentTests(ITestOutputHelper output)
{
    // Simple SDD amplifier + IProbe in the drain branch.
    // The IProbe (IP1) sits between n_drain and n_rload so both DC and HB can measure the
    // drain current.
    private const string IprobeCnl = @"
TV0 = 3.5
B   = 0.02
Sc  = 0.3
Vgg = -3.0
Vdd = 28.0

SDD:M1  n_gate 0  n_drain 0  Ports=2
  I[1,0]=0
  I[2,0]=if(_v1+TV0>0, B*(_v1+TV0)^2*tanh(Sc*_v2), 0)

V_1Tone:Vs   n_gbias 0  Vdc=Vgg  Freq=2e9  V=0.1  Phase=0
L:Lbias_g    n_gbias n_gate  L=1  R=0

V:Vdd        n_dbias 0  V=Vdd
L:Lbias_d    n_dbias n_drain  L=1  R=0

IProbe:IP1   n_drain n_rload
R:Rload      n_rload 0  R=50

analysis DC1  type=dc
analysis HB1  type=hb  Tone=2e9  MaxHarm=3  Tol=1e-4
";

    private static (DataSet ds, int K) RunHb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        var ds  = (DataSet)new HbEngine(nl, tb).Run(p);
        return (ds, p.MaxHarmonic);
    }

    private static DataSet RunDc(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl);
        return DcResultPacker.Pack(dc, nl);
    }

    // ── T1: unified I cube present, rank-2, branch + harmonic axes ────────────

    [Fact]
    public void Hb_IProbe_CurrentCube_Present()
    {
        var (ds, K) = RunHb(IprobeCnl);

        Assert.True(ds.Contains("I"),
            "HB DataSet must contain unified 'I' cube");

        var iCube = ds["I"];
        output.WriteLine($"I rank={iCube.Rank}  axes=[{string.Join(", ", iCube.Axes.Select(a => a.Name))}]");
        output.WriteLine($"  branch count={iCube.Axes[0].Length}  harmonic count={iCube.Axes[1].Length}");

        Assert.Equal(2, iCube.Rank);
        Assert.Equal("branch",   iCube.Axes[0].Name);
        Assert.Equal("harmonic", iCube.Axes[1].Name);
        Assert.Equal(K + 1, iCube.Axes[1].Length);

        var branchLabels = iCube.Axes[0].Labels;
        Assert.NotNull(branchLabels);
        Assert.Contains("IP1", branchLabels!);

        output.WriteLine($"T1 Hb_IProbe_CurrentCube_Present: PASS.");
    }

    // ── T2: k=0 component of HB I[IP1] ≈ DC operating-point probe current ─────

    [Fact]
    public void Hb_IProbe_DcComponent_MatchesDc()
    {
        var (dsHb, _) = RunHb(IprobeCnl);
        var dsDc      = RunDc(IprobeCnl);

        Assert.True(dsHb.Contains("I"), "HB must contain unified I cube");
        Assert.True(dsDc.Contains("I"), "DC must contain unified I cube");

        // HB: find IP1 branch index and read k=0.
        var iHbCube  = dsHb["I"];
        var brLabels = iHbCube.Axes[0].Labels!;
        int brIdx    = Array.IndexOf(brLabels, "IP1");
        Assert.True(brIdx >= 0, "IP1 must be a labeled branch in HB I cube");
        int K1      = iHbCube.Axes[1].Length;
        Complex iHbDc = iHbCube.ComplexValues[brIdx * K1 + 0];

        // DC: find IP1 branch and read real value.
        var iDcCube   = dsDc["I"];
        var dcLabels  = iDcCube.Axes[0].Labels!;
        int dcBrIdx   = Array.IndexOf(dcLabels, "IP1");
        Assert.True(dcBrIdx >= 0, "IP1 must be a labeled branch in DC I cube");
        double iDc = iDcCube.RealValues[dcBrIdx];

        output.WriteLine($"HB I[IP1, k=0] = {iHbDc.Real*1e3:F3} + j{iHbDc.Imaginary*1e3:F3} mA");
        output.WriteLine($"DC I[IP1]      = {iDc*1e3:F3} mA");

        double relDiff = Math.Abs(iHbDc.Real - iDc) / (Math.Abs(iDc) + 1e-9);
        output.WriteLine($"Relative difference = {relDiff:G3}");

        Assert.True(relDiff < 5e-2,
            $"HB k=0 probe current ({iHbDc.Real*1e3:F3} mA) should match DC ({iDc*1e3:F3} mA) within 5%");

        output.WriteLine("T2 Hb_IProbe_DcComponent_MatchesDc: PASS.");
    }

    // ── T3: __ProbeBranches lists probe names; no device-port keys ────────────

    [Fact]
    public void Hb_IProbe_Provenance()
    {
        var (ds, _) = RunHb(IprobeCnl);

        Assert.True(ds.Contains("__ProbeBranches"),
            "HB DataSet must contain '__ProbeBranches' provenance cube");

        var pbCube = ds["__ProbeBranches"];
        Assert.Equal(1, pbCube.Rank);
        Assert.Equal("probe", pbCube.Axes[0].Name);

        var probeNames = pbCube.Axes[0].Labels;
        Assert.NotNull(probeNames);
        output.WriteLine($"__ProbeBranches labels: [{string.Join(", ", probeNames!)}]");

        Assert.Contains("IP1", probeNames!);

        // Device-port branch keys (e.g. "M1:d", "M1:g") must NOT appear in __ProbeBranches.
        foreach (var name in probeNames!)
            Assert.False(name.Contains(':'),
                $"Device-port key '{name}' must not appear in __ProbeBranches — only IProbe names.");

        output.WriteLine("T3 Hb_IProbe_Provenance: PASS.");
    }

    // ── T4: device-port branches appear in the unified I cube (not as I:* cubes) ─

    [Fact]
    public void Hb_DevicePortCurrents_InUnifiedCube()
    {
        var (ds, _) = RunHb(IprobeCnl);

        Assert.True(ds.Contains("I"), "Unified I cube must be present");

        var iCube = ds["I"];
        var branchLabels = iCube.Axes[0].Labels;
        Assert.NotNull(branchLabels);

        output.WriteLine($"Branch labels in I cube: [{string.Join(", ", branchLabels!)}]");

        // SDD M1 emits drain and gate port currents — one of the standard key forms.
        bool hasDrain = branchLabels!.Any(l => l == "M1:d" || l == "M1:1");
        bool hasGate  = branchLabels!.Any(l => l == "M1:g" || l == "M1:0");

        Assert.True(hasDrain, "I cube must contain a drain branch label for M1 (M1:d or M1:1)");
        Assert.True(hasGate,  "I cube must contain a gate branch label for M1 (M1:g or M1:0)");

        // Legacy I:* cubes must NOT exist.
        Assert.False(ds.Cubes.Keys.Any(k => k.StartsWith("I:", StringComparison.Ordinal)),
            "No legacy I:* separate cubes should exist — all branches are in the unified I cube");

        output.WriteLine("T4 Hb_DevicePortCurrents_InUnifiedCube: PASS.");
    }

    // ── T5: DcResultPacker emits unified I [branch] + __ProbeBranches ─────────

    [Fact]
    public void Dc_ProbeBranches_Provenance()
    {
        var dsDc = RunDc(IprobeCnl);

        Assert.True(dsDc.Contains("I"), "DC must emit unified I cube");
        var iCube = dsDc["I"];
        Assert.Equal(1, iCube.Rank);
        Assert.Equal("branch", iCube.Axes[0].Name);
        Assert.NotNull(iCube.Axes[0].Labels);
        Assert.Contains("IP1", iCube.Axes[0].Labels!);

        Assert.True(dsDc.Contains("__ProbeBranches"),
            "DcResultPacker must emit '__ProbeBranches' provenance cube");

        var pbCube = dsDc["__ProbeBranches"];
        Assert.Equal(1, pbCube.Rank);
        Assert.Equal("probe", pbCube.Axes[0].Name);

        var probeNames = pbCube.Axes[0].Labels;
        Assert.NotNull(probeNames);
        Assert.Contains("IP1", probeNames!);

        output.WriteLine($"DC __ProbeBranches labels: [{string.Join(", ", probeNames!)}]");
        output.WriteLine("T5 Dc_ProbeBranches_Provenance: PASS.");
    }
}
