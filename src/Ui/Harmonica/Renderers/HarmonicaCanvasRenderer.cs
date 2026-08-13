using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Harmonica.Renderers;

/// <summary>
/// Draws a whole harmonicaRF document — or one panel of it — onto an arbitrary
/// <see cref="SKCanvas"/> at an arbitrary size.
///
/// <para><b>Why this exists as its own class.</b> The panel composition used to live inside
/// <c>HarmonicaCanvas</c>'s draw operation, which meant the only thing that could produce it was a
/// live Avalonia control. <i>Copy Plot</i> (§7.6) and any future export need the same picture on a
/// PDF, an SVG and a bitmap surface, and a second composition would be a second answer to "where do
/// the panels go" — which is exactly what <c>CharmLayout</c> exists to stop. One composer, several
/// consumers.</para>
/// </summary>
public static class HarmonicaCanvasRenderer
{
    /// <summary>One frame's worth of everything the panels need, snapshotted away from the view
    /// model so a render can safely happen off the UI thread or after the model has moved on.</summary>
    /// <param name="Frame">The solved frame the panels draw.</param>
    /// <param name="Layout">§7.1's placement, in fractions.</param>
    /// <param name="Theme">The resolved Layer-2 colour tokens.</param>
    /// <param name="Dark">Whether the dark variant is active — the renderers take it explicitly.</param>
    /// <param name="Picked">§7.7's picked traces, each with its own panel.</param>
    /// <param name="TopmostMarker">R-h9r2-5 — the session's promoted marker, so the composed picture
    /// agrees with the live canvas about which marker is drawn on top.</param>
    public readonly record struct Snapshot(
        HarmonicaFrame                    Frame,
        CharmLayout                       Layout,
        HarmonicaRenderTheme              Theme,
        bool                              Dark,
        IReadOnlyList<HarmonicaPickedTrace> Picked,
        bool                              ShowGridPoints,
        HarmonicaMarker?                  TopmostMarker = null)
    {
        public static Snapshot Of(HarmonicaViewModel? vm) => new(
            vm?.Frame       ?? HarmonicaFrame.Empty,
            vm?.Layout      ?? CharmLayout.Default,
            vm?.RenderTheme ?? HarmonicaRenderTheme.Dark,
            vm?.Variant     != Theming.ColorVariant.Light,
            vm is null ? [] : [.. vm.PickedTraces],
            vm?.ShowGridPoints ?? false,
            vm?.TopmostMarker);
    }

    /// <summary>Fills the target rect with the document background. Never <c>canvas.Clear</c>, which
    /// uses Src blend and would replace the whole surface — this codebase has shipped that twice.
    ///
    /// <para><b>R-h9a-12 — <paramref name="transparent"/> skips the fill entirely, it never draws a
    /// transparent rect.</b> Mirrors <c>LayoutRenderer</c>'s own <c>TransparentBackground</c> option
    /// (R-L1f-5): the destination surface is expected to ARRIVE transparent (a fresh PDF page / SVG
    /// canvas already does; <c>HarmonicaClipboard</c>'s bitmap path calls <c>SKBitmap.Erase(SKColors.
    /// Transparent)</c> first) — drawing a rect with alpha-0 paint would still be a real draw call, and
    /// a real draw call is exactly what a future accidental non-zero alpha would silently corrupt.
    /// Defaults <c>false</c> so the live canvas — which must stay unaffected — needs no change at its
    /// one call site.</para>
    /// </summary>
    public static void FillBackground(SKCanvas canvas, double w, double h, HarmonicaRenderTheme theme,
                                      bool transparent = false)
    {
        if (transparent) return;
        using var bg = new SKPaint { Color = theme.Background, IsAntialias = false };
        canvas.DrawRect(new SKRect(0, 0, (float)w, (float)h), bg);
    }

    /// <summary>Every panel, laid out from <see cref="CharmLayout"/> across a <c>w × h</c> area.</summary>
    public static void DrawAll(SKCanvas canvas, double w, double h, Snapshot s)
    {
        foreach (string id in PanelIds(s))
        {
            string panelId = id;
            InPanel(canvas, w, h, s.Layout, panelId, size => DrawPanelBody(canvas, panelId, size, s));
        }
    }

    /// <summary>
    /// ONE panel, filling the whole <c>w × h</c> area rather than its own fraction of a document —
    /// what <i>Copy Plot</i> hands to a page.
    /// </summary>
    public static void DrawPanel(SKCanvas canvas, double w, double h, string panelId, Snapshot s)
        => DrawPanelBody(canvas, panelId, (w, h), s);

    /// <summary>
    /// Every panel that draws something, in draw order. The readout strip is deliberately absent: it
    /// is Avalonia <c>TextBlock</c>s, not a Skia panel (H4–H5's own note), so it has no body here.
    /// </summary>
    public static IReadOnlyList<string> PanelIds(Snapshot s)
        => [HarmonicaPanelId.SmithPower, HarmonicaPanelId.SmithEfficiency,
            HarmonicaPanelId.Loadline,   HarmonicaPanelId.PowerSweep,
            .. s.Picked.Select(p => p.PanelId)];

    /// <summary>A human-facing name for a panel id, for the Copy Plot report.</summary>
    public static string DisplayName(string panelId, Snapshot s)
        => panelId switch
        {
            HarmonicaPanelId.SmithPower      => "Power",
            HarmonicaPanelId.SmithEfficiency => "Efficiency",
            HarmonicaPanelId.Loadline        => "Loadline",
            HarmonicaPanelId.PowerSweep      => "Power sweep",
            _ => s.Picked.FirstOrDefault(p => p.PanelId == panelId) is { } t
                ? (t.Label is { Length: > 0 } l ? l : t.Spec)
                : panelId,
        };

    private static void DrawPanelBody(SKCanvas canvas, string panelId,
                                      (double W, double H) size, Snapshot s)
    {
        switch (panelId)
        {
            case HarmonicaPanelId.SmithPower:
                HarmonicaPanelRenderer.DrawSmithPanel(canvas, size, s.Frame.SmithPower, s.Theme, s.Dark,
                                                      s.ShowGridPoints, s.TopmostMarker);
                return;
            case HarmonicaPanelId.SmithEfficiency:
                HarmonicaPanelRenderer.DrawSmithPanel(canvas, size, s.Frame.SmithEfficiency, s.Theme, s.Dark,
                                                      s.ShowGridPoints, s.TopmostMarker);
                return;
            case HarmonicaPanelId.Loadline:
                HarmonicaPanelRenderer.DrawLoadlinePanel(canvas, size, s.Frame.Loadline, s.Theme, s.Dark);
                return;
            case HarmonicaPanelId.PowerSweep:
                HarmonicaPanelRenderer.DrawPowerSweepPanel(canvas, size, s.Frame.PowerSweep, s.Theme, s.Dark);
                return;
        }

        var picked = s.Picked.FirstOrDefault(p => p.PanelId == panelId);
        if (picked is null) return;

        var plot = HarmonicaTracePicker.TryBuild(picked, s.Frame.Published, s.Theme, out string? error);
        HarmonicaPanelRenderer.DrawPickedTracePanel(canvas, size, plot, error, s.Theme, s.Dark);
    }

    /// <summary>Clips and translates into one panel's own rect, then hands the panel renderer a
    /// canvas whose origin is that panel's top-left — so no renderer knows it is one of several.</summary>
    private static void InPanel(SKCanvas canvas, double w, double h, CharmLayout layout,
                                string panelId, Action<(double W, double H)> body)
    {
        var p = layout.PlacementOf(panelId);
        float x = (float)(p.X * w), y = (float)(p.Y * h);
        float pw = (float)(p.W * w), ph = (float)(p.H * h);
        if (pw <= 1 || ph <= 1) return;

        canvas.Save();
        canvas.ClipRect(new SKRect(x, y, x + pw, y + ph));
        canvas.Translate(x, y);
        body((pw, ph));
        canvas.Restore();
    }
}
