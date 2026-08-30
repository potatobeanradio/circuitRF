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
/// <b><see cref="AnalysisSettings.HbTwoToneOnLattice"/> — which solver a two-tone run takes</b>
/// (brief-hb-p1-dense-solve-and-apft-cost, M4).
///
/// <para>With the setting on — <b>the default since 2026-08-30</b> — a two-tone analysis takes the
/// same T-tone lattice path three or more tones already use. Clearing it routes the run back to the
/// rectangular-FFT path. Both solve the same retained diamond; the lattice evaluates the device on
/// roughly a quarter of the time samples to do it, which is a measured 3.5×.</para>
///
/// <para>Two things have to hold for the setting to be usable at all, and they are what this file
/// pins. The result must be the SAME SHAPE — a caller, a measurement, a cube export or the data
/// display's two-tone spectrum must not be able to tell which path ran except by reading the
/// numbers. And the numbers must agree wherever anything reads them.</para>
/// </summary>
public class HbTwoToneOnLatticeTests(ITestOutputHelper output)
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

    private static DataSet RunHero5(bool onLattice)
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(Hero5Dir(), "hero5.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var hba       = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p         = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
        Assert.Equal(2, p.ToneFreqsHz.Length);
        return new HbEngine(netlist, tb, new AnalysisSettings { HbTwoToneOnLattice = onLattice }).Run(p);
    }

    private static (int K1, int K2) ParseLabel(string s)
    {
        var t = s.Trim().Trim('(', ')').Split(',');
        return (int.Parse(t[0]), int.Parse(t[1]));
    }

    /// <summary>
    /// <b>The setting must not silently fall through to the SINGLE-TONE path.</b> This is the bug
    /// the first measurement of M4 actually caught, and it is the reason this test leads with the
    /// axis name rather than with the numbers: gating the lattice route on
    /// <c>ToneFreqsHz.Length >= 3</c> instead of <c>>= 2</c> sends a two-tone run past both
    /// multi-tone branches and into the single-tone solver, which CONVERGES CLEANLY (residual
    /// 1.2e-9) and hands back a perfectly plausible DataSet — carrying a <c>harmonic</c> axis of
    /// length 5 where the caller expects a <c>mixIndex</c> axis of length 31. Nothing throws, no
    /// warning is emitted, and every intermodulation product the analysis exists to produce is
    /// simply absent. It looked like a 28× speed-up.
    /// </summary>
    [Fact]
    public void OnTheLattice_TheResultIsStillATwoToneSpectrum_NotASingleToneOne()
    {
        var ds = RunHero5(onLattice: true);
        var v  = ds["V"];

        var mix = v.Axes.FirstOrDefault(a => a.Name == "mixIndex");
        Assert.True(mix is not null,
            $"V has axes [{string.Join(", ", v.Axes.Select(a => a.Name))}] — a two-tone run on the " +
            "lattice fell through to the single-tone path");
        Assert.DoesNotContain(v.Axes, a => a.Name == "harmonic");

        // The retained set is the order-5 diamond, M = 1 + order·(order+1) = 31.
        Assert.Equal(new MixingGrid(5).MixCount, mix!.Values.Length);
    }

    /// <summary>
    /// <b>Same shape, same index order, same labels.</b> <c>MixingLattice</c> at T = 2 reproduces
    /// <c>MixingGrid</c>'s locked enumeration element for element (<c>MixingLatticeTests</c>), so a
    /// consumer that addresses a product by index — a measurement, a stored cube, the data display
    /// — reads the same product either way. If this reddens, the setting is not a routing choice
    /// any more; it is a format change.
    /// </summary>
    [Fact]
    public void BothPaths_ProduceTheSameCubeShape_AndTheSameMixIndexLabels()
    {
        var fft = RunHero5(onLattice: false);
        var lat = RunHero5(onLattice: true);

        foreach (string cube in new[] { "V", "INl", "I" })
        {
            var a = fft[cube];
            var b = lat[cube];
            Assert.Equal(a.Rank, b.Rank);
            Assert.Equal(a.Axes.Select(x => x.Name),          b.Axes.Select(x => x.Name));
            Assert.Equal(a.Axes.Select(x => x.Values.Length),  b.Axes.Select(x => x.Values.Length));
            Assert.Equal(a.DataKind, b.DataKind);
        }

        var axA = fft["V"].Axes.First(x => x.Name == "mixIndex");
        var axB = lat["V"].Axes.First(x => x.Name == "mixIndex");
        Assert.Equal(axA.Labels!, axB.Labels!);
        Assert.Equal(axA.Values,  axB.Values);          // signed product frequencies, in Hz
        Assert.Equal(axA.Unit,    axB.Unit);
    }

    /// <summary>
    /// <b>The two paths agree wherever anything reads them, and disagree at the diamond's edge.</b>
    ///
    /// <para>They are not expected to agree bit for bit and cannot: they truncate the same infinite
    /// problem differently — the FFT grid aliases everything above the diamond back onto it by
    /// periodic wrap, the lattice least-squares projects it (harmonic-balance.md §6.5). So the
    /// disagreement is smallest at DC and grows monotonically with mixing order, being largest at
    /// the products on the outer edge, which are the ones most exposed to what was discarded. That
    /// shape is the physics, and asserting it is a stronger statement than any single tolerance:
    /// a defect in the routing would not politely arrange itself in order.</para>
    ///
    /// <para>The gate is set where <c>Hero5GateTests</c> actually looks — it ignores bins below
    /// 1e-5 and allows 1e-4 relative — so this asserts the property that keeps the committed
    /// goldens valid now that the lattice is the default: they were produced on the FFT path and
    /// are still met from the lattice. Measured at the shipping drive:
    /// DC 1e-16, carriers 2e-11, IM3 1e-7, and the order-5 edge below the residual floor entirely.
    /// </para>
    /// </summary>
    [Fact]
    public void TheTwoPathsAgree_WhereAnythingReadsThem_AndDisagreeMostAtTheDiamondEdge()
    {
        var fft = RunHero5(onLattice: false);
        var lat = RunHero5(onLattice: true);

        var a  = fft["V"];
        var b  = lat["V"];
        var ax = a.Axes.First(x => x.Name == "mixIndex");
        int M  = ax.Values.Length;

        var va = a.ComplexValues;
        var vb = b.ComplexValues;

        var worstRel = new Dictionary<int, double>();
        var peak     = new Dictionary<int, double>();
        for (int i = 0; i < va.Length; i++)
        {
            var (k1, k2) = ParseLabel(ax.Labels![i % M]);
            int order = Math.Abs(k1) + Math.Abs(k2);
            peak[order] = Math.Max(peak.GetValueOrDefault(order), va[i].Magnitude);

            // Only where the frozen path has real signal: below the Newton residual floor both
            // paths are reporting noise, and a ratio of two noises means nothing.
            if (va[i].Magnitude < 1e-9) continue;
            double rel = (va[i] - vb[i]).Magnitude / va[i].Magnitude;
            worstRel[order] = Math.Max(worstRel.GetValueOrDefault(order), rel);
        }

        foreach (int o in peak.Keys.OrderBy(x => x))
            output.WriteLine($"  order {o}: peak |V| {peak[o]:E3}   worst relative disagreement " +
                             (worstRel.ContainsKey(o) ? $"{worstRel[o]:E3}" : "— (all below the residual floor)"));

        // (a) Everywhere the golden gate looks — a bin at or above its 1e-5 noise floor — the two
        //     paths are inside its 1e-4 relative tolerance, with room to spare.
        for (int i = 0; i < va.Length; i++)
        {
            if (va[i].Magnitude < 1e-5) continue;
            double rel = (va[i] - vb[i]).Magnitude / va[i].Magnitude;
            var (k1, k2) = ParseLabel(ax.Labels![i % M]);
            Assert.True(rel < 1e-4,
                $"({k1},{k2}) |V|={va[i].Magnitude:E3} disagrees by {rel:E3} — the frozen two-tone " +
                "goldens would move if the default changed");
        }

        // (b) DC is common to both truncations and must be exact to round-off.
        Assert.True(worstRel[0] < 1e-12, $"DC disagrees by {worstRel[0]:E3}");

        // (c) The disagreement grows with order. Checked over the orders that carry real signal;
        //     an order whose every bin sits below the residual floor has nothing to compare.
        var orders = worstRel.Keys.OrderBy(x => x).ToArray();
        for (int i = 1; i < orders.Length; i++)
            Assert.True(worstRel[orders[i]] >= worstRel[orders[i - 1]],
                $"order {orders[i]} ({worstRel[orders[i]]:E3}) disagrees LESS than order " +
                $"{orders[i - 1]} ({worstRel[orders[i - 1]]:E3}) — truncation does not behave that way");
    }

    /// <summary>
    /// <b>Every cube either path emits, the other emits too.</b> Shape parity on <c>V</c>/<c>INl</c>/
    /// <c>I</c> is not enough on its own: an entire metadata cube going missing — <c>__LabeledNodes</c>,
    /// <c>__ProbeBranches</c>, <c>ToneFreqs</c>, <c>MetaMixOrder</c> — would not change any shape that
    /// is compared, and would quietly blank a node picker or a stacking axis instead. Now that the
    /// lattice path is the DEFAULT, that omission would ship.
    /// </summary>
    [Fact]
    public void BothPaths_EmitTheSameSetOfCubes_MetadataIncluded()
    {
        var fft = RunHero5(onLattice: false);
        var lat = RunHero5(onLattice: true);

        foreach (string group in fft.Groups.Union(lat.Groups))
        {
            var namesF = fft.ContainsGroup(group) ? fft.CubesIn(group).Keys.OrderBy(x => x).ToArray() : [];
            var namesL = lat.ContainsGroup(group) ? lat.CubesIn(group).Keys.OrderBy(x => x).ToArray() : [];
            output.WriteLine($"  group '{group}': FFT [{string.Join(", ", namesF)}]");
            output.WriteLine($"  group '{group}': LAT [{string.Join(", ", namesL)}]");
            Assert.Equal(namesF, namesL);
        }
    }

    /// <summary>
    /// The setting is ON by default — an owner decision taken on 2026-08-30, recorded in code
    /// rather than left to an accident of initialisation. It is worth an assertion because the
    /// value chooses which of two solvers every two-tone analysis in the product runs, and because
    /// this test is the tripwire that proves a deliberate flip of the default actually reached the
    /// built binary (an incremental build that silently skips the recompile shows up here first).
    /// </summary>
    [Fact]
    public void TheSetting_IsOnByDefault()
    {
        Assert.True(AnalysisSettings.Default.HbTwoToneOnLattice);
        Assert.True(new AnalysisSettings().HbTwoToneOnLattice);
    }

    /// <summary>
    /// And the default is what an ordinary run actually gets: a two-tone analysis built with no
    /// settings at all takes the lattice. The assertion above pins the flag's value; this pins that
    /// the flag is still wired to the dispatch.
    /// </summary>
    [Fact]
    public void ADefaultTwoToneRun_TakesTheLattice()
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(Hero5Dir(), "hero5.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var hba       = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p         = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);

        DataSet ds = new HbEngine(netlist, tb).Run(p);          // no AnalysisSettings supplied
        var onLattice = RunHero5(onLattice: true);

        // Bit-for-bit with the explicitly-on run, and NOT with the FFT one.
        Assert.Equal(onLattice["V"].ComplexValues, ds["V"].ComplexValues);
        Assert.NotEqual(RunHero5(onLattice: false)["V"].ComplexValues, ds["V"].ComplexValues);
    }
}
