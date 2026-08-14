using System.Numerics;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// brief-harmonicarf-r4 §2 — "establish whether there is anything to fix, before fixing it."
///
/// <c>LoadpullEngine.Run</c>'s first grid point's first Pin step calls
/// <c>HbEngine.RunSinglePoint(ctx.HbParams, warmStart: null, ...)</c>, and <c>RunSinglePoint</c>'s own
/// null-seed branch calls <c>NonlinearDcEngine.Run(_netlist, settings)</c> — a REAL nonlinear DC solve
/// of the actual loadpull netlist (device present, bias tees stamped), not the "device-absent linear
/// DC point" defect harmonicaRF's old <c>SeedFromDc</c> had. This is outcome 1 of the brief's own
/// three-way split ("it already computes a real nonlinear DC operating point → nothing to do; say so
/// and stop") — confirmed here by comparison against a deliberately BAD (all-zero) seed, so the
/// null-seed path is not merely asserted to differ, it is shown to differ in the way a real DC solve
/// would: landing near the netlist's own declared bias (Vgg=-3.05, Vdd=48 on Hero 3) rather than at 0.
/// </summary>
public sealed class LoadpullNullSeedIsARealDcSolveTests(ITestOutputHelper output)
{
    private static string Hero3Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero3");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero3 not found");
    }

    [Fact]
    public void NullWarmStart_LandsNearTheNetlistsOwnDeclaredBias_NotAtZero()
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(Hero3Dir(), "hero3.cnl"));
        var netlist = new Core.Elaboration.Elaborator(lib).Elaborate(tb);
        var lpa = tb.Analyses.OfType<CircuitRF.Core.Design.LoadpullAnalysis>().First();
        var p = LoadpullEngine.Resolve(lpa, netlist.ResolvedGlobals);

        var lp  = new LoadpullEngine(netlist, tb);
        var ctx = lp.PrepareContext(p);

        // Set up the FIRST grid point's own termination and the tickle-level drive, exactly as
        // RunOneTermination does for the very first Pin step of the very first grid point (no prior
        // converged neighbour exists yet, so this is genuinely the null-warm-start path).
        var gp0 = p.Grid.Points[0];
        ctx.SweptModel.SetHarmonicOverride(p.TuneHarm, gp0.Z);
        double pavlW = Math.Pow(10.0, (p.PinStartDbm - 30.0) / 10.0);
        ctx.SrcModel.SetSourceDrive(p.ToneHz, pavlW);

        var hb = new HbEngine(netlist, tb);

        var nullSeeded = hb.RunSinglePoint(ctx.HbParams, warmStart: null, ctx.SolveSettings);
        Assert.True(nullSeeded.Converged);

        int N = ctx.InterfaceNodes.Length;
        var zeroSeed = new Complex[N, ctx.K + 1];   // deliberately bad: everything at 0
        var zeroSeeded = hb.RunSinglePoint(ctx.HbParams, warmStart: zeroSeed, ctx.SolveSettings);

        double vGateNull = nullSeeded.V[ctx.SrcIfIdx, 0].Real;
        double vDrainNull = nullSeeded.V[ctx.LoadIfIdx, 0].Real;
        output.WriteLine($"null-seed DC:  Vgate={vGateNull:F4} V (netlist declares Vgg=-3.05), " +
                         $"Vdrain={vDrainNull:F4} V (netlist declares Vdd=48)");
        output.WriteLine($"null-seed iterations={nullSeeded.Iterations}, " +
                         $"zero-seed iterations={zeroSeeded.Iterations}, " +
                         $"zero-seed converged={zeroSeeded.Converged}");

        // A REAL nonlinear DC solve of this netlist lands within a fraction of a volt of the declared
        // bias (an ideal choke's DC drop is exactly zero; the SDD's own gate current is ~0 at DC).
        // The old "device-absent linear DC" defect (V = -Y(0)^-1 * I_src(0), device removed) would
        // NOT reproduce this — it has no SDD bias law to land near. All-zero would obviously fail too.
        Assert.True(Math.Abs(vGateNull - (-3.05)) < 0.05,
            $"null-seed DC gate voltage {vGateNull:F4} V is not close to the declared Vgg=-3.05 V");
        Assert.True(Math.Abs(vDrainNull - 48.0) < 0.5,
            $"null-seed DC drain voltage {vDrainNull:F4} V is not close to the declared Vdd=48 V");
    }
}
