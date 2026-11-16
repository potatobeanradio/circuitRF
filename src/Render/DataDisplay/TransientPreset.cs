// ================================================================
//  TransientPreset.cs  —  a transient run's waveforms, spectrum and measures, in one action
//  (docs/design/transient.md §8, brief TRN-5 R-trn5-3)
//
//  Three plots of a transient result: the recorded voltages against time; the spectrum of the
//  output at the bench's fundamental, read through fft — the power through the output IProbe in
//  dBm when there is one, the output net's voltage in dBV when there is not; and the bench's
//  measures as a table. Every trace is an ordinary trace the user can re-point afterwards; nothing
//  here computes a number. A parametric sweep's axes are read at their first point.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class TransientPreset
{
    /// <summary>The preset's name, as the toolbar and the menu say it.</summary>
    public const string Title = "Transient";

    /// <summary>The gap the preset leaves between its plots, logical px.</summary>
    public const double Gap = 24;

    /// <summary>The most voltages the waveform plot draws, in the order the run recorded them.</summary>
    public const int MaxWaveforms = 12;

    /// <summary>A cube every transient result carries and no other analysis does.</summary>
    private const string Marker = "Tran.AcceptedSteps";

    /// <summary>The metadata cube holding the bench's fundamental (TransientRunner.FundamentalCube).</summary>
    public const string FundamentalCube = "__TranFundamental_Hz";

    /// <summary>The analysis group of <paramref name="ds"/> holding a transient run. Null when there is none.</summary>
    public static string? GroupOf(DataSet? ds)
    {
        if (ds is null) return null;
        foreach (var g in ds.Groups)
            if (ds.CubesIn(g).ContainsKey(Marker) && ds.CubesIn(g).ContainsKey("V")) return g;
        return null;
    }

    /// <summary>True when <paramref name="ds"/> holds a transient run.</summary>
    public static bool Available(DataSet? ds) => GroupOf(ds) is not null;

    /// <summary>The output the spectrum reads: the IProbe or net whose name says "out", else the last one recorded.</summary>
    public static int OutputIndex(IReadOnlyList<string> names)
    {
        for (int i = names.Count - 1; i >= 0; i--)
            if (names[i].Contains("out", StringComparison.OrdinalIgnoreCase)) return i;
        return names.Count - 1;
    }

    /// <summary>
    /// The waveforms, the output's spectrum and the measures table of <paramref name="ds"/>'s transient result, each trace
    /// reading <paramref name="sourceRef"/>, laid out from (0, 0): waveforms and spectrum side by side, the table under them.
    /// Empty when there is no transient result.
    /// </summary>
    public static IReadOnlyList<PlotContainerConfig> Build(DataSet ds, string sourceRef)
    {
        if (GroupOf(ds) is not { } g) return [];
        var cubes = ds.CubesIn(g);
        string q = string.IsNullOrEmpty(g) ? "" : g + ".";
        var v = cubes["V"];
        int tDim = IndexOf(v, "time"), nDim = IndexOf(v, "node");
        if (tDim < 0 || nDim < 0) return [];
        var plots = new List<PlotContainerConfig>();

        // ── the recorded voltages against time ──
        var nodes = v.Axes[nDim].Labels ?? [];
        var waves = new List<TraceConfig>();
        for (int k = 0; k < Math.Min(nodes.Length, MaxWaveforms); k++)
        {
            var slice = new List<AxisSliceConfig>();
            for (int d = 0; d < v.Rank; d++)
                slice.Add(d == tDim ? new AxisSliceConfig { AxisName = "time", Role = AxisRole.KeepAsX }
                        : d == nDim ? new AxisSliceConfig { AxisName = "node", Index = k, Label = nodes[k] }
                        : new AxisSliceConfig { AxisName = v.Axes[d].Name, Index = 0 });
            int color = TraceProperties.LineColorOrder[k % TraceProperties.LineColorOrder.Length];
            waves.Add(new TraceConfig
            {
                SourcePath = sourceRef, CubeName = q + "V", CubeSlice = slice, Label = nodes[k],
                Properties = new TracePropertiesConfig { LineColorIndex = color, MarkerColorIndex = color },
            });
        }
        var wavePlot = new PlotContainerConfig
        {
            PlotType = PlotType.Rect, Width = 560, Height = 400, Left = 0, Top = 0,
            CustomTitle = "Waveforms", CustomTitleOn = true,
            CustomXLabel = "Time (s)", CustomXLabelOn = true,
            CustomYLabel = "Voltage (V)", CustomYLabelOn = true,
            Legend = LegendPlacement.Below, Traces = waves,
        };
        plots.Add(wavePlot);

        // ── the output's spectrum at the fundamental, through fft ──
        // Only when the record holds a whole period of it: fft refuses a shorter one, and the plot would hold nothing but an
        // error (a pulse whose Period is longer than Stop, a 1 kHz tone over 100 us).
        if (cubes.TryGetValue(FundamentalCube, out var fc) && fc.RealValues.Length > 0 && fc.RealValues[0] > 0
            && tDim >= 0 && v.Axes[tDim].Values is { Length: >= 2 } tAxis && tAxis[^1] - tAxis[0] >= (1 - 1e-9) / fc.RealValues[0])
        {
            double f0 = fc.RealValues[0];
            string f0Text = f0.ToString("R", CultureInfo.InvariantCulture);
            string expr, label, yLabel;
            if (cubes.TryGetValue("I", out var i) && cubes.ContainsKey("Vprobe") && IndexOf(i, "branch") is int bDim and >= 0
                && i.Axes[bDim].Labels is { Length: > 0 } probes)
            {
                int b = OutputIndex(probes);
                string at = Tokens(i, "time", ("branch", b));
                expr = $"dBm(fft_power({q}Vprobe{at}, {q}I{at}, {f0Text}))";
                label = $"P({probes[b]})";
                yLabel = "Power (dBm)";
            }
            else
            {
                int n = OutputIndex(nodes);
                expr = $"dB(fft({q}V{Tokens(v, "time", ("node", n))}, {f0Text}))";
                label = $"V({nodes[n]})";
                yLabel = "Voltage (dBV)";
            }
            int color = TraceProperties.LineColorOrder[0];
            plots.Add(new PlotContainerConfig
            {
                PlotType = PlotType.Rect, Width = 420, Height = 400, Left = wavePlot.Width + Gap, Top = 0,
                CustomTitle = $"Spectrum of {label}, last period", CustomTitleOn = true,
                CustomXLabel = $"Harmonic of {Hz(f0)}", CustomXLabelOn = true,
                CustomYLabel = yLabel, CustomYLabelOn = true,
                Traces =
                [
                    new TraceConfig
                    {
                        SourcePath = sourceRef, Expression = expr, Label = label,
                        // a harmonic axis is drawn as stems, as a harmonic-balance spectrum is
                        Properties = new TracePropertiesConfig { LineColorIndex = color, MarkerColorIndex = color },
                    },
                ],
            });
        }

        // ── the bench's measures, the ones that are one number, as a table ──
        if (ds.ContainsGroup(DataSet.MeasurementsGroup))
        {
            var table = new List<TraceConfig>();
            foreach (var (name, cube) in ds.CubesIn(DataSet.MeasurementsGroup))
            {
                if (cube.Rank != 0 || name.StartsWith("__", StringComparison.Ordinal) || name.Contains(':')) continue;
                int color = TraceProperties.LineColorOrder[table.Count % TraceProperties.LineColorOrder.Length];
                table.Add(new TraceConfig
                {
                    SourcePath = sourceRef, CubeName = name, Label = name,
                    FormatString = PrecisionFormat.G, MaximumFractionDigits = 4, ColumnWidth = 110,
                    Properties = new TracePropertiesConfig { LineColorIndex = color, MarkerColorIndex = color },
                });
            }
            if (table.Count > 0)
                plots.Add(new PlotContainerConfig
                {
                    PlotType = PlotType.Table, Width = Math.Max(240, table.Sum(t => t.ColumnWidth) + 8), Height = 90,
                    Left = 0, Top = wavePlot.Height + Gap,
                    CustomTitle = "Measures", CustomTitleOn = true, Traces = table,
                });
        }
        return plots;
    }

    private static int IndexOf(DataCube c, string axis)
    {
        for (int d = 0; d < c.Rank; d++) if (c.Axes[d].Name == axis) return d;
        return -1;
    }

    /// <summary>A bracket slice of <paramref name="c"/>: <paramref name="keep"/> whole, <paramref name="pin"/> at its index, every
    /// other axis (a sweep's) at its first point — <c>[0, :, 2]</c>.</summary>
    private static string Tokens(DataCube c, string keep, (string Axis, int Index) pin)
        => "[" + string.Join(", ", c.Axes.Select(a => a.Name == keep ? ":" : a.Name == pin.Axis
               ? pin.Index.ToString(CultureInfo.InvariantCulture) : "0")) + "]";

    private static string Hz(double f) => f switch
    {
        >= 1e9 => (f / 1e9).ToString("G4", CultureInfo.InvariantCulture) + " GHz",
        >= 1e6 => (f / 1e6).ToString("G4", CultureInfo.InvariantCulture) + " MHz",
        >= 1e3 => (f / 1e3).ToString("G4", CultureInfo.InvariantCulture) + " kHz",
        _ => f.ToString("G4", CultureInfo.InvariantCulture) + " Hz",
    };
}
