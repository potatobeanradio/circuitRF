using System.Diagnostics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// The multi-tone analysis ceiling (harmonic-balance.md §6.5).
///
/// <para>Two properties matter, and the second is the one that is easy to lose. The refusal must
/// name a knob that BINDS and a value that works — a bare "too large" leaves the user guessing
/// which of tone count and mix order to move. And it must fire at SETUP time: the failure this
/// prevents is the one this project has already been bitten by, where an over-ambitious analysis
/// ran for twenty minutes and then threw. Both are asserted here.</para>
/// </summary>
public class HbMultiToneCeilingTests(ITestOutputHelper output)
{
    private static string Hero5Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero5");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero5 not found");
    }

    private static (HbEngine Engine, HbAnalysisParams Params) Load(string file)
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(Hero5Dir(), file));
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var hba       = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p         = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
        return (new HbEngine(netlist, tb), p);
    }

    [Fact]
    public void TooManyProducts_RefusesBeforeSolving_AndNamesTheOrderThatFits()
    {
        var (engine, p0) = Load("hero5_6tone.cnl");
        var p = p0 with { MaxMixOrder = 5 };     // 6 tones at order 5 → 1,827 products

        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<InvalidOperationException>(() => engine.Run(p));
        sw.Stop();
        output.WriteLine($"refused in {sw.Elapsed.TotalMilliseconds:F0} ms: {ex.Message}");

        Assert.Contains("1,827", ex.Message);              // what was asked for
        Assert.Contains("600",   ex.Message);              // the cap
        Assert.Contains("MaxMixOrder to 3", ex.Message);   // a value that actually works

        // Refusing at SETUP time is the whole point: building the lattice and its APFT transform
        // for 1,827 products would allocate hundreds of MB and factor a 3,654² normal matrix — tens
        // of seconds at least. Completing in a small fraction of that proves nothing was built.
        Assert.True(sw.Elapsed.TotalSeconds < 10,
            $"the ceiling took {sw.Elapsed.TotalSeconds:F1} s — it is not refusing before setup.");
    }

    [Fact]
    public void TooManyTones_Refuses_NamingTheLimit()
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(Hero5Dir(), "hero5_6tone.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var hba       = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p0        = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);

        // Seven tones — one past the supported maximum.
        var tones = p0.ToneFreqsHz.Concat([2.035e9]).ToArray();
        var p     = p0 with { ToneFreqsHz = tones, MaxMixOrder = 2 };

        var ex = Assert.Throws<InvalidOperationException>(() => new HbEngine(netlist, tb).Run(p));
        output.WriteLine(ex.Message);
        Assert.Contains("7 tones", ex.Message);
        Assert.Contains("at most 6", ex.Message);
    }

    [Fact]
    public void TheCapAdmits_WhatTheEngineAdvertises()
    {
        // The complement of the refusal tests: the cap must not quietly exclude the configurations
        // the engine claims to support. Six tones is the advertised maximum, and it must have a
        // usable mix order — a cap that refused every 6-tone analysis would satisfy the refusal
        // tests above and still be useless.
        int cap = AnalysisSettings.Default.HbMaxMixProducts;

        Assert.True(MixingLattice.CountFor(6, 3) <= cap, "6 tones has no usable mix order");
        Assert.True(MixingLattice.CountFor(4, 4) <= cap, "4 tones cannot reach order 4");
        Assert.True(MixingLattice.CountFor(3, 8) <= cap, "3 tones cannot reach order 8");

        for (int t = 1; t <= AnalysisSettings.Default.HbMaxTones; t++)
        {
            int best = 0;
            for (int o = 30; o >= 1; o--)
                if (MixingLattice.CountFor(t, o) <= cap) { best = o; break; }
            output.WriteLine($"{t} tones: largest MaxMixOrder within the {cap}-product cap = {best} " +
                             $"({MixingLattice.CountFor(t, best)} products)");
            Assert.True(best >= 3, $"{t} tones cannot reach MaxMixOrder 3 within the cap");
        }
    }
}
