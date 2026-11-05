using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Design.VendorLibraries;

/// <summary>
/// The frequency-range warning (brief vendorlib-3 R-vl3-3): a vendor part is characterised over a
/// stated range, and a run whose analyses leave it gets one sentence per part number saying so.
///
/// <para><b>A warning, never a refusal, and never a panel note.</b> The run goes ahead and its exit
/// code is unchanged; the sentence travels with the extraction's results, so the GUI posts it to
/// Messages and the CLI writes it to stderr.</para>
///
/// <para><b>0 Hz is exempt</b>: a DC solve of a passive R/L/C network is exact whatever range the
/// part was measured over. A part whose header states no range gets no warning.</para>
///
/// <para><b>What a run reaches</b> is read from the analyses' own fields, at the design's nominal
/// globals: an S-parameter sweep's points, a harmonic-balance or loadpull tone times its highest
/// harmonic (and a small-signal sweep's stop), a fields analysis's frequency. A field that does not
/// evaluate contributes nothing — the run itself reports it — and a parametric sweep's axis is not
/// walked, so a swept frequency is judged at its nominal value.</para>
/// </summary>
public static class VendorPartRange
{
    /// <summary>The lowest non-zero and the highest frequency the enabled analyses of
    /// <paramref name="tb"/> reach, in hertz, or null when none reaches one above 0 Hz.</summary>
    public static (double Low, double High)? Reach(TestBench tb)
    {
        ArgumentNullException.ThrowIfNull(tb);
        var (globals, withUnit) = Globals(tb);
        double lo = double.PositiveInfinity, hi = 0;

        void Add(double f)
        {
            if (!double.IsFinite(f) || f <= 0) return;
            lo = Math.Min(lo, f);
            hi = Math.Max(hi, f);
        }

        foreach (var a in tb.Analyses)
        {
            if (!a.Enabled) continue;
            try
            {
                switch (a)
                {
                    case SParameterAnalysis sp:
                        foreach (var f in sp.Expand(globals, withUnit)) Add(f);
                        break;
                    case HarmonicBalanceAnalysis hb:
                    {
                        int k = Int(hb.MaxHarmonicExpr, globals, 1);
                        var tones = hb.ToneExprs.Length > 0
                            ? hb.ToneExprs.Select((t, i) => (t, i < hb.ToneUnits.Length ? hb.ToneUnits[i] : "Hz"))
                            : [(hb.ToneExpr, hb.ToneUnit)];
                        foreach (var (t, u) in tones)
                        {
                            double f0 = FreqUnit.ResolveHz(t, u, globals, withUnit);
                            Add(f0);
                            Add(f0 * k);
                        }
                        if (hb.SsSweep() is { } ss)
                            foreach (var f in ss.Expand(globals, withUnit)) Add(f);
                        break;
                    }
                    case LoadpullAnalysis lp:
                        AddTone(lp.ToneExpr, lp.ToneUnit, lp.MaxHarmonicExpr);
                        break;
                    case LoadpullPursuitAnalysis lpp:
                        AddTone(lpp.ToneExpr, lpp.ToneUnit, lpp.MaxHarmonicExpr);
                        break;
                    // freq=all: the frequencies are the run's, in a record this directive does not read; its sweep is the band
                    // the block was solved over, and a part's range is judged by the analyses that name a frequency.
                    case FieldsAnalysis { AtEveryFrequency: true }:
                        break;
                    case FieldsAnalysis fa when fa.FreqExpr.Trim().Length > 0:
                        Add(FreqUnit.ResolveHz(fa.FreqExpr, fa.FreqUnit, globals, withUnit));
                        break;
                }
            }
            catch { /* a field that does not evaluate is the run's to report, not this warning's */ }
        }

        return hi > 0 ? (lo, hi) : null;

        void AddTone(string tone, string unit, string maxHarm)
        {
            double f0 = FreqUnit.ResolveHz(tone, unit, globals, withUnit);
            Add(f0);
            Add(f0 * Int(maxHarm, globals, 1));
        }
    }

    /// <summary>
    /// One warning per part number among <paramref name="parts"/> whose stated range the analyses of
    /// <paramref name="tb"/> leave, in the order the parts are given.
    /// </summary>
    public static IReadOnlyList<string> Warnings(TestBench tb, IEnumerable<VendorLibraryEntry> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var list = parts.ToList();
        if (list.Count == 0) return [];
        var reached = Reach(tb);
        // A MODEL is not exempt at 0 Hz unless it says it holds there (brief vendorlib-9 R-vl9-3d): a model built on
        // S-parameters measured from 100 MHz has no exact DC answer the way a passive R/L/C network has.
        bool dc = list.Any(e => e.IsModel) && ReachesDc(tb);
        if (reached is null && !dc) return [];
        var reach = reached ?? (double.PositiveInfinity, 0.0);

        var said = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var e in list)
        {
            if (e.Header.FrequencyLowHz is not { } low || e.Header.FrequencyHighHz is not { } high) continue;
            if (!said.Add(e.Part)) continue;

            // A hair of slack, so a sweep that stops exactly at the stated end is not "outside" it
            // through rounding in the unit conversion.
            const double slack = 1e-9;
            var beyond = new List<double>();
            if (e.IsModel && dc && low > 0) beyond.Add(0);
            if (reached is not null && reach.Low < low * (1 - slack)) beyond.Add(reach.Low);
            if (reached is not null && reach.High > high * (1 + slack)) beyond.Add(reach.High);
            if (beyond.Count == 0) continue;

            warnings.Add($"{e.Part} is characterised from {Hz(low)} to {Hz(high)}; this run reaches " +
                         string.Join(" and ", beyond.Select(Hz)) + ".");
        }
        return warnings;
    }

    /// <summary>
    /// Whether an enabled analysis of <paramref name="tb"/> solves at 0 Hz: a DC operating point, a harmonic-balance,
    /// loadpull or envelope run (each solves its DC term), or an S-parameter sweep holding a 0 Hz point.
    /// </summary>
    public static bool ReachesDc(TestBench tb)
    {
        ArgumentNullException.ThrowIfNull(tb);
        var (globals, withUnit) = Globals(tb);
        foreach (var a in tb.Analyses)
        {
            if (!a.Enabled) continue;
            switch (a)
            {
                case DcAnalysis or HarmonicBalanceAnalysis or LoadpullAnalysis or LoadpullPursuitAnalysis or EnvelopeAnalysis:
                    return true;
                case SParameterAnalysis sp:
                    try { if (sp.Expand(globals, withUnit).Any(f => f == 0)) return true; }
                    catch { /* the run reports it */ }
                    break;
            }
        }
        return false;
    }

    private static string Hz(double f)
    {
        if (f == 0) return "0 Hz (DC)";
        (double Scale, string Prefix)[] steps = [(1e9, "G"), (1e6, "M"), (1e3, "k"), (1, "")];
        foreach (var (scale, prefix) in steps)
            if (f >= scale * 0.9995)
                return (f / scale).ToString("0.###", CultureInfo.InvariantCulture) + " " + prefix + "Hz";
        return f.ToString("0.###", CultureInfo.InvariantCulture) + " Hz";
    }

    private static int Int(string expr, IReadOnlyDictionary<string, Value> globals, int fallback)
    {
        if (int.TryParse(expr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return Math.Max(1, n);
        try
        {
            var scope = new Scope("range");
            foreach (var (k, v) in globals)
                if (v.Kind == ValueKind.Real) scope.Bind(k, v.AsReal().ToString("R", CultureInfo.InvariantCulture));
            return Math.Max(1, (int)Math.Round(new Evaluator().Eval(expr, scope).AsReal()));
        }
        catch { return fallback; }
    }

    /// <summary>The design's global variables resolved at their nominal values, and which carry a
    /// unit — what <see cref="FreqUnit.ResolveHz"/>'s var-unit-wins rule needs.</summary>
    private static (Dictionary<string, Value>, HashSet<string>) Globals(TestBench tb)
    {
        var scope = new Scope("globals");
        var withUnit = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in tb.GlobalVariables)
        {
            scope.Bind(v.Name, v.Expression, string.IsNullOrEmpty(v.Unit) ? null : v.Unit);
            if (!string.IsNullOrEmpty(v.Unit)) withUnit.Add(v.Name);
        }

        var evaluator = new Evaluator();
        foreach (var fn in tb.Functions) evaluator.RegisterFunction(fn);

        var values = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var v in tb.GlobalVariables)
        {
            try
            {
                var val = evaluator.Resolve(v.Name, scope);
                if (val.Kind is ValueKind.Real or ValueKind.Complex) values[v.Name] = val;
            }
            catch { /* unresolvable here; a field naming it contributes nothing */ }
        }
        return (values, withUnit);
    }
}
