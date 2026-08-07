// ================================================================
//  HarmonicaTracePicker.cs  —  M2 of brief-harmonicarf-h7
//
//  R-h7-5  the picker plots CUBES, through the EXISTING parser (CubeTraceSpecParser) and the existing
//          trace machinery (Trace.SetCubeData). harmonicaRF's job is to hand them
//          HarmonicaDataSet.Build's output — not to write a second slicer.
//  R-h7-6  the DataSet a picker sees is the one the panels drew from: HarmonicaFrame.Published.
//  R-h7-7  a picked trace is part of the document and survives a reload.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Renderers;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// One trace the user picked over harmonicaRF's own <c>DataSet</c>, as it lives in the <c>.charm</c>.
/// </summary>
/// <param name="Spec">
/// The cube shorthand, exactly as <see cref="CubeTraceSpecParser"/> parses it —
/// <c>mag(Gamma_intr[1, :])</c>, <c>Zs_conv[2, 1]</c>, <c>db20 V[0, :]</c>. Stored verbatim so the
/// document records what the user asked for rather than a re-derivation of it.
/// </param>
/// <param name="PanelId">
/// Which layout panel it draws into. Distinct per trace, so Edit Display can move each independently
/// and <see cref="CircuitRF.Harmonica.CharmLayout"/> can carry its placement like any other panel.
/// </param>
/// <param name="Label">A user-visible title, or null to use the spec.</param>
public sealed record HarmonicaPickedTrace(string Spec, string PanelId, string? Label = null)
{
    /// <summary>The panel-id prefix every picked trace uses. Reserved — no §7.1 panel starts with it,
    /// so a placement can always be attributed to one side or the other.</summary>
    public const string PanelPrefix = "trace.";

    public string Title => Label is { Length: > 0 } l ? l : Spec;
}

/// <summary>
/// §7.7's "plots anything harmonicaRF solved", over the §5 <c>DataSet</c>.
///
/// <para><b>Nothing here parses or slices.</b> <see cref="CubeTraceSpecParser"/> reads the spec and
/// <see cref="PlotInspectorViewModel.SetCubeDataFrom"/> does the slicing — the same two calls the
/// <c>.cdd</c> trace card makes. What this type adds is the harmonicaRF-specific part: which cubes are
/// worth offering, and what a sensible default spec for each one is.</para>
/// </summary>
public static class HarmonicaTracePicker
{
    /// <summary>One offer in the picker: a cube, and a spec that plots something useful from it.</summary>
    /// <param name="CubeName">The cube's name in the published <c>DataSet</c>.</param>
    /// <param name="Spec">A ready-to-use spec — the whole cube for a rank-1, a sensible slice above.</param>
    /// <param name="Description">What it is, for the picker's own list.</param>
    public readonly record struct Offer(string CubeName, string Spec, string Description);

    /// <summary>
    /// Everything in <paramref name="ds"/> that can be plotted, each with a default spec.
    ///
    /// <para><b><c>Zs_conv</c>'s off-diagonals are offered explicitly</b> and are the interesting case
    /// (§4.5.3): the full source-side conversion matrix is published precisely so they can be
    /// plotted, and until now nothing has ever displayed them. A rank-2 cube's default spec pins the
    /// first axis and sweeps the second, which for <c>Zs_conv</c> is "how strongly harmonic i is
    /// converted from every input harmonic".</para>
    /// </summary>
    public static IReadOnlyList<Offer> Offers(DataSet? ds)
    {
        if (ds is null) return [];

        var offers = new List<Offer>();
        foreach (string group in ds.Groups)
        {
            foreach (var (name, cube) in ds.CubesIn(group))
            {
                if (name.StartsWith("__", StringComparison.Ordinal)) continue;   // metadata
                string qualified = group == DataSet.DefaultGroup ? name : $"{group}.{name}";
                if (cube.Rank == 0) continue;                                    // scalars need a Table

                offers.Add(new Offer(qualified, DefaultSpec(qualified, cube), Describe(name, cube)));
            }
        }
        return offers;
    }

    /// <summary>
    /// A default spec for a cube: the whole thing when rank 1, and otherwise every axis pinned at 0
    /// except the LAST, which sweeps. The last axis is the natural X for every cube §5 publishes —
    /// <c>harmonic</c> on <c>[side, harmonic]</c>, <c>harmonic_in</c> on <c>Zs_conv</c>.
    /// </summary>
    public static string DefaultSpec(string cubeName, DataCube cube)
    {
        if (cube.Rank == 1) return cubeName;
        var tokens = Enumerable.Repeat("0", cube.Rank).ToArray();
        tokens[^1] = ":";
        return $"{cubeName}[{string.Join(", ", tokens)}]";
    }

    private static string Describe(string name, DataCube cube)
    {
        string axes = string.Join(", ", cube.Axes.Select(a => a.Name));
        string what = name switch
        {
            "Zs_conv" =>
                "the FULL source-side harmonic-conversion impedance matrix (§4.5.3). Its diagonal is " +
                "the source glyph; its off-diagonals say how strongly the source network converts " +
                "harmonic i into harmonic k, which nothing has displayed before.",
            "Gamma_intr" => "the intrinsic glyph values (§4.5) — load by ratio, source by the J′ route.",
            "Z_intr"     => "the intrinsic impedances the glyphs are drawn from.",
            "Gamma_ext"  => "the marker terminations as set.",
            "Zin"        => "extrinsic input impedance from the TRUE delivered current (§4.5.4).",
            "V"          => "user-facing node voltage spectra, DC included.",
            "I_intr"     => "intrinsic conduction currents per device port.",
            "Vds_intr_t" => "the time-domain loadline's drain voltage.",
            "Ids_intr_t" => "the time-domain loadline's drain current.",
            _            => "",
        };
        return what.Length > 0 ? $"[{axes}] — {what}" : $"[{axes}]";
    }

    // ── building a renderable trace ───────────────────────────────────────────

    /// <summary>
    /// Turns a picked trace into a drawable <see cref="Plot"/> against the frame's own
    /// <c>DataSet</c>, or reports why not.
    ///
    /// <para>The parse is <see cref="CubeTraceSpecParser"/>'s and the slicing is
    /// <see cref="PlotInspectorViewModel.SetCubeDataFrom"/>'s — this method only carries the values
    /// between them and applies harmonicaRF's palette.</para>
    /// </summary>
    public static Plot? TryBuild(HarmonicaPickedTrace picked, DataSet? ds,
                                 HarmonicaRenderTheme theme, out string? error)
    {
        ArgumentNullException.ThrowIfNull(picked);
        error = null;

        if (ds is null) { error = "Nothing has been solved yet."; return null; }

        if (!CubeTraceSpecParser.TryParse(picked.Spec, ds, out string cubeName, out var slice,
                                          out var transform, out string parseError))
        {
            error = parseError;
            return null;
        }

        // A COMPLEX cube on a Rect plot needs a scalar reduction or it renders nothing — the
        // "<invalid>" state the Data Display already has a rule for. Reuse that rule
        // (TraceRowViewModel.DefaultTransformFor: dB20 for a parameter cube, mag otherwise) rather
        // than writing a second one, and only when the spec did not state a transform itself.
        if (transform == CubeTransform.None)
            transform = TraceRowViewModel.DefaultTransformFor(ds[cubeName], PlotType.Rect);

        var trace = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Real)
        {
            CubeName   = cubeName,
            Slice      = slice,
            Transform  = transform,
            Expression = picked.Spec,
        };

        var colour = theme.GainTrace;
        trace.Properties.LineColorStorage =
            Avalonia.Media.Color.FromArgb(colour.Alpha, colour.Red, colour.Green, colour.Blue);
        trace.Properties.LineWidth   = 1.6;
        trace.Properties.LineEnabled = true;

        PlotInspectorViewModel.SetCubeDataFrom(trace, ds, PlotType.Rect, FreqUnit.GHz);

        if (trace.ExpressionError is { Length: > 0 } exprError) { error = exprError; return null; }
        if (trace.Points.Count == 0 && trace.FamilyCurves.Count == 0)
        {
            error = $"'{picked.Spec}' resolved to nothing to plot.";
            return null;
        }

        var plot = new Plot(PlotType.Rect, FreqUnit.GHz)
        {
            ShowWatermark  = false,
            CustomTitleOn  = true,  CustomTitle  = picked.Title,
            CustomXLabelOn = false,
            CustomYLabelOn = false,
        };
        plot.Traces.Add(trace);
        Renderers.HarmonicaPanelRenderer.AutoScale(plot);
        return plot;
    }
}
