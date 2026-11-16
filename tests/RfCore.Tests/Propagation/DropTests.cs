using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RfCore.Propagation;
using RfCore.Statistics;
using Xunit;
using Xunit.Abstractions;

namespace RfCore.Tests.Propagation;

/// <summary>
/// Brief PCH-10's gates on what a drop draws, each at a fixed seed against a stated statistic:
///   2  over 10⁴ drops the shadowing is N(0, σ) by a one-sample Kolmogorov–Smirnov distance, and the line-of-sight fraction is the
///      model's probability within its 99.9 % binomial interval;
///   3  a TR 38.901 scenario drop's realized delay spread, angle spreads and K over 10⁴ drops match an independent implementation
///      written from the report alone (testdata/propagation/scenario_drop_reference.py) by a two-sample Kolmogorov–Smirnov distance
///      at α = 0.001, for UMi and InH in both states, and its cluster powers sum to 1;
///   4  ITU-R P.681's state at the start of a drop is GOOD with eq. 19's probability, and the event lengths average eq. 17a's, each
///      within its interval, over 10⁴ drops.
/// </summary>
public class DropTests(ITestOutputHelper output)
{
    private const int Drops = 10_000;

    private static string Data(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var p = Path.Combine(dir, "testdata", "propagation", relative);
            if (File.Exists(p)) return p;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("testdata/propagation/" + relative + " not found");
    }

    [Fact]
    public void Gate2_ShadowingIsTheModelsSigma_AndTheLosFractionItsProbability()
    {
        const double sigma = 7.82, pLos = 0.3712;
        var x = Enumerable.Range(1, Drops).Select(d => DropDraws.Shadowing(sigma, 11, d, "CH1")).OrderBy(v => v).ToArray();
        double ks = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double f = Normal.Cdf(x[i] / sigma);
            ks = Math.Max(ks, Math.Max(Math.Abs(f - (double)i / x.Length), Math.Abs(f - (i + 1.0) / x.Length)));
        }
        double critical = 1.949 / Math.Sqrt(Drops);   // α = 0.001
        double std = Math.Sqrt(x.Select(v => v * v).Average() - Math.Pow(x.Average(), 2));
        output.WriteLine($"shadowing: KS {ks:G4} (critical {critical:G4}), sample σ {std:G5} dB against {sigma}");
        Assert.True(ks < critical, $"KS {ks}");

        int los = Enumerable.Range(1, Drops).Count(d => DropDraws.LineOfSight(pLos, 11, d, "CH1"));
        var (lo, hi) = BetaFunction.BinomialInterval(los, Drops, 0.999);
        output.WriteLine($"line of sight in {los} of {Drops}: interval [{lo:G4}, {hi:G4}] against {pLos}");
        Assert.InRange(pLos, lo, hi);
    }

    // ── gate 3 ───────────────────────────────────────────────────────────────────────────────

    private sealed record Realized(double Ds, double Asd, double Asa, double Zsd, double Zsa, double KDb, double PowerKept);

    private static double ZenithRms(IEnumerable<(double Deg, double P)> a)
    {
        var l = a.ToList();
        double p = l.Sum(v => v.P), mean = l.Sum(v => v.Deg * v.P) / p;
        return Math.Sqrt(l.Sum(v => v.P * (v.Deg - mean) * (v.Deg - mean)) / p);
    }

    private static Realized Stats(ScenarioDropResult r)
    {
        var rays = r.Rays.Rays;
        double los = rays.Where(x => x.Los).Sum(x => x.Power), rest = rays.Where(x => !x.Los).Sum(x => x.Power);
        return new Realized(r.RealizedDelaySpread,
                            CdlProfiles.RmsAngle(rays.Select(x => (x.AodDeg, x.Power))), CdlProfiles.RmsAngle(rays.Select(x => (x.AoaDeg, x.Power))),
                            ZenithRms(rays.Select(x => (x.ZodDeg, x.Power))), ZenithRms(rays.Select(x => (x.ZoaDeg, x.Power))),
                            r.Los ? 10 * Math.Log10(los / rest) : double.NaN, r.Clusters.Sum(c => c.Power));
    }

    /// <summary>The largest gap between the sample's CDF and the reference's at the reference's 199 quantiles.</summary>
    private static double Distance(double[] sample, JsonElement quantiles, double[] probabilities)
    {
        var s = sample.OrderBy(v => v).ToArray();
        double worst = 0;
        int k = 0;
        foreach (var q in quantiles.EnumerateArray())
        {
            double qv = q.GetDouble();
            int below = Array.BinarySearch(s, qv);
            if (below < 0) below = ~below; else while (below < s.Length && s[below] <= qv) below++;
            worst = Math.Max(worst, Math.Abs((double)below / s.Length - probabilities[k++]));
        }
        return worst;
    }

    [Theory]
    [InlineData("UMi-LOS")]
    [InlineData("UMi-NLOS")]
    [InlineData("InH-LOS")]
    [InlineData("InH-NLOS")]
    public void Gate3_ScenarioDrops_MatchTheIndependentImplementation(string key)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Data("scenario_drop_reference.json")));
        var root = doc.RootElement;
        double[] probs = [.. root.GetProperty("quantile_probabilities").EnumerateArray().Select(v => v.GetDouble())];
        var c = root.GetProperty("cases").GetProperty(key);
        var inp = c.GetProperty("inputs");
        bool los = key.EndsWith("-LOS", StringComparison.Ordinal);
        var scenario = key.StartsWith("UMi", StringComparison.Ordinal) ? Scenario38901.UMi : Scenario38901.InHMixed;
        double f = inp.GetProperty("fc_GHz").GetDouble() * 1e9, d2D = inp.GetProperty("d2D_m").GetDouble();
        double hBS = inp.GetProperty("hBS_m").GetDouble(), hUT = inp.GetProperty("hUT_m").GetDouble();
        var dir = c.GetProperty("los_directions_deg");
        double aod = dir.GetProperty("AOD").GetDouble(), aoa = dir.GetProperty("AOA").GetDouble();
        double zod = dir.GetProperty("ZOD").GetDouble(), zoa = dir.GetProperty("ZOA").GetDouble();
        // this transcription removes clusters on eq. 7.5-6's powers: in line of sight the reference's variant of that reading
        var stats = (los ? c.GetProperty("variant_removal_on_7_5_6_powers") : c).GetProperty("statistics");

        var all = Enumerable.Range(1, Drops)
                            .Select(d => Stats(Tr38901Drop.Draw(scenario, los, f, d2D, hBS, hUT, aod, aoa, zod, zoa, 38901, d, "CH1"))).ToList();
        double critical = 1.949 * Math.Sqrt(2.0 / Drops);   // two samples of 10⁴, α = 0.001
        var checks = new List<(string Name, double[] Sample)>
        {
            ("ds", [.. all.Select(r => r.Ds)]), ("asd", [.. all.Select(r => r.Asd)]), ("asa", [.. all.Select(r => r.Asa)]),
            ("zsd", [.. all.Select(r => r.Zsd)]), ("zsa", [.. all.Select(r => r.Zsa)]),
        };
        if (los) checks.Add(("k_db", [.. all.Select(r => r.KDb)]));
        foreach (var (name, sample) in checks)
        {
            var q = stats.GetProperty(name);
            double dist = Distance(sample, q.GetProperty("quantiles"), probs);
            output.WriteLine($"{key} {name}: KS distance {dist:G4} (critical {critical:G4}); median {sample.OrderBy(v => v).ElementAt(Drops / 2):G5}, reference mean {q.GetProperty("mean").GetDouble():G5}");
            Assert.True(dist < critical, $"{key} {name}: KS distance {dist}");
        }
        // the cluster powers (eq. 7.5-6) sum to 1; the −25 dB removal keeps nearly all of it
        double minKept = all.Min(r => r.PowerKept), maxKept = all.Max(r => r.PowerKept);
        output.WriteLine($"{key} kept power from {minKept:G6} to {maxKept:G17}");
        Assert.True(maxKept <= 1 + 1e-12 && minKept > 0.9, $"{minKept} … {maxKept}");
        var full = Tr38901Drop.Draw(scenario, los, f, d2D, hBS, hUT, aod, aoa, zod, zoa, 38901, 1, "CH1");
        Assert.Equal(full.Rays.Rays.Count, Tr38901Drop.Draw(scenario, los, f, d2D, hBS, hUT, aod, aoa, zod, zoa, 38901, 1, "CH1").Rays.Rays.Count);
    }

    [Fact]
    public void Gate3_EveryScenariosCorrelationMatrix_IsPositiveDefinite()
    {
        foreach (var s in Enum.GetValues<Scenario38901>())
            foreach (bool los in new[] { true, false })
            {
                if (s == Scenario38901.InFHH && !los) continue;
                var p = Tr38901Drop.SmallScale(s, los, 3.5);
                _ = Tr38901Drop.Cholesky(Tr38901Drop.CorrelationMatrix(p, los), $"{s} {los}");
            }
    }

    // ── gate 4 ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(P681Environment.Urban, 2.2e9, 20)]
    [InlineData(P681Environment.Suburban, 3.8e9, 45)]
    [InlineData(P681Environment.Highway, 20e9, 30)]
    public void Gate4_SatelliteStates_OccupancyAndMeanDurations(P681Environment env, double f, double el)
    {
        var set = ItuP681.Select(env, f, el);
        var (pG, _) = ItuP681.Occupancy(set);
        int good = 0;
        var lenG = new List<double>(); var lenB = new List<double>(); var maB = new List<double>();
        for (int d = 1; d <= Drops; d++)
        {
            var series = new P681Series(set, 681, d, "CH1");
            if (series.GoodAt(0)) good++;
            // past the event the drop starts in (length-biased), every event's length is drawn whole
            series.At(series.At(0).EndM + 1e-9);
            var ev = series.Events.ToList();
            foreach (var e in series.Over(ev[0].EndM + 4 * (ItuP681.MeanDuration(set.Good) + ItuP681.MeanDuration(set.Bad))).Where(s => s.Good is not null && s.StartM > ev[0].StartM).Take(4))
            {
                (e.Good!.Value ? lenG : lenB).Add(e.EndM - e.StartM);
                if (!e.Good.Value) maB.Add(e.From.Ma);
            }
        }
        var (lo, hi) = BetaFunction.BinomialInterval(good, Drops, 0.999);
        output.WriteLine($"{set.Label}: GOOD at the start in {good} of {Drops} [{lo:G4}, {hi:G4}], eq. 19 pG {pG:G5}");
        Assert.InRange(pG, lo, hi);
        void Mean(List<double> xs, double expected, string what)
        {
            double m = xs.Average(), se = Math.Sqrt(xs.Select(v => (v - m) * (v - m)).Sum() / (xs.Count - 1) / xs.Count);
            output.WriteLine($"  {what}: mean {m:G5} over {xs.Count} against {expected:G5} (±{4 * se:G3} at 4 standard errors)");
            Assert.True(Math.Abs(m - expected) < 4 * se, $"{what}: {m} against {expected}");
        }
        Mean(lenG, ItuP681.MeanDuration(set.Good), "GOOD event length (m), eq. 17a");
        Mean(lenB, ItuP681.MeanDuration(set.Bad), "BAD event length (m), eq. 17a");
        Mean(maB, ItuP681.MeanMa(set, false), "BAD MA (dB), truncated mean");
    }
}
