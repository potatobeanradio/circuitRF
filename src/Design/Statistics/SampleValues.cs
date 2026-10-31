using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Optimization;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// One trial's values (docs/design/yield.md §6).
/// </summary>
/// <param name="Trial">The trial number, from 1.</param>
/// <param name="Values">Value key → the text a schematic would hold — what the evaluation's tuned values
/// are. A complex value is WHOLE, composed from its drawn parts in the form the schematic wrote it.</param>
/// <param name="Draws">Entry key → the drawn number in the entry's own unit (a phase in degrees) — what
/// a run records per trial.</param>
/// <param name="Refusal">Why this trial does not evaluate (a non-physical draw); null when it does.
/// <paramref name="Draws"/> still holds every draw that was made.</param>
public sealed record SampledValues(
    int                                 Trial,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, double> Draws,
    Diagnostic?                         Refusal)
{
    public bool Refused => Refusal is not null;
}

/// <summary>
/// Samples → values (yield overview D2, D5): each statistical entry's distribution, resolved against
/// the nominal it is GIVEN — so a percent spread moves with a moved nominal and an absolute one keeps
/// its width — evaluated at the sample's z. A whole-number value is rounded. A draw that is not
/// physical (≤ 0 where the value cannot be) refuses the TRIAL; it is never clamped, because a clamp
/// would pile probability on the boundary and report a yield of a distribution nobody wrote.
/// </summary>
public static class SampleValues
{
    /// <param name="entries">The setup's entries; only the statistical ones are drawn.</param>
    /// <param name="nominals">The values the spreads are relative to — the catalog's tunables, or
    /// <see cref="Nominals"/> of them at a moved design.</param>
    /// <param name="sigmaScale">Multiplies every spread (<see cref="ResolvedSpread.Marginal"/>).</param>
    public static SampledValues Apply(
        IReadOnlyList<TunableEntry> entries, IReadOnlyCollection<Tunable> nominals, StatisticalSample sample,
        double sigmaScale = 1)
    {
        var byKey  = new Dictionary<string, Tunable>(StringComparer.Ordinal);
        foreach (var t in nominals) byKey.TryAdd(t.Key, t);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var draws  = new Dictionary<string, double>(StringComparer.Ordinal);
        var parts  = new Dictionary<string, (Tunable Any, Dictionary<ComplexPart, double> Parts)>(StringComparer.Ordinal);
        Diagnostic? refusal = null;

        foreach (var e in entries.Where(e => e.IsStatistical))
        {
            if (!byKey.TryGetValue(e.Key, out var t)) { refusal ??= StatisticsDiagnostics.TrialNoNominal(e.Key); continue; }
            if (!sample.Z.TryGetValue(e.Key, out double z)) { refusal ??= StatisticsDiagnostics.TrialNoDraw(e.Key); continue; }
            if (ResolvedSpread.Of(e, t) is not { } spread || spread.Marginal(sigmaScale) is not { } marginal)
            { refusal ??= StatisticsDiagnostics.TrialNoDistribution(e.Key); continue; }

            double scale = t.Unit.Length == 0 ? 1 : Units.Scale(t.Unit) ?? 1;
            double x = marginal.FromNormal(z) / scale;
            if (t.IsInteger) x = Math.Round(x, MidpointRounding.AwayFromZero);
            draws[e.Key] = x;

            if (x <= 0 && StatisticsValidator.MustBePositive(t, spread.NominalSi))
            {
                refusal ??= StatisticsDiagnostics.TrialNonPhysical(e.Key, TunableValue.Format(x, t.Unit, "G6"));
                continue;
            }

            if (t.Part is { } part && t.WholeKey is { } whole)
            {
                if (!parts.TryGetValue(whole, out var p)) parts[whole] = p = (t, []);
                p.Parts[part] = x;
            }
            else values[e.Key] = TunableValue.Format(x, t.Unit);
        }

        foreach (var (whole, (t, drawn)) in parts)
        {
            if (ComplexValue.Compose(t.Whole, drawn) is { } z)
                values[whole] = ComplexValue.Format(z, t.WholeUnit, t.Form, "G15");
            else refusal ??= StatisticsDiagnostics.TrialNoComplexValue(whole);
        }
        return new SampledValues(sample.Trial, values, draws, refusal);
    }

    /// <summary>
    /// The catalog's tunables at a MOVED design — the nominals a centred or tuned design draws around.
    /// <paramref name="moved"/> maps a value key to its text, as a preset or the optimizer's best point
    /// writes it: a real value by its own key, a complex value WHOLE by its key, and every part of it
    /// then reads the new value. Keys it does not name keep the design's value.
    /// </summary>
    public static IReadOnlyList<Tunable> Nominals(TunableCatalog catalog, IReadOnlyDictionary<string, string>? moved = null)
    {
        if (moved is null || moved.Count == 0) return catalog.Tunables;
        var result = new List<Tunable>(catalog.Tunables.Count);
        foreach (var t in catalog.Tunables)
        {
            if (t.Part is { } part && t.WholeKey is { } whole)
            {
                if (moved.TryGetValue(whole, out var text) && ComplexValue.TryParse(text, out var z, out string unit, out _))
                {
                    // A whole written in another unit of the same kind is rescaled to the design's.
                    double from = unit.Length == 0 ? 1 : Units.Scale(unit) ?? 1;
                    double to   = t.WholeUnit.Length == 0 ? 1 : Units.Scale(t.WholeUnit) ?? 1;
                    z *= from / to;
                    double v = ComplexValue.Get(z, part, t.Value);
                    result.Add(t with { Whole = z, Value = v, ValueText = TunableValue.Format(v, t.Unit, "G6") });
                    continue;
                }
            }
            else if (t.ValueOf is { } vendor && moved.TryGetValue(vendor.Key, out var chosen)
                     && vendor.CandidateOfCell(chosen.Trim()) is { Value: > 0 } candidate)
            {
                // A part moved to a candidate draws around THAT part's value with THAT part's tolerance (brief
                // vendorlib-5 R-vl5-3d).
                double inUnit = candidate.Value!.Value / (Units.Scale(t.Unit) ?? 1);
                result.Add(t with { Value = inUnit, ValueText = TunableValue.Format(inUnit, t.Unit, "G6"), PartEntry = candidate.Entry });
                continue;
            }
            else if (moved.TryGetValue(t.Key, out var text) && TunableValue.InUnit(text, t.Unit) is { } v)
            {
                result.Add(t with { Value = v, ValueText = text });
                continue;
            }
            result.Add(t);
        }
        return result;
    }
}
