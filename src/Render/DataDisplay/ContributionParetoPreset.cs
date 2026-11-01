// ================================================================
//  ContributionParetoPreset.cs  —  which variables drive a goal, as a
//  Pareto chart (brief-yield-9 R-ya9-5)
//
//  Contributions are never computed unasked (YA-4): the chart is made
//  by an explicit action, which ranks the goal ONCE through
//  ResultContributions.Store — the same ranking the run and the CLI give
//  — and keeps it in the source as `yield.contrib:<goal>` over a
//  labelled `contributor` axis. The plot is then two ordinary traces
//  over that cube: the shares as bars (in percent, largest first, the
//  contributors' names on the X axis) and their running total as a line
//  on the right axis. Nothing here computes a number.
//
//  The bars read the SHOWN shares and the line their renormalized total
//  (owner decision D-a, brief-yield-16): with correlated entries β·r can
//  pass 100 % or fall below 0, which a bar cannot honestly draw. The raw
//  β·r stays in the source beside them.
// ================================================================

using System.Collections.Generic;
using CircuitRF.Design.Statistics;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class ContributionParetoPreset
{
    /// <summary>
    /// Ranks <paramref name="name"/> into <paramref name="ds"/> unless it is already there, and returns the Pareto plot
    /// over it bound to <paramref name="sourceRef"/> — or the refusal's sentence when nothing could be ranked.
    /// <paramref name="stored"/> is true when this call added the cubes (the caller then writes the source back).
    /// <paramref name="group"/> is where the ranking lives: a Monte Carlo's <c>yield</c> (ranked here on request) or a
    /// sensitivity run's <c>sens</c> (brief-sens-1 R-sens1-4), which the run wrote whole and nothing here computes.
    /// <para><paramref name="by"/> picks a sensitivity run's roll-up (brief-sens-2 R-sens2-6): <c>entry</c>,
    /// <c>port</c> or <c>instance</c>, its shares already 0–1 so they are drawn as they stand. A port's shares overlap
    /// (S31 counts toward ports 3 and 1), so a port Pareto has no cumulative line (brief-sens-3 R-sens3-7).</para>
    /// </summary>
    public static (PlotContainerConfig? Plot, string? Refusal) Build(DataSet ds, string name, string sourceRef, out bool stored,
                                                                     string group = ResultContributions.Group, string? by = null)
    {
        stored = false;
        string level = by is null ? "" : $":by={by}";
        string share = $"{group}.{ResultContributions.CubeName(name)}{level}";
        string shown = by is null ? $"{group}.{ResultContributions.ShownName(name)}" : share;
        string? cumulative = by is null ? $"{group}.{ResultContributions.CumulativeName(name)}"
                           : by == "port" ? null : $"{share}:cumulative";
        if (by is not null)
        {
            if (!ds.Contains(share) || (cumulative is not null && !ds.Contains(cumulative)))
                return (null, $"{name} has no {by} ranking in this result's {group} group.");
        }
        else if (!ds.Contains(share) || !ds.Contains(shown) || !ds.Contains(cumulative!))
        {
            if (group != ResultContributions.Group) return (null, $"{name} is not ranked in this result's {group} group.");
            var report = ResultContributions.Store(ds, name);
            if (report.Refusal is { } why) return (null, why.Render());
            if (report.Contributors.Count == 0) return (null, $"Nothing drives {name}: no statistical variable varied.");
            stored = true;
        }

        int count = ds[share].Axes[0].Length;
        var bars = new TraceConfig
        {
            SourcePath = sourceRef,
            Expression = $"100*{shown}",
            Properties = new TracePropertiesConfig
            {
                LineColorIndex = TraceProperties.LineColorOrder[0], MarkerColorIndex = TraceProperties.LineColorOrder[0],
                DrawStyle = TraceDrawStyle.Bars,
            },
        };
        var line = cumulative is null ? null : new TraceConfig
        {
            SourcePath       = sourceRef,
            Expression       = $"100*{cumulative}",
            UseSecondaryAxis = true,
            Properties       = new TracePropertiesConfig
            {
                LineColorIndex = TraceProperties.LineColorOrder[1], MarkerColorIndex = TraceProperties.LineColorOrder[1],
                MarkerEnabled  = true,
            },
        };
        return (new PlotContainerConfig
        {
            PlotType      = PlotType.Rect,
            Width         = System.Math.Max(420, 90 + 70 * count),
            Height        = 300,
            CustomTitle   = by is null ? $"Contributions to {name}" : $"Contributions to {name} by {by}",
            CustomTitleOn = true,
            CustomYLabel  = by == "port" ? "share, overlapping (%)" : "share of variance (%)",
            CustomYLabelOn = true,
            Traces        = line is null ? new List<TraceConfig> { bars } : new List<TraceConfig> { bars, line },
        }, null);
    }

    /// <summary>
    /// A display document of one Pareto per response in <paramref name="responses"/> (a sensitivity run's goals, or its
    /// measures when it ranked no goal) at the <paramref name="by"/> level, stacked in one tab (brief-sens-3 R-sens3-7).
    /// Null when none is ranked at that level.
    /// </summary>
    public static DataDisplayConfig? Document(DataSet ds, string sourceRef, string group, string by, IReadOnlyList<string> responses)
    {
        var plots = new List<PlotContainerConfig>();
        double top = 24;
        foreach (var n in responses)
        {
            // A design with no S-parameter block has no port roll-up: its entries are the whole ranking.
            string level = by == "port" && !ds.Contains($"{group}.{ResultContributions.CubeName(n)}:by=port") ? "entry" : by;
            if (Build(ds, n, sourceRef, out _, group, level).Plot is not { } plot) continue;
            plot.Left = 24;
            plot.Top = top;
            top += plot.Height + 24;
            plots.Add(plot);
        }
        if (plots.Count == 0) return null;
        return new DataDisplayConfig
        {
            FormatVersion      = DataDisplayConfig.CurrentFormatVersion,
            SelectedDataSource = sourceRef,
            Tabs               = [new TabConfig { Name = "Sensitivity", Plots = plots }],
        };
    }
}
