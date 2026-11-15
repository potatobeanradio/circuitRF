// ================================================================
//  ChainPropagationTests.cs — the Chain Budget's Propagation section (brief PCH-3): every existing chain unchanged,
//  the margins at an availability, the required SNR, a satellite downlink, and the format version.
//
//  Expected numbers come from closed forms written here, or from testdata/chain/link_reference.py (standard-library
//  Python, its own arithmetic), never from the code under test.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Cli;
using CircuitRF.Design.Chain;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Chain;

[Collection(CellStatGlobalsCollection.Name)]
public sealed class ChainPropagationTests(ITestOutputHelper output)
{
    private static string Repo => CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot();
    private static string Golden => Path.Combine(Repo, "testdata", "chain", "unchanged_budgets.json");

    /// <summary>Every chain there is: the shipped examples and the link reference's design.</summary>
    private static IEnumerable<(string Name, ChainDesign Design)> Chains()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(Repo, "examples", "RF Chain Budget"), "*.cchain").Order(StringComparer.Ordinal))
            yield return (Path.GetFileNameWithoutExtension(f), ChainDesignIo.LoadFromFile(f));
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(Repo, "testdata", "chain", "link_reference.json")))!;
        yield return ("link_reference", ChainDesignIo.Deserialize(root["design"]!.ToJsonString()));
    }

    /// <summary>A budget's every cube, sweeps included, as exact text: group, name, axes, values.</summary>
    private static JsonObject Cubes(ChainDesign d)
    {
        var ds = ChainBudget.ToDataSet(ChainBudget.Evaluate(d));
        ChainBudget.AddSweeps(ds, d);
        var o = new JsonObject();
        foreach (var g in ds.Groups)
            foreach (var (name, cube) in ds.CubesIn(g))
                o[$"{g}/{name}"] = new JsonObject
                {
                    ["unit"] = cube.Unit,
                    ["axes"] = new JsonArray([.. cube.Axes.Select(a => (JsonNode)$"{a.Name}:{string.Join(",", a.Values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))}:{string.Join(",", a.Labels ?? [])}")]),
                    ["values"] = new JsonArray([.. cube.RealValues.Select(v => (JsonNode)v.ToString("R", CultureInfo.InvariantCulture))]),
                };
        return o;
    }

    /// <summary>
    /// Gate 1: every chain budgets as it did before the Propagation section. Every cube the budget had then is here
    /// again, bit for bit (testdata/chain/unchanged_budgets.json, written by the code before PCH-3); the section only
    /// adds cubes. Set CRF_WRITE_CHAIN_GOLDEN=1 to write the file instead.
    /// </summary>
    [Fact]
    public void EveryExistingChain_BudgetsBitForBitAsBefore()
    {
        if (Environment.GetEnvironmentVariable("CRF_WRITE_CHAIN_GOLDEN") == "1")
        {
            var all = new JsonObject();
            foreach (var (name, d) in Chains()) all[name] = Cubes(d);
            File.WriteAllText(Golden, all.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            return;
        }
        var golden = JsonNode.Parse(File.ReadAllText(Golden))!.AsObject();
        int n = 0;
        foreach (var (name, d) in Chains())
        {
            var now = Cubes(d);
            foreach (var (key, was) in golden[name]!.AsObject())
            {
                Assert.True(now.ContainsKey(key), $"{name}: {key} is gone");
                Assert.Equal(was!.ToJsonString(), now[key]!.ToJsonString());
                n++;
            }
        }
        output.WriteLine($"{n} cubes unchanged");
        Assert.True(n > 100);
    }

    // ── margins, required SNR, a satellite path ──────────────────────────────

    private static JsonNode Reference => JsonNode.Parse(File.ReadAllText(Path.Combine(Repo, "testdata", "chain", "link_reference.json")))!;

    private static ChainLink Link(params (string K, string V)[] path)
    {
        var l = new ChainLink();
        foreach (var (k, v) in path) l.Path[k] = v;
        return l;
    }

    private static ChainMargins Margins(params (string K, string V)[] path) => ChainPropagation.Evaluate(Link(path), 2.4e9).Margins;

    /// <summary>
    /// Gate 2. Rayleigh's margin is −10·log10(−ln p): 19.978 dB at 99 %, which is the design note's 20.0 dB to the one
    /// decimal it writes (it is 0.022 dB from 20.00, so "to 0.01 dB" holds against the formula, not the rounded 20.0),
    /// and 30.00 dB at 99.9 %; lognormal σ = 8 dB at 90 % is 10.25 dB; and the shadowing, fading and composite margins of
    /// six cases agree with link_reference.py's own integration to 1e-6 dB.
    /// </summary>
    [Fact]
    public void TheMargins_AreTheClosedFormsAndTheIndependentIntegration()
    {
        var r99 = Margins(("Fading", "rayleigh"), ("Availability", "99"));
        Assert.Equal(-10 * Math.Log10(-Math.Log(0.99)), r99.FadeDb, 12);
        Assert.Equal(20.0, Math.Round(r99.FadeDb, 1));
        Assert.Equal(30.0, Margins(("Fading", "rayleigh"), ("Availability", "99.9")).FadeDb, 0.01);
        Assert.Equal(10.25, Margins(("Sigma", "8"), ("Availability", "90")).ShadowDb, 0.01);

        int n = 0;
        foreach (var c in Reference["margins"]!.AsArray())
        {
            var path = new List<(string, string)> { ("Fading", (string)c!["Fading"]!), ("Sigma", F(c["Sigma"])), ("Availability", F(c["Availability"])) };
            if (c["KFactor"] is { } k) path.Add(("KFactor", F(k)));
            if (c["m"] is { } m) path.Add(("m", F(m)));
            var got = Margins([.. path]);
            var want = c["expected"]!;
            Assert.Equal((double)want["Mshadow_dB"]!, got.ShadowDb, 1e-9);
            Assert.True(Math.Abs((double)want["Mfade_dB"]! - got.FadeDb) < 1e-6, $"{c["Fading"]}: M_fade {got.FadeDb:R}, the reference {want["Mfade_dB"]}");
            Assert.True(Math.Abs((double)want["Mcomp_dB"]! - got.CompositeDb) < 1e-6, $"{c["Fading"]}: M_comp {got.CompositeDb:R}, the reference {want["Mcomp_dB"]}");
            n++;
        }
        Assert.Equal(6, n);
    }

    private static string F(JsonNode? v) => ((double)v!).ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Gate 3: the required Eb/N0 and SNR at a target BER, QPSK at 10⁻⁶ among them, are the closed forms
    /// link_reference.py inverts, to 1e-9 dB; and the target sets the budget's required SNR, so its sensitivity.</summary>
    [Fact]
    public void TheRequiredSnr_IsTheClosedForm_AndSetsTheSensitivity()
    {
        foreach (var c in Reference["required_snr"]!.AsArray())
        {
            var q = ChainPropagation.RequiredSnr(Link(("TargetBer", F(c!["TargetBer"])), ("Modulation", (string)c["Modulation"]!)))!;
            Assert.Equal((double)c["expected"]!["EbN0req_dB"]!, q.EbN0Db, 1e-9);
            Assert.Equal((double)c["expected"]!["SNRreq_dB"]!, q.SnrDb, 1e-9);
        }
        var d = ChainDesignIo.Deserialize(Reference["design"]!.ToJsonString());
        d.Link!.Path["TargetBer"] = "1e-6";
        d.Link.Path["Modulation"] = "qpsk";
        var r = ChainBudget.Evaluate(d);
        Assert.Equal(13.540131656211258, r.Design!.RequiredSnrDb, 1e-9);
        Assert.Equal(r.Totals!.MdsDbm + r.Design.RequiredSnrDb, r.Totals.SensitivityDbm, 12);
    }

    /// <summary>Gate 4: a geostationary downlink at 20 GHz and 99.9 % — slant range, P.676 gas, P.618 rain, clouds and
    /// scintillation, Rayleigh fading, QPSK at 10⁻⁶ — gives link_reference.py's path loss, received power, margin,
    /// latency and link margin to 1e-6 dB.</summary>
    [Fact]
    public void AGeostationaryDownlink_IsTheIndependentReference()
    {
        var sat = Reference["satellite"]!;
        var d = ChainDesignIo.Deserialize(Reference["design"]!.ToJsonString());
        d.FrequencyHz = (double)sat["FrequencyHz"]!;
        d.Link!.Path = sat["Path"]!.AsObject().ToDictionary(kv => kv.Key, kv => (string)kv.Value!, StringComparer.Ordinal);
        var r = ChainBudget.Evaluate(d);
        Assert.Empty(r.Refusals);
        var ds = ChainBudget.ToDataSet(r);
        foreach (var (key, v) in sat["expected"]!.AsObject())
            Assert.True(Math.Abs(ds[key].RealValues[0] - (double)v!) < 1e-6, $"{key} = {ds[key].RealValues[0]:R}, the reference {v}");
        Assert.True(ds["Doppler_Hz"].RealValues[0] == 0, "a geostationary satellite does not move");
    }

    /// <summary>Gate 5: a version-1 chain saves as the bytes it was read from; a chain naming 38901 writes version 3,
    /// which a reader of version 2 refuses; Model=free alone stays at its version.</summary>
    [Fact]
    public void TheFormatVersion_RisesOnlyForTheNewFields()
    {
        string v1 = File.ReadAllText(Path.Combine(Repo, "examples", "RF Chain Budget", "Superhet.cchain"));
        Assert.Contains("\"FormatVersion\": 1", v1);
        Assert.Equal(v1.TrimEnd(), ChainDesignIo.Serialize(ChainDesignIo.Deserialize(v1)).TrimEnd());

        var d = ChainDesignIo.Deserialize(Reference["design"]!.ToJsonString());
        d.Link!.Path["Model"] = "free";
        Assert.Contains("\"FormatVersion\": 2", ChainDesignIo.Serialize(d));
        d.Link.Path["Model"] = "38901";
        string v3 = ChainDesignIo.Serialize(d);
        Assert.Contains("\"FormatVersion\": 3", v3);
        Assert.Equal("38901", ChainDesignIo.Deserialize(v3).Link!.Path["Model"]);
        Assert.Throws<InvalidDataException>(() => ChainDesignIo.Deserialize(v3.Replace("\"FormatVersion\": 3", "\"FormatVersion\": 4")));
    }

    /// <summary>A terrestrial link under 38901 UMi: margin against distance is one sweep, each point the budget at that
    /// distance, and at the reported zero-margin distance the link margin is 0.</summary>
    [Fact]
    public void MarginAgainstDistance_IsOneSweep_AndCrossesZeroWhereTheReadoutSays()
    {
        var d = ChainDesignIo.Deserialize(Reference["design"]!.ToJsonString());
        foreach (var (k, v) in new[] { ("Model", "38901"), ("Scenario", "umi"), ("Los", "los"), ("TxHeight", "10"), ("Fading", "rician"),
                                       ("Sigma", "4"), ("TargetBer", "1e-6"), ("Modulation", "qpsk") })
            d.Link!.Path[k] = v;
        d.Sweeps.Add(new ChainSweep(ChainSweep.Distance, 50, 2000, 5));
        var r = ChainBudget.Evaluate(d);
        Assert.Empty(r.Refusals);
        var lm = ChainPropagation.LinkMargin(r)!;
        Assert.True(double.IsFinite(lm.ZeroMarginDistanceM), lm.DistanceNote);

        var sweep = ChainBudget.Sweep(d, d.Sweeps[0]);
        var axis = sweep["LinkMargin_dB"].Axes[0];
        Assert.Equal("distance", axis.Name);
        for (int i = 0; i < axis.Values.Length; i++)
        {
            var at = d.Clone();
            at.Sweeps.Clear();
            at.Link!.Path["Distance"] = axis.Values[i].ToString("R", CultureInfo.InvariantCulture);
            Assert.Equal(ChainBudget.ToDataSet(ChainBudget.Evaluate(at))["LinkMargin_dB"].RealValues[0], sweep["LinkMargin_dB"].RealValues[i], 12);
        }
        var zero = d.Clone();
        zero.Sweeps.Clear();
        zero.Link!.Path["Distance"] = lm.ZeroMarginDistanceM.ToString("R", CultureInfo.InvariantCulture);
        Assert.True(Math.Abs(ChainPropagation.LinkMargin(ChainBudget.Evaluate(zero))!.LinkMarginDb) < 1e-6);
    }

    /// <summary>The verb prints the breakdown and the margins, and <c>-o budget.npy</c> carries them.</summary>
    [Fact]
    public void TheVerb_PrintsAndWritesTheBreakdownAndMargins()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-pch3-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var d = ChainDesignIo.Deserialize(Reference["design"]!.ToJsonString());
            d.Link!.Path["Model"] = "38901";
            d.Link.Path["Scenario"] = "umi";
            d.Link.Path["Rain"] = "fixed";
            d.Link.Path["Fading"] = "rayleigh";
            string path = Path.Combine(dir, "L.cchain"), npy = Path.Combine(dir, "budget.npy");
            ChainDesignIo.SaveToFile(path, d);
            var was = Console.Out;
            var text = new StringWriter();
            Console.SetOut(text);
            int code;
            try { code = CliEntry.Run(["chain", path, "-o", npy]); }
            finally { Console.SetOut(was); }
            Assert.Equal(0, code);
            Assert.Contains("propagation: 38901", text.ToString());
            Assert.Contains("margins at 99 %", text.ToString());
            Assert.Contains("link margin", text.ToString());
            var ds = CircuitRF.Render.DataDisplay.PlotSourceFile.LoadWithNotes(npy).Item1!;
            var want = ChainBudget.ToDataSet(ChainBudget.Evaluate(ChainDesignIo.LoadFromFile(path)));
            foreach (var name in new[] { "Excess_dB", "Rain_dB", "PathLoss_dB", "Mfade_dB", "Mcomp_dB", "LinkMargin_dB" })
                Assert.Equal(want[name].RealValues[0], ds[name].RealValues[0], 12);
            Assert.Equal("rain", ds["Loss_dB"].Axes[0].Labels![4]);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}

/// <summary>The window's Propagation section: picking a model rebuilds the model's rows, keeps what a row of the same
/// name held, writes the model into the link, and is one undo step.</summary>
public sealed class ChainPropagationWindowTests
{
    [Fact]
    public void PickingAModel_RebuildsItsRows_AndIsOneUndoStep()
    {
        var d = new ChainDesign { Stages = [new ChainStage { Name = "LNA", Part = "Amp" }], Link = new ChainLink() };
        d.Link.Path["Distance"] = "200 m";
        d.Link.Path["Rain"] = "fixed";
        var vm = new CircuitRF.Ui.Chain.ChainBudgetViewModel(d);
        Assert.Empty(vm.LinkLargeScale);                                   // free space has no parameters of its own
        var model = vm.LinkPath.Single(p => p.Name == "Model");
        Assert.Equal(["free", "logdist"], model.Options!.Take(2));

        model.Value = "38901";
        Assert.Contains(vm.LinkLargeScale, p => p.Name == "Scenario");
        Assert.Equal("fixed", vm.LinkAddedLosses.Single(p => p.Name == "Rain").Value);   // kept across the pick
        Assert.Equal("38901", vm.ToDesign().Link!.Path["Model"]);

        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.LinkLargeScale);
        Assert.False(vm.ToDesign().Link!.Path.ContainsKey("Model"));
    }
}
