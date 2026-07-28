using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Loadpull;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// Loadpull UI brief 07 end-to-end: the GUI Run path round-trips the TestBench through CnlWriter →
/// netlist.cnl → CnlReader before the engine runs it. This confirms the writer emits a loadpull
/// directive the reader+engine consume identically — a GUI-authored loadpull reproduces the
/// hand-authored Hero 3 numbers (the engine math is unchanged; this validates the plumbing).
/// </summary>
[Trait("Category", "Slow")]
public class LoadpullCnlWriterRunTests
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

    private static DataSet RunLoadpull(TestBench tb, Library lib)
    {
        var netlist = new Elaborator(lib).Elaborate(tb);
        var lpa     = tb.Analyses.OfType<LoadpullAnalysis>().First();
        var p       = LoadpullEngine.Resolve(lpa, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
        return new LoadpullEngine(netlist, tb).Run(p);
    }

    [Fact]
    public void Hero3_WriterRoundTrip_ReproducesDirectRun()
    {
        var dir = Hero3Dir();

        // Baseline: read hero3.cnl directly and run.
        var (lib0, tb0) = CnlReader.ReadFile(Path.Combine(dir, "hero3.cnl"));
        var direct      = RunLoadpull(tb0, lib0);

        // GUI Run path: write the TestBench back to .cnl text, re-read it (sourceDirectory = the
        // Hero3 dir, where the resolved .gam lives), and run again.
        var text        = CnlWriter.Write(tb0);
        var (lib1, tb1) = new CnlReader().Read(text, sourceDirectory: dir);
        var roundTrip   = RunLoadpull(tb1, lib1);

        // The loadpull directive survived the writer: identical sweep shape + Pout at every point.
        var d = direct["Pout"];
        var r = roundTrip["Pout"];
        Assert.Equal(d.Axes[0].Length, r.Axes[0].Length);   // grid points
        Assert.Equal(d.Axes[1].Length, r.Axes[1].Length);   // Pin steps

        int nG = d.Axes[0].Length, nP = d.Axes[1].Length;
        int comparedConverged = 0;
        for (int gi = 0; gi < nG; gi++)
        for (int pi = 0; pi < nP; pi++)
        {
            bool conv = (double)direct["Converged"][gi, pi] > 0.5;
            if (!conv) continue;
            comparedConverged++;
            Assert.Equal((double)d[gi, pi], (double)r[gi, pi], precision: 9);
        }

        Assert.True(comparedConverged > 0, "expected at least one converged grid point to compare");
    }
}
