using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Synthesis;

/// <summary>
/// The lumped realiser's MLAD, its sites filled with ideal parts of the values the realisation states (as
/// LadderSitesSParamTests fills its sites), through the engine on 20 mil RO4350B at 200 MHz, where the sites' pads
/// cost about 1 % (docs/design/rf-passives.md §3.16): each −3 dB edge is the ideal filter's (<see cref="FilterNetwork"/>)
/// to that, and an elliptic ladder's zeros, its tanks in the line, are where its prototype puts them.
/// </summary>
public class LumpedLadderRealiserTests(ITestOutputHelper output)
{
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=5.8e7 TanD=0.0037";

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static readonly FilterSpec[] Specs =
    [
        new(FilterResponse.Chebyshev, FilterBand.Lowpass, 5, 200e6, 200e6, 0.1),
        new(FilterResponse.Butterworth, FilterBand.Highpass, 3, 200e6, 200e6),
        new(FilterResponse.Butterworth, FilterBand.Bandpass, 3, 160e6, 250e6),
        new(FilterResponse.Elliptic, FilterBand.Lowpass, 5, 200e6, 200e6, 0.1, 40.0),
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TheLadderFilledWithItsSiteValues_HasTheIdealEdges(int which)
    {
        var spec = Specs[which];
        var r = new LumpedLadderRealiser().Realise(spec, null);
        string Net(string? pin) => pin is null ? "0" : $"g{pin[1..]}";
        var b = new StringBuilder("""
            Port:P1 p1 0 Num=1 Z=50 Ohm
            Port:P2 p2 0 Num=2 Z=50 Ohm
            """).AppendLine();
        int pins = r.Sites.Max(s => int.Parse((s.PinB ?? s.PinA)[1..], CultureInfo.InvariantCulture));
        b.AppendLine($"MLAD:X1 p1 p2{string.Concat(Enumerable.Range(1, pins).Select(k => $" g{k}"))} "
                     + $"Sections=\"{r.Parameters["Sections"]}\" Z0={r.Parameters["Z0"]} F={r.Parameters["F"]} {Sub}");
        int k = 0;
        foreach (var s in r.Sites)
            b.AppendLine($"{s.Role}:{s.Role}{++k} {Net(s.PinA)} {Net(s.PinB)} {s.Role}={F(s.Value)}");
        var (lib, tb) = new CnlReader().Read(b.ToString());
        var circuit = new Elaborator(lib).Elaborate(tb);
        double Db(double f) => 20.0 * Math.Log10(((Complex)SParameterEngine.Run(circuit, [f])["S"][0, 1, 0]).Magnitude);

        var form = spec.Band switch { FilterBand.Lowpass => NetworkForm.Lowpass, FilterBand.Highpass => NetworkForm.Highpass, _ => NetworkForm.Bandpass };
        var ideal = FilterNetwork.Create(spec.Response, form, spec.Order, spec.F1Hz, spec.F1Hz, spec.F2Hz, spec.RippleDb,
            spec.StopAttenuationDb, 0.0);
        double IdealDb(double f) => 20.0 * Math.Log10(ideal.At(2.0 * Math.PI * f).S21.Magnitude);

        // Each edge between a frequency that passes and one that stops; an elliptic lowpass's stops at its first zero.
        var zeros = PrototypeLadder.For(spec.Response, spec.Order, spec.RippleDb, spec.StopAttenuationDb).Omega
            .Where(double.IsFinite).Select(z => z * spec.F1Hz).ToArray();
        (double Pass, double Stop)[] edges = spec.Band switch
        {
            FilterBand.Lowpass => [(0.5 * spec.F1Hz, zeros.Length > 0 ? zeros.Min() : 2.0 * spec.F1Hz)],
            FilterBand.Highpass => [(2.0 * spec.F1Hz, 0.5 * spec.F1Hz)],
            _ => [(spec.CentreHz, 0.6 * spec.F1Hz), (spec.CentreHz, 1.5 * spec.F2Hz)],
        };
        output.WriteLine(r.Parameters["Sections"]);
        foreach (var (pass, stop) in edges)
        {
            double want = Edge(IdealDb, pass, stop), got = Edge(Db, pass, stop);
            output.WriteLine($"−3 dB at {got / 1e6:F3} MHz, ideal {want / 1e6:F3} MHz ({(got / want - 1) * 100:F2} %)");
            Assert.InRange(got / want, 0.98, 1.02);
        }
        foreach (double z in zeros)
        {
            output.WriteLine($"zero at {z / 1e6:F3} MHz: {Db(z):F1} dB");
            Assert.True(Db(z) < -80.0, $"{Db(z):F1} dB at the zero, {z / 1e6:F3} MHz");
        }
    }

    /// <summary>Where |S21| crosses −3 dB between <paramref name="pass"/> and <paramref name="stop"/>, by bisection.</summary>
    private static double Edge(Func<double, double> db, double pass, double stop)
    {
        Assert.True(db(pass) > -3.0 && db(stop) < -3.0, $"{db(pass):F2} dB at {pass}, {db(stop):F2} dB at {stop}");
        while (Math.Abs(stop - pass) > 1e-6 * pass)
        {
            double mid = Math.Sqrt(pass * stop);
            if (db(mid) > -3.0) pass = mid; else stop = mid;
        }
        return Math.Sqrt(pass * stop);
    }
}
