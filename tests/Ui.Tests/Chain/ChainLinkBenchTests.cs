// ================================================================
//  ChainLinkBenchTests.cs — the Chain Budget's Link bench (brief PCH-11): the margins checked by a drop sweep, the signal
//  demodulated at two drops, and the bench as Transfer writes it.
//
//  The budget's side is its closed forms (a Rayleigh fade at 99 % is 10·log10(−ln 0.99) below the mean; P.618's rain at
//  0.1 % of the time); the simulated side is circuitRF's own runs of the bench, and every comparison is a binomial
//  interval over the drops, never a tolerance picked to pass.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Cli;
using CircuitRF.Design.Chain;
using CircuitRF.Design.Schematic;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Chain;

[Collection(CellStatGlobalsCollection.Name)]
public sealed class ChainLinkBenchTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-linkbench-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    private static ChainStage S(string name, string part, params (string K, string V)[] p)
    {
        var s = new ChainStage { Name = name, Part = part };
        foreach (var (k, v) in p) s.Parameters[k] = v;
        return s;
    }

    /// <summary>A 2 GHz receiver 500 m from its transmitter over free space, Rayleigh fading at the default 99 %.</summary>
    private static ChainDesign Terrestrial() => new()
    {
        FrequencyHz = 2e9, BandwidthHz = 20e6, InputPowerDbm = -60,
        Stages = [S("LNA", "Amp", ("Gain", "20"), ("NF", "2")), S("DRV", "Amp", ("Gain", "20"), ("NF", "6"))],
        Link = new ChainLink
        {
            Tx = new() { ["Psat"] = "30", ["Backoff"] = "8", ["Loss"] = "1", ["AntennaGain"] = "10", ["Elements"] = "1", ["PaGain"] = "20" },
            Path = new() { ["Distance"] = "500 m", ["RxAntennaGain"] = "3", ["Fading"] = "rayleigh" },
        },
    };

    /// <summary>A 12 GHz geostationary downlink at 99.9 %, its rain by P.618 as <paramref name="rain"/> says.</summary>
    private static ChainDesign Geostationary(string rain) => new()
    {
        FrequencyHz = 12e9, BandwidthHz = 20e6, InputPowerDbm = -60,
        Stages = [S("LNB", "Amp", ("Gain", "50"), ("NF", "1"))],
        Link = new ChainLink
        {
            Tx = new() { ["Psat"] = "40", ["Backoff"] = "3", ["Loss"] = "1", ["AntennaGain"] = "35", ["Elements"] = "1", ["PaGain"] = "30" },
            Path = new() { ["Model"] = "satellite", ["Altitude"] = "35786", ["Elevation"] = "40", ["RxAntennaGain"] = "38",
                           ["Rain"] = rain, ["Availability"] = "99.9" },
        },
    };

    private static double M(DataSet ds, string name) => ds["measurements." + name].RealValues[0];

    private DataSet Run(string cnlText, string name)
    {
        Directory.CreateDirectory(_ws);
        if (!File.Exists(Path.Combine(_ws, ".cws")))
            File.WriteAllText(Path.Combine(_ws, ".cws"), "{\"FormatVersion\": 2, \"LibraryRefs\": [], \"KnownFiles\": []}");
        string netlist = Path.Combine(_ws, name + ".cnl");
        File.WriteAllText(netlist, cnlText);
        var run = SchematicRunService.RunNetlist(netlist, _ws);
        foreach (var x in run.Warnings) output.WriteLine($"warning: {x}");
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        Assert.Empty(run.Errors);
        Assert.DoesNotContain(run.Warnings, x => x.Contains("failed to evaluate", StringComparison.Ordinal));
        return run.GroupedResults!;
    }

    private static DataCube Report(DataSet ds, string quantity)
    {
        string name = $"link:{ChainLinkBench.Channel}:{quantity}";
        return ds.CubesIn(ds.Groups.First(g => ds.CubesIn(g).ContainsKey(name)))[name];
    }

    /// <summary>
    /// Gates 1 and 3. Transfer writes the Link bench beside the others, it passes check, and it simulates: on a Rayleigh link at
    /// 99 % the drops' outage at the budget's margin (20 dB below the mean) is 1 % within its own 95 % interval; the envelope runs
    /// at the very drops the sweep ranks as its median and its outage percentile, and demodulate both; and every row of the
    /// display is a cube the run wrote, its margins budget, simulated and difference.
    /// </summary>
    [Fact]
    public void Gate1And3_TheRayleighOutage_AndTheBenchAsTransferred()
    {
        var d = Terrestrial();
        var budget = ChainBudget.Evaluate(d);
        Assert.Empty(budget.Refusals);
        Assert.Contains(ChainBench.Link, ChainBenches.BenchesFor(d));
        var w = ChainBenches.Write(d, _ws, "Ln");
        Assert.True(w.SchematicPaths.Count == ChainBenches.BenchesFor(d).Count, string.Join("\n", w.Refusals));
        string cell = ChainBenches.CellName("Ln", ChainBench.Link);
        string csch = ChainBenches.WrittenSchematic(_ws, cell)!;
        Assert.Equal(0, CliEntry.Run(["check", csch]));

        var (model, _, _) = SchematicPersistence.LoadFromFile(csch);
        string text = SchematicCircuit.CnlTextOf(model, cell);
        Assert.Contains("Stop=10000 ", text);   // 100/(1 − 0.99)
        var ds = Run(text, cell);

        double outage = M(ds, "Outage_sim"), lo = M(ds, "Outage_sim:lo"), hi = M(ds, "Outage_sim:hi");
        output.WriteLine($"outage {outage:G4} in [{lo:G4}, {hi:G4}]; margin {M(ds, "Margin_calc"):0.###} dB budget, {M(ds, "Margin_sim"):0.###} dB drops");
        Assert.Equal(0.01, M(ds, "Outage_calc"), 12);
        Assert.Equal(-10 * Math.Log10(-Math.Log(0.99)), M(ds, "Margin_calc"), 6);
        Assert.InRange(0.01, lo, hi);
        Assert.Equal(outage - 0.01, M(ds, "Outage_err"), 12);

        // The picks are the sweep's own drops: each one's received power is that drop's in the run.
        var prx = ds["measurements.Prx_sim"];
        foreach (var tag in new[] { "med", "out" })
        {
            int drop = (int)M(ds, $"Drop_{tag}");
            Assert.Equal(prx.RealValues[drop - 1], M(ds, $"Prx_{tag}"), 9);
            output.WriteLine($"{tag}: drop {drop}, {M(ds, $"Prx_{tag}"):0.##} dBm, EVM {M(ds, $"EVM_{tag}_sim"):0.###} % " +
                             $"(budget's SNR {M(ds, $"EVM_{tag}_calc"):0.###} %), BER {M(ds, $"BER_{tag}_sim"):G3} / est. {M(ds, $"BERest_{tag}_sim"):G3}");
            Assert.True(double.IsFinite(M(ds, $"EVM_{tag}_sim")) && double.IsFinite(M(ds, $"BERest_{tag}_sim")), tag);
        }
        Assert.Equal(prx.RealValues.Order().ElementAt(5000), M(ds, "Prx_med"), 9);
        Assert.Equal(prx.RealValues.Order().ElementAt(100), M(ds, "Prx_out"), 9);
        Assert.True(M(ds, "EVM_out_sim") > M(ds, "EVM_med_sim"));

        var cdd = ChainBudgetDisplay.Build("Ln", ChainBench.Link, d, budget);
        var tables = cdd.Tabs[0].Plots.Where(p => p.PlotType == PlotType.Table).ToList();
        foreach (var t in cdd.Tabs[0].Plots.SelectMany(p => p.Traces).Where(t => t.CubeName is not null))
            Assert.True(ds.Contains("measurements." + t.CubeName), t.CubeName);
        foreach (var t in tables.Take(3))
            Assert.Equal(["_calc", "_sim", "_err"], t.Traces.Take(3).Select(x => x.CubeName![x.CubeName!.LastIndexOf('_')..]));
    }

    /// <summary>
    /// Gate 2. A geostationary link at 99.9 % with Rain=drawn: the drops' rain is the budget's rain margin at the availability —
    /// the fraction of drops whose rain exceeds it is 0.1 % within its 95 % interval, over 10,000 drops (the bench's own sweep is
    /// 100,000; a shorter one tests the same statistic in a tenth of the time, only less tightly). With Rain=fixed every drop's
    /// rain term is the budget's to 1e-9 dB. The loss breakdown keeps one term axis over the drops.
    /// </summary>
    [Fact]
    public void Gate2_AGeostationaryLinksDrawnRain_IsTheBudgetsRainAtTheAvailability()
    {
        foreach (var (rain, drops) in new[] { ("drawn", 10_000), ("fixed", 200) })
        {
            var d = Geostationary(rain);
            var r = ChainBudget.Evaluate(d);
            Assert.Empty(r.Refusals);
            double budgetRain = r.Design!.Link!.Budget(d.FrequencyHz).Propagation!.Margins.RainDb;
            Assert.True(budgetRain > 1, $"rain {budgetRain} dB");
            var bench = ChainBenches.Build(d, r, "Geo").Single(b => b.Kind == ChainBench.Link);
            Assert.Contains("Stop=100000 ", bench.Netlist);
            Assert.Contains("SC;", bench.Netlist);   // a satellite link sends the single carrier
            // The drop sweep alone, shortened: the envelope runs and the rows that read them left out.
            var lines = bench.Netlist.Split('\n')
                .Where(l => !l.StartsWith("analysis ENVL", StringComparison.Ordinal) && !l.StartsWith("analysis SWD", StringComparison.Ordinal)
                            && !l.Contains("ENVL.", StringComparison.Ordinal) && !l.Contains("_med", StringComparison.Ordinal) && !l.Contains("_out", StringComparison.Ordinal))
                .Select(l => l.Replace("Stop=100000 ", $"Stop={drops} "));
            var ds = Run(string.Join('\n', lines), "Geo" + rain);
            var rains = Report(ds, "Rain_dB").RealValues;
            Assert.Equal(drops, rains.Length);
            // The loss breakdown's terms are the same at every drop: a drawn rain's name once carried its percentage, and the
            // stacked term axis grew with the drops (10,000 drops: 10,001 terms, 807 MB).
            Assert.Equal(Report(ds, "Loss_dB").Axes[^1].Length, Report(ds, "Loss_dB").RealValues.Length / drops);
            Assert.True(Report(ds, "Loss_dB").Axes[^1].Length < 10);
            if (rain == "fixed")
            {
                foreach (double x in rains) Assert.Equal(budgetRain, x, 9);
                continue;
            }
            int above = rains.Count(x => x > budgetRain);
            var (lo, hi) = RfCore.Statistics.BetaFunction.BinomialInterval(above, drops);
            output.WriteLine($"budget rain {budgetRain:0.###} dB; {above} of {drops} drops above it ({lo:G3} to {hi:G3}); " +
                             $"the drops' 99.9th percentile {M(ds, "Rain_sim"):0.###} dB");
            Assert.InRange(0.001, lo, hi);
        }
    }

    /// <summary>Two things the bench once got wrong without a word: 1 − 0.999 is 0.0009999999999998899 in binary, so 99.9 % asked
    /// for 100,001 drops (and noted its interval wider); and a P526/P1812 link's terrain profile, the chain's Profile, went onto the
    /// channel as Profile=, which there names a tapped delay line — the channel refused it and no Link bench was written.</summary>
    [Fact]
    public void Drops_AtThreeNines_AndTheTerrainProfile_GoOnTheChannelAsTerrain()
    {
        Assert.Equal(100_000, ChainLinkBench.Wanted(99.9));
        Assert.Equal(10_000, ChainLinkBench.Wanted(99));
        Directory.CreateDirectory(_ws);
        string terrain = Path.Combine(_ws, "terrain.csv");
        File.WriteAllText(terrain, "distance (km), height (m)\n0, 100\n1, 140\n2, 100\n");
        var d = Terrestrial();
        d.Link!.Path["Distance"] = "2 km";
        d.Link.Path["Model"] = "P526";
        d.Link.Path["Profile"] = terrain;
        var notes = new List<string>();
        string line = ChainLinkBench.ChannelParameters(d, d.Link.Budget(d.FrequencyHz), notes);
        output.WriteLine(line);
        Assert.Contains("Terrain=", line);
        Assert.DoesNotContain(" Profile=", " " + line);
    }
}
