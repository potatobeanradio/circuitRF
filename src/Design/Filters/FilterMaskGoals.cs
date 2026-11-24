using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Filters;

/// <summary>
/// A filter mask as optimizer goals: |S21| in dB at least −Ap across each passband and at most −As
/// across each stopband, over the bands the Designer checks (<see cref="FilterMask.Bands"/>). The
/// goals read the schematic's S-parameter analysis between its first two ports, so a ladder placed
/// from the Designer, with its parts made tunable, can be tuned back inside the mask as built.
/// </summary>
public static class FilterMaskGoals
{
    /// <summary>What <see cref="For"/> made: the goals, the analysis and ports they read, and what the
    /// caller should be told. <see cref="Refusal"/> is set, and the goals empty, when none can be made.</summary>
    public sealed record Result(IReadOnlyList<OptimizationGoal> Goals, string? Analysis, int InPort, int OutPort,
                                IReadOnlyList<string> Notes, string? Refusal);

    /// <summary>
    /// The goals for <paramref name="mask"/> on a schematic with these analyses and components. The
    /// first enabled S-parameter analysis is read; the drive port is the lowest-numbered port and the
    /// response port the next (1 and 2 when the sheet states them). Each band is clipped to that
    /// analysis's sweep, and a band the sweep does not reach is left out and named in the notes.
    /// </summary>
    public static Result For(FilterMaskSpec mask, IEnumerable<Analysis> analyses, IEnumerable<EditableComponent> components)
    {
        var all = analyses.ToList();
        var sp = all.OfType<SParameterAnalysis>().FirstOrDefault(a => a.Enabled) ?? all.OfType<SParameterAnalysis>().FirstOrDefault();
        if (sp is null)
            return new([], null, 0, 0, [],
                       "The schematic has no S-parameter analysis for the goals to read. Add one that sweeps the filter's bands, then send the mask again.");

        var notes = new List<string>();
        var numbers = components.Where(PortPlacement.IsPort)
            .Select(c => int.TryParse(PortPlacement.NumberText(c), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0)
            .Where(n => n > 0).Distinct().Order().ToList();
        int inPort = 1, outPort = 2;
        if (numbers.Count >= 2 && !(numbers.Contains(1) && numbers.Contains(2)))
        {
            (inPort, outPort) = (numbers[0], numbers[1]);
        }
        else if (numbers.Count < 2)
        {
            notes.Add("The schematic does not have two numbered ports yet; the goals read port 1 to port 2.");
        }
        if (numbers.Count > 2) notes.Add($"The goals read S{outPort}{inPort}; change the ports in each goal if the filter sits between others.");

        var (sweepLo, sweepHi) = SweepExtent(sp);
        return new(Build(mask, sp.Name, inPort, outPort, sweepLo, sweepHi, notes), sp.Name, inPort, outPort, notes, null);
    }

    /// <summary>
    /// The goals for <paramref name="mask"/>, reading <c>dB(analysis.S(out, in))</c>. With a sweep
    /// extent, each band is clipped to it; a band outside it is dropped and named in <paramref name="notes"/>.
    /// </summary>
    public static IReadOnlyList<OptimizationGoal> Build(FilterMaskSpec mask, string analysis, int inPort, int outPort,
                                                        double? sweepLoHz = null, double? sweepHiHz = null,
                                                        List<string>? notes = null)
    {
        var bands = FilterMask.Bands(mask);
        string expr = $"dB({analysis}.S({outPort}, {inPort}))";
        var goals = new List<OptimizationGoal>();
        int stops = bands.Count(b => !b.Pass), passes = bands.Count(b => b.Pass);
        int stopIndex = 0, passIndex = 0;
        foreach (var (pass, lo0, hi0) in bands)
        {
            string name = pass
                ? (passes > 1 ? $"mask_pass{++passIndex}" : "mask_pass")
                : (stops > 1 ? (++stopIndex == 1 ? "mask_stop_lower" : "mask_stop_upper") : "mask_stop");
            double lo = sweepLoHz is { } sl ? Math.Max(lo0, sl) : lo0;
            double hi = sweepHiHz is { } sh ? Math.Min(hi0, sh) : hi0;
            if (!(hi >= lo))
            {
                notes?.Add($"The {(pass ? "passband" : "stopband")} {Hz(lo0)} to {Hz(hi0)} lies outside {analysis}'s sweep, so it has no goal. Widen the sweep to hold it.");
                continue;
            }
            goals.Add(new OptimizationGoal
            {
                Name = name,
                Expression = expr,
                Analysis = analysis,
                Range = new GoalRange { Axis = "freq", Lo = Hz(lo), Hi = Hz(hi) },
                Type = pass ? GoalType.Ge : GoalType.Le,
                Limit = Number(pass ? -mask.ApDb : -mask.AsDb),
            });
        }
        return goals;
    }

    /// <summary>The lowest and highest frequency an S-parameter analysis sweeps, Hz; null where an end is not a plain value.</summary>
    public static (double? Lo, double? Hi) SweepExtent(SParameterAnalysis sp)
    {
        double? lo = null, hi = null;
        foreach (var s in sp.Sweeps)
            foreach (var end in new[] { $"{s.StartExpr.Trim()} {s.StartUnit}".Trim(), $"{s.StopExpr.Trim()} {s.StopUnit}".Trim() })
            {
                if (!TunableValue.TryParse(end, out _, out _, out double si)) return (null, null);
                lo = lo is { } l ? Math.Min(l, si) : si;
                hi = hi is { } h ? Math.Max(h, si) : si;
            }
        return (lo, hi);
    }

    /// <summary>A frequency as value text a goal range reads: <c>1800 MHz</c>, <c>2.4 GHz</c>.</summary>
    public static string Hz(double hz)
    {
        var (scale, unit) = Math.Abs(hz) switch
        {
            >= 1e9 => (1e9, "GHz"),
            >= 1e6 => (1e6, "MHz"),
            >= 1e3 => (1e3, "kHz"),
            _ => (1.0, "Hz"),
        };
        return Number(hz / scale) + " " + unit;
    }

    /// <summary>
    /// The placed ladder's parts as optimizer variables, so the Optimizer can tune the ladder the Designer placed:
    /// a library part as its <c>Part</c> (the parts of the same kind in its own library), an L or C as its value
    /// between half and twice what it was placed at, on the chosen E-series when the values were one. A
    /// component that is neither is left out.
    /// </summary>
    public static IReadOnlyList<TunableEntry> LadderTunables(IEnumerable<EditableComponent> parts, FilterSeriesKind series)
    {
        var entries = new List<TunableEntry>();
        foreach (var c in parts)
        {
            if (c.Symbol == SymbolKind.SpiceModel)
            {
                entries.Add(new TunableEntry { Key = c.InstanceName + ".Part", Opt = true });
                continue;
            }
            string? name = c.Symbol switch { SymbolKind.Inductor => "L", SymbolKind.Capacitor => "C", _ => null };
            if (name is null || c.Parameters.FirstOrDefault(p => p.Name == name) is not { } param) continue;
            if (!double.TryParse(param.Expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !(v > 0))
                continue;
            string unit = param.Unit.Length > 0 ? " " + param.Unit : "";
            entries.Add(new TunableEntry
            {
                Key = c.InstanceName + "." + name,
                Opt = true,
                Min = Number(v / 2) + unit,
                Max = Number(v * 2) + unit,
                Scale = TuneScale.Log,
                Discrete = series switch
                {
                    FilterSeriesKind.E6 => TuneDiscrete.E6,
                    FilterSeriesKind.E12 => TuneDiscrete.E12,
                    FilterSeriesKind.E24 => TuneDiscrete.E24,
                    FilterSeriesKind.E96 => TuneDiscrete.E96,
                    _ => TuneDiscrete.None,
                },
            });
        }
        return entries;
    }

    private static string Number(double v) => Math.Round(v, 9).ToString("0.#########", CultureInfo.InvariantCulture);
}
