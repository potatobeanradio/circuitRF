// ================================================================
//  SignalQualityPreset.cs  —  what an envelope run did to a modulated signal, in one action (brief NZ-8)
//
//  Three plots of an envelope result that measured EVM: the constellation (the received points over
//  the ideal ones, Im against Re on one scale), the EVM per carrier as a table (with MER, the peak, and
//  with EvmSplit the distortion / noise split and the SNR), and the output spectrum. Each is read at
//  one IProbe — the first whose EVM is a number, which is the probe carrying the signal — and one
//  carrier, the first with an EVM. Every trace is an ordinary cube trace the user can re-point
//  afterwards; nothing here computes a number.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class SignalQualityPreset
{
    /// <summary>The preset's name, as the toolbar and the menu say it.</summary>
    public const string Title = "Signal quality";

    /// <summary>The metrics the table shows, those the result holds, in this order, with their column headers.</summary>
    private static readonly (string Cube, string Header)[] Metrics =
    [
        ("EVM_pct", "EVM %"), ("MER_dB", "MER dB"), ("EVMpeak_pct", "EVM peak %"),
        ("EVMdist_pct", "Distortion %"), ("EVMnoise_pct", "Noise %"),
        ("FSKerr_pct", "FSK error %"), ("FreqDev_Hz", "Deviation Hz"), ("FreqOffset_Hz", "Offset Hz"),
        ("FSKerrDist_pct", "FSK dist. %"), ("FSKerrNoise_pct", "FSK noise %"),
        ("CSSerr_pct", "CSS error %"), ("CSSerrDist_pct", "CSS dist. %"), ("CSSerrNoise_pct", "CSS noise %"),
        ("SNR_dB", "SNR dB"),
    ];

    /// <summary>The error cubes, one per family: a result holding any of them gets the preset.</summary>
    private static readonly string[] Errors = ["EVM_pct", "FSKerr_pct", "CSSerr_pct"];

    /// <summary>The gap the preset leaves between its plots, logical px.</summary>
    public const double Gap = 24;

    /// <summary>The analysis group of <paramref name="ds"/> holding an envelope run's signal quality (EVM, the FSK error or
    /// the de-chirped error). Null when there is none.</summary>
    public static string? GroupOf(DataSet? ds)
    {
        if (ds is null) return null;
        foreach (var g in ds.Groups)
            if (Errors.Any(ds.CubesIn(g).ContainsKey)) return g;
        return null;
    }

    /// <summary>True when <paramref name="ds"/> holds an envelope run's signal quality.</summary>
    public static bool Available(DataSet? ds) => GroupOf(ds) is not null;

    /// <summary>
    /// The constellation, the EVM table and the spectrum of <paramref name="ds"/>'s envelope result, each trace reading
    /// <paramref name="sourceRef"/>, laid out from (0, 0): the constellation and the spectrum side by side, the table under
    /// them. Empty when there is no EVM.
    /// </summary>
    public static IReadOnlyList<PlotContainerConfig> Build(DataSet ds, string sourceRef)
    {
        if (GroupOf(ds) is not { } g) return [];
        var cubes = ds.CubesIn(g);
        string q = string.IsNullOrEmpty(g) ? "" : g + ".";
        // the probe and carrier shown: the first a family's error is a number at — EVM first, then FSK, then CSS
        var families = Errors.Where(cubes.ContainsKey).Select(k => cubes[k]).ToList();
        var evm = families.FirstOrDefault(f => FirstMeasured(f, f.Rank - 2) is not null) ?? families[0];
        // A parametric sweep prepends its axes: each is read at its first point.
        int lead = evm.Rank - 2;
        string pins = string.Concat(Enumerable.Repeat("0, ", lead));
        var (b, c) = FirstMeasured(evm, lead) ?? (0, 0);
        var plots = new List<PlotContainerConfig>();
        int received = TraceProperties.LineColorOrder[1], ideal = TraceProperties.LineColorOrder[0];
        // the carrier's own name, without the source and frequency the axis label adds ("S1:LTE 5 MHz 64QAM @ 2 GHz")
        string carrier = Label(evm, lead + 1, c);
        int atSign = carrier.IndexOf(" @ ", StringComparison.Ordinal), colon = carrier.IndexOf(':');
        carrier = carrier[(colon + 1)..(atSign > colon ? atSign : carrier.Length)];
        TracePropertiesConfig Dots(int color, MarkerType shape, double size) => new()
        {
            LineEnabled = false, MarkerEnabled = true, MarkerType = shape, MarkerSize = size,
            LineColorIndex = color, MarkerColorIndex = color,
        };

        if (ReferenceEquals(evm, cubes.GetValueOrDefault("EVM_pct")) && cubes.ContainsKey("Const"))
        {
            // ── the constellation: Im against Re, points only, one scale on both axes ──
            string at = $"[{pins}{b}, {c}, :]", refAt = $"[{pins}{c}, :]";
            TraceConfig Points(string cube, string slice, string label, int color, MarkerType shape, double size) => new()
            {
                SourcePath = sourceRef,
                Expression = $"imag({q}{cube}{slice})",
                XSpec      = $"real({q}{cube}{slice})",
                Label      = label,
                Properties = Dots(color, shape, size),
            };
            plots.Add(new PlotContainerConfig
            {
                PlotType = PlotType.Rect, Width = 420, Height = 440,
                CustomTitle = $"Constellation, {carrier}", CustomTitleOn = true,
                CustomXLabel = "In-phase (I)", CustomXLabelOn = true,
                CustomYLabel = "Quadrature (Q)", CustomYLabelOn = true,
                Legend = LegendPlacement.Below,
                Traces =
                [
                    Points("Const", at, "Received", received, MarkerType.Circle, 0.8),
                    Points("ConstRef", refAt, "Ideal", ideal, MarkerType.Cross, 1.6),
                ],
                // a zero window is "missing": the loader autoscales it, then draws it on one scale
                Axes = new AxesConfig { WindowWidth = 0, WindowHeight = 0, WindowSecondaryWidth = 0, WindowSecondaryHeight = 0, EqualAspect = true },
            });
        }
        else if (ReferenceEquals(evm, cubes.GetValueOrDefault("FSKerr_pct")) && cubes.ContainsKey("FreqPts"))
        {
            // ── FSK: the frequency at each bit centre, received over sent; the envelope is constant, so there is no
            // constellation to draw ──
            plots.Add(new PlotContainerConfig
            {
                PlotType = PlotType.Rect, Width = 420, Height = 440,
                CustomTitle = "Frequency at each bit", CustomTitleOn = true,
                CustomXLabel = "Bit", CustomXLabelOn = true,
                CustomYLabel = "Deviation (Hz)", CustomYLabelOn = true,
                Legend = LegendPlacement.Below,
                Traces =
                [
                    new TraceConfig { SourcePath = sourceRef, Expression = $"{q}FreqPts[{pins}{b}, {c}, :]", Label = "Received",
                                      Properties = Dots(received, MarkerType.Circle, 0.8) },
                    new TraceConfig { SourcePath = sourceRef, Expression = $"{q}FreqRef[{pins}{c}, :]", Label = "Sent",
                                      Properties = Dots(ideal, MarkerType.Cross, 1.6) },
                ],
            });
        }

        // ── EVM per carrier: a table, one column per metric the result holds ──
        var table = new List<TraceConfig>();
        foreach (var (m, header) in Metrics.Where(x => cubes.ContainsKey(x.Cube)))
        {
            var cube = cubes[m];
            var slice = new List<AxisSliceConfig>();
            for (int i = 0; i < lead; i++) slice.Add(new AxisSliceConfig { AxisName = cube.Axes[i].Name, Index = 0 });
            slice.Add(new AxisSliceConfig { AxisName = "branch", Index = b, Label = Label(cube, lead, b) });
            slice.Add(new AxisSliceConfig { AxisName = "carrier", Role = AxisRole.KeepAsX });
            int color = TraceProperties.LineColorOrder[table.Count % TraceProperties.LineColorOrder.Length];
            table.Add(new TraceConfig
            {
                SourcePath = sourceRef, CubeName = q + m, CubeSlice = slice, Label = header,
                FormatString = PrecisionFormat.F, MaximumFractionDigits = 3, ColumnWidth = 92,
                Properties = new TracePropertiesConfig { LineColorIndex = color, MarkerColorIndex = color },
            });
        }
        double carrierColumn = Math.Ceiling(Math.Max(140, TableRenderer.FitWidth("carrier", evm.Axes[lead + 1].Labels ?? [], 12, xAxis: true)));
        plots.Add(new PlotContainerConfig
        {
            PlotType = PlotType.Table, Width = carrierColumn + table.Sum(t => t.ColumnWidth), Height = 120,
            CustomTitle = $"Signal quality at {Label(evm, lead, b)}", CustomTitleOn = true,
            FreqColumnWidth = carrierColumn, Traces = table,
        });

        // ── the spectrum at the same probe ──
        if (cubes.ContainsKey("PSD"))
            plots.Add(new PlotContainerConfig
            {
                PlotType = PlotType.Rect, Width = 520, Height = 440,
                CustomTitle = $"Spectrum at {Label(evm, lead, b)}", CustomTitleOn = true,
                CustomYLabel = "PSD (dBm/Hz)", CustomYLabelOn = true,
                Traces =
                [
                    new TraceConfig
                    {
                        SourcePath = sourceRef, Expression = $"{q}PSD[{pins}{b}, :]", Label = "PSD",
                        Properties = new TracePropertiesConfig { LineColorIndex = received, MarkerColorIndex = received },
                    },
                ],
            });
        // the layout: picture | spectrum, the table under both (a CSS result has no picture: spectrum, then the table)
        var pic = plots.FirstOrDefault(p => p.PlotType == PlotType.Rect && p.Traces.Any(t => t.XSpec is not null || t.Expression!.Contains("FreqPts")));
        var tab = plots.First(p => p.PlotType == PlotType.Table);
        var spec = plots.FirstOrDefault(p => p.PlotType == PlotType.Rect && p != pic);
        double x = 0, h = 0;
        foreach (var p in new[] { pic, spec })
        {
            if (p is null) continue;
            p.Left = x; p.Top = 0;
            x += p.Width + Gap; h = Math.Max(h, p.Height);
        }
        tab.Left = 0; tab.Top = h > 0 ? h + Gap : 0;
        return plots;
    }

    /// <summary>The first probe and carrier (after the <paramref name="lead"/> sweep axes, at their first point) whose error
    /// is a number; null when none is.</summary>
    private static (int Branch, int Carrier)? FirstMeasured(DataCube evm, int lead)
    {
        int nb = evm.Axes[lead].Length, nc = evm.Axes[lead + 1].Length;
        for (int b = 0; b < nb; b++)
            for (int c = 0; c < nc; c++)
                if (double.IsFinite(evm.RealValues[b * nc + c])) return (b, c);
        return null;
    }

    private static string Label(DataCube cube, int axis, int index)
        => cube.Axes[axis].Labels is { } l && index < l.Length ? l[index] : index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
