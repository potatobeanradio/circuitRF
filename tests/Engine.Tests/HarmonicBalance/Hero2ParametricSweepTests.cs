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
/// Phase 5-6: Composable nested parametric sweep.
///
/// Guards:
///   (a) Single-level: Vgg outer x Pin inner → V cube gains one prepended axis [Vgg,node,harm,Pin].
///   (b) Two-level: Vgg outer x Vdd middle x Pin inner → V cube gains two prepended axes.
///   (c) CNL round-trip: "analysis SW type=parametric_sweep ..." parses correctly.
///   (d) Axis-count-agnostic slicing: positional indexer works regardless of prepended axis count.
/// </summary>
public class Hero2ParametricSweepTests(ITestOutputHelper output)
{
    // Minimal Hero 2 circuit with a 3-point Pin sweep for fast tests.
    // Topology mirrors hero2.cnl: single-ended SDD ports (negative = ground),
    // bias via V_1Tone (Vdc) + choke, RF drive at 2 GHz.
    // SDD equation uses _v1 (gate voltage) and _v2 (drain voltage) — the standard
    // SDD port-voltage names injected by the evaluator.
    // Simple JFET-like square-law model: Ids = B*(_v1+TV0)^2 * tanh(Sc*_v2) above pinch-off.
    private const string Cnl = @"
; FET model parameters
TV0 = 3.5
Sc  = 0.3
B   = 0.02

; bias
Vgg = -3.0
Vdd = 28

; drive level (swept by HB1)
Pin = -20
Vs_mag = sqrt(8 * 10^((Pin-30)/10) * 50)

; FET: single-ended ports (n_gate/0, n_drain/0); simple square-law model
SDD:M1  n_gate 0  n_drain 0  Ports=2  \
  I[1,0]=0  \
  I[2,0]=if(_v1+TV0>0, B*(_v1+TV0)^2*tanh(Sc*_v2), 0)

; gate bias-tee
V_1Tone:Vgate  n_gbias 0  Vdc=Vgg  Freq=2e9  V=Vs_mag  Phase=0
L:Lchoke_g     n_gbias n_gate  L=1  R=0

; drain bias-tee
V:Vdrain       n_dbias 0  V=Vdd
L:Lchoke_d     n_dbias n_drain  L=1  R=0

; load
R:Rload  n_drain 0  R=50

; HB inner analysis: 3-point Pin sweep for test speed
analysis HB1  type=hb  Tone=2e9  MaxHarm=3  Sweep=""Pin:-20..-18 step 1""  Tol=1e-6

; outer parametric sweep over Vgg
analysis SW1  type=parametric_sweep  Var=Vgg  Values=-3.0,-3.2  Inner=HB1
";

    // ── (a) Single-level parametric sweep ──────────────────────────────────────

    [Fact]
    public void SingleLevel_VggSweep_PrependsSweepAxis()
    {
        var (lib, tb) = new CnlReader().Read(Cnl);
        var sw1 = tb.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW1");

        var ds = ParametricSweepEngine.Run(sw1, lib, tb);

        var vCube = ds["V"];
        output.WriteLine($"V cube rank={vCube.Rank}  axes=[{string.Join(", ", vCube.Axes.Select(a => $"{a.Name}({a.Length})"))}]");

        // Axes: [Vgg(2), node(?), harmonic(4), Pin(3)]
        Assert.True(vCube.Rank >= 4, $"Expected rank ≥ 4, got {vCube.Rank}");
        Assert.Equal("Vgg", vCube.Axes[0].Name);
        Assert.Equal(2, vCube.Axes[0].Length);
        Assert.Equal(-3.0, vCube.Axes[0].Values[0], 12);
        Assert.Equal(-3.2, vCube.Axes[0].Values[1], 12);

        // Harmonic axis should be last-but-one (before Pin).
        Assert.Equal("harmonic", vCube.Axes[vCube.Rank - 2].Name);
        Assert.Equal("Pin", vCube.Axes[vCube.Rank - 1].Name);
        Assert.Equal(3, vCube.Axes[vCube.Rank - 1].Length);   // 3 Pin points

        // Branch current cubes also get the Vgg axis prepended.
        var iDrain = ds["I:M1:d"];
        Assert.Equal("Vgg", iDrain.Axes[0].Name);
        Assert.Equal(2, iDrain.Axes[0].Length);

        output.WriteLine("Single-level Vgg sweep: V cube structure correct. PASS.");
    }

    // ── (b) Two-level nested sweep ─────────────────────────────────────────────

    [Fact]
    public void TwoLevel_VggVdd_PrependsTwoAxes()
    {
        // Build tb from the same CNL but add a Vdd middle sweep wrapping HB1,
        // then a Vgg outer sweep wrapping the Vdd sweep.
        const string cnl2Level = @"
TV0 = 3.5
Sc  = 0.3
B   = 0.02

Vgg = -3.0
Vdd = 28

Pin = -20
Vs_mag = sqrt(8 * 10^((Pin-30)/10) * 50)

SDD:M1  n_gate 0  n_drain 0  Ports=2  \
  I[1,0]=0  \
  I[2,0]=if(_v1+TV0>0, B*(_v1+TV0)^2*tanh(Sc*_v2), 0)

V_1Tone:Vgate  n_gbias 0  Vdc=Vgg  Freq=2e9  V=Vs_mag  Phase=0
L:Lchoke_g     n_gbias n_gate  L=1  R=0

V:Vdrain       n_dbias 0  V=Vdd
L:Lchoke_d     n_dbias n_drain  L=1  R=0

R:Rload  n_drain 0  R=50

; innermost: single Pin point (fastest possible)
analysis HB1  type=hb  Tone=2e9  MaxHarm=3  Sweep=""Pin:-20..-20 step 1""  Tol=1e-6

; middle: 2 Vdd values  (wraps HB1)
analysis SW_Vdd  type=parametric_sweep  Var=Vdd  Values=20,28  Inner=HB1

; outer: 2 Vgg values  (wraps SW_Vdd)
analysis SW_Vgg  type=parametric_sweep  Var=Vgg  Values=-3.0,-3.2  Inner=SW_Vdd
";
        var (lib, tb) = new CnlReader().Read(cnl2Level);
        var swOuter = tb.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW_Vgg");

        var ds = ParametricSweepEngine.Run(swOuter, lib, tb);

        var vCube = ds["V"];
        output.WriteLine($"V cube rank={vCube.Rank}  axes=[{string.Join(", ", vCube.Axes.Select(a => $"{a.Name}({a.Length})"))}]");

        // Axes: [Vgg(2), Vdd(2), node(?), harmonic(4), Pin(1)]
        Assert.True(vCube.Rank >= 5, $"Expected rank ≥ 5, got {vCube.Rank}");
        Assert.Equal("Vgg", vCube.Axes[0].Name);
        Assert.Equal(2, vCube.Axes[0].Length);
        Assert.Equal("Vdd", vCube.Axes[1].Name);
        Assert.Equal(2, vCube.Axes[1].Length);

        // Axis values are correct.
        Assert.Equal(-3.0, vCube.Axes[0].Values[0], 12);
        Assert.Equal(-3.2, vCube.Axes[0].Values[1], 12);
        Assert.Equal(20.0, vCube.Axes[1].Values[0], 12);
        Assert.Equal(28.0, vCube.Axes[1].Values[1], 12);

        // Innermost axes are harmonic + Pin.
        Assert.Equal("harmonic", vCube.Axes[vCube.Rank - 2].Name);
        Assert.Equal("Pin", vCube.Axes[vCube.Rank - 1].Name);

        output.WriteLine("Two-level (Vgg×Vdd) sweep: V cube structure correct. PASS.");
    }

    // ── (c) CNL round-trip ────────────────────────────────────────────────────

    [Fact]
    public void CnlRoundTrip_ParametricSweepDirective_Parses()
    {
        var (_, tb) = new CnlReader().Read(Cnl);

        var sw1 = tb.Analyses.OfType<ParametricSweepAnalysis>().FirstOrDefault(a => a.Name == "SW1");
        Assert.NotNull(sw1);
        Assert.Equal("Vgg", sw1.SweepVarName);
        Assert.Equal(2, sw1.SweepValues.Length);
        Assert.Equal(-3.0, sw1.SweepValues[0], 12);
        Assert.Equal(-3.2, sw1.SweepValues[1], 12);
        Assert.Equal("HB1", sw1.InnerAnalysisName);

        output.WriteLine("CNL round-trip: SW1 parsed correctly. PASS.");
    }

    // ── (d) Axis-count-agnostic slicing ──────────────────────────────────────

    [Fact]
    public void SingleLevel_PositionalSlice_WorksAtEachVggPoint()
    {
        var (lib, tb) = new CnlReader().Read(Cnl);
        var sw1 = tb.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW1");

        var ds = ParametricSweepEngine.Run(sw1, lib, tb);

        var vCube = ds["V"];
        int nNodes    = vCube.Axes[1].Length;
        int nHarm     = vCube.Axes[2].Length;
        int nPin      = vCube.Axes[3].Length;

        // Slice to Vgg=0 (-3.0), all remaining axes — should be a rank-3 cube.
        var vAtVgg0 = (DataCube)vCube[0, Range.All, Range.All, Range.All];
        Assert.Equal(3, vAtVgg0.Rank);
        Assert.Equal(nNodes, vAtVgg0.Axes[0].Length);

        // Slice to Vgg=1 (-3.2), drain node, harmonic=1 (fundamental), all Pin.
        int drainIdx = vCube.Axes[1].Labels is { } lbl && Array.IndexOf(lbl, "n_drain") >= 0
            ? Array.IndexOf(lbl, "n_drain")
            : 0;
        var vDrainFund = (DataCube)vCube[1, drainIdx, 1, Range.All];
        Assert.Equal(1, vDrainFund.Rank);
        Assert.Equal(nPin, vDrainFund.Axes[0].Length);

        // Drain fundamental voltage at Vgg=-3.2 should be non-zero.
        Complex vFund0 = (Complex)vDrainFund[0];
        output.WriteLine($"V_drain,fund(Vgg=-3.2, Pin=-20 dBm) = {vFund0.Magnitude * 1e3:F2} mV");
        Assert.True(vFund0.Magnitude > 1e-6, "Expected non-zero fundamental drain voltage.");

        output.WriteLine("Axis-count-agnostic slicing: PASS.");
    }

    // ── Physical sanity: DC bias shifts with Vgg ─────────────────────────────

    [Fact]
    public void SingleLevel_DcDrainCurrent_ShiftsWithVgg()
    {
        var (lib, tb) = new CnlReader().Read(Cnl);
        var sw1 = tb.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW1");

        var ds = ParametricSweepEngine.Run(sw1, lib, tb);

        // I:M1:d axes: [Vgg, harmonic, Pin]
        var iDrain = ds["I:M1:d"];
        output.WriteLine($"I:M1:d axes: [{string.Join(", ", iDrain.Axes.Select(a => $"{a.Name}({a.Length})"))}]");

        Assert.Equal("Vgg", iDrain.Axes[0].Name);

        // DC component (harmonic index 0), first Pin point.
        Complex idc0 = (Complex)iDrain[0, 0, 0];   // Vgg=-3.0, DC, Pin[0]
        Complex idc1 = (Complex)iDrain[1, 0, 0];   // Vgg=-3.2, DC, Pin[0]

        output.WriteLine($"Idc(Vgg=-3.0) = {idc0.Real * 1e3:F2} mA");
        output.WriteLine($"Idc(Vgg=-3.2) = {idc1.Real * 1e3:F2} mA");

        // Less negative gate bias → more drain current (HEMT physics).
        Assert.True(idc0.Real > idc1.Real,
            $"Expected Idc(Vgg=-3.0)>{idc1.Real * 1e3:F2} mA > Idc(Vgg=-3.2)={idc1.Real * 1e3:F2} mA.");

        // Both should be positive and physically plausible (1–100 mA range).
        Assert.InRange(idc0.Real, 1e-3, 0.5);
        Assert.InRange(idc1.Real, 1e-3, 0.5);

        output.WriteLine("Physical sanity (Idc shifts with Vgg): PASS.");
    }
}
