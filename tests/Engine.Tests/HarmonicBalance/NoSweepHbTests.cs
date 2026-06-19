using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// C3 gate: optional sweep axis.
///
/// When a HarmonicBalanceAnalysis carries no sweep directive, the resulting DataSet must
/// have 2-axis V/INl cubes [node, harmonic] — no Pin axis.  Branch-current cubes ("I:*")
/// must have axis [harmonic] only.  Converged and Residual must be scalars (0-rank).
///
/// Values must be physically consistent with a single HB solve at the default operating point.
/// </summary>
public class NoSweepHbTests(ITestOutputHelper output)
{
    private const string SinglePointCnl = @"
define lib hero2_nosweep
  define cell hero2_ns (
    SDD:M1   n_gate n_gate_neg n_drain n_drain_neg  Ports=2
             I[1,0]=0  I[2,0]=-B*tanh(Sc*(Vd-Id*Rd))*(1-tanh((Vg+TV0)^2)^2)

    V:Vgg    n_gate_neg 0  V=-3.05
    V:Vdd    n_vdd      0  V=48

    L:Lbias_d  n_vdd   n_drain  L=1   R=0
    L:Lbias_g  n_gate  n_gate_neg  L=1  R=0

    R:Rload  n_drain n_drain_neg  R=50
  ) end_cell
end_define

instance X1 of hero2_ns ()

hb_analysis HB1 Tone=2e9 MaxHarm=4 Tol=1e-6
";

    /// <summary>
    /// A simpler hero-style circuit without sweep: verify axis count and data integrity.
    /// </summary>
    [Fact]
    public void NoSweep_VCube_Is2D_NodeHarmonic()
    {
        var dir = Hero2Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero2.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        // Remove the sweep from the analysis params — keep everything else
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var pFull = HbEngine.Resolve(hba, netlist.ResolvedGlobals);
        // No sweep: null SweepVarName
        var p = new HbAnalysisParams(
            pFull.ToneFreqsHz, pFull.MaxHarmonic, pFull.MaxMixOrder,
            pFull.FFTOverSample, pFull.Tol, pFull.DriveStepping,
            pFull.GuardHarmonic,
            SweepVarName: null, SweepStart: 0, SweepStop: 0, SweepStep: 1,
            MaxIter: pFull.MaxIter, Lambda: pFull.Lambda);

        Assert.False(p.HasSweep, "Precondition: analysis must have no sweep");

        var result = new HbEngine(netlist, tb).Run(p);
        var ds = (DataSet)result;

        // ── V cube: rank 2, axes [node, harmonic] ────────────────────────────
        var vCube = ds["V"];
        output.WriteLine($"V cube rank={vCube.Rank}  axes=[{string.Join(", ", vCube.Axes.Select(a => a.Name))}]");
        Assert.Equal(2, vCube.Rank);
        Assert.Equal("node",     vCube.Axes[0].Name);
        Assert.Equal("harmonic", vCube.Axes[1].Name);

        // ── Unified I cube: rank 2, axes [branch, harmonic] ──────────────────
        Assert.True(ds.Contains("I"), "Expected unified 'I' cube");
        var iCube = ds["I"];
        Assert.Equal(2, iCube.Rank);
        Assert.Equal("branch",   iCube.Axes[0].Name);
        Assert.Equal("harmonic", iCube.Axes[1].Name);

        var branchLabels = iCube.Axes[0].Labels;
        Assert.NotNull(branchLabels);

        // M1 drain branch must be present (by name or port-number convention).
        bool hasDrain = branchLabels!.Any(l => l == "M1:d" || l == "M1:1");
        bool hasGate  = branchLabels!.Any(l => l == "M1:g" || l == "M1:0");
        Assert.True(hasDrain, "I cube must contain M1 drain branch (M1:d or M1:1)");
        Assert.True(hasGate,  "I cube must contain M1 gate branch (M1:g or M1:0)");

        // No legacy I:* separate cubes.
        Assert.False(ds.Cubes.Keys.Any(k => k.StartsWith("I:", StringComparison.Ordinal)),
            "No legacy I:* cubes should exist");

        // ── Converged / Residual: scalars (rank 0) ───────────────────────────
        var convCube = ds["Converged"];
        output.WriteLine($"Converged rank={convCube.Rank}");
        Assert.Equal(0, convCube.Rank);
        Assert.True(convCube.RealValues[0] > 0.5, "No-sweep solve must converge");

        // ── Values: DC drain current is positive (FET is in saturation at the bias point) ──
        int drainIdx = Array.FindIndex(branchLabels!, l => l == "M1:d" || l == "M1:1");
        int K1       = iCube.Axes[1].Length;
        Complex drainDcI = iCube.ComplexValues[drainIdx * K1 + 0];
        output.WriteLine($"I[M1:d, k=0] = {drainDcI.Real*1e3:F2} mA  (expect ~49 mA for Hero2 bias)");
        Assert.True(drainDcI.Real > 1e-3,
            $"Drain DC component should be positive (FET bias current ~49 mA), got {drainDcI.Real*1e3:F2} mA");

        output.WriteLine("No-sweep C3 gate: V 2D, unified I 2D [branch,harmonic], Converged scalar. PASS.");
    }

    private static string Hero2Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero2");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero2 not found");
    }
}
