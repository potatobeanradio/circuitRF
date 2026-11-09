using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.External;

/// <summary>
/// An external device's own noise sources in the S-parameter noise analysis, through the real OSDI worker and
/// <c>tools/fake-osdi-model</c>'s <c>crf_noise</c>: a conductance g with thermal <c>4kTg</c>, shot <c>2q|g·v|</c> and
/// flicker <c>kf·|g·v|/f</c> between its two terminals. The bench is refs.json's <c>osdi_crf_noise</c>: the device shunts
/// a node X between two 1 F blocks to the ports, fed from Vdc through 1 H, so
/// <c>F = 1 + (T/T0)·g·Z0 + 2q|g·Vdc|·Z0/(4kT0)</c> and every Cs entry is <c>S_i·Z0/(2 + g·Z0)²</c>. Skips with a reason
/// when the worker is not built.
/// </summary>
[Collection("ExternalDeviceRegistry")]
public sealed class OsdiNoiseTests : IDisposable
{
    private const string WorkerRel = "tools/osdi-worker/osdi-worker";
    private const string ModelRel  = "tools/fake-osdi-model/fake_osdi.osdi";
    private const string HowTo     = "run tools/osdi-worker/build.sh (needs a C compiler)";
    private const string Provider  = "OsdiNoise";
    private const double Z0 = 50, Q = 1.602176634e-19, K = NoiseMetrics.Boltzmann;

    private readonly ITestOutputHelper _output;

    public OsdiNoiseTests(ITestOutputHelper output)
    {
        _output = output;
        ExternalDeviceRegistry.Clear();
        ExternalDeviceRegistry.Register(DeviceWorkerProvider.Launch(Provider, Locate(WorkerRel), [Locate(ModelRel)]));
    }

    public void Dispose() => ExternalDeviceRegistry.Clear();

    private static string Locate(string relative)
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new InvalidOperationException($"fixture '{relative}' not found");
    }

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The osdi_crf_noise bench at 1 and 2 GHz, the device stated by <paramref name="device"/>.</summary>
    private static (DataSet Ds, ElaboratedNetlist Nl) Run(double tempC, double vdc, string device)
    {
        var (lib, tb) = new CnlReader().Read($"""
            temp = {N(tempC)}
            Port:P1  p1 0  Num=1 Z=50 Ohm
            Port:P2  p2 0  Num=2 Z=50 Ohm
            C:C1  p1 x  C=1 F
            C:C2  x p2  C=1 F
            L:L1  v x  L=1 H
            Vdc:V1  v 0  Vdc={N(vdc)}
            ExtDevice:X1  x 0  Provider={Provider} {device}
            analysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz Noise=yes
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        var spa = tb.Analyses.OfType<SParameterAnalysis>().First();
        var ds = SParameterEngine.Run(nl, spa.Expand(nl.ResolvedGlobals), AnalysisSettings.Default.WithNoise(true));
        return (ds, nl);
    }

    /// <summary>The device's current noise density for the closed form, A²/Hz.</summary>
    private static double Si(double g, double vdc, double kelvin, double kf, double hz) =>
        4 * K * kelvin * g + 2 * Q * Math.Abs(g * vdc) + kf * Math.Abs(g * vdc) / hz;

    /// <summary>Worst |Cs − S_i·Z0/(2 + gZ0)²| over the four entries, relative to that value, at frequency index fi.</summary>
    private static double CsDeviation(DataSet ds, int fi, double expected)
    {
        double worst = 0;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                worst = Math.Max(worst, (((Complex)ds["Cs"][fi, i, j]) - expected).Magnitude / expected);
        return worst;
    }

    /// <summary>refs.json's points (g, Vdc, T): NF and every Cs entry against the closed form, at its tolerances.</summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void CrfNoise_MatchesTheClosedForm_AtEveryReferencePoint()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(NoiseDir(), "refs.json")));
        var bench = doc.RootElement.GetProperty("benches").GetProperty("osdi_crf_noise");
        double dbTol = bench.GetProperty("tolerance").GetProperty("db_abs").GetDouble();
        double csTol = bench.GetProperty("tolerance").GetProperty("cs_rel").GetDouble();

        double worstNf = 0, worstCs = 0;
        foreach (var pt in bench.GetProperty("points").EnumerateArray())
        {
            double g = pt.GetProperty("g_S").GetDouble(), vdc = pt.GetProperty("vdc_V").GetDouble();
            var (ds, nl) = Run(pt.GetProperty("temp_C").GetDouble(), vdc, $"Type=crf_noise g={N(g)}");
            using (nl)
            {
                var ed = (ExternalDeviceModel)nl.Components.Single(c => c.InstancePath == "X1").Model;
                Assert.Equal(new[] { "thermal", "shot", "flicker" }, ed.Descriptor.NoiseSources.Select(s => s.Name));
                Assert.All(ed.Descriptor.NoiseSources, s => Assert.Equal((0, 1), (s.NodeA, s.NodeB)));
                Assert.DoesNotContain(nl.Warnings, w => w.StartsWith("Noise:", StringComparison.Ordinal));

                for (int fi = 0; fi < 2; fi++)   // white: the 1 GHz reference holds at 2 GHz
                {
                    worstNf = Math.Max(worstNf, Math.Abs(ds["NF"].RealValues[fi] - pt.GetProperty("nf_db").GetDouble()));
                    worstCs = Math.Max(worstCs, CsDeviation(ds, fi, pt.GetProperty("cs_all_entries").GetDouble()));
                }
            }
        }
        _output.WriteLine($"osdi_crf_noise: worst NF {worstNf:E2} dB, worst Cs rel {worstCs:E2}");
        Assert.True(worstNf <= dbTol, $"NF {worstNf:E2} dB");
        Assert.True(worstCs <= csTol, $"Cs {worstCs:E2}");
    }

    /// <summary>With kf above 0 the flicker source is there, and it falls as 1/f.</summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void Flicker_IsOneOverF()
    {
        const double g = 4e-3, vdc = 1, kf = 1e-9;
        var (ds, nl) = Run(26.85, vdc, $"Type=crf_noise g={N(g)} kf={N(kf)}");
        using (nl)
        {
            double worst = 0;
            for (int fi = 0; fi < 2; fi++)
            {
                double hz = (fi + 1) * 1e9;
                worst = Math.Max(worst, CsDeviation(ds, fi, Si(g, vdc, 300, kf, hz) * Z0 / Math.Pow(2 + g * Z0, 2)));
            }
            _output.WriteLine($"flicker: worst Cs rel {worst:E2}");
            Assert.True(worst < 1e-9, $"{worst:E2}");
            double white = Si(g, vdc, 300, 0, 1) * Z0 / Math.Pow(2 + g * Z0, 2);
            Assert.True(((Complex)ds["Cs"][0, 1, 1]).Real - white > 1.9 * (((Complex)ds["Cs"][1, 1, 1]).Real - white),
                        "the flicker part should halve from 1 to 2 GHz");
        }
    }

    /// <summary>m = 2 is two independent devices: the same network and noise as one device of conductance 2g.</summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void Multiplicity_DoublesTheDensity()
    {
        var (two, nl2) = Run(26.85, 0.5, "Type=crf_noise g=1e-3 m=2");
        var (wide, nlw) = Run(26.85, 0.5, "Type=crf_noise g=2e-3");
        using (nl2) using (nlw)
            for (int fi = 0; fi < 2; fi++)
            {
                double expected = Si(2e-3, 0.5, 300, 0, 1) * Z0 / Math.Pow(2 + 2e-3 * Z0, 2);
                Assert.True(CsDeviation(two, fi, expected) < 1e-9, $"m=2: {CsDeviation(two, fi, expected):E2}");
                Assert.True(CsDeviation(wide, fi, expected) < 1e-9, $"2g: {CsDeviation(wide, fi, expected):E2}");
            }
    }

    /// <summary>
    /// Brief NZ-1: the noise table lists the device with the count and names of the sources its model declares; with
    /// Noise=no it is switched off, the key never reaches the provider (which refuses a parameter it does not know), and
    /// the network around it is lossless, so Cs is zero.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void Table_NamesTheDeviceSources_AndNoiseNoQuietsTheDevice()
    {
        var (_, nl) = Run(26.85, 0.5, "Type=crf_noise g=1e-3");
        using (nl)
        {
            var row = SParameterNoise.Table(nl, [1e9]).Single(r => r.Label == "X1");
            Assert.Equal(NoiseModelKind.DeviceSources, row.Kind);
            var ed = (ExternalDeviceModel)nl.Components.Single(c => c.InstancePath == "X1").Model;
            Assert.Equal(ed.NoiseSources.Select(x => x.Name), row.SourceNames);
            Assert.NotEmpty(row.SourceNames!);
        }

        var (quiet, nlq) = Run(26.85, 0.5, "Type=crf_noise g=1e-3 Noise=no");
        using (nlq)
        {
            Assert.Equal(NoiseModelKind.SwitchedOff, SParameterNoise.Table(nlq, [1e9]).Single(r => r.Label == "X1").Kind);
            foreach (Complex c in quiet["Cs"].ComplexValues)
                Assert.True(c.Magnitude < 1e-12 * NoiseMetrics.KT0, $"Cs entry {c}");
        }
    }

    /// <summary>A model that declares no noise sources contributes nothing, and the run names it.</summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void AModelWithNoSources_ContributesNothing_AndIsNamed()
    {
        var (ds, nl) = Run(26.85, 0.5, "Type=crf_rc g0=0.002 c=1e-12");
        using (nl)
        {
            var ed = (ExternalDeviceModel)nl.Components.Single(c => c.InstancePath == "X1").Model;
            Assert.Empty(ed.NoiseSources);
            Assert.Null(ed.ReadNoiseAt([0.5, 0], [1e9]));
            var w = Assert.Single(nl.Warnings, x => x.StartsWith("Noise:", StringComparison.Ordinal));
            Assert.Contains("ExtDevice:X1", w);
            foreach (Complex c in ds["Cs"].ComplexValues)
                Assert.True(c.Magnitude < 1e-12 * NoiseMetrics.KT0, $"Cs entry {c}");
        }
    }

    private static string NoiseDir()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "testdata", "noise"))) return Path.Combine(dir, "testdata", "noise");
        throw new DirectoryNotFoundException("testdata/noise not found above " + AppContext.BaseDirectory);
    }
}
