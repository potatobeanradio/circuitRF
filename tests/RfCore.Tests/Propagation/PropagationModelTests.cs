using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RfCore.Propagation;
using Xunit;

namespace RfCore.Tests.Propagation;

/// <summary>
/// Brief PCH-1's gates: the large-scale models against published validation examples (P.1812) or an independent
/// script written from the documents alone (testdata/propagation/reference), their validity refusals, the
/// continuity of every breakpoint, and the registry that describes every parameter once.
/// </summary>
public class PropagationModelTests
{
    private static string Data(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var p = Path.Combine(dir, "testdata", "propagation", relative);
            if (File.Exists(p) || Directory.Exists(p)) return p;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/propagation/" + relative + " not found");
    }

    private static IEnumerable<string[]> Rows(string path, out string[] header)
    {
        var lines = File.ReadAllLines(path).Where(l => !l.StartsWith('#')).ToList();
        header = lines[0].Split(',');
        return lines.Skip(1).Select(SplitCsv);
    }

    private static string[] SplitCsv(string line)
    {
        var cells = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { cells.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        cells.Add(cur.ToString());
        return cells.ToArray();
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- gate 1: published examples

    /// <summary>Every P.1812-8 validation example, every value the published results print: the path analysis, the
    /// zones, every loss of the method. Printed to ten significant digits, so 1e-7 absolute (and 1e-9 relative for
    /// the effective Earth radius).</summary>
    [Fact]
    public void P1812_ReproducesEveryPublishedValidationExample()
    {
        string dir = Data("p1812");
        var rows = Rows(Path.Combine(dir, "cases.csv"), out var h).ToList();
        int Col(string n) => Array.IndexOf(h, n);
        var failures = new List<string>();
        foreach (var c in rows)
        {
            double V(string n) => Num(c[Col(n)]);
            var profile = TerrainProfile.Read(Path.Combine(dir, c[1] + ".csv"));
            var r = ItuP1812.Predict(profile, new P1812Inputs(V("f_GHz") * 1e9, V("p"), V("pL"), V("htg"), V("hrg"), V("pol") == 2,
                V("DN"), V("N0"), V("phi"), V("dct"), V("dcr"), V("sigmaL")));
            var mine = new (string, double)[]
            {
                ("d", r.D), ("dlt", r.Dlt), ("dlr", r.Dlr), ("th_t", r.ThetaT), ("th_r", r.ThetaR), ("th", r.Theta), ("w", r.Omega),
                ("dtm", r.Dtm), ("dlm", r.Dlm), ("b0", r.Beta0), ("ae", r.Ae), ("hst", r.Hst), ("hsr", r.Hsr), ("hstd", r.Hstd),
                ("hsrd", r.Hsrd), ("htcp", r.HtcP), ("hrcp", r.HrcP), ("hte", r.Hte), ("hre", r.Hre), ("hm", r.Hm), ("Fj", r.Fj),
                ("Fk", r.Fk), ("Lbfs", r.Lbfs), ("Lb0p", r.Lb0p), ("Lb0b", r.Lb0b), ("Lbulla", r.Lbulla), ("Lbulls", r.Lbulls),
                ("Ldsph", r.Ldsph), ("Ld50", r.Ld50), ("Ldb", r.Ldb), ("Ldp", r.Ldp), ("Lbd50", r.Lbd50), ("Lbd", r.Lbd),
                ("Lminb0p", r.Lminb0p), ("Lba", r.Lba), ("Lminbap", r.Lminbap), ("Lbda", r.Lbda), ("Lbam", r.Lbam), ("Lbs", r.Lbs),
                ("Lbc", r.Lbc), ("Lb", r.Lb),
                // Fi at p = 50 % is exactly 0 here; the published results print Attachment 2's I(0.5) ≈ 9e-10 ratio.
                ("Fi", V("p") == 50 ? V("Fi") : r.Fi),
            };
            foreach (var (name, value) in mine)
            {
                double reference = V(name), tol = 1e-7 + 1e-9 * Math.Abs(reference);
                if (Math.Abs(value - reference) > tol) failures.Add($"{c[0]} {name}: {value:R} against {reference:R}");
            }
        }
        Assert.True(rows.Count >= 60, $"{rows.Count} validation cases");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    /// <summary>The delta-Bullington validation examples ITU-R Study Group 3 publishes for P.526 §4.5 (Document 3J/77):
    /// four terrain profiles, twenty cases, every intermediate loss and the effective heights, computed with
    /// ae = 8500 km. Kept outside the repository (its terms of reuse are the publisher's).</summary>
    [FixtureFact("testdata/propagation/p526-validation/cases.csv",
                 "extract the four profiles and the outputs sheet of ITU-R SG3's delta-Bullington validation examples (Document 3J/77) as described in src/RfCore/RESOLVED.md")]
    public void P526_ReproducesThePublishedDeltaBullingtonExamples()
    {
        string dir = Data("p526-validation");
        var failures = new List<string>();
        int n = 0;
        foreach (var c in Rows(Path.Combine(dir, "cases.csv"), out _))
        {
            var x = c.Select(v => v.Length == 0 ? double.NaN : Num(v)).ToArray();
            var profile = TerrainProfile.Read(Path.Combine(dir, $"path{(int)x[0]}.csv"));
            var r = ItuP526.GeneralPathLoss(x[3] * 1e6, profile, x[1], x[2], 8500.0 / 6371, 22, 0.003, vertical: false);
            foreach (var (name, mine, reference) in new[] { ("h'ts", r.HtsEff, x[4]), ("h'rs", r.HrsEff, x[5]), ("Lba", r.Lba, x[12]),
                                                            ("Lbs", r.Lbs, x[13]), ("Lsph", r.Lsph, x[14]), ("L", r.L, x[15]) })
                if (Math.Abs(mine - reference) > 1e-9) failures.Add($"path {x[0]} {x[3]} MHz {name}: {mine:R} against {reference:R}");
            n++;
        }
        Assert.Equal(20, n);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    // ---------------------------------------------------------------- gate 2: independent scripts

    /// <summary>Every case of the independent reference script, through the registry (so the adapters and the added
    /// losses are what is checked): the loss to 1e-9 dB — 1e-6 for the knife-edge, whose Fresnel integrals are the
    /// recommendation's coefficient expansion against the script's numerical integration — and the shadowing σ and
    /// line-of-sight probability where the case gives them.</summary>
    [Fact]
    public void EveryModel_MatchesTheIndependentReferenceScript()
    {
        var failures = new List<string>();
        var models = new HashSet<string>();
        foreach (var c in Rows(Data("reference/reference.csv"), out _))
        {
            var values = c[6].Length == 0 ? new Dictionary<string, string>()
                : c[6].Split(';').Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv[1]);
            var inputs = new ModelInputs { Model = c[1], FrequencyHz = Num(c[2]), DistanceM = Num(c[3]), TxHeightM = Num(c[4]), RxHeightM = Num(c[5]), Values = values };
            models.Add(c[1]);
            PathLossResult r;
            try { r = PropagationModels.Evaluate(inputs); }
            catch (Exception e) { failures.Add($"{c[0]}: {e.Message}"); continue; }
            double tol = c[0].Contains("knife-edge") ? 1e-6 : 1e-9;
            if (Math.Abs(r.LossDb - Num(c[7])) > tol) failures.Add($"{c[0]}: loss {r.LossDb:R} against {c[7]}");
            if (Math.Abs(r.SigmaDb - Num(c[8])) > 1e-12) failures.Add($"{c[0]}: σ {r.SigmaDb:R} against {c[8]}");
            if (c[9].Length > 0 && Math.Abs((r.LosProbability ?? double.NaN) - Num(c[9])) > 1e-12)
                failures.Add($"{c[0]}: P(LOS) {r.LosProbability:R} against {c[9]}");
            if (Math.Abs(r.Terms.Sum(t => t.Db) - r.LossDb) > 1e-9) failures.Add($"{c[0]}: the terms do not sum to the loss");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Superset(new HashSet<string> { "free", "logdist", "multislope", "tworay", "38901", "P1238", "P1411", "P526" }, models);
    }

    /// <summary>TR 38.901's large-scale parameter statistics (delay, azimuth and zenith spreads, K) against the script,
    /// to 1e-12, every scenario family, both states, with the report's frequency clamps.</summary>
    [Fact]
    public void Tr38901Spreads_MatchTheIndependentReferenceScript()
    {
        foreach (var c in Rows(Data("reference/spreads_38901.csv"), out _))
        {
            Assert.True(Tr38901.TryParse(c[0], out var s));
            var st = Tr38901.Spreads(s, c[1] == "los", Num(c[2]), Num(c[3]), Num(c[4]), Num(c[5]))!;
            var mine = new[] { st.DelaySpread, st.AzimuthDeparture, st.AzimuthArrival, st.ZenithArrival, st.ZenithDeparture };
            for (int k = 0; k < 5; k++)
            {
                Assert.Equal(Num(c[6 + 2 * k]), mine[k].Mean, 12);
                Assert.Equal(Num(c[7 + 2 * k]), mine[k].Std, 12);
            }
            if (c[16].Length == 0) Assert.Null(st.KFactorDb);
            else { Assert.Equal(Num(c[16]), st.KFactorDb!.Value.Mean, 12); Assert.Equal(Num(c[17]), st.KFactorDb!.Value.Std, 12); }
        }
    }

    // ---------------------------------------------------------------- gate 3: validity

    /// <summary>Every model refuses one point beyond each of its stated limits, naming the range in the sentence.</summary>
    [Fact]
    public void EveryModel_RefusesOnePointBeyondEachLimit()
    {
        static ModelInputs In(string model, double fGhz, double d, double ht = 1.5, double hr = 1.5, params string[] kv)
            => new()
            {
                Model = model, FrequencyHz = fGhz * 1e9, DistanceM = d, TxHeightM = ht, RxHeightM = hr,
                Values = kv.Select(s => s.Split('=', 2)).ToDictionary(p => p[0], p => p[1]),
            };
        var cases = new (ModelInputs Inputs, string Range)[]
        {
            (In("38901", 0.49, 100, 25, 1.5, "Scenario=UMa", "Los=los"), "between 0.5 and 100"),
            (In("38901", 100.01, 100, 25, 1.5, "Scenario=UMa", "Los=los"), "between 0.5 and 100"),
            (In("38901", 30.01, 500, 35, 1.5, "Scenario=RMa", "Los=los"), "between 0.5 and 30"),
            (In("38901", 3.5, 9.99, 25, 1.5, "Scenario=UMa", "Los=los"), "between 10 and 5000"),
            (In("38901", 3.5, 5000.01, 25, 1.5, "Scenario=UMi", "Los=nlos"), "between 10 and 5000"),
            (In("38901", 3.5, 300, 25, 1.49, "Scenario=UMa", "Los=los"), "between 1.5 and 22.5"),
            (In("38901", 3.5, 300, 25, 22.51, "Scenario=UMa", "Los=los"), "between 1.5 and 22.5"),
            (In("38901", 3.5, 10000.01, 35, 1.5, "Scenario=RMa", "Los=los"), "between 10 and 10000"),
            (In("38901", 3.5, 5000.01, 35, 1.5, "Scenario=RMa", "Los=nlos"), "between 10 and 5000"),
            (In("38901", 3.5, 500, 9.99, 1.5, "Scenario=RMa", "Los=los"), "between 10 and 150"),
            (In("38901", 3.5, 500, 35, 10.01, "Scenario=RMa", "Los=los"), "between 1 and 10"),
            (In("38901", 3.5, 500, 35, 1.5, "Scenario=RMa", "Los=los", "StreetWidth=50.01"), "between 5 and 50"),
            (In("38901", 3.5, 150.1, 3, 3, "Scenario=InH-Mixed", "Los=los"), "between 1 and 150"),
            (In("38901", 3.5, 0.99, 3, 3, "Scenario=InH-Open", "Los=nlos"), "between 1 and 150"),
            (In("38901", 3.5, 600.1, 1.5, 1.5, "Scenario=InF-SL", "Los=los"), "between 1 and 600"),
            (In("P1238", 0.29, 10, 1.5, 1.5, "Environment=office", "Los=los"), "between 0.3 and 294"),
            (In("P1238", 294.1, 10, 1.5, 1.5, "Environment=office", "Los=los"), "between 0.3 and 294"),
            (In("P1238", 2.4, 1.99, 1.5, 1.5, "Environment=office", "Los=los"), "between 2 and 27"),
            (In("P1238", 2.4, 30.01, 1.5, 1.5, "Environment=office", "Los=nlos"), "between 4 and 30"),
            (In("P1411", 3.5, 715.1, 1.5, 1.5, "Mode=belowroof", "Environment=urbanhighrise", "Los=nlos"), "between 20 and 715"),
            (In("P1411", 100, 150.1, 1.5, 1.5, "Mode=belowroof", "Environment=urbanhighrise", "Los=nlos"), "between 20 and 150"),
            (In("P1411", 159.1, 100, 1.5, 1.5, "Mode=belowroof", "Environment=urbanhighrise", "Los=nlos"), "between 0.8 and 159"),
            (In("P1411", 2.19, 500, 25, 1.5, "Mode=aboveroof", "Los=los"), "between 2.2 and 73"),
            (In("P1411", 3.5, 1200.1, 25, 1.5, "Mode=aboveroof", "Los=los"), "between 55 and 1200"),
            (In("P1411", 0.299, 100, 2, 2, "Mode=lowheight"), "between 300 and 3000"),
            (In("P1411", 3.001, 100, 2, 2, "Mode=lowheight"), "between 300 and 3000"),
            (In("P1411", 0.4, 3000.1, 2, 2, "Mode=lowheight"), "between 0.001 and 3000"),
            (In("P1411", 0.799, 100, 2, 2, "Mode=corner", "X1=50", "X2=50"), "800 to 38000"),
            (In("P1411", 38.01, 100, 2, 2, "Mode=corner", "X1=50", "X2=50"), "800 to 38000"),
            (In("free", 3, 999, 30, 5, "Clutter=terrestrial", "ClutterEnd=both"), "at least 1 km"),
            (In("free", 3, 249, 30, 5, "Clutter=terrestrial"), "at least 0.25 km"),
            (In("free", 0.49, 1000, 30, 5, "Clutter=terrestrial"), "between 0.5 and 67"),
            (In("free", 3.01, 1000, 30, 5, "Clutter=terminal"), "between 0.03 and 3"),
            (In("free", 9.99, 1000, 30, 5, "Clutter=earthspace", "ClutterElevation=30"), "between 10 and 100"),
            (In("free", 0.079, 1000, 30, 5, "BuildingEntry=traditional"), "between 0.08 and 100"),
            (In("free", 2.2, 1000, 30, 5, "Vegetation=on", "VegDepth=10"), "between 105.9 and 2117.5"),
            (In("P526", 0.0299, 10000, 30, 30, "Mode=knifeedge", "EdgeDistance=4000"), "at least 30 MHz"),
            (In("P526", 0.0099, 10000, 30, 30, "Mode=sphere"), "at least 10 MHz"),
        };
        var missed = new List<string>();
        foreach (var (inputs, range) in cases)
        {
            string at = $"{inputs.Model} {string.Join(" ", inputs.Values.Select(kv => kv.Key + "=" + kv.Value))} f={inputs.FrequencyHz / 1e9} GHz d={inputs.DistanceM} m";
            try { PropagationModels.Evaluate(inputs); missed.Add($"{at}: no refusal"); }
            catch (PropagationRefusal e) { if (!e.Message.Contains(range, StringComparison.Ordinal)) missed.Add($"{at}: '{e.Message}' does not say '{range}'"); }
        }
        Assert.True(missed.Count == 0, string.Join("\n", missed));

        // P.1812's own limits, on a short flat profile.
        var flat = new TerrainProfile(new[] { 0, 0.5, 1.0 }, new[] { 10.0, 10, 10 });
        var ok = new P1812Inputs(1e9, 50, 50, 10, 10);
        foreach (var (inp, range) in new[]
                 {
                     (ok with { FrequencyHz = 0.0299e9 }, "between 0.03 and 6"), (ok with { FrequencyHz = 6.01e9 }, "between 0.03 and 6"),
                     (ok with { TimePercent = 0.99 }, "between 1 and 50"), (ok with { TimePercent = 50.1 }, "between 1 and 50"),
                     (ok with { LocationPercent = 0.99 }, "between 1 and 99"), (ok with { LocationPercent = 99.1 }, "between 1 and 99"),
                     (ok with { TxHeightM = 0.99 }, "between 1 and 3000"), (ok with { RxHeightM = 3000.1 }, "between 1 and 3000"),
                     (ok with { LatitudeDeg = 80.1 }, "between -80 and 80"),
                 })
            Assert.Contains(range, Assert.Throws<PropagationRefusal>(() => ItuP1812.Predict(flat, inp)).Message, StringComparison.Ordinal);
        var tooShort = new TerrainProfile(new[] { 0, 0.1, 0.249 }, new[] { 10.0, 10, 10 });
        Assert.Contains("between 0.25 and 3000", Assert.Throws<PropagationRefusal>(() => ItuP1812.Predict(tooShort, ok)).Message);
    }

    /// <summary>A profile is refused unless it starts at the transmitter, increases, ends at the receiver, and says its
    /// units.</summary>
    [Fact]
    public void TerrainProfile_RefusesAProfileThatIsNotTransmitterToReceiver()
    {
        Assert.Contains("distance 0", Assert.Throws<PropagationRefusal>(() =>
            TerrainProfile.Parse("distance (km), height (m)\n0.1, 5\n0.2, 6\n0.3, 7")).Message);
        Assert.Contains("must increase", Assert.Throws<PropagationRefusal>(() =>
            TerrainProfile.Parse("distance (km), height (m)\n0, 5\n0.2, 6\n0.2, 7")).Message);
        Assert.Contains("unit in parentheses", Assert.Throws<PropagationRefusal>(() =>
            TerrainProfile.Parse("distance, height (m)\n0, 5\n0.2, 6\n0.3, 7")).Message);
        var p = TerrainProfile.Parse("distance (m), height (ft), clutter (m), zone\n0, 10, 5, inland\n500, 20, 5, coastal\n1000, 30, 5, sea");
        Assert.Equal(1.0, p.LengthKm, 12);
        Assert.Equal(20 * 0.3048 + 5, p.SurfaceM[1], 12);   // clutter added at the intermediate point
        Assert.Equal(10 * 0.3048, p.SurfaceM[0], 12);       // never at a terminal
        Assert.Contains("end at the receiver", Assert.Throws<PropagationRefusal>(() => PropagationModels.Evaluate(
            new ModelInputs { Model = "P1812", FrequencyHz = 1e9, DistanceM = 2000, TxHeightM = 10, RxHeightM = 10, Profile = p })).Message);
    }

    // ---------------------------------------------------------------- gate 4: continuity

    /// <summary>Multi-slope at both its breakpoints and the 38.901 UMa and UMi line-of-sight breakpoints are continuous
    /// to 1e-12 dB. RMa's is not, as the report writes it (PL1 is evaluated at d3D before the breakpoint and at dBP
    /// after it, while the switch is on d2D): the jump is measured here and recorded in RESOLVED.</summary>
    [Fact]
    public void Breakpoints_AreContinuous()
    {
        const double eps = 1e-9;
        foreach (double b in new[] { 10.0, 100.0 })
        {
            double lo = LogDistanceModel.MultiSlope("multislope", 2.4e9, b * (1 - eps), 1, new[] { 10.0, 100 }, new[] { 2.0, 3, 4 }, 0).LossDb;
            double hi = LogDistanceModel.MultiSlope("multislope", 2.4e9, b * (1 + eps), 1, new[] { 10.0, 100 }, new[] { 2.0, 3, 4 }, 0).LossDb;
            Assert.True(Math.Abs(hi - lo) < 1e-12 + 40 * eps, $"multislope at {b} m: {lo:R} / {hi:R}");
        }
        foreach (var (s, hBS) in new[] { (Scenario38901.UMa, 25.0), (Scenario38901.UMi, 10.0) })
        {
            double dbp = 4 * (hBS - 1) * (1.5 - 1) * 3.5e9 / 3e8;
            double lo = Tr38901.PathLoss(s, true, 3.5e9, dbp, hBS, 1.5).Pl;
            double hi = Tr38901.PathLoss(s, true, 3.5e9, dbp * (1 + 1e-12), hBS, 1.5).Pl;
            Assert.True(Math.Abs(hi - lo) < 1e-10, $"{s} at d'BP: {lo:R} / {hi:R}");
        }
        double rbp = 2 * Math.PI * 35 * 1.5 * 3.5e9 / 3e8;
        double rJump = Tr38901.PathLoss(Scenario38901.RMa, true, 3.5e9, rbp * (1 + 1e-12), 35, 1.5).Pl
                       - Tr38901.PathLoss(Scenario38901.RMa, true, 3.5e9, rbp, 35, 1.5).Pl;
        Assert.InRange(Math.Abs(rJump), 1e-5, 1e-3);   // the report's own step, ~1e-4 dB at this geometry
    }

    // ---------------------------------------------------------------- gate 5: the registry

    /// <summary>Every parameter is described once, with a unit (or none, stated) and, for a number, a finite range; and
    /// what each model's evaluation reads is a subset of what its entry names (a read of an unnamed parameter throws,
    /// and every model runs here once in each of its modes).</summary>
    [Fact]
    public void Registry_DescribesEveryParameterAModelReads()
    {
        foreach (var m in PropagationModels.All)
        {
            var names = m.Parameters.Select(p => p.Name).Concat(AddedLosses.Parameters.Select(p => p.Name)).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var p in m.Parameters.Concat(AddedLosses.Parameters))
            {
                Assert.NotNull(p.Unit);
                Assert.False(string.IsNullOrWhiteSpace(p.Help), $"{m.Name}.{p.Name} has no help");
                if (p.Kind == ParameterKind.Number) Assert.True(double.IsFinite(p.Min) && double.IsFinite(p.Max), $"{m.Name}.{p.Name} has no finite range");
                if (p.Kind == ParameterKind.Word) Assert.True(p.Words is { Count: > 0 } && p.Words.Contains(p.Default, StringComparer.OrdinalIgnoreCase), $"{m.Name}.{p.Name}");
            }
        }

        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Rows(Data("reference/reference.csv"), out _))
        {
            var values = c[6].Length == 0 ? new Dictionary<string, string>()
                : c[6].Split(';').Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv[1]);
            PropagationModels.Evaluate(new ModelInputs { Model = c[1], FrequencyHz = Num(c[2]), DistanceM = Num(c[3]), TxHeightM = Num(c[4]), RxHeightM = Num(c[5]), Values = values },
                                       out var names);
            read.UnionWith(names);
        }
        var p1812 = new TerrainProfile(new[] { 0, 0.5, 1.0 }, new[] { 10.0, 10, 10 });
        PropagationModels.Evaluate(new ModelInputs { Model = "P1812", FrequencyHz = 1e9, TxHeightM = 10, RxHeightM = 10, Profile = p1812 }, out var n1812);
        PropagationModels.Evaluate(new ModelInputs { Model = "P526", FrequencyHz = 1e9, TxHeightM = 10, RxHeightM = 10, Profile = p1812 }, out var n526);
        read.UnionWith(n1812);
        read.UnionWith(n526);
        var registered = PropagationModels.All.SelectMany(m => m.Parameters).Concat(AddedLosses.Parameters).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Subset(registered, read);
        Assert.Contains("no parameter 'Sigma'", Assert.Throws<PropagationRefusal>(() => PropagationModels.Evaluate(new ModelInputs
            { Model = "free", FrequencyHz = 1e9, DistanceM = 10, Values = new Dictionary<string, string> { ["Sigma"] = "3" } })).Message);
    }

    /// <summary>P.1812's location variability (§4.7, §4.9), which the published examples do not exercise (all at
    /// pL = 50 %): eq (64)'s σL from the resolution, eq (65)'s height function at the receiver's clutter, and
    /// eq (69).</summary>
    [Fact]
    public void P1812_LocationVariability_FollowsEquations64To69()
    {
        var p = new TerrainProfile(new[] { 0, 2.0, 4.0, 6.0 }, new[] { 100.0, 120, 110, 100 }, clutterM: new[] { 0, 10.0, 10, 0 });
        var median = ItuP1812.Predict(p, new P1812Inputs(0.6e9, 50, 50, 30, 5, ResolutionM: 100));
        foreach (var (hrg, u) in new[] { (5.0, 1.0), (15.0, 0.5), (25.0, 0.0) })   // R at the receiver's neighbour is 10 m
        {
            var at90 = ItuP1812.Predict(p, new P1812Inputs(0.6e9, 50, 90, 30, hrg, ResolutionM: 100));
            var at50 = ItuP1812.Predict(p, new P1812Inputs(0.6e9, 50, 50, 30, hrg, ResolutionM: 100));
            double sigmaL = (0.024 * 0.6 + 0.52) * Math.Pow(100, 0.28);
            Assert.Equal(u * sigmaL, at90.SigmaLoc, 12);
            Assert.Equal(Math.Max(at50.Lb0p, at50.Lbc - ItuP1812.I(0.9) * u * sigmaL), at90.Lb, 9);
        }
        Assert.True(median.Lb >= median.Lb0p);
    }
}
