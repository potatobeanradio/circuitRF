// ================================================================
//  NoiseCirclesPreset.cs  —  one Smith chart of a two-port's source plane: the trade an LNA's
//  input match makes between noise figure, available gain and stability (brief NZ-2)
//
//  Sopt as a marker; three noise circles at NFmin + 0.25, 0.5 and 1 dB; three available-gain
//  circles at max_gain − 1, 2 and 3 dB; and the source stability circle, its unstable side shaded.
//  Every one is an ordinary expression trace on the result's own analysis (ns_circle, ga_circle,
//  stab_circle), drawn at one frequency through the trace card's Freq picker — so a user can edit
//  any of them afterwards, and nothing here computes a number.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class NoiseCirclesPreset
{
    /// <summary>The preset's name, as the toolbar and the plot's title say it.</summary>
    public const string Title = "Noise and gain circles";

    /// <summary>The NF circles, dB above NFmin.</summary>
    public static readonly double[] NoiseSteps = [0.25, 0.5, 1.0];

    /// <summary>The available-gain circles, dB below the maximum gain.</summary>
    public static readonly double[] GainSteps = [1.0, 2.0, 3.0];

    /// <summary>
    /// The analysis group of <paramref name="ds"/> the chart is drawn from: the first with an <c>NF</c> cube (a two-port
    /// <c>Noise=yes</c> S-parameter run) beside its S. Null when there is none.
    /// </summary>
    public static string? GroupOf(DataSet? ds)
    {
        if (ds is null) return null;
        foreach (var g in ds.Groups)
        {
            var cubes = ds.CubesIn(g);
            if (cubes.ContainsKey("NF") && cubes.ContainsKey("NFmin") && cubes.ContainsKey("Sopt")
                && cubes.TryGetValue("S", out var s) && s.Rank >= 3 && s.Axes[^1].Length == 2 && s.Axes[^2].Length == 2)
                return g;
        }
        return null;
    }

    /// <summary>True when <paramref name="ds"/> has a two-port noise result to draw.</summary>
    public static bool Available(DataSet? ds) => GroupOf(ds) is not null;

    /// <summary>
    /// The Smith chart over <paramref name="ds"/>'s noise result, each trace reading <paramref name="sourceRef"/> and drawn
    /// at the middle frequency of the sweep. Null when there is no noise result.
    /// </summary>
    public static PlotContainerConfig? Build(DataSet ds, string sourceRef)
    {
        if (GroupOf(ds) is not { } g) return null;
        string q = string.IsNullOrEmpty(g) ? "" : g + ".";
        string s = q + "S";
        var freq = ds.CubesIn(g)["NF"].Axes.FirstOrDefault(a => a.Name == "freq");
        int k = freq is null ? 0 : freq.Length / 2;

        var traces = new List<TraceConfig>();
        TraceConfig Trace(string expr, string label, int color, LineType line = LineType.Solid, double width = 1.0) => new()
        {
            SourcePath          = sourceRef,
            Expression          = expr,
            ExpressionFreqIndex = k,
            Label               = label,
            Properties          = new TracePropertiesConfig
            {
                LineColorIndex = color, MarkerColorIndex = color, LineType = line, LineWidth = width,
            },
        };

        // Colours from the Data Display's own order: noise, gain and stability each one family.
        int noise = TraceProperties.LineColorOrder[0], gain = TraceProperties.LineColorOrder[1],
            stab = TraceProperties.LineColorOrder[2];

        foreach (double d in NoiseSteps)
            traces.Add(Trace($"ns_circle({s}, {q}NFmin + {Num(d)})", $"NFmin + {Num(d)} dB", noise));
        // The gain and stability circles can run far off the chart — a gain circle of a part that is not unconditionally
        // stable, a stable part's stability circle outside |Γ| = 1 — and only |Γs| ≤ 1 is a passive source, so they do not
        // set the frame; the noise circles and Sopt, which always lie inside it, do.
        foreach (double d in GainSteps)
        {
            var ga = Trace($"ga_circle({s}, max_gain({s}) - {Num(d)})", $"Ga max - {Num(d)} dB", gain, LineType.Dashed);
            ga.ExcludeFromAutoscale = true;
            traces.Add(ga);
        }
        var stability = Trace($"stab_circle({s}, \"source\")", "Source stability", stab, width: 1.5);
        stability.ExcludeFromAutoscale = true;
        traces.Add(stability);

        var sopt = Trace($"{q}Sopt", "Sopt", noise);
        sopt.Properties.LineEnabled   = false;
        sopt.Properties.MarkerEnabled = true;
        sopt.Properties.MarkerType    = MarkerType.Diamond;
        sopt.Properties.MarkerSize    = 2.5;
        traces.Add(sopt);

        return new PlotContainerConfig
        {
            PlotType      = PlotType.Smith,
            Width         = 420,
            Height        = 420,
            CustomTitle   = Title,
            CustomTitleOn = true,
            // The legend names the traces, so the axis is named once, as the plane it is; the X caption stays the
            // frequency the chart is drawn at.
            CustomYLabel  = "Source plane (Γs)", CustomYLabelOn = true,
            Legend        = LegendPlacement.Below,
            Traces        = traces,
        };
    }

    private static string Num(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
