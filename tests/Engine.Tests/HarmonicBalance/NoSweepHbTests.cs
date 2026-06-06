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

        // ── Branch current cubes: rank 1, axis [harmonic] ────────────────────
        Assert.True(ds.Contains("I:M1:d"), "Expected 'I:M1:d' branch cube");
        Assert.True(ds.Contains("I:M1:g"), "Expected 'I:M1:g' branch cube");

        var iDrainCube = ds["I:M1:d"];
        Assert.Equal(1, iDrainCube.Rank);
        Assert.Equal("harmonic", iDrainCube.Axes[0].Name);

        // ── Converged / Residual: scalars (rank 0) ───────────────────────────
        var convCube = ds["Converged"];
        output.WriteLine($"Converged rank={convCube.Rank}");
        Assert.Equal(0, convCube.Rank);
        Assert.True(convCube.RealValues[0] > 0.5, "No-sweep solve must converge");

        // ── Port-number aliases ("M1:0" = gate port 0, "M1:1" = drain port 1) ──────────────
        // Generic SDD blocks that aren't FETs access current by 0-based port index.
        Assert.True(ds.Contains("I:M1:0"), "Expected port-number alias 'I:M1:0' (gate, port 0)");
        Assert.True(ds.Contains("I:M1:1"), "Expected port-number alias 'I:M1:1' (drain, port 1)");

        // Both conventions must return the same value at every harmonic.
        var iGateName = ds["I:M1:g"];
        var iGateNum  = ds["I:M1:0"];
        var iDrainNum = ds["I:M1:1"];
        int K1 = iDrainCube.Axes[0].Length;
        for (int k = 0; k < K1; k++)
        {
            Assert.Equal((Complex)iGateName[k], (Complex)iGateNum[k]);
            Assert.Equal((Complex)iDrainCube[k], (Complex)iDrainNum[k]);
        }

        // ── Values: DC drain current is positive (FET is in saturation at the bias point) ──
        Complex drainDcI = (Complex)iDrainCube[0];
        output.WriteLine($"I:M1:d[DC] = {drainDcI.Real*1e3:F2} mA  (expect ~49 mA for Hero2 bias)");
        Assert.True(drainDcI.Real > 1e-3,
            $"I:M1:d DC component should be positive (FET bias current ~49 mA), got {drainDcI.Real*1e3:F2} mA");

        output.WriteLine("No-sweep C3 gate: V 2D, I:M1:d and I:M1:1 both 1D, Converged scalar. PASS.");
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
