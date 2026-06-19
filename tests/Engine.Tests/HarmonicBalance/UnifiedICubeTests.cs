// ================================================================
//  UnifiedICubeTests.cs
//  Gate tests for brief-unify-i-cube-engine (E1).
//
//  T1 — Dc_I_Cube_Branch           DC: I has [branch] axis; two probes labeled; __ProbeBranches present.
//  T2 — Hb_I_Cube_BranchHarmonic   HB single-tone: I has [branch, harmonic]; probe + device-port.
//  T3 — Hb_I_k0_MatchesDc          HB k=0 of probe branch == DC I value for that probe.
//  T4 — TwoTone_I_NoProbe           Two-tone HB: I has [branch, mixIndex]; no __ProbeBranches.
//  T5 — No_Legacy_I_Cubes           No I:* cubes emitted in DC, single-tone, or two-tone DataSets.
//  T6 — I_Accessor_PinsBranch       Evaluator I("Ids") pins branch; I("Ids",1) returns fundamental.
//  T7 — I_Accessor_UnknownBranch_Throws  I("nope") throws with available-branch list.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

public class UnifiedICubeTests(ITestOutputHelper output)
{
    // ── Shared circuit fixtures ───────────────────────────────────────────────

    // Simple SDD amplifier with one IProbe in the drain path.
    private const string SingleProbeCnl = @"
TV0 = 3.5
B   = 0.02
Sc  = 0.3
Vgg = -3.0
Vdd = 28.0

SDD:M1  n_gate 0  n_drain 0  Ports=2
  I[1,0]=0
  I[2,0]=if(_v1+TV0>0, B*(_v1+TV0)^2*tanh(Sc*_v2), 0)

V_1Tone:Vs  n_gbias 0  Vdc=Vgg  Freq=2e9  V=0.1  Phase=0
L:Lg        n_gbias n_gate  L=1  R=0

V:Vdd        n_dbias 0  V=Vdd
L:Ld         n_dbias n_drain  L=1  R=0

IProbe:Ids   n_drain n_rload
R:Rload      n_rload 0  R=50

analysis DC1  type=dc
analysis HB1  type=hb  Tone=2e9  MaxHarm=3  Tol=1e-4
";

    // Same amplifier but with two IProbes (drain + gate) for the DC two-probe test.
    private const string TwoProbeDcCnl = @"
TV0 = 3.5
B   = 0.02
Sc  = 0.3
Vgg = -3.0
Vdd = 28.0

SDD:M1  n_gate 0  n_drain 0  Ports=2
  I[1,0]=0
  I[2,0]=if(_v1+TV0>0, B*(_v1+TV0)^2*tanh(Sc*_v2), 0)

V:Vgg        n_gbias 0  V=Vgg
IProbe:Ig    n_gbias n_gate
R:Rg         n_gate 0  R=10000

V:Vdd        n_dbias 0  V=Vdd
L:Ld         n_dbias n_drain  L=1  R=0
IProbe:Id    n_drain n_rload
R:Rload      n_rload 0  R=50

analysis DC1  type=dc
";

    // SDD amplifier with two-tone HB and NO IProbe (device-port branches only).
    private const string TwoToneCnl = @"
TV0 = 3.5
B   = 0.02
Sc  = 0.3
Vgg = -3.0
Vdd = 28.0

SDD:M1  n_gate 0  n_drain 0  Ports=2
  I[1,0]=0
  I[2,0]=if(_v1+TV0>0, B*(_v1+TV0)^2*tanh(Sc*_v2), 0)

V_1Tone:Vf1  n_gbias 0  Vdc=Vgg  Freq=1.995e9  V=0.1  Phase=0
L:Lg         n_gbias n_gate  L=1  R=0

V:Vdd        n_dbias 0  V=Vdd
L:Ld         n_dbias n_drain  L=1  R=0
R:Rload      n_drain 0  R=50

analysis HB1  type=hb  Tone[1]=1.995e9  Tone[2]=2.005e9  NumFreqs=2  MaxHarm=3  MaxMixOrder=3  Tol=1e-4
";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DataSet RunDc(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl);
        return DcResultPacker.Pack(dc, nl);
    }

    private static (DataSet ds, int K1) RunHb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        var ds  = (DataSet)new HbEngine(nl, tb).Run(p);
        return (ds, p.MaxHarmonic + 1);
    }

    private static (DataSet ds, int M) RunHbTwoTone(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        var ds  = (DataSet)new HbEngine(nl, tb).Run(p);
        var grid = new MixingGrid(p.MaxMixOrder);
        return (ds, grid.MixCount);
    }

    // ── T1: DC: unified I cube with labeled branch axis ──────────────────────

    [Fact]
    public void Dc_I_Cube_Branch()
    {
        var ds = RunDc(TwoProbeDcCnl);

        Assert.True(ds.Contains("I"), "DC DataSet must contain unified 'I' cube");
        var iCube = ds["I"];

        Assert.Equal(1, iCube.Rank);
        Assert.Equal(DataKind.Real, iCube.DataKind);
        Assert.Equal("branch", iCube.Axes[0].Name);

        var labels = iCube.Axes[0].Labels;
        Assert.NotNull(labels);
        Assert.Equal(2, labels!.Length);
        Assert.Contains("Ig", labels);
        Assert.Contains("Id", labels);

        // __ProbeBranches must list both probe names.
        Assert.True(ds.Contains("__ProbeBranches"), "DC must emit __ProbeBranches");
        var pbCube  = ds["__ProbeBranches"];
        var pbLabels = pbCube.Axes[0].Labels!;
        Assert.Contains("Ig", pbLabels);
        Assert.Contains("Id", pbLabels);

        output.WriteLine($"T1 PASS — DC I cube branches: [{string.Join(", ", labels)}]");
    }

    // ── T2: HB single-tone: I is [branch, harmonic] ──────────────────────────

    [Fact]
    public void Hb_I_Cube_BranchHarmonic()
    {
        var (ds, K1) = RunHb(SingleProbeCnl);

        Assert.True(ds.Contains("I"), "HB DataSet must contain unified 'I' cube");
        var iCube = ds["I"];

        Assert.Equal(2, iCube.Rank);
        Assert.Equal(DataKind.Complex, iCube.DataKind);
        Assert.Equal("branch",   iCube.Axes[0].Name);
        Assert.Equal("harmonic", iCube.Axes[1].Name);
        Assert.Equal(K1,         iCube.Axes[1].Length);

        var branchLabels = iCube.Axes[0].Labels!;
        Assert.Contains("Ids", branchLabels);                     // IProbe
        Assert.True(branchLabels.Any(l => l.StartsWith("M1:")),   // device-port
            "Branch axis must include M1 device-port entries");

        // __ProbeBranches lists only the IProbe.
        Assert.True(ds.Contains("__ProbeBranches"));
        var pbLabels = ds["__ProbeBranches"].Axes[0].Labels!;
        Assert.Contains("Ids",  pbLabels);
        Assert.DoesNotContain("M1:d", pbLabels);  // device-port must NOT appear in __ProbeBranches

        output.WriteLine($"T2 PASS — I [branch({iCube.Axes[0].Length}), harmonic({K1})]; " +
                         $"branches: [{string.Join(", ", branchLabels)}]");
    }

    // ── T3: HB k=0 of probe branch matches DC I ──────────────────────────────

    [Fact]
    public void Hb_I_k0_MatchesDc()
    {
        var (hbDs, _) = RunHb(SingleProbeCnl);
        var dcDs       = RunDc(SingleProbeCnl);

        var iHb = hbDs["I"];
        var iDc = dcDs["I"];

        var hbLabels = iHb.Axes[0].Labels!;
        var dcLabels = iDc.Axes[0].Labels!;
        int hbIdsIdx = Array.FindIndex(hbLabels, l => l == "Ids");
        int dcIdsIdx = Array.FindIndex(dcLabels, l => l == "Ids");
        Assert.True(hbIdsIdx >= 0, "HB I cube must contain 'Ids' branch");
        Assert.True(dcIdsIdx >= 0, "DC I cube must contain 'Ids' branch");

        int K1 = iHb.Axes[1].Length;
        double hbDc = iHb.ComplexValues[hbIdsIdx * K1 + 0].Real;
        double dcVal = iDc.RealValues[dcIdsIdx];

        output.WriteLine($"T3: HB I_Ids[k=0]={hbDc:G6} A  DC I_Ids={dcVal:G6} A");
        Assert.Equal(dcVal, hbDc, 4);   // 4 decimal places ≈ 0.1 mA tolerance
    }

    // ── T4: Two-tone HB: I has [branch, mixIndex]; no __ProbeBranches ─────────

    [Fact]
    public void TwoTone_I_NoProbe()
    {
        var (ds, M) = RunHbTwoTone(TwoToneCnl);

        Assert.True(ds.Contains("I"), "Two-tone DataSet must contain unified 'I' cube");
        var iCube = ds["I"];

        Assert.Equal(2, iCube.Rank);
        Assert.Equal(DataKind.Complex, iCube.DataKind);
        Assert.Equal("branch",   iCube.Axes[0].Name);
        Assert.Equal("mixIndex", iCube.Axes[1].Name);
        Assert.Equal(M,          iCube.Axes[1].Length);

        var branchLabels = iCube.Axes[0].Labels!;
        Assert.True(branchLabels.Any(l => l.StartsWith("M1:")),
            "Two-tone I cube must contain M1 device-port branches");

        // No IProbe → no __ProbeBranches.
        Assert.False(ds.Contains("__ProbeBranches"),
            "Two-tone without IProbes must not emit __ProbeBranches");

        output.WriteLine($"T4 PASS — Two-tone I [branch({iCube.Axes[0].Length}), mixIndex({M})]; " +
                         $"branches: [{string.Join(", ", branchLabels)}]");
    }

    // ── T5: No I:* legacy cubes in any DataSet ───────────────────────────────

    [Fact]
    public void No_Legacy_I_Cubes()
    {
        var dcDs          = RunDc(SingleProbeCnl);
        var (hbDs, _)     = RunHb(SingleProbeCnl);
        var (ttDs, _)     = RunHbTwoTone(TwoToneCnl);

        foreach (var (label, ds) in new[] { ("DC", dcDs), ("HB", hbDs), ("TwoTone", ttDs) })
        {
            var legacyKeys = ds.Cubes.Keys
                .Where(k => k.StartsWith("I:", StringComparison.Ordinal))
                .ToList();
            Assert.Empty(legacyKeys);
            output.WriteLine($"T5 {label}: no legacy I:* cubes. PASS.");
        }
    }

    // ── T6: Evaluator I("Ids") pins branch axis ───────────────────────────────

    [Fact]
    public void I_Accessor_PinsBranch()
    {
        var (lib, tb) = new CnlReader().Read(SingleProbeCnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        var ds  = (DataSet)new HbEngine(nl, tb).Run(p);

        var results = new System.Collections.Generic.Dictionary<string, DataSet> { ["HB1"] = ds };
        var ctx  = new MeasurementContext(results);
        var eval = new Evaluator(ctx);
        var scope = new Scope("test");

        // HB1.I("Ids") → should return a 1-D cube over harmonic axis.
        var vSpectrum = eval.Eval("HB1.I(\"Ids\")", scope);
        Assert.Equal(ValueKind.Cube, vSpectrum.Kind);
        var specCube = vSpectrum.AsCube();
        Assert.Equal(1,          specCube.Rank);
        Assert.Equal("harmonic", specCube.Axes[0].Name);
        output.WriteLine($"T6a: HB1.I(\"Ids\") → rank-1 harmonic cube ({specCube.Axes[0].Length} pts). PASS.");

        // HB1.I("Ids", 1) → should return the fundamental as a complex scalar.
        var vFund = eval.Eval("HB1.I(\"Ids\", 1)", scope);
        Assert.Equal(ValueKind.Complex, vFund.Kind);
        output.WriteLine($"T6b: HB1.I(\"Ids\", 1) = {vFund.AsComplex().Real:G4}+{vFund.AsComplex().Imaginary:G4}j A. PASS.");

        // HB1.I (no args) → should return the full [branch, harmonic] cube.
        var vFull = eval.Eval("HB1.I", scope);
        Assert.Equal(ValueKind.Cube, vFull.Kind);
        var fullCube = vFull.AsCube();
        Assert.Equal(2, fullCube.Rank);
        Assert.Equal("branch",   fullCube.Axes[0].Name);
        Assert.Equal("harmonic", fullCube.Axes[1].Name);
        output.WriteLine($"T6c: HB1.I (bare) → rank-2 [{fullCube.Axes[0].Length} × {fullCube.Axes[1].Length}] cube. PASS.");
    }

    // ── T7: Evaluator I("nope") throws with branch list ──────────────────────

    [Fact]
    public void I_Accessor_UnknownBranch_Throws()
    {
        var (lib, tb) = new CnlReader().Read(SingleProbeCnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        var ds  = (DataSet)new HbEngine(nl, tb).Run(p);

        var results = new System.Collections.Generic.Dictionary<string, DataSet> { ["HB1"] = ds };
        var ctx  = new MeasurementContext(results);
        var eval = new Evaluator(ctx);
        var scope = new Scope("test");

        var ex = Assert.Throws<ExpressionException>(
            () => eval.Eval("HB1.I(\"nope\")", scope));

        Assert.Contains("nope",      ex.Message);
        Assert.Contains("Available", ex.Message);
        output.WriteLine($"T7 PASS — threw: {ex.Message}");
    }
}
