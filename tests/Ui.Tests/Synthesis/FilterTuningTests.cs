using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Engine;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Synthesis;

/// <summary>
/// The optimizer hand-off (docs/design/filter-synthesis.md §6a): an edge-coupled MCRC, detuned (gaps +15 %, lengths
/// −3 %), with <see cref="FilterTuning.Lines"/> appended to its bench, is brought back by the optimizer: its stop goals
/// met and its ripple band centred within 1 %; and each refusal has its code.
/// </summary>
public class FilterTuningTests(ITestOutputHelper output)
{
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=1e20 TanD=0";
    private static readonly MicrostripSubstrate Board = new(0.508e-3, 35e-6, 3.66, 1e20, 0.0);
    private const double F0 = 2.4e9, Fbw = 0.10, Ripple = 0.5;

    private static readonly FilterSpec Spec = new(FilterResponse.Chebyshev, FilterBand.Bandpass, 3,
        F0 * (Math.Sqrt(1 + Fbw * Fbw / 4) - Fbw / 2), F0 * (Math.Sqrt(1 + Fbw * Fbw / 4) + Fbw / 2), Ripple);

    [Fact]
    public void ADetunedEdgeCoupledFilter_IsTunedBackOntoItsBand()
    {
        var r = new EdgeCoupledRealiser().Realise(Spec, Board);
        var detuned = r with
        {
            Parameters = r.Parameters.ToDictionary(p => p.Key, p => p.Key[0] is 'S' or 'L' && char.IsDigit(p.Key[^1])
                ? (Mm(p.Value) * (p.Key[0] == 'S' ? 1.15 : 0.97)).ToString("R", CultureInfo.InvariantCulture) + "mm" : p.Value),
        };
        var lines = FilterTuning.Lines(Spec, detuned, "X1");
        string cnl = Bench(detuned.Parameters) + string.Join("\n", lines);
        output.WriteLine(cnl);

        var run = OptimizationRun.Create(PreparedCircuit.FromText(cnl, null, null));
        Assert.Null(run.Refusal);
        var result = run.Run();
        foreach (var g in result.Goals) output.WriteLine($"{g.Name}: worst {g.WorstValue:F2} at {g.WorstAt / 1e9:F4} GHz, met {g.Met}");
        output.WriteLine($"{result.Evaluations} evaluations, cost {result.BestCost}");
        output.WriteLine($"outcome {result.Outcome}");
        // ponytail: the Pass goal stays unmet, as it is for the undetuned design: the one-width chain's ripple is about
        // 1.6 dB in the model and no gap or length removes it (filter-synthesis.md §6a). Assert GoalsMet once W tunes.
        Assert.All(result.Goals.Where(g => g.Name.StartsWith("Stop", StringComparison.Ordinal)), g => Assert.True(g.Met, g.Name));

        // The best point written back into the part: its ripple band centred on the spec's within 1 %.
        var tuned = r.Parameters.ToDictionary(p => p.Key, p => result.BestValues.TryGetValue("X1." + p.Key, out var v)
            ? v.Replace(" ", "") : p.Value);
        var (lib, tb) = new CnlReader().Read(Bench(tuned));
        var (f1, f2) = RippleEdges(new Elaborator(lib).Elaborate(tb), 0.85 * F0, 1.15 * F0);
        output.WriteLine($"ripple band {f1 / 1e9:F4}-{f2 / 1e9:F4} GHz, centre {((f1 + f2) / 2) / Spec.CentreHz - 1:P2} off");
        Assert.InRange((f1 + f2) / 2 / Spec.CentreHz, 0.99, 1.01);
    }

    [Fact]
    public void EachRefusal_HasItsCode()
    {
        var mcrc = new EdgeCoupledRealiser().Realise(Spec, Board);
        var ladder = new FilterRealisation(ComponentModelFactory.LadderTypeName,
            new Dictionary<string, string> { ["Sections"] = "line(W=1mm, L=5mm)" }, [new SiteValue("C", "G1", null, 1e-12)], []);
        var deep = Spec with { Response = FilterResponse.Butterworth, Order = 1, StopAttenuationDb = 200 };
        Assert.Equal("synthesis.opt.nothing-to-tune", Code(() => FilterTuning.Lines(Spec, ladder, "X1")));
        Assert.Equal("synthesis.opt.site-parts", Code(() => FilterTuning.Lines(Spec, ladder, "X1", ["C1", "C2"])));
        Assert.Equal("synthesis.opt.unreachable", Code(() => FilterTuning.Lines(deep, mcrc, "X1")));
        Assert.Contains("tune C1.C min=0.95 pF max=1.05 pF opt=1", FilterTuning.Lines(Spec, ladder, "X1", ["C1"]));
    }

    private static string Code(Action a) => Assert.Throws<ParameterRefusalException>(a).Diagnostic!.Id;

    private static double Mm(string text) => double.Parse(text[..^2], CultureInfo.InvariantCulture);

    private static string Bench(IReadOnlyDictionary<string, string> parameters) => $"""
        Port:P1 p1 0 Num=1 Z=50 Ohm
        Port:P2 p2 0 Num=2 Z=50 Ohm
        MCRC:X1 p1 p2 {string.Join(" ", parameters.Select(p => $"{p.Key}={p.Value}"))} {Sub}

        """;

    private static double S21Db(ElaboratedNetlist nl, double f)
        => 20.0 * Math.Log10(((Complex)SParameterEngine.Run(nl, [f])["S"][0, 1, 0]).Magnitude);

    /// <summary>The lowest and highest frequencies in [lo, hi] where |S21| is within the ripple of 0 dB, each refined by
    /// bisection (as CoupledResonatorRealiserTests reads a band).</summary>
    private static (double F1, double F2) RippleEdges(ElaboratedNetlist nl, double lo, double hi)
    {
        var f = Enumerable.Range(0, 601).Select(i => lo + (hi - lo) * i / 600.0).ToArray();
        var s = SParameterEngine.Run(nl, f)["S"];
        bool[] inside = [.. Enumerable.Range(0, f.Length).Select(i => 20.0 * Math.Log10(((Complex)s[i, 1, 0]).Magnitude) >= -Ripple)];
        int i1 = Array.IndexOf(inside, true), i2 = Array.LastIndexOf(inside, true);
        Assert.True(i1 > 0 && i2 < f.Length - 1, "no passband inside the sweep");
        double Edge(double a, double b)
        {
            for (int k = 0; k < 40; k++) { double m = 0.5 * (a + b); if (S21Db(nl, m) >= -Ripple) b = m; else a = m; }
            return 0.5 * (a + b);
        }
        return (Edge(f[i1 - 1], f[i1]), Edge(f[i2 + 1], f[i2]));
    }
}
