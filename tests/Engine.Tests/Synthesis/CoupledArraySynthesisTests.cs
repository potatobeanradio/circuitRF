using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using CircuitRF.Engine.Tests.Devices;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Synthesis;

/// <summary>
/// MCLA's realisers (docs/design/filter-synthesis.md §5): each realisation, written as the part's netlist line from its
/// stored parameters with its site parts on its E pins, run through the engine lossless (the closed forms are), lands on
/// the specification: the centre (the middle of the 3 dB band) within 3 % and the 3 dB band within 20 % of the
/// prototype's, the quasi-static N-line model keeping microstrip's unequal mode speeds, which the TEM design leaves out.
/// A combline's loading capacitors tune each resonator to the centre within 1 %. Every refusal is coded.
/// </summary>
public class CoupledArraySynthesisTests(ITestOutputHelper output)
{
    private const string Board = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=1e15 TanD=0";
    private const string GaAs = "H=100e-6 T=3e-6 Er=12.9 Sigma=1e15 TanD=0";

    private static MicrostripSubstrate Substrate(string line)
    {
        double V(string key) => double.Parse(line.Split(' ').Single(x => x.StartsWith(key + "=")).Split('=')[1], CultureInfo.InvariantCulture);
        return new(V("H"), V("T"), V("Er"), V("Sigma"), V("TanD"));
    }

    /// <summary>The realisation's part on ports 1 and 2, each via site a short and each C site an ordinary capacitor;
    /// <paramref name="shorted"/> grounds the C sites it names in place of their capacitors.</summary>
    private static string Netlist(FilterRealisation r, string sub, Func<SiteValue, bool>? shorted = null)
    {
        bool Grounded(SiteValue s) => s.Role == "Via" || shorted?.Invoke(s) == true;
        string nets = "p1 p2 " + string.Join(" ", r.Sites.Select(s => Grounded(s) ? "0" : s.PinA));
        string loads = string.Concat(r.Sites.Where(s => !Grounded(s)).Select(s => $"C:C{s.PinA} {s.PinA} 0 C={s.Value:R}\n"));
        return "Port:P1 p1 0 Num=1 Z=50 Ohm\nPort:P2 p2 0 Num=2 Z=50 Ohm\n"
             + $"{r.PartType}:A1 {nets} {string.Join(" ", r.Parameters.Select(p => $"{p.Key}={p.Value}"))} {sub}\n{loads}";
    }

    /// <summary>The realised filter's 3 dB centre over <paramref name="f0"/>, and its 3 dB band over the 0.1 dB Chebyshev
    /// prototype's, the ripple band times cosh(arcosh(1/ε)/n).</summary>
    private (double Centre, double Band) Measure(FilterRealisation r, string sub, FilterSpec spec, double f0)
    {
        output.WriteLine(string.Join(" ", r.Parameters.Select(p => $"{p.Key}={p.Value}")));
        double[] freqs = [.. Enumerable.Range(0, 601).Select(i => f0 * (0.6 + 0.8 * i / 600))];
        var db = CoupledArrayTests.S21Db(Netlist(r, sub), freqs);
        var (lo3, hi3) = CoupledArrayTests.Edges(freqs, db, -3.0);
        var peaks = Enumerable.Range(1, db.Length - 2).Where(i => db[i] >= db[i - 1] && db[i] > db[i + 1] && db[i] > -1.0).ToList();
        double ripple = -Enumerable.Range(peaks[0], peaks[^1] - peaks[0] + 1).Min(i => db[i]);
        double eps = Math.Sqrt(Math.Pow(10, spec.RippleDb / 10) - 1);
        double design = (spec.F2Hz - spec.F1Hz) * Math.Cosh(Math.Acosh(1 / eps) / spec.Order);
        output.WriteLine($"3 dB {lo3 / 1e9:F4}-{hi3 / 1e9:F4} GHz against {design / 1e9:F4} GHz wide; ripples to {ripple:F3} dB");
        return ((lo3 + hi3) / 2 / f0, (hi3 - lo3) / design);
    }

    /// <summary>Matthaei's tests' design, now asked for by its specification: three resonators, 0.1 dB, 20 % wide at
    /// 2.4 GHz on 20 mil RO4350B; and the same at 10 GHz on 100 µm GaAs.</summary>
    [Theory]
    [InlineData(Board, 2.4e9)]
    [InlineData(GaAs, 10e9)]
    public void AnInterdigitalRealisation_MeetsItsSpecification(string sub, double f0)
    {
        var spec = new FilterSpec(FilterResponse.Chebyshev, FilterBand.Bandpass, 3, 0.9 * f0, 1.1 * f0);
        var r = new CoupledArrayInterdigitalRealiser().Realise(spec, Substrate(sub));
        Assert.Equal("5", r.Parameters["N"]);
        Assert.Equal(5, r.Sites.Count(s => s.Role == "Via"));
        var (centre, band) = Measure(r, sub, spec, f0);
        Assert.InRange(centre, 0.97, 1.03);
        Assert.InRange(band, 0.8, 1.2);
    }

    /// <summary>
    /// A tapped combline, 0.1 dB, 10 % wide at 2 GHz (and at 10 GHz on GaAs), θ0 = 45°: the filter lands within the
    /// tolerances, and each resonator, alone with every other loaded end grounded (the coupled-resonator definition), peaks
    /// within 1 % of the centre (its port's reflection slowest there): the loading capacitors set it.
    /// </summary>
    [Theory]
    [InlineData(Board, 2e9)]
    [InlineData(GaAs, 10e9)]
    public void AComblineRealisation_MeetsItsSpecification(string sub, double f0)
    {
        var spec = new FilterSpec(FilterResponse.Chebyshev, FilterBand.Bandpass, 3, 0.95 * f0, 1.05 * f0);
        var r = new CoupledArrayComblineRealiser().Realise(spec, Substrate(sub));
        var loads = r.Sites.Where(s => s.Role == "C").ToList();
        Assert.Equal(3, loads.Count);
        var (centre, band) = Measure(r, sub, spec, spec.CentreHz);
        Assert.InRange(centre, 0.97, 1.03);
        Assert.InRange(band, 0.8, 1.2);

        double[] freqs = [.. Enumerable.Range(0, 801).Select(i => spec.CentreHz * (0.9 + 0.2 * i / 800))];
        for (int k = 0; k < loads.Count; k++)
        {
            double peak = Resonance(Netlist(r, sub, s => s.Role == "C" && s != loads[k]), freqs, k == loads.Count - 1 ? 1 : 0);
            output.WriteLine($"{loads[k].PinA} ({loads[k].Value * 1e12:F4} pF) alone resonates at {peak / 1e9:F4} GHz");
            Assert.InRange(peak / spec.CentreHz, 0.99, 1.01);
        }
    }

    /// <summary>Where a port's reflection is slowest, its group delay's peak: the one resonator left's resonance.</summary>
    private static double Resonance(string cnl, double[] f, int port)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var s = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), f)["S"];
        double[] phase = [.. Enumerable.Range(0, f.Length).Select(i => ((Complex)s[i, port, port]).Phase)];
        for (int i = 1; i < phase.Length; i++) phase[i] -= 2 * Math.PI * Math.Round((phase[i] - phase[i - 1]) / (2 * Math.PI));
        return f[Enumerable.Range(1, f.Length - 1).MaxBy(i => phase[i - 1] - phase[i])];
    }

    [Fact]
    public void BothAreFoundByType()
    {
        Assert.Contains(FilterRealisers.All, x => x is CoupledArrayInterdigitalRealiser);
        Assert.Contains(FilterRealisers.All, x => x is CoupledArrayComblineRealiser);
    }

    public static TheoryData<string, IFilterRealiser, FilterSpec> Refused => new()
    {
        { "band", new CoupledArrayInterdigitalRealiser(), new(FilterResponse.Chebyshev, FilterBand.Lowpass, 3, 2e9, 2e9) },
        { "zeros", new CoupledArrayComblineRealiser(), new(FilterResponse.Chebyshev, FilterBand.Bandpass, 3, 1.9e9, 2.1e9, TransmissionZerosHz: [2.3e9]) },
        { "edges", new CoupledArrayInterdigitalRealiser(), new(FilterResponse.Chebyshev, FilterBand.Bandpass, 3, 2.1e9, 1.9e9) },
        { "prototype", new CoupledArrayComblineRealiser(), new(FilterResponse.Elliptic, FilterBand.Bandpass, 3, 1.9e9, 2.1e9) },
        { "band-too-wide", new CoupledArrayInterdigitalRealiser(), new(FilterResponse.Chebyshev, FilterBand.Bandpass, 3, 1e9, 3e9) },
        { "band-too-wide", new CoupledArrayComblineRealiser(), new(FilterResponse.Chebyshev, FilterBand.Bandpass, 3, 1e9, 3e9) },
        { "order", new CoupledArrayComblineRealiser(), new(FilterResponse.Butterworth, FilterBand.Bandpass, 1, 1.9e9, 2.1e9) },
        { "theta", new CoupledArrayComblineRealiser { ThetaDeg = 90 }, new(FilterResponse.Chebyshev, FilterBand.Bandpass, 3, 1.9e9, 2.1e9) },
        { "scale", new CoupledArrayInterdigitalRealiser { H = 10 }, new(FilterResponse.Chebyshev, FilterBand.Bandpass, 3, 2.16e9, 2.64e9) },
        { "scale", new CoupledArrayComblineRealiser(), new(FilterResponse.Chebyshev, FilterBand.Bandpass, 3, 1.56e9, 2.56e9) },
    };

    /// <summary>Each refusal is coded, before any field solve.</summary>
    [Theory]
    [MemberData(nameof(Refused))]
    public void ARefusal_IsCoded(string code, IFilterRealiser realiser, FilterSpec spec)
    {
        var e = Assert.Throws<ParameterRefusalException>(() => realiser.Realise(spec, Substrate(Board)));
        Assert.Equal("synthesis.mcla." + code, e.Diagnostic?.Id);
        if (code != "scale") Assert.False(realiser.Supports(spec, out _));
    }

    [Fact]
    public void NoSubstrate_IsRefused()
        => Assert.Equal("synthesis.mcla.substrate", Assert.Throws<ParameterRefusalException>(() =>
            new CoupledArrayComblineRealiser().Realise(new(FilterResponse.Chebyshev, FilterBand.Bandpass, 3, 1.9e9, 2.1e9), null))
            .Diagnostic?.Id);

    /// <summary>Admittances no strips reach (here a negative mutual, which no gap gives): the fit leaves the range of
    /// strips it walks in, and says so.</summary>
    [Fact]
    public void AnUnreachableCoupling_IsRefused()
        => Assert.Equal("synthesis.mcla.unrealisable", Assert.Throws<ParameterRefusalException>(() =>
            CoupledArraySynthesis.Fit([1.0, 1.0], [-1.0], 50.0, Substrate(Board))).Diagnostic?.Id);
}
