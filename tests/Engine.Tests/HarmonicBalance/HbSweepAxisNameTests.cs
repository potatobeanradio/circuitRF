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
/// Sweep-Fix gate: the parametric-sweep axis carries the actual SweepVarName, not a
/// hardcoded sentinel.  Unit is empty string.  Swept axis is prepended first.
/// </summary>
public class HbSweepAxisNameTests(ITestOutputHelper output)
{
    // Minimal circuit: simple square-law FET driven by V_1Tone whose amplitude is
    // parameterised via Vdrive.  Three-point sweep keeps the test fast.
    // HB1 has no internal sweep; SW_Vdrive is the parametric sweep wrapper.
    private const string SingleToneCnl = @"
TV0 = 3.5
Sc  = 0.3
B   = 0.02
Vgg = -3.0
Vdd = 28

Vdrive = 0.1
Vs_mag = Vdrive

SDD:M1  n_gate 0  n_drain 0  Ports=2
  I[1,0]=0
  I[2,0]=if(_v1+TV0>0, B*(_v1+TV0)^2*tanh(Sc*_v2), 0)

V_1Tone:Vgate  n_gbias 0  Vdc=Vgg  Freq=2e9  V=Vs_mag  Phase=0
L:Lchoke_g     n_gbias n_gate  L=1  R=0

V:Vdrain       n_dbias 0  V=Vdd
L:Lchoke_d     n_dbias n_drain  L=1  R=0

R:Rload  n_drain 0  R=50

analysis HB1  type=hb  Tone=2e9  MaxHarm=3  Tol=1e-4
analysis SW_Vdrive  type=parametric_sweep  Var=Vdrive  Values=0.1,0.2,0.3  Inner=HB1
";

    // Two-tone variant with the same Vdrive sweep variable.
    private const string TwoToneCnl = @"
TV0 = 3.5
Sc  = 0.3
B   = 0.02
Vgg = -3.0
Vdd = 28

Vdrive = 0.1
Vs_mag = Vdrive

SDD:M1  n_gate 0  n_drain 0  Ports=2
  I[1,0]=0
  I[2,0]=if(_v1+TV0>0, B*(_v1+TV0)^2*tanh(Sc*_v2), 0)

V_1Tone:Vgate  n_gbias 0  Vdc=Vgg  Freq=1.995e9  V=Vs_mag  Phase=0
L:Lchoke_g     n_gbias n_gate  L=1  R=0

V:Vdrain       n_dbias 0  V=Vdd
L:Lchoke_d     n_dbias n_drain  L=1  R=0

R:Rload  n_drain 0  R=50

analysis HB1  type=hb  Tone[1]=1.995e9  Tone[2]=2.005e9  NumFreqs=2  MaxHarm=3  MaxMixOrder=3  Tol=1e-4
analysis SW_Vdrive  type=parametric_sweep  Var=Vdrive  Values=0.1,0.2  Inner=HB1
";

    // ── A1: single-tone parametric sweep names axis after sweep variable ──────

    [Fact]
    public void HbSweep_AxisNamedAfterVariable()
    {
        var (lib, tb) = new CnlReader().Read(SingleToneCnl);
        var sw = tb.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW_Vdrive");

        var ds = ParametricSweepEngine.Run(sw, lib, tb);

        // V cube: [Vdrive, node, harmonic]
        var vCube = ds["V"];
        output.WriteLine($"V axes: [{string.Join(", ", vCube.Axes.Select(a => $"{a.Name}({a.Unit})"))}]");
        Assert.Equal(3,          vCube.Rank);
        Assert.Equal("Vdrive",   vCube.Axes[0].Name);
        Assert.Equal("",         vCube.Axes[0].Unit);
        Assert.Equal("node",     vCube.Axes[1].Name);
        Assert.Equal("harmonic", vCube.Axes[2].Name);

        // Converged cube carries the same first axis.
        var convCube = ds["Converged"];
        Assert.Equal(1,        convCube.Rank);
        Assert.Equal("Vdrive", convCube.Axes[0].Name);
        Assert.Equal("",       convCube.Axes[0].Unit);

        output.WriteLine("PASS: parametric swept axis named 'Vdrive' at Axes[0], unit empty.");
    }

    // ── A2: no-sweep single-point HB produces rank-2 V and scalar Converged ──

    [Fact]
    public void HbSweep_NoSweep_NoAxis()
    {
        var (lib, tb) = new CnlReader().Read(SingleToneCnl);
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var hba       = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var pFull     = HbEngine.Resolve(hba, netlist.ResolvedGlobals);

        // Force no-sweep.
        var p = new HbAnalysisParams(
            pFull.ToneFreqsHz, pFull.MaxHarmonic, pFull.MaxMixOrder,
            pFull.FFTOverSample, pFull.Tol, pFull.DriveStepping,
            pFull.GuardHarmonic,
            SweepVarName: null, SweepStart: 0, SweepStop: 0, SweepStep: 1,
            MaxIter: pFull.MaxIter, Lambda: pFull.Lambda);

        Assert.False(p.HasSweep, "Precondition: no sweep");

        var ds = (DataSet)new HbEngine(netlist, tb).Run(p);

        // V cube: rank 2, [node, harmonic], no swept axis.
        var vCube = ds["V"];
        output.WriteLine($"V axes: [{string.Join(", ", vCube.Axes.Select(a => a.Name))}]");
        Assert.Equal(2,          vCube.Rank);
        Assert.Equal("node",     vCube.Axes[0].Name);
        Assert.Equal("harmonic", vCube.Axes[1].Name);

        // Converged is a rank-0 scalar.
        Assert.Equal(0, ds["Converged"].Rank);

        output.WriteLine("PASS: no-sweep V is rank 2, Converged is scalar.");
    }

    // ── A3: two-tone parametric sweep names axis after sweep variable ─────────

    [Fact]
    public void HbSweep_TwoTone_AxisNamedAfterVariable()
    {
        var (lib, tb) = new CnlReader().Read(TwoToneCnl);
        var sw = tb.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW_Vdrive");

        var ds = ParametricSweepEngine.Run(sw, lib, tb);

        // V cube: [Vdrive, node, mixIndex]
        var vCube = ds["V"];
        output.WriteLine($"V axes (2-tone): [{string.Join(", ", vCube.Axes.Select(a => $"{a.Name}({a.Unit})"))}]");
        Assert.Equal(3,          vCube.Rank);
        Assert.Equal("Vdrive",   vCube.Axes[0].Name);
        Assert.Equal("",         vCube.Axes[0].Unit);
        Assert.Equal("node",     vCube.Axes[1].Name);
        Assert.Equal("mixIndex", vCube.Axes[2].Name);

        var convCube = ds["Converged"];
        Assert.Equal(1,        convCube.Rank);
        Assert.Equal("Vdrive", convCube.Axes[0].Name);

        output.WriteLine("PASS: two-tone parametric swept axis named 'Vdrive' at Axes[0], unit empty.");
    }
}
