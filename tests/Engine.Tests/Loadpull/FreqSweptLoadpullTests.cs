using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.Loadpull;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Loadpull;

// Layer A+B of FreqSweptLoadpull_Brief: a Loadpull wrapped in a parametric sweep over the tone
// variable (RFfreq) runs per frequency and stacks with a "freq" (Hz) axis that LoadpullSurface
// recognizes — so the result is a genuine multi-frequency loadpull.
public sealed class FreqSweptLoadpullTests(ITestOutputHelper o)
{
    private static string Hero3Dir()
    {
        var d = AppContext.BaseDirectory;
        while (d is not null)
        {
            var c = Path.Combine(d, "testdata", "Hero3");
            if (Directory.Exists(c)) return c;
            d = Path.GetDirectoryName(d);
        }
        throw new DirectoryNotFoundException("testdata/Hero3 not found");
    }

    private const string Eq =
        @"I[1,0]=_v1/50  I[2,0]=(B*TC*tanh(_v2*a*(tanh(g*(TV0 - _v1 + _v2*th + Sc*ln(exp(-(Sv - _v1)/Sc) + 1)))+1))*ln(exp(-(2*TV0 - 2*_v1 +2*_v2*th + 2*Sc*ln(exp(-(Sv - _v1)/Sc) + 1))/TC) + 1) * (_v2*lam + 1))/2";

    private static string Netlist(string gridPath) => $@"
Sv=-0.837
Sc=0.71
TV0=4.268
TC=1.507
th=0.001
a=0.176
g=0.089
lam=0.0012
B=1130
Vgs=-3.05
VDD=48
RFfreq = 2 GHz
SDD:M1 n1 0 n2 0 {Eq}
Tuner:Src n1 0 Z[1]=25 Zdefault=1e-6 BiasTee=on Vbias=Vgs
Tuner:Load n2 0 Z[1]=80+j*10 Z[2]=1 Zdefault=1e-6 BiasTee=on Vbias=VDD
analysis LP1 type=loadpull Tone=""RFfreq"" ToneUnit=GHz MaxHarm=5 LoadTuner=Load SourceTuner=Src Grid=""{gridPath}"" Sweep=Load TuneHarm=1 Compression=3 GainType=Gt PinStart=-20 PinStep=1 PinMax=10 Tickle=-50 MaxIter=100 FFTOverSample=1 Tol=1e-6 DriveStepping=IfNecessary GuardHarmonic=0";

    [Fact]
    public void FreqSweptLoadpull_StacksFreqAxisInHz_RecognizedBySurface()
    {
        var grid = Path.Combine(Hero3Dir(), "hero3_load.gam");
        var (lib, tb) = new CnlReader().Read(Netlist(grid), sourceDirectory: Hero3Dir());

        // Wrap LP1 in a parametric sweep over RFfreq (base-SI Hz values).
        double[] freqsHz = { 1.8e9, 2.0e9, 2.2e9 };
        var sw = new ParametricSweepAnalysis("SW1", "RFfreq", freqsHz, "LP1");

        var ds = ParametricSweepEngine.Run(sw, lib, tb, baseDirectory: Hero3Dir());

        // The leading axis is the tone frequency, named "freq" (Hz), with the resolved per-point tones.
        var pout = ds["Pout_dBm"];
        var freqAxis = pout.Axes.FirstOrDefault(ax => ax.Name == "freq");
        Assert.NotNull(freqAxis);
        Assert.Equal("Hz", freqAxis!.Unit);
        Assert.Equal(freqsHz.Length, freqAxis.Length);
        for (int i = 0; i < freqsHz.Length; i++)
            Assert.Equal(freqsHz[i], freqAxis.Values[i], precision: 0);

        // The result is a genuine multi-frequency loadpull as far as the surface is concerned.
        var surf = new LoadpullSurface(ds);
        Assert.Equal(freqsHz.Length, surf.Frequencies.Count);
        for (int i = 0; i < freqsHz.Length; i++)
            Assert.Equal(freqsHz[i], surf.Frequencies[i], precision: 0);

        o.WriteLine($"freq axis = [{string.Join(", ", freqAxis.Values.Select(v => $"{v/1e9:F2} GHz"))}]");
    }

    private static string Hero3BDir()
    {
        var d = AppContext.BaseDirectory;
        while (d is not null)
        {
            var c = Path.Combine(d, "testdata", "Hero3B");
            if (Directory.Exists(c)) return c;
            d = Path.GetDirectoryName(d);
        }
        throw new DirectoryNotFoundException("testdata/Hero3B not found");
    }

    // Layers A/E: a freq-swept Loadpull-Pursuit runs the full MXP/MXE search per frequency and stacks the
    // optima into per-frequency trends on a clean "freq" (Hz) axis. The follow-on loadpull cubes stack too
    // (when per-freq grids match), and the follow-on metadata stays cleanly __-prefixed (no LP___ mangling).
    [Fact]
    public void FreqSweptPursuit_StacksOptimaTrends_OnFreqAxis()
    {
        var dir     = Hero3BDir();
        var gamOut  = Path.Combine(Path.GetTempPath(), $"swept_pursuit_{Guid.NewGuid():N}.gam");
        var text = File.ReadAllText(Path.Combine(dir, "hero3B_at_compression.cnl"))
            .Replace("loadpull_pursuit_output.gam", gamOut.Replace("\\", "/"));   // freq-tagged multi-block output
        var (lib, tb) = new CnlReader().Read(text, sourceDirectory: dir);
        var lppa = tb.Analyses.OfType<LoadpullPursuitAnalysis>().Single();

        double[] freqsHz = { 1.8e9, 2.2e9 };
        var sw = new ParametricSweepAnalysis("SW1", "RFfreq", freqsHz, lppa.Name);

        var ds = ParametricSweepEngine.Run(sw, lib, tb, baseDirectory: dir);

        // Layer D: the OutputGrid .gam accumulated one freq-tagged block per swept frequency.
        var blocks = GamReader.ReadBlocks(gamOut);
        File.Delete(gamOut);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(1.8e9, blocks[0].FreqHz!.Value, 0);
        Assert.Equal(2.2e9, blocks[1].FreqHz!.Value, 0);

        // MXP/MXE optima are now per-frequency trends on a "freq" (Hz) axis.
        foreach (var metric in new[] { "MXP_PoutDbm", "MXE_Eff", "MXP_ZRe", "MXE_ZsourceRe", "RecommTermCount" })
        {
            var cube = ds[metric];
            var fax  = cube.Axes.Single(a => a.Name == "freq");
            Assert.Equal("Hz", fax.Unit);
            Assert.Equal(freqsHz, fax.Values);
        }

        // Both per-freq pursuits converged and produced a recommended-termination grid.
        Assert.All(ds["MXP_Converged"].RealValues, v => Assert.Equal(1.0, v));
        Assert.All(ds["MXE_Converged"].RealValues, v => Assert.Equal(1.0, v));

        // Follow-on loadpull stacked over freq under original names (recognizable loadpull surface),
        // enriched (canonical FOM names), metadata cleanly __-prefixed.
        Assert.True(ds.Contains("Pout_dBm"));          // follow-on FOM (original name, no LP_ prefix)
        Assert.Equal("freq", ds["Pout_dBm"].Axes[0].Name);
        Assert.True(ds.Contains("GammaLoad"));         // loadpull termination cube
        Assert.True(ds.Contains("__SrcNodeIdx"));      // provenance kept __-prefixed
        Assert.True(ds.Contains("__LoadNodeIdx"));

        o.WriteLine($"MXP Pout(dBm) vs freq = [{string.Join(", ", ds["MXP_PoutDbm"].RealValues.Select(v => v.ToString("F2")))}]");
        o.WriteLine($"MXE Eff(%) vs freq    = [{string.Join(", ", ds["MXE_Eff"].RealValues.Select(v => (v * 100).ToString("F1")))}]");
    }

    // A NESTED sweep (outer RFfreq × inner dummy var) over a pursuit must accumulate one .gam block per
    // outer point — the OutputGrid is truncated ONCE across the whole run, not re-truncated per outer point
    // (which would leave only the last outer point's blocks). Two outer freqs → two blocks.
    [Fact]
    public void NestedSweptPursuit_GamAccumulatesAllOuterPoints()
    {
        var dir    = Hero3BDir();
        var gamOut = Path.Combine(Path.GetTempPath(), $"nested_pursuit_{Guid.NewGuid():N}.gam");
        var text = File.ReadAllText(Path.Combine(dir, "hero3B_at_compression.cnl"))
            .Replace("loadpull_pursuit_output.gam", gamOut.Replace("\\", "/"));
        var (lib, tb) = new CnlReader().Read(text, sourceDirectory: dir);
        var lppa = tb.Analyses.OfType<LoadpullPursuitAnalysis>().Single();

        // outer SWo(RFfreq, 2 pts) → inner SWi(Xdummy, 1 pt) → pursuit. Two nesting levels.
        var inner = new ParametricSweepAnalysis("SWi", "Xdummy", new[] { 0.0 }, lppa.Name);
        var outer = new ParametricSweepAnalysis("SWo", "RFfreq", new[] { 1.8e9, 2.2e9 }, "SWi");
        tb.Analyses.Add(inner);
        tb.Analyses.Add(outer);

        ParametricSweepEngine.Run(outer, lib, tb, baseDirectory: dir);

        var blocks = GamReader.ReadBlocks(gamOut);
        File.Delete(gamOut);
        Assert.Equal(2, blocks.Count);                       // both outer points survived (not just the last)
        Assert.Equal(1.8e9, blocks[0].FreqHz!.Value, 0);
        Assert.Equal(2.2e9, blocks[1].FreqHz!.Value, 0);
    }

    // Coarse-Pin loadpull (few HB solves) for fast integration tests.
    private static string NetlistCoarse(string gridPath) => $@"
Sv=-0.837
Sc=0.71
TV0=4.268
TC=1.507
th=0.001
a=0.176
g=0.089
lam=0.0012
B=1130
Vgs=-3.05
VDD=48
RFfreq = 2 GHz
SDD:M1 n1 0 n2 0 {Eq}
Tuner:Src n1 0 Z[1]=25 Zdefault=1e-6 BiasTee=on Vbias=Vgs
Tuner:Load n2 0 Z[1]=80+j*10 Z[2]=1 Zdefault=1e-6 BiasTee=on Vbias=VDD
analysis LP1 type=loadpull Tone=""RFfreq"" ToneUnit=GHz MaxHarm=5 LoadTuner=Load SourceTuner=Src Grid=""{gridPath}"" Sweep=Load TuneHarm=1 Compression=3 GainType=Gt PinStart=-10 PinStep=5 PinMax=0 Tickle=-50 MaxIter=100 FFTOverSample=1 Tol=1e-6 DriveStepping=IfNecessary GuardHarmonic=0";

    // Regression: a freq-tagged .gam from a swept pursuit can have a DIFFERENT point count per frequency
    // (a reactive output cap makes more terminations unscorable at some freqs). A freq-swept loadpull
    // reading it produces ragged per-freq grids; the engine pads them to a common shape with NaN so they
    // stack (LoadpullSurface drops the NaN-padded points). Previously: "Cube [1] axis 0 length N != M".
    [Fact]
    public void FreqSweptLoadpull_RaggedPerFreqGrid_PadsAndStacks()
    {
        var gam = Path.Combine(Path.GetTempPath(), $"ragged_{Guid.NewGuid():N}.gam");
        // 1.8 GHz block: 3 terminations; 2.2 GHz block: 4 terminations (ragged).
        File.WriteAllText(gam,
            "# impedance Z0=50 re+j*imag\n" +
            "freq=1.8e9Hz\n80+j*10\n60-j*5\n40+j*0\n" +
            "freq=2.2e9Hz\n85+j*5\n70+j*0\n50+j*0\n30+j*0\n");
        try
        {
            var (lib, tb) = new CnlReader().Read(NetlistCoarse(gam));
            var sw = new ParametricSweepAnalysis("SW1", "RFfreq", new[] { 1.8e9, 2.2e9 }, "LP1");

            // Must not throw (the ragged-grid PrependAxis mismatch).
            var ds = ParametricSweepEngine.Run(sw, lib, tb);

            // Stacked to the across-freq max gridPoint count (4); freq axis = the two tones.
            var gl = ds["GammaLoad"];
            Assert.Equal("freq",      gl.Axes[0].Name);
            Assert.Equal("gridPoint", gl.Axes[1].Name);
            Assert.Equal(4, gl.Axes[1].Length);

            var surf = new LoadpullSurface(ds);
            Assert.Equal(2, surf.Frequencies.Count);
        }
        finally { File.Delete(gam); }
    }
}
