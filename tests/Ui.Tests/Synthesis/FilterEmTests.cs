using System.Diagnostics;
using CircuitRF.Cli;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Synthesis;

/// <summary>A realised filter on Planar MoM (docs/design/filter-synthesis.md §6d): the service's refusal of a site no
/// port can close, <c>circuitrf filter --em</c> on an edge-coupled bandpass, and a lumped ladder's sites closed on their
/// ports, each against its circuit model.</summary>
public sealed class FilterEmTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-filter-em-" + Guid.NewGuid().ToString("N")[..12]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private static readonly MicrostripSubstrate Ro4350B = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);

    /// <summary>A jumper has no value to close its port with, and a pin the part does not draw has nowhere to put one:
    /// both are <c>filter.em.sites</c>, refused before anything is meshed.</summary>
    [Fact]
    public void ASiteNoPortCanClose_IsRefused()
    {
        var spec = new FilterSpec(FilterResponse.Butterworth, FilterBand.Lowpass, 3, 1e9, 1e9);
        var lumped = new LumpedLadderRealiser().Realise(spec, Ro4350B);
        Assert.NotEmpty(lumped.Sites);
        foreach (var site in new[] { lumped.Sites[0] with { Role = "Jumper" }, lumped.Sites[0] with { PinA = "G99" } })
        {
            var ex = Assert.Throws<ParameterRefusalException>(() =>
                FilterEmService.Run(spec, lumped with { Sites = [site, .. lumped.Sites.Skip(1)] }, Ro4350B, [1e9]));
            Assert.Equal("filter.em.sites", ex.Diagnostic?.Id);
        }
    }

    /// <summary>
    /// §6a's 0.5 dB Chebyshev of order 3, 2.28 to 2.52 GHz, edge-coupled on the shipped 20 mil RO4350B board: <c>--em</c> solves it
    /// at 11 points on the coarse mesh and writes <c>bp.em.s2p</c>, which passes the band and whose centre is the circuit
    /// model's within 3 %.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheVerbsEm_CentresAnEdgeCoupledBandpass_OnTheCircuitModel()
    {
        string cnl = Path.Combine(_dir, "bp.cnl");
        var clock = Stopwatch.StartNew();
        using (JsonRun.Isolate())
            Assert.Equal(0, FilterVerb.Run(["--band", "bandpass", "--order", "3", "--f1", "2.28GHz", "--f2", "2.52GHz",
                "--ripple", "0.5", "--tech", ShippedTechnologies.DefaultId, "--realiser", "Edge-coupled", "-o", cnl, "--points", "11", "--em"]));
        output.WriteLine($"--em took {clock.Elapsed.TotalSeconds:0.0} s");

        var em = TouchstoneIO.ReadFile(Path.ChangeExtension(cnl, ".em.s2p"));
        Assert.Equal(11, em.FrequencyCount);
        var circuit = FilterSynthesisService.Response(File.ReadAllText(cnl), em.Frequencies, 50.0);
        double emCentre = Centre(em), circuitCentre = Centre(circuit);
        output.WriteLine($"centre: EM {emCentre / 1e9:0.####} GHz, circuit {circuitCentre / 1e9:0.####} GHz");
        Assert.InRange(emCentre / circuitCentre, 0.97, 1.03);
        // And it passes: the ports return through the ground plane, where a series gap's end cell left it at −38 dB.
        Assert.True(em.Matrices.Max(m => 20.0 * Math.Log10(m[1, 0].Magnitude)) > -6.0);
    }

    /// <summary>
    /// A lumped Butterworth lowpass at 1 GHz on MLAD sites: a port on each of its three sites (two shunt, one series gap),
    /// closed with the site's part. The two-port passes 300 MHz and is within 1.5 dB of the circuit model at the cutoff (the
    /// pads and the shunt parts' vias lower it a little).
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void ALumpedLadder_ClosesItsSitePorts_NearTheCircuitModel()
    {
        var spec = new FilterSpec(FilterResponse.Butterworth, FilterBand.Lowpass, 3, 1e9, 1e9);
        var lumped = new LumpedLadderRealiser().Realise(spec, Ro4350B);
        double[] f = [0.3e9, 1e9];
        var clock = Stopwatch.StartNew();
        var run = FilterEmService.Run(spec, lumped, Ro4350B, f);
        output.WriteLine($"EM took {clock.Elapsed.TotalSeconds:0.0} s");

        Assert.Equal(["G1", "G2-G3", "G4"], run.SitePorts);
        Assert.Equal(5, run.Raw!.Ports);
        var circuit = FilterSynthesisService.Response(FilterSynthesisService.Cnl(spec, lumped, "", Ro4350B, null, 2, []), f, spec.Z0);
        static double Db(SNP s, int i) => 20.0 * Math.Log10(s.Matrices[i][1, 0].Magnitude);
        output.WriteLine($"|S21| at 1 GHz: EM {Db(run.S, 1):0.00} dB, circuit {Db(circuit, 1):0.00} dB");
        Assert.InRange(Db(run.S, 0), -0.2, 0.05);
        Assert.InRange(Db(run.S, 1) - Db(circuit, 1), -1.5, 1.5);
    }

    /// <summary>The |S21|²-weighted mean frequency: the band's centre on a coarse grid, without reading edges off it.</summary>
    private static double Centre(SNP s)
    {
        var w = s.Matrices.Select(m => m[1, 0].Magnitude * m[1, 0].Magnitude).ToArray();
        return s.Frequencies.Select((f, i) => f * w[i]).Sum() / w.Sum();
    }
}
