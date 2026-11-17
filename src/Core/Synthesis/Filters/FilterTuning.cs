using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Systems;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Synthesis.Filters;

/// <summary>
/// The optimizer hand-off (docs/design/filter-synthesis.md §6a): for a realised filter placed as one instance, the
/// <c>.cnl</c> lines that let circuitRF's own optimizer close what the closed forms leave. <c>tune</c> lines on the
/// part's stored lengths and gaps (MCRC's S_k, L_k and Tap; MCLA's S, S[k], L and Tap), ±<see cref="GeometryRange"/>,
/// and on the placed site parts, ± their tolerance; MLAD's lengths and widths are inside its <c>Sections</c> text, which
/// a <c>tune</c> key cannot reach, so a ladder tunes through its sites alone. <c>goal</c> lines from the spec, each on
/// an S-parameter analysis of its own written with it; and one <c>optimize</c> line.
/// </summary>
public static partial class FilterTuning
{
    /// <summary>How far each length and gap may move either way, as a fraction of its synthesised value.</summary>
    public const double GeometryRange = 0.2;

    /// <summary>How far past <see cref="FilterSpec.StopAttenuationDb"/> the spec's prototype is where the stop goals
    /// sit. The prototype is the one response that meets a ripple goal and a stop goal at its own edges exactly, and a
    /// distributed filter's skirts are not the prototype's, so goals at those edges leave the optimizer nothing to
    /// trade.</summary>
    public const double StopAllowanceDb = 6.0;

    /// <summary>
    /// The analysis, tune, goal and optimize lines for <paramref name="realisation"/> placed as
    /// <paramref name="instance"/>, with its sites filled by <paramref name="siteParts"/> (instance names in the order of
    /// <see cref="FilterRealisation.Sites"/>; null when none are placed). Ports 1 and 2 are the filter's.
    /// </summary>
    public static IReadOnlyList<string> Lines(FilterSpec spec, FilterRealisation realisation, string instance,
        IReadOnlyList<string>? siteParts = null, double siteTolerance = 0.05)
    {
        var setup = new TuningSetup { Optimizer = new OptimizerSettings { Algorithm = "auto", Seed = 1 } };
        foreach (var (name, text) in realisation.Parameters)
        {
            if (!Geometry().IsMatch(name)) continue;
            if (!Units.TrySplitGluedNumber(text.Trim(), out string number, out string unit)) (number, unit) = (text.Trim(), "");
            if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0)
                setup.Variables.Add(Tune($"{instance}.{name}", v, unit, GeometryRange));
        }
        if (siteParts is not null)
        {
            if (siteParts.Count != realisation.Sites.Count)
                throw Refuse("site-parts", "{n} site parts were named for {count} sites.", ("n", siteParts.Count),
                    ("count", realisation.Sites.Count));
            for (int i = 0; i < siteParts.Count; i++)
            {
                var site = realisation.Sites[i];
                (double scale, string unit) = site.Role switch { "C" => (1e12, " pF"), "L" => (1e9, " nH"), _ => (1.0, " Ohm") };
                if (site.Role is "R" or "L" or "C")
                    setup.Variables.Add(Tune($"{siteParts[i]}.{site.Role}", site.Value * scale, unit, siteTolerance));
            }
        }
        if (setup.Variables.Count == 0)
            throw Refuse("nothing-to-tune", "a {type} has no stored length or gap a tune line can reach"
                + (realisation.PartType == ComponentModelFactory.LadderTypeName ? " (its Sections are text)" : "")
                + " and no site part was named.", ("type", realisation.PartType));

        // The passband edge is the prototype's Ω = 1, but for an inverse Chebyshev, whose Ω = 1 is its stop edge: there
        // it is where the maximally flat passband is RippleDb down.
        var (s11, s21) = Prototype(spec);
        double wp = spec.Response == FilterResponse.InvChebyshev ? Edge(w => Db(s21(w)) >= -spec.RippleDb, 1.0 / 1.01) : 1.0;
        double passDb = Db(s11(wp)), stopDb = spec.StopAttenuationDb;
        var goals = new List<(string Name, double Lo, double Hi, string S, double Limit)>();
        int points = 20 * spec.Order + 1;
        switch (spec.Band)
        {
            case FilterBand.Lowpass:
                goals.Add(("Pass", Freq(spec, 0.05, 1), Freq(spec, wp, 1), "1,1", passDb));
                goals.Add(Stop("StopHi", 1));
                break;
            case FilterBand.Highpass:
                goals.Add(("Pass", Freq(spec, wp, 1), Freq(spec, 0.5, 1), "1,1", passDb));
                goals.Add(Stop("StopLo", 1));
                break;
            case FilterBand.Bandpass:
                goals.Add(("Pass", Freq(spec, wp, -1), Freq(spec, wp, 1), "1,1", passDb));
                goals.Add(Stop("StopLo", -1));
                goals.Add(Stop("StopHi", 1));
                break;
            default:
                goals.Add(("PassLo", Freq(spec, 0.5, -1), Freq(spec, wp, -1), "1,1", passDb));
                goals.Add(("PassHi", Freq(spec, wp, 1), Freq(spec, 0.5, 1), "1,1", passDb));
                goals.Add(Stop("StopLo", -1));
                goals.Add(Stop("StopHi", 1));
                break;
        }
        int z = 0;
        foreach (double fz in spec.TransmissionZerosHz ?? []) goals.Add(($"Zero{++z}", fz, fz, "2,1", -stopDb));

        var lines = new List<string>();
        foreach (var (name, lo, hi, s, limit) in goals)
        {
            lines.Add($"analysis SP{name} type=sparam start={Ghz(lo)} stop={Ghz(hi)} npts={(lo == hi ? 1 : points)} Unit=GHz");
            setup.Goals.Add(new OptimizationGoal
            {
                Name = name, Expression = $"dB(SP{name}.S({s}))", Analysis = "SP" + name, Type = GoalType.Le,
                Limit = limit.ToString("0.###", CultureInfo.InvariantCulture),
            });
        }
        return [.. lines, .. TuningDirectiveText.Write(setup)];

        // A stop goal: |S21| at most −As where the prototype is StopAllowanceDb past it, on one side of the band (a
        // bandpass's prototype frequency is negative below the centre; the other bands' prototypes are symmetric).
        (string, double, double, string, double) Stop(string name, int side)
        {
            int sign = spec.Band == FilterBand.Bandpass ? side : 1;
            double f = Freq(spec, Edge(w => Db(s21(sign * w)) <= -(stopDb + StopAllowanceDb), 1.01), side);
            return (name, f, f, "2,1", -stopDb);
        }
    }

    /// <summary>The first prototype frequency from Ω = 1 on, in steps of <paramref name="step"/>, where
    /// <paramref name="past"/> holds, refined by bisection.</summary>
    private static double Edge(Func<double, bool> past, double step)
    {
        double lo = 1.0, hi = 1.0;
        while (!past(hi))
        {
            (lo, hi) = (hi, hi * step);
            if (hi is > 1e4 or < 1e-4) throw Refuse("unreachable", "the prototype never reaches the attenuation asked for.");
        }
        for (int k = 0; k < 50; k++)
        {
            double m = Math.Sqrt(lo * hi);
            if (past(m)) hi = m; else lo = m;
        }
        return hi;
    }

    // The part's stored lengths and gaps: MCRC's S1 … S(N+1), L1 … LN and Tap; MCLA's S, S[k], L and Tap.
    [GeneratedRegex(@"^(S\d*|S\[\d+\]|L\d*|Tap)$")]
    private static partial Regex Geometry();

    private static TunableEntry Tune(string key, double v, string unit, double fraction) => new()
    {
        Key = key, Opt = true,
        Min = (v * (1 - fraction)).ToString("G9", CultureInfo.InvariantCulture) + unit,
        Max = (v * (1 + fraction)).ToString("G9", CultureInfo.InvariantCulture) + unit,
    };

    /// <summary>S11 and S21 of the spec's lowpass prototype at Ω: the generalised Chebyshev of
    /// <see cref="CouplingMatrix.Characteristic"/> for a bandpass with transmission zeros, else the family's own
    /// (<see cref="FilterPrototype"/>), whose ±1 is the passband edge.</summary>
    private static (Func<double, Complex> S11, Func<double, Complex> S21) Prototype(FilterSpec spec)
    {
        if (spec is { Band: FilterBand.Bandpass, TransmissionZerosHz.Count: > 0 })
        {
            double rl = -10.0 * Math.Log10(1.0 - Math.Pow(10.0, -spec.RippleDb / 10.0));
            var poly = CouplingMatrix.Characteristic(spec.Order, rl,
                [.. spec.TransmissionZerosHz!.Select(f => (f / spec.CentreHz - spec.CentreHz / f) / spec.FractionalBandwidth)]);
            return (w => poly.Response(w).S11, w => poly.Response(w).S21);
        }
        var p = FilterPrototype.Create(spec.Response, spec.Order, spec.RippleDb, spec.StopAttenuationDb);
        return (w => p.At(w).S11, w => p.At(w).S21);
    }

    /// <summary>The frequency at prototype frequency Ω &gt; 0, above the band's centre for <paramref name="side"/> +1 and
    /// below it for −1 (a lowpass's or highpass's one side either way).</summary>
    private static double Freq(FilterSpec spec, double omega, int side)
    {
        double f0 = spec.CentreHz, fbw = spec.FractionalBandwidth;
        double x = spec.Band switch
        {
            FilterBand.Bandpass => omega * fbw / 2.0,
            FilterBand.Bandstop => fbw / (2.0 * omega),
            _ => 0.0,
        };
        return spec.Band switch
        {
            FilterBand.Lowpass => omega * spec.F1Hz,
            FilterBand.Highpass => spec.F1Hz / omega,
            _ => f0 * (Math.Sqrt(x * x + 1.0) + side * x),
        };
    }

    private static double Db(Complex s) => 20.0 * Math.Log10(Math.Max(s.Magnitude, 1e-300));

    private static string Ghz(double hz) => (hz / 1e9).ToString("0.#########", CultureInfo.InvariantCulture);

    private static ParameterRefusalException Refuse(string code, string message, params (string, object?)[] args)
        => new(Diagnostic.Create("synthesis.opt." + code, DiagnosticSeverity.Error, "Filter tuning: " + message, args));
}
