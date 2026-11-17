// ================================================================
//  HbPreset.cs  —  a harmonic-balance run's waveforms, spectrum and measures, in one action
//  (brief-run-display-hb-preset-and-units R-rdh-2)
//
//  The Transient preset's three plots, read from a harmonic-balance result so the two analyses of one bench look alike:
//  the labelled nets' voltages over one period, rebuilt from the spectrum by waveform(); the output's spectrum as stems —
//  the power through the output IProbe in dBm when there is one, the output net's voltage in dBV when there is not; and
//  the bench's measures as a table. The output is chosen by TransientPreset.OutputIndex, so both presets pick the same
//  net. Every trace is an ordinary trace the user can re-point; nothing here computes a number. A parametric sweep's axes
//  are read at their first point.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class HbPreset
{
    /// <summary>The preset's name, as the toolbar and the menu say it.</summary>
    public const string Title = "Harmonic Balance";

    /// <summary>The gap the preset leaves between its plots, logical px — the Transient preset's.</summary>
    public const double Gap = TransientPreset.Gap;

    /// <summary>The analysis group of <paramref name="ds"/> holding a harmonic-balance run: a complex <c>V</c> over a
    /// <c>harmonic</c> or <c>mixIndex</c> axis beside the run's <c>ToneFreqs</c>. Null when there is none.</summary>
    public static string? GroupOf(DataSet? ds)
    {
        if (ds is null) return null;
        foreach (var g in ds.Groups)
        {
            var cubes = ds.CubesIn(g);
            if (cubes.TryGetValue("V", out var v) && v.DataKind == DataKind.Complex && SpectralDim(v) >= 0
                && cubes.ContainsKey("ToneFreqs"))
                return g;
        }
        return null;
    }

    /// <summary>True when <paramref name="ds"/> holds a harmonic-balance run.</summary>
    public static bool Available(DataSet? ds) => GroupOf(ds) is not null;

    /// <summary>
    /// The waveforms, the output's spectrum and (when <paramref name="includeMeasures"/>) the measures table of
    /// <paramref name="ds"/>'s harmonic-balance result, each trace reading <paramref name="sourceRef"/>, laid out from (0, 0)
    /// as the Transient preset lays out its own. The waveform plot is left out when the tones share no period short enough
    /// to draw (waveform() refuses them), rather than holding nothing but that refusal. Empty when there is no HB result.
    /// </summary>
    public static IReadOnlyList<PlotContainerConfig> Build(DataSet ds, string sourceRef, bool includeMeasures = true)
    {
        if (GroupOf(ds) is not { } g) return [];
        var cubes = ds.CubesIn(g);
        string q = string.IsNullOrEmpty(g) ? "" : g + ".";
        var v = cubes["V"];
        int sDim = SpectralDim(v), nDim = TransientPreset.IndexOf(v, "node");
        if (nDim < 0) return [];
        string spectral = v.Axes[sDim].Name;
        bool oneTone = spectral == "harmonic";
        var tones = cubes["ToneFreqs"].RealValues;
        double f0 = tones.Length > 0 ? tones[0] : 0;
        if (oneTone && !(f0 > 0)) return [];
        string f0Arg = oneTone ? ", " + f0.ToString("R", CultureInfo.InvariantCulture) : "";

        // The nets to draw: the labelled ones, in the order the run recorded them; every named node without that record.
        var nodeNames = v.Axes[nDim].Labels ?? [];
        var labelled = cubes.TryGetValue("__LabeledNodes", out var ln) && ln.Rank == 1 && ln.Axes[0].Labels is { } names
            ? names.Where(n => Array.IndexOf(nodeNames, n) >= 0).ToArray()
            : nodeNames.Where(n => n.Length > 0 && !n.StartsWith("__", StringComparison.Ordinal)).ToArray();
        var plots = new List<PlotContainerConfig>();

        // ── the labelled nets against time, over one period ──
        var waves = new List<TraceConfig>();
        for (int k = 0; k < Math.Min(labelled.Length, TransientPreset.MaxWaveforms); k++)
        {
            int node = Array.IndexOf(nodeNames, labelled[k]);
            int color = TraceProperties.LineColorOrder[k % TraceProperties.LineColorOrder.Length];
            waves.Add(new TraceConfig
            {
                SourcePath = sourceRef, Label = labelled[k],
                Expression = $"waveform({q}V{TransientPreset.Tokens(v, spectral, ("node", node))}{f0Arg})",
                Properties = new TracePropertiesConfig { LineColorIndex = color, MarkerColorIndex = color },
            });
        }
        // the tones may share no period short enough to draw: the plot is left out, as the Transient preset leaves its spectrum out
        bool drawable = waves.Count > 0 && TraceExpression.TryEvaluate(waves[0].Expression!, ds, PlotType.Rect,
            out _, out _, out _, out _, out _, out _, out _);
        PlotContainerConfig? wavePlot = null;
        if (drawable)
        {
            wavePlot = new PlotContainerConfig
            {
                PlotType = PlotType.Rect, Width = 560, Height = 400, Left = 0, Top = 0,
                CustomTitle = "Waveforms", CustomTitleOn = true,
                CustomXLabel = "Time (s)", CustomXLabelOn = true,
                CustomYLabel = "Voltage (V)", CustomYLabelOn = true,
                Legend = LegendPlacement.Below, Traces = waves,
            };
            plots.Add(wavePlot);
        }

        // ── the output's spectrum, as stems ──
        string expr, label, yLabel;
        if (cubes.TryGetValue("Pprobe_W", out var pk) && TransientPreset.IndexOf(pk, "branch") is int bDim and >= 0
            && pk.Axes[bDim].Labels is { Length: > 0 } probes)
        {
            int b = TransientPreset.OutputIndex(probes);
            expr = $"dBm({q}Pprobe_W{TransientPreset.Tokens(pk, spectral, ("branch", b))})";
            label = $"P({probes[b]})";
            yLabel = "Power (dBm)";
        }
        else
        {
            var candidates = labelled.Length > 0 ? labelled : nodeNames;
            if (candidates.Length == 0) return plots;
            string outName = candidates[TransientPreset.OutputIndex(candidates)];
            expr = $"dB({q}V{TransientPreset.Tokens(v, spectral, ("node", Array.IndexOf(nodeNames, outName)))})";
            label = $"V({outName})";
            yLabel = "Voltage (dBV)";
        }
        int c0 = TraceProperties.LineColorOrder[0];
        plots.Add(new PlotContainerConfig
        {
            PlotType = PlotType.Rect, Width = 420, Height = 400, Left = wavePlot is null ? 0 : wavePlot.Width + Gap, Top = 0,
            CustomTitle = $"Spectrum of {label}", CustomTitleOn = true,
            // a harmonic axis counts harmonics, so it says of what; a mixIndex axis is in hertz and labels itself
            CustomXLabel = oneTone ? $"Harmonic of {TransientPreset.Hz(f0)}" : "", CustomXLabelOn = oneTone,
            // a mixIndex axis is drawn in the plot's frequency unit: the one its highest product reads in, not GHz always
            FreqUnit = oneTone ? FreqUnit.GHz : UnitFor(v.Axes[sDim].Values.Select(Math.Abs).DefaultIfEmpty(0).Max()),
            CustomYLabel = yLabel, CustomYLabelOn = true,
            Traces =
            [
                new TraceConfig
                {
                    SourcePath = sourceRef, Expression = expr, Label = label,
                    Properties = new TracePropertiesConfig { LineColorIndex = c0, MarkerColorIndex = c0 },
                },
            ],
        });

        // ── the bench's measures, as the Transient preset's table ──
        if (includeMeasures && TransientPreset.MeasuresTable(ds, sourceRef, 400 + Gap) is { } table) plots.Add(table);
        return plots;
    }

    /// <summary>The frequency unit a span up to <paramref name="fmax"/> Hz reads in: 1 kHz in kHz, 2 GHz in GHz.</summary>
    internal static FreqUnit UnitFor(double fmax) => fmax switch
    {
        >= 1e9 => FreqUnit.GHz,
        >= 1e6 => FreqUnit.MHz,
        >= 1e3 => FreqUnit.kHz,
        _ => FreqUnit.Hz,
    };

    private static int SpectralDim(DataCube c)
    {
        for (int d = 0; d < c.Rank; d++) if (c.Axes[d].Name is "harmonic" or "mixIndex") return d;
        return -1;
    }
}
