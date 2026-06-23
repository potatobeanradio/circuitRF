// ================================================================
//  HbSpectrumMetadataTests.cs
//  Gate tests for brief-hb-spectrum-1-tone-metadata
//
//  1. SingleTone_EmitsToneFreqs
//  2. ToneFreqs_StacksPerSweepPoint  (single-tone sweep over tone freq)
//  3. TwoTone_ToneFreqs_StacksPerSweepPoint
// ================================================================

using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

public class HbSpectrumMetadataTests(ITestOutputHelper output)
{
    // Minimal single-tone FET circuit. Tone fixed at 2 GHz.
    private const string SingleToneCnl = @"
TV0 = 3.5
B   = 0.02
Sc  = 0.3
Vgg = -3.0
Vdd = 28.0

SDD:M1  n_gate 0  n_drain 0  Ports=2
  I[1,0]=0
  I[2,0]=if(_v1+TV0>0, B*(_v1+TV0)^2*tanh(Sc*_v2), 0)

V_1Tone:Vs  n_gbias 0  Vdc=Vgg  Freq=2e9  V=0.1  Phase=0
L:Lbias_g   n_gbias n_gate  L=1  R=0

V:Vdd        n_dbias 0  V=Vdd
L:Lbias_d   n_dbias n_drain  L=1  R=0

R:Rload  n_drain 0  R=50

analysis HB1  type=hb  Tone=2e9  MaxHarm=3  Tol=1e-4
";

    // Same circuit with RFfreq swept over three values so ToneFreqs must differ per point.
    private const string SingleToneFreqSweepCnl = @"
TV0 = 3.5
B   = 0.02
Sc  = 0.3
Vgg = -3.0
Vdd = 28.0

RFfreq = 1e9

SDD:M1  n_gate 0  n_drain 0  Ports=2
  I[1,0]=0
  I[2,0]=if(_v1+TV0>0, B*(_v1+TV0)^2*tanh(Sc*_v2), 0)

V_1Tone:Vs  n_gbias 0  Vdc=Vgg  Freq=RFfreq  V=0.1  Phase=0
L:Lbias_g   n_gbias n_gate  L=1  R=0

V:Vdd        n_dbias 0  V=Vdd
L:Lbias_d   n_dbias n_drain  L=1  R=0

R:Rload  n_drain 0  R=50

analysis HB1  type=hb  Tone=RFfreq  MaxHarm=3  Tol=1e-4
analysis SW_Freq  type=parametric_sweep  Var=RFfreq  Values=1e9,5.5e9,10e9  Inner=HB1
";

    // Two-tone circuit; Vdrive swept so ToneFreqs shape is [sweep(2), tone(2)].
    private const string TwoToneVdriveSweepCnl = @"
TV0 = 3.5
B   = 0.02
Sc  = 0.3
Vgg = -3.0
Vdd = 28.0

Vdrive = 0.1

SDD:M1  n_gate 0  n_drain 0  Ports=2
  I[1,0]=0
  I[2,0]=if(_v1+TV0>0, B*(_v1+TV0)^2*tanh(Sc*_v2), 0)

V_1Tone:Vgate  n_gbias 0  Vdc=Vgg  Freq=1.995e9  V=Vdrive  Phase=0
L:Lchoke_g     n_gbias n_gate  L=1  R=0

V:Vdrain       n_dbias 0  V=Vdd
L:Lchoke_d     n_dbias n_drain  L=1  R=0

R:Rload  n_drain 0  R=50

analysis HB1  type=hb  Tone[1]=1.995e9  Tone[2]=2.005e9  NumFreqs=2  MaxHarm=3  MaxMixOrder=3  Tol=1e-4
analysis SW_Vdrive  type=parametric_sweep  Var=Vdrive  Values=0.1,0.2  Inner=HB1
";

    // ── 1. SingleTone_EmitsToneFreqs ─────────────────────────────────────────────
    // A bare single-tone run must contain ToneFreqs with axis "tone" (length 1)
    // and the actual tone frequency in its data.

    [Fact]
    public void SingleTone_EmitsToneFreqs()
    {
        var (lib, tb) = new CnlReader().Read(SingleToneCnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        var ds  = (DataSet)new HbEngine(nl, tb).Run(p);

        Assert.True(ds.Contains("ToneFreqs"), "HB DataSet must contain ToneFreqs");

        var tf = ds["ToneFreqs"];
        output.WriteLine($"ToneFreqs rank={tf.Rank}  axes=[{string.Join(", ", tf.Axes.Select(a => $"{a.Name}({a.Length})"))}]");

        Assert.Equal(1, tf.Rank);
        Assert.Equal("tone", tf.Axes[0].Name);
        Assert.Equal(1, tf.Axes[0].Length);
        Assert.Equal(2e9, tf.RealValues[0]);

        output.WriteLine("PASS: SingleTone_EmitsToneFreqs");
    }

    // ── 2. ToneFreqs_StacksPerSweepPoint (single-tone) ──────────────────────────
    // After a parametric sweep over RFfreq, ToneFreqs must be rank-2 [sweep, tone]
    // with per-point values — NOT frozen at the first-point frequency.

    [Fact]
    public void ToneFreqs_StacksPerSweepPoint()
    {
        var (lib, tb) = new CnlReader().Read(SingleToneFreqSweepCnl);
        var sw = tb.Analyses.OfType<ParametricSweepAnalysis>().First();
        var ds = ParametricSweepEngine.Run(sw, lib, tb);

        Assert.True(ds.Contains("ToneFreqs"), "Stacked DataSet must contain ToneFreqs");

        var tf = ds["ToneFreqs"];
        output.WriteLine($"ToneFreqs rank={tf.Rank}  axes=[{string.Join(", ", tf.Axes.Select(a => $"{a.Name}({a.Length})={string.Join(",", tf.RealValues)}"))}]");

        Assert.Equal(2, tf.Rank);
        Assert.Equal("RFfreq", tf.Axes[0].Name);
        Assert.Equal(3,        tf.Axes[0].Length);
        Assert.Equal("tone",   tf.Axes[1].Name);
        Assert.Equal(1,        tf.Axes[1].Length);

        // Per-point tone frequencies must NOT be frozen at the first-point value.
        var vals = tf.RealValues;
        Assert.Equal(3, vals.Length);
        Assert.Equal(1e9,  vals[0]);
        Assert.Equal(5.5e9, vals[1]);
        Assert.Equal(10e9, vals[2]);

        output.WriteLine("PASS: ToneFreqs_StacksPerSweepPoint");
    }

    // ── 3. TwoTone_ToneFreqs_StacksPerSweepPoint ────────────────────────────────
    // Two-tone already emits ToneFreqs; verify it stacks to [sweep, tone(2)]
    // with each sweep point's pair of tone frequencies.

    [Fact]
    public void TwoTone_ToneFreqs_StacksPerSweepPoint()
    {
        var (lib, tb) = new CnlReader().Read(TwoToneVdriveSweepCnl);
        var sw = tb.Analyses.OfType<ParametricSweepAnalysis>().First();
        var ds = ParametricSweepEngine.Run(sw, lib, tb);

        Assert.True(ds.Contains("ToneFreqs"), "Two-tone stacked DataSet must contain ToneFreqs");

        var tf = ds["ToneFreqs"];
        output.WriteLine($"ToneFreqs rank={tf.Rank}  axes=[{string.Join(", ", tf.Axes.Select(a => $"{a.Name}({a.Length})"))}]");
        output.WriteLine($"  values=[{string.Join(", ", tf.RealValues)}]");

        Assert.Equal(2, tf.Rank);
        Assert.Equal("Vdrive", tf.Axes[0].Name);
        Assert.Equal(2,        tf.Axes[0].Length);
        Assert.Equal("tone",   tf.Axes[1].Name);
        Assert.Equal(2,        tf.Axes[1].Length);

        // Both sweep points have the same pair; tone freqs don't change with Vdrive.
        var vals = tf.RealValues;
        Assert.Equal(4, vals.Length);
        Assert.Equal(1.995e9, vals[0]);  // pt0, tone1
        Assert.Equal(2.005e9, vals[1]);  // pt0, tone2
        Assert.Equal(1.995e9, vals[2]);  // pt1, tone1
        Assert.Equal(2.005e9, vals[3]);  // pt1, tone2

        output.WriteLine("PASS: TwoTone_ToneFreqs_StacksPerSweepPoint");
    }
}
