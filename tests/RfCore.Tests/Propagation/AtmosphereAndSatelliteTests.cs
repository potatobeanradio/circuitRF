using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RfCore.Propagation;
using RfCore.Statistics;
using Xunit;

namespace RfCore.Tests.Propagation;

/// <summary>
/// Brief PCH-2's gates: gas, rain, clouds and scintillation against ITU-R Study Group 3's published validation examples
/// (a local fixture), the satellite geometry, and the validity refusals. The independent-script cases (P.530, P.531,
/// TR 38.811, the satellite model through the registry) run in <see cref="PropagationModelTests"/>.
/// </summary>
public class AtmosphereAndSatelliteTests
{
    private const string Sg3 = "testdata/propagation/sg3-validation/p618_rain.csv";
    private const string HowTo = "extract ITU-R SG3's Earth-space validation workbook (CG-3M3J-13-ValEx, Rev 8.3.0) as described in src/RfCore/RESOLVED.md";

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

    private static double Num(string s) => s.Trim().Equals("nan", StringComparison.OrdinalIgnoreCase) ? double.NaN : double.Parse(s, CultureInfo.InvariantCulture);

    private static double[][] Load(string relative)
        => File.ReadAllLines(Data(relative)).Where(l => !l.StartsWith('#')).Skip(1).Select(l => l.Split(',').Select(Num).ToArray()).ToArray();

    private static void Close(List<string> failures, string what, double mine, double reference, double rel)
    {
        if (Math.Abs(mine - reference) > rel * Math.Max(Math.Abs(reference), 1e-300)) failures.Add($"{what}: {mine:R} against {reference:R}");
    }

    // ---------------------------------------------------------------- gate 1: published examples

    /// <summary>Every published example of P.676 (the 1-350 GHz specific attenuation curve at the reference conditions,
    /// and the Annex 1 slant path layer by layer), P.838 (coefficients and specific attenuation), P.840 (KL and the cloud
    /// attenuation), and P.618 (rain, scintillation, the total from its parts), to 1e-12 relative (the slant path to
    /// 1e-9 dB, its 922-layer sum).</summary>
    [FixtureFact(Sg3, HowTo)]
    public void PublishedValidationExamples_AreReproduced()
    {
        var f = new List<string>();
        double e = 7.5 * 288.15 / 216.7;
        foreach (var r in Load("sg3-validation/p676_spatt.csv"))
        {
            var (o, w) = ItuP676.Specific(r[0], 1013.25, e, 288.15);
            Close(f, $"P.676 γo at {r[0]} GHz", o, r[1], 1e-12);
            Close(f, $"P.676 γw at {r[0]} GHz", w, r[2], 1e-12);
        }
        var s = Load("sg3-validation/p676_slant.csv")[0];
        var (a, bend) = ItuP676.Slant(s[0], s[1], s[2]);
        if (Math.Abs(a - s[3]) > 1e-9) f.Add($"P.676 slant: {a:R} against {s[3]:R}");
        if (Math.Abs(bend - s[4]) > 1e-12) f.Add($"P.676 bending: {bend:R} against {s[4]:R}");

        foreach (var r in Load("sg3-validation/p838.csv"))
        {
            var (kh, kv, ah, av) = ItuP838.Coefficients(r[1]);
            var (k, al) = ItuP838.Coefficients(r[1], r[0], r[2]);
            foreach (var (n, m, x) in new[] { ("kH", kh, r[3]), ("kV", kv, r[4]), ("αH", ah, r[5]), ("αV", av, r[6]), ("k", k, r[7]), ("α", al, r[8]),
                                              ("γR", ItuP838.Specific(r[1], r[9], r[0], r[2]), r[10]) })
                Close(f, $"P.838 {n} at {r[1]} GHz", m, x, 1e-12);
        }
        foreach (var r in Load("sg3-validation/p840.csv"))
        {
            Close(f, $"P.840 KL at {r[1]} GHz", ItuP840.KL(r[1]), r[6], 1e-12);
            Close(f, $"P.840 Ac at {r[1]} GHz", ItuP840.Cloud(r[1], r[2], r[7]), r[8], 1e-12);
        }
        foreach (var r in Load("sg3-validation/p618_rain.csv"))
            Close(f, $"P.618 rain lat {r[0]} p {r[5]}", ItuP618.Rain(r[2], r[3], r[4], r[5], r[1], r[6], r[7], r[0]), r[16], 1e-12);
        foreach (var r in Load("sg3-validation/p618_scint.csv"))
            Close(f, $"P.618 scintillation el {r[1]} p {r[4]}", ItuP618.Scintillation(r[0], r[1], r[4], r[5], r[2], r[3]), r[11], 1e-12);
        foreach (var r in Load("sg3-validation/p618_total.csv"))
            Close(f, $"P.618 total p {r[0]}", ItuP618.Total(r[0], r[1], r[2], r[3], r[4]), r[5], 1e-12);
        Assert.True(f.Count == 0, string.Join("\n", f.Take(20)));
    }

    // ---------------------------------------------------------------- gate 3: geometry

    /// <summary>The slant range is the altitude at the zenith; at 600 km on the horizon the range rate is the orbital
    /// velocity times R/(R + h); a geostationary satellite has none; and every rate and its drift agree with central
    /// differences of the simulated position (testdata/propagation/reference/satellite_geometry.csv).</summary>
    [Fact]
    public void SatelliteGeometry_MatchesTheOrbit()
    {
        Assert.Equal(600, SatelliteGeometry.SlantRangeKm(600, 90), 9);
        var horizon = SatelliteGeometry.At(600, 0, 2e9, rising: false);
        double r = SatelliteGeometry.EarthRadiusKm * 1e3, a = r + 600e3, v = Math.Sqrt(SatelliteGeometry.Mu / a);
        Assert.Equal(v * r / a, horizon.RangeRateMps, 6);
        var geo = SatelliteGeometry.At(SatelliteGeometry.GeostationaryKm, 40, 20e9, rising: true);
        Assert.Equal(0, geo.RangeRateMps);
        Assert.Equal(0, geo.DopplerHz);

        foreach (var c in File.ReadAllLines(Data("reference/satellite_geometry.csv")).Where(l => !l.StartsWith('#')).Skip(1).Select(l => l.Split(',')))
        {
            var g = SatelliteGeometry.At(Num(c[0]), Num(c[1]), Num(c[2]), c[3] == "rising");
            Assert.Equal(Num(c[4]), g.SlantRangeKm, 6);
            Assert.True(Math.Abs(g.RangeRateMps - Num(c[5])) < 1e-6 * Math.Max(1, Math.Abs(Num(c[5]))), $"range rate {g.RangeRateMps:R} against {c[5]}");
            Assert.True(Math.Abs(g.RangeAccelMps2 - Num(c[6])) < 1e-4, $"range acceleration {g.RangeAccelMps2:R} against {c[6]}");
            Assert.True(Math.Abs(g.DopplerHz - Num(c[7])) < 1e-6 * Math.Max(1, Math.Abs(Num(c[7]))), $"Doppler {g.DopplerHz:R} against {c[7]}");
        }
    }

    /// <summary>The Nakagami fade depth inverts the incomplete gamma function: P(m, m·I) at the returned intensity is
    /// the stated probability.</summary>
    [Fact]
    public void IonosphericFadeDepth_InvertsTheNakagamiDistribution()
    {
        double s4 = 0.6, m = ItuP531.NakagamiM(s4), fade = ItuP531.FadeDepth(s4, 1);
        Assert.Equal(0.01, GammaFunction.P(m, m * Math.Pow(10, -fade / 10)), 12);
        Assert.Equal(0, ItuP531.FadeDepth(0.05, 1));
    }

    // ---------------------------------------------------------------- gate 4: validity

    /// <summary>Each model refuses one point past each limit, and an Earth-space effect on a terrestrial path.</summary>
    [Fact]
    public void EveryModel_RefusesOnePointPastEachLimit()
    {
        var atm = new AtmosphereState(288.15, 1013.25, 7.5);
        var cases = new (Action Call, string Says)[]
        {
            (() => ItuP676.Terrestrial(0.99, 1, atm), "between 1 and 1000"),
            (() => ItuP676.Terrestrial(1000.1, 1, atm), "between 1 and 1000"),
            (() => ItuP676.Slant(20, 0, -0.1), "between 0 and 90"),
            (() => ItuP838.Coefficients(0.99), "between 1 and 1000"),
            (() => ItuP530.Rain(175.1, 5, 40, 0.1, 90), "between 1 and 175"),
            (() => ItuP530.Rain(20, 60.1, 40, 0.1, 90), "between 0 and 60"),
            (() => ItuP530.Rain(20, 5, 40, 1.01, 90), "between 0.001 and 1"),
            (() => ItuP530.Rain(20, 5, 40, 0.00099, 90), "between 0.001 and 1"),
            (() => ItuP618.Rain(55.1, 30, 45, 0.1, 0, 3, 40, 45), "between 1 and 55"),
            (() => ItuP618.Rain(20, 30, 45, 5.01, 0, 3, 40, 45), "between 0.001 and 5"),
            (() => ItuP618.Scintillation(3.99, 30, 1, 50, 1, 0.5), "between 4 and 55"),
            (() => ItuP618.Scintillation(20, 4.99, 1, 50, 1, 0.5), "between 5 and 90"),
            (() => ItuP618.Scintillation(20, 30, 0.0099, 50, 1, 0.5), "between 0.01 and 50"),
            (() => ItuP618.Total(50.1, 0, 0, 0, 0), "between 0.001 and 50"),
            (() => ItuP840.Cloud(200.1, 30, 0.5), "between 1 and 200"),
            (() => ItuP840.Cloud(20, 4.99, 0.5), "between 5 and 90"),
            (() => ItuP531.FadeDepth(1.01, 1), "between 0 and 1"),
            (() => Tr38811.LosProbability(NtnScenario.Urban, 9.99), "between 10 and 90"),
            (() => Tr38811.LargeScale(NtnScenario.Urban, true, 12, 30), "S band (2 to 4 GHz) and Ka band (17.7 to 40 GHz)"),
            (() => SatelliteGeometry.At(600, 90.1, 2e9, false), "between 0 and 90"),
            (() => Eval("free", 20e9, 1000, ("Clouds", "on")), "Earth-space effects"),
            (() => Eval("free", 20e9, 1000, ("Rain", "drawn")), "per drop"),
            (() => Eval("free", 20e9, 1000, ("Rain", "fixed"), ("Availability", "98")), "between 0.001 and 1"),
        };
        var missed = new List<string>();
        for (int i = 0; i < cases.Length; i++)
        {
            try { cases[i].Call(); missed.Add($"case {i}: no refusal"); }
            catch (PropagationRefusal e) { if (!e.Message.Contains(cases[i].Says, StringComparison.Ordinal)) missed.Add($"case {i}: '{e.Message}'"); }
        }
        Assert.True(missed.Count == 0, string.Join("\n", missed));
    }

    private static PathLossResult Eval(string model, double fHz, double d, params (string K, string V)[] kv)
        => PropagationModels.Evaluate(new ModelInputs
        {
            Model = model, FrequencyHz = fHz, DistanceM = d, TxHeightM = 10, RxHeightM = 10,
            Values = kv.ToDictionary(x => x.K, x => x.V),
        });

    /// <summary>The availability is converted to a time percentage once: Rain=fixed at 99.9 % equals P.530 at 0.1 %, and
    /// the satellite model's rain at 99 % equals P.618 at 1 %.</summary>
    [Fact]
    public void Availability_IsTheOneTimePercentage()
    {
        var r = Eval("free", 28e9, 5000, ("Rain", "fixed"), ("Availability", "99.9"), ("RainTilt", "90"));
        Assert.Equal(ItuP530.Rain(28, 5, 40, 0.1, 90), r.Terms.Single(t => t.Name.StartsWith("P.530")).Db, 12);
        var s = PropagationModels.Evaluate(new ModelInputs
        {
            Model = "satellite", FrequencyHz = 20e9,
            Values = new Dictionary<string, string> { ["Altitude"] = "35786", ["Elevation"] = "40", ["Rain"] = "fixed", ["Availability"] = "99" },
        });
        Assert.Equal(ItuP618.Rain(20, 40, 45, 1, 0, 3.4, 40, 45), s.Terms.Single(t => t.Name.StartsWith("P.618")).Db, 12);
        Assert.NotNull(s.Geometry);
    }
}
