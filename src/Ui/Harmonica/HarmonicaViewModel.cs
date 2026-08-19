using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// The shared circuit state the four panels are all views of.
///
/// <para><b>Markers live HERE, once (R-h45-3).</b> "A marker is a property of the CIRCUIT, not of a
/// plot. Moving L2 on the power chart moves it on the efficiency chart in the same frame, because
/// both are views of the same model object." That is why <see cref="Markers"/> is one list handed to
/// both <c>SmithPanelData</c>s by reference — the linkage is structural, not a synchronisation step
/// somebody has to remember to perform.</para>
///
/// <para><b>Solving is synchronous here, deliberately.</b> M5's solve pool and M6's frame scheduler
/// are what make it concurrent, cancellable and adaptive; this VM's job is to own the state and
/// produce a frame from it, and doing threading twice would be worse than doing it once, later.</para>
/// </summary>
public sealed partial class HarmonicaViewModel : ObservableObject
{
    private readonly HarmonicaSolver _solver = new();
    private HarmonicaContext? _ctx;

    public HarmonicaViewModel(CircuitModel? model = null)
    {
        Model        = model ?? DefaultModel();
        Terminations = new TerminationSet(Model.Settings.HarmonicCount);
        EditDisplay  = new HarmonicaEditDisplay(() => Layout, l => Layout = l);
        ColorEditor  = new HarmonicaColorEditor(() => Appearance, a => Appearance = a);

        // R8B §3 — S1/S2 start with NO marker: the owner must turn them on from Add Source Marker
        // (§4) before either is visible or draggable. The Source band-1 termination is still written
        // (50 Ω, matching the default DUT's own input impedance) so REMOVING the marker never changes
        // the circuit — AddMarkerBand's own invariant, read backwards; band 2 keeps
        // TerminationSet.UnmarkedBandOhms exactly as before, unchanged from R-h9r2-1's own ruling.
        // R9A §7 — L1 is now 80+j0 Ω, not 80+j10: 80 Ω is the default DUT's own R_opt AND the default
        // HarmonicaSettings.Z0 (CircuitModel.cs), so the default document now opens with L1 at the
        // CENTRE of its own Smith chart — which is what Z0 = R_opt is for. The L2/L3 unmarked-epsilon
        // markers are UNCHANGED.
        //
        // A LOADED .charm is completely unaffected: this only ever runs in the constructor's own
        // default-model path, never on load — RebuildMarkersFromTerminations (the load path) replaces
        // Markers wholesale from whatever TerminationSet the file actually carried.
        if (Terminations.HarmonicCount < 3)
            throw new InvalidOperationException(
                $"the default marker set needs harmonic bands 1..3, but this model's HarmonicCount is " +
                $"only {Terminations.HarmonicCount}");

        Terminations.Set(TerminationSide.Source, 1, new Complex(50.0, 0.0));

        Markers.Add(new HarmonicaMarker(TerminationSideKind.Load, 1));
        SetMarkerImpedance(Markers[0], new Complex(80, 0));

        var unmarked = new Complex(TerminationSet.UnmarkedBandOhms, 0);
        SetMarkerImpedance(AddMarkerBand(TerminationSideKind.Load, 2), unmarked);
        SetMarkerImpedance(AddMarkerBand(TerminationSideKind.Load, 3), unmarked);
    }

    // ── the circuit ──────────────────────────────────────────────────────────

    public CircuitModel   Model        { get; private set; }
    /// <summary>The terminations the engine reads. REPLACED wholesale on load rather than copied
    /// into: a loaded .charm may carry a different harmonic count, and TerminationSet fixes that at
    /// construction — a copy-in would silently drop every band past the old K.</summary>
    public TerminationSet Terminations { get; private set; }

    /// <summary>Every marker, once. Both Smith panels hold this same list (R-h45-3).</summary>
    public ObservableCollection<HarmonicaMarker> Markers { get; } = [];

    /// <summary>
    /// R-h9r2-5 (§3) — the marker promoted to the top of the z-order for THIS SESSION, by having been
    /// successfully grabbed (its own round marker, or its intrinsic glyph — <see cref="HarmonicaGesture.
    /// PointerDown"/> is the one place this is set). Consulted by both the renderer
    /// (<see cref="HarmonicaMarkerZOrder"/>) and <see cref="HarmonicaHitTest.Resolve"/>, so a click and
    /// what it visually promotes can never disagree. <b>Session state only — never persisted to
    /// <c>.charm</c></b>: <c>CharmIo</c> has no field for it, and it is never re-seeded from a load.
    /// </summary>
    public HarmonicaMarker? TopmostMarker { get; private set; }

    /// <summary>Promotes <paramref name="marker"/> to the top of the z-order (R-h9r2-5). A no-op if it
    /// is already there — no redundant redraw for a marker the user is still dragging.</summary>
    public void PromoteMarker(HarmonicaMarker marker)
    {
        if (ReferenceEquals(TopmostMarker, marker)) return;
        TopmostMarker = marker;
        RedrawRequested?.Invoke();
    }

    /// <summary>
    /// R-h7-5/7 — the traces the user picked over the §5 <c>DataSet</c>. Each draws into its own
    /// layout panel and each persists in the <c>.charm</c>.
    /// </summary>
    public ObservableCollection<HarmonicaPickedTrace> PickedTraces { get; } = [];

    /// <summary>
    /// Adds a picked trace on the next free panel id and returns it. The id is derived from a
    /// counter rather than from the spec, so two traces over the same cube get distinct panels and a
    /// trace whose spec is later edited keeps its placement.
    /// </summary>
    public HarmonicaPickedTrace AddPickedTrace(string spec, string? label = null)
    {
        int n = 1;
        while (PickedTraces.Any(t => t.PanelId == HarmonicaPickedTrace.PanelPrefix + n)) n++;

        var picked = new HarmonicaPickedTrace(spec, HarmonicaPickedTrace.PanelPrefix + n, label);
        PickedTraces.Add(picked);

        // A picked trace needs somewhere to go. Nothing in §7.1 reserves room for one, so it lands
        // in the middle at a quarter of the document and Edit Display moves it from there.
        Layout = Layout with
        {
            Panels = [.. Layout.Panels.Where(p => p.PanelId != picked.PanelId),
                      new CharmPanelPlacement(picked.PanelId, 0.30, 0.30, 0.40, 0.35)],
        };

        RedrawRequested?.Invoke();
        DirtyChanged?.Invoke();
        return picked;
    }

    /// <summary>Removes a picked trace and its panel placement together — a placement for a panel
    /// nothing draws is exactly the orphan <c>CharmLayout</c> drops on read.</summary>
    public bool RemovePickedTrace(HarmonicaPickedTrace picked)
    {
        if (!PickedTraces.Remove(picked)) return false;
        Layout = Layout with
        {
            Panels = [.. Layout.Panels.Where(p => p.PanelId != picked.PanelId)],
        };
        RedrawRequested?.Invoke();
        DirtyChanged?.Invoke();
        return true;
    }

    /// <summary>R-h45-1 — the §7.1 layout, locked by default, persisted in the <c>.charm</c>.</summary>
    [ObservableProperty] private CharmLayout _layout = CharmLayout.Default;

    /// <summary>§7.7's Edit Display mode. It writes <see cref="Layout"/> — R-h7-8: H7 flips
    /// <c>Locked</c> and writes the same field R-h45-1 already created for it.</summary>
    public HarmonicaEditDisplay EditDisplay { get; }

    /// <summary>R-h45-12 — the stored appearance. Default until the user recolours.</summary>
    [ObservableProperty] private CharmAppearance _appearance = CharmAppearance.Default;

    /// <summary>
    /// R-h9c-7 (R1C §5) — snapshots <see cref="Appearance"/>'s persisted per-row formats into a
    /// plain delegate, the shape a worker-thread solve needs (the same reason <see cref="RequestFrame"/>
    /// snapshots <c>Model</c>/<c>Terminations</c>/<c>Markers</c> before submitting — this class's own
    /// state must not be read from a pool worker thread).
    /// </summary>
    private Func<string, ReadoutFormat> ReadoutFormatLookup()
    {
        var formats = Appearance.ReadoutFormats;
        return key => formats.TryGetValue(key, out var v) && Enum.TryParse<ReadoutFormat>(v, out var f)
            ? f : HarmonicaReadoutFormatting.DefaultReadoutFormat(key);
    }

    /// <summary>The most recently solved frame. The panels render this and nothing else.</summary>
    [ObservableProperty] private HarmonicaFrame _frame = HarmonicaFrame.Empty;

    /// <summary>Non-null when the last solve failed. Shown in the status strip rather than thrown —
    /// a live tool that throws on a bad parameter is unusable.</summary>
    [ObservableProperty] private string? _solveError;

    [ObservableProperty] private bool _isSolving;

    /// <summary>
    /// §3 (R1C) — true while the IN-FLIGHT request includes a grid build (i.e. <c>!SkipContours</c>).
    /// A tier-A-only frame never sets this, so the message line's "Solving…" bar cannot appear for a
    /// frame that has nothing for it to track. Reset on publish, exactly like <see cref="IsSolving"/>.
    /// </summary>
    [ObservableProperty] private bool _isSolvingGrid;

    partial void OnIsSolvingGridChanged(bool value) => RedrawRequested?.Invoke();

    /// <summary>
    /// §3 (R1C) — fires on a WORKER thread, once per Γ point, while a grid build is under way.
    /// <b>This class raises nothing on the UI thread itself</b> (§6.7) — the view marshals, exactly as
    /// it already does for <see cref="Pool"/>'s own <c>Completed</c>/<c>Failed</c> events.
    /// </summary>
    public event Action<int, int>? GridSolveProgress;

    /// <summary>§7.3's plane toggle — ONE toggle, moving the DCIV family and the loadline together.</summary>
    [ObservableProperty] private bool _intrinsicPlane = true;

    /// <summary>§7.4's click-to-cycle X-axis unit.</summary>
    [ObservableProperty] private PowerSweepXUnit _powerSweepXUnit = PowerSweepXUnit.PoutDbm;

    /// <summary>D11 — iso-line labels default OFF. The default setting is also the fast one.</summary>
    [ObservableProperty] private bool _showIsoLineLabels;

    /// <summary>R-h9b-7 — the Γ grid-point dots, default OFF. Display-only: hit-testing follows this
    /// too (an invisible point must not be grabbable), and the drag still works once shown.</summary>
    [ObservableProperty] private bool _showGridPoints;

    /// <summary>brief-harmonicarf-r5 §1 — the diagnostics overlay HUD, default OFF (guardrail 6: it
    /// must cost nothing measurable when off). <see cref="HarmonicaCanvas"/>'s draw operation reads
    /// this to decide whether to record a frame into <see cref="Diagnostics"/> at all — the rolling
    /// buffers are allocated regardless (negligible, one-time), but nothing WRITES to them, and
    /// nothing draws, unless this is true.</summary>
    [ObservableProperty] private bool _showDiagnosticsOverlay;

    /// <summary>brief-harmonicarf-r6d §4 — the power-sweep panel's title fly menu: false = Power
    /// Sweep (default), true = Time Domain. Same shape of display-only toggle as
    /// <see cref="ShowGridPoints"/>.</summary>
    [ObservableProperty] private bool _showPowerSweepTimeDomain;

    /// <summary>
    /// brief-harmonicarf-r5 §1 — this document's own diagnostics overlay state (the rolling
    /// frame-interval window, GC deltas). Owned here, not on the canvas, so <c>Reset()</c> is reachable
    /// directly from a menu command — the same reason <see cref="EditDisplay"/>/<see cref="ColorEditor"/>
    /// are VM-owned rather than living on the view.
    /// </summary>
    public HarmonicaDiagnosticsOverlay Diagnostics { get; } = new();

    /// <summary>Raised whenever the frame, the theme or a marker changes and the canvas must repaint.</summary>
    public event Action? RedrawRequested;

    /// <summary>brief-harmonicarf-r5 §1 — lets a caller with no state change of its own to report
    /// (<c>Diagnostics.Reset()</c> touches only session-only overlay state, nothing
    /// <see cref="RedrawRequested"/>'s other callers already invalidate on) still ask for an immediate
    /// repaint, so the HUD does not sit showing stale numbers until the next unrelated redraw.</summary>
    public void RequestRedraw() => RedrawRequested?.Invoke();

    /// <summary>Raised when anything that belongs in the <c>.charm</c> changes.</summary>
    public event Action? DirtyChanged;

    /// <summary>
    /// Raised when the harmonic order moved and <see cref="Terminations"/> was retargeted onto it —
    /// the ONE signal for "K changed", which is what any per-band menu has to rebuild against.
    ///
    /// <para><b>An explicit event because the implicit one was a bug.</b> Every per-band list used to
    /// key off <see cref="Markers"/>'s own <c>CollectionChanged</c>, on the reasoning that a K change
    /// always went through a wholesale marker rebuild and therefore always fired it. Round 11 §3
    /// removed that rebuild (it was inventing an S1 marker on every K edit — see
    /// <see cref="RetargetTerminations"/>), and with it the accidental signal: RAISING K adds no
    /// marker and so changed nothing anyone was listening to. Naming the signal is what makes the two
    /// facts independent, rather than leaving one riding on a side effect of the other.</para>
    /// </summary>
    public event Action? HarmonicCountChanged;

    // ── theme ────────────────────────────────────────────────────────────────

    /// <summary>The active variant. Follows <c>ActualThemeVariant</c>, as the schematic canvas does;
    /// the view sets it.</summary>
    [ObservableProperty] private ColorVariant _variant = ColorVariant.Dark;

    /// <summary>
    /// The Layer-2 tokens the panels draw with, re-projected on demand.
    /// <b>R-h45-11: this is the WHOLE cost of a colour change</b> — no re-solve, and specifically no
    /// contour-cache or RBF-factorization invalidation.
    ///
    /// <para><b>R-h9a-9 — the base theme is <see cref="ThemeService.Active"/>, not the built-in
    /// default.</b> A circuitRF Settings-dialog colour edit changes <c>ThemeService.Active</c>; before
    /// this, <see cref="HarmonicaAppearanceBridge.ToRenderTheme"/>'s <c>baseTheme</c> defaulted to
    /// <see cref="ColorTheme.BuiltIn"/> here, so that edit was structurally invisible to harmonicaRF —
    /// the app-wide theme and this document's own <c>Appearance</c> overrides now compose the same way
    /// the schematic canvas already composes <c>ThemeService.Active</c> with its own local state.</para>
    /// </summary>
    public HarmonicaRenderTheme RenderTheme
        => HarmonicaAppearanceBridge.ToRenderTheme(Appearance, Variant, ThemeService.Active);

    /// <summary>§7.9.4's colour editor. It writes <see cref="Appearance"/> and nothing else, which is
    /// R-h7-16's guarantee expressed as a type rather than as a rule.</summary>
    public HarmonicaColorEditor ColorEditor { get; }

    partial void OnVariantChanged(ColorVariant value)   => RedrawRequested?.Invoke();
    partial void OnAppearanceChanged(CharmAppearance value) { RedrawRequested?.Invoke(); DirtyChanged?.Invoke(); }
    partial void OnFrameChanged(HarmonicaFrame value)
    {
        CaptureAxisWindows(value);
        RedrawRequested?.Invoke();
    }
    partial void OnLayoutChanged(CharmLayout value)     { RedrawRequested?.Invoke(); DirtyChanged?.Invoke(); }

    /// <summary>
    /// brief-harmonicarf-r6e §2.2/§2.3 — the write-back half of the stored-axis-limits mechanism.
    /// <see cref="Renderers.HarmonicaPanelRenderer.ApplyStoredWindow"/> (the read half) only ever
    /// READS <see cref="Model"/>; this is the one place anything WRITES the stored limits.
    ///
    /// <para>Fires on every published frame (this is <see cref="Frame"/>'s own
    /// <c>OnFrameChanged</c>), but only actually touches <see cref="Model"/> when it has to:
    /// autoscale ON recomputes and re-stores the natural (AutoScale/PinAxisPin/headroom-fitted)
    /// window EVERY time, so turning it back off freezes exactly what is on screen (§2.3); autoscale
    /// OFF with no stored limit yet computes it ONCE, from the first frame that actually has data,
    /// and never again (§2.2) — which is the "axes never move while the user drags markers" property
    /// itself. Autoscale OFF with a limit already stored is a pure no-op, by construction: neither
    /// condition below is true.</para>
    ///
    /// <para>Each plot's own <c>Build*Plot</c> is called with <c>Autoscale: true</c> regardless of
    /// what is actually stored — that makes <see cref="Renderers.HarmonicaPanelRenderer.
    /// ApplyStoredWindow"/> a no-op for this call, so the returned <c>Axes.Window</c>/
    /// <c>WindowSecondary</c> is always the NATURAL fit, never a stale stored value read back at
    /// itself.</para>
    /// </summary>
    // ── R-hui-6 — the natural-fit window per plot, factored out of CaptureAxisWindows so
    // ── LockDcivAxes/LockPowerSweepAxes/LockTimeDomainAxes can force the SAME computation at an
    // ── ARBITRARY moment (a "Locked" click) rather than only ever reading whatever the last
    // ── published frame happened to store — see those methods' own remark for why that distinction
    // ── is the actual fix for "the axis shifts when Locked is turned on".

    private static Avalonia.Rect NaturalDcivWindow(HarmonicaFrame frame, HarmonicaSettings s, HarmonicaRenderTheme theme)
        => Renderers.HarmonicaPanelRenderer.BuildLoadlinePlot(
            frame.Loadline, theme, Renderers.HarmonicaPanelRenderer.DcivLimits(s) with { Autoscale = true }).Axes.Window;

    private static (Avalonia.Rect Window, Avalonia.Rect Window2) NaturalPowerSweepWindow(
        HarmonicaFrame frame, HarmonicaSettings s, HarmonicaRenderTheme theme)
    {
        var axes = Renderers.HarmonicaPanelRenderer.BuildPowerSweepPlot(
            frame.PowerSweep, theme, Renderers.HarmonicaPanelRenderer.PowerSweepLimits(s) with { Autoscale = true }).Axes;
        return (axes.Window, axes.WindowSecondary);
    }

    private static (Avalonia.Rect Window, Avalonia.Rect Window2) NaturalTimeDomainWindow(
        HarmonicaFrame frame, HarmonicaSettings s, HarmonicaRenderTheme theme)
    {
        var axes = Renderers.HarmonicaPanelRenderer.BuildTimeDomainPlot(
            frame.Loadline, theme, Renderers.HarmonicaPanelRenderer.TimeDomainLimits(s) with { Autoscale = true }).Axes;
        return (axes.Window, axes.WindowSecondary);
    }

    private void CaptureAxisWindows(HarmonicaFrame frame)
    {
        var s = Model.Settings;
        var theme = RenderTheme;
        var next = s;

        if ((s.DcivAutoscale || s.DcivXMin is null)
            && (frame.Loadline.Dciv.Count > 0 || frame.Loadline.LoadlineVds.Length > 1))
        {
            var w = NaturalDcivWindow(frame, s, theme);
            if (w.Width > 0 && w.Height > 0 &&
                (next.DcivXMin != w.X || next.DcivXMax != w.X + w.Width ||
                 next.DcivYMin != w.Y || next.DcivYMax != w.Y + w.Height))
            {
                next = next with
                {
                    DcivXMin = w.X, DcivXMax = w.X + w.Width, DcivYMin = w.Y, DcivYMax = w.Y + w.Height,
                };
            }
        }

        if ((s.PowerSweepAutoscale || s.PowerSweepXMin is null) && frame.PowerSweep.GainDb.Length > 1)
        {
            var (w, w2) = NaturalPowerSweepWindow(frame, s, theme);
            if (w.Width > 0 && w.Height > 0 &&
                (next.PowerSweepXMin != w.X || next.PowerSweepXMax != w.X + w.Width ||
                 next.PowerSweepYMin != w.Y || next.PowerSweepYMax != w.Y + w.Height ||
                 next.PowerSweepY2Min != w2.Y || next.PowerSweepY2Max != w2.Y + w2.Height))
            {
                next = next with
                {
                    PowerSweepXMin = w.X, PowerSweepXMax = w.X + w.Width,
                    PowerSweepYMin = w.Y, PowerSweepYMax = w.Y + w.Height,
                    PowerSweepY2Min = w2.Y, PowerSweepY2Max = w2.Y + w2.Height,
                };
            }
        }

        if ((s.TimeDomainAutoscale || s.TimeDomainXMin is null) && frame.Loadline.LoadlineVds.Length > 1)
        {
            var (w, w2) = NaturalTimeDomainWindow(frame, s, theme);
            if (w.Width > 0 && w.Height > 0 &&
                (next.TimeDomainXMin != w.X || next.TimeDomainXMax != w.X + w.Width ||
                 next.TimeDomainYMin != w.Y || next.TimeDomainYMax != w.Y + w.Height ||
                 next.TimeDomainY2Min != w2.Y || next.TimeDomainY2Max != w2.Y + w2.Height))
            {
                next = next with
                {
                    TimeDomainXMin = w.X, TimeDomainXMax = w.X + w.Width,
                    TimeDomainYMin = w.Y, TimeDomainYMax = w.Y + w.Height,
                    TimeDomainY2Min = w2.Y, TimeDomainY2Max = w2.Y + w2.Height,
                };
            }
        }

        if (!ReferenceEquals(next, s))
        {
            Model = Model with { Settings = next };
            DirtyChanged?.Invoke();
        }
    }

    partial void OnIntrinsicPlaneChanged(bool value)
    {
        // §7.3 — the toggle moves the DCIV family and the loadline TOGETHER, so the two curves are
        // always in the same plane and cannot be misleadingly superimposed. One flag, both curves.
        Frame = Frame with { Loadline = Frame.Loadline with { Intrinsic = value } };
        DirtyChanged?.Invoke();
    }

    partial void OnPowerSweepXUnitChanged(PowerSweepXUnit value)
    {
        // A unit change is a RELABEL of data already in hand — never a re-solve.
        Frame = Frame with { PowerSweep = Frame.PowerSweep with { XUnit = value } };
        DirtyChanged?.Invoke();
    }

    /// <summary>§7.4 — the X-axis unit cycles when the axis itself is clicked.</summary>
    [RelayCommand] private void CyclePowerSweepXUnit() => PowerSweepXUnit = PowerSweepXUnit.Next();

    /// <summary>R-h9b-10 — the direct pick from the X-axis label's right-click menu. A relabel of data
    /// already in hand, exactly like <see cref="CyclePowerSweepXUnit"/> — no re-solve.</summary>
    [RelayCommand] private void SetPowerSweepXUnit(PowerSweepXUnit unit) => PowerSweepXUnit = unit;

    /// <summary>§7.3 — one toggle for both curves.</summary>
    [RelayCommand] private void ToggleLoadlinePlane() => IntrinsicPlane = !IntrinsicPlane;

    // ── markers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a marker's termination. The marker's Γ and the <see cref="TerminationSet"/> the engine
    /// reads are kept in step HERE, in one place — two sources for "what is band 2 terminated in"
    /// would drift the moment either was written without the other.
    /// </summary>
    public void SetMarkerImpedance(HarmonicaMarker marker, Complex z)
    {
        Terminations.Set(marker.Side == TerminationSideKind.Source
                             ? TerminationSide.Source : TerminationSide.Load,
                         marker.Band, z);
        marker.Gamma = HarmonicaDataSet.GammaOf(z, Model.Settings.Z0);
        RedrawRequested?.Invoke();
        DirtyChanged?.Invoke();
    }

    /// <summary>
    /// R9A §1 — re-stamps the CURRENT frame's marker snapshot from the live <see cref="Markers"/> list.
    /// The panels draw <c>SmithPanelData.Markers</c>, not this collection (see HarmonicaSolver's own
    /// snapshot at RequestFrame), so a marker added or removed between frames is otherwise invisible
    /// until the next solve completes — while being fully hit-testable, because HarmonicaHitTest reads
    /// the live list. UI-thread only, and a pure re-projection of an already-published immutable frame:
    /// nothing is re-solved, and PublishFrame's own `frame with { PowerSweep = ... }` is the precedent.
    /// </summary>
    private void SyncMarkerSnapshotIntoFrame()
    {
        var snapshot = Markers.ToArray();
        Frame = Frame with
        {
            Markers         = snapshot,
            SmithPower      = Frame.SmithPower      with { Markers = snapshot },
            SmithEfficiency = Frame.SmithEfficiency with { Markers = snapshot },
        };
    }

    /// <summary>
    /// §4.2 / R-h7-2 — adds a band marker, or returns the existing one.
    ///
    /// <para><b>The marker and the <see cref="TerminationSet"/> entry are created TOGETHER</b>, through
    /// <see cref="SetMarkerImpedance"/>, because two sources for "what is band 2 terminated in" drift
    /// the moment either is written without the other. A new band starts at the unmarked value it
    /// already had (D9's near-short), so adding a marker does not itself change the circuit — it makes
    /// the existing termination visible and draggable.</para>
    /// </summary>
    public HarmonicaMarker AddMarkerBand(TerminationSideKind side, int band)
    {
        if (band < 1 || band > Terminations.HarmonicCount)
            throw new ArgumentOutOfRangeException(nameof(band),
                $"band {band} is outside 1…{Terminations.HarmonicCount}");

        var existing = Markers.FirstOrDefault(m => m.Side == side && m.Band == band);
        if (existing is not null) return existing;

        var engineSide = side == TerminationSideKind.Source
            ? TerminationSide.Source : TerminationSide.Load;

        var marker = new HarmonicaMarker(side, band);
        // Insert in the order RebuildMarkersFromTerminations would produce — source bands ascending,
        // then load bands ascending — so a reload does not silently reorder the list a drag holds
        // references into.
        int at = 0;
        while (at < Markers.Count && Rank(Markers[at]) < Rank(marker)) at++;
        Markers.Insert(at, marker);

        SetMarkerImpedance(marker, Terminations.Z(engineSide, band));
        SyncMarkerSnapshotIntoFrame();
        return marker;

        static int Rank(HarmonicaMarker m)
            => (m.Side == TerminationSideKind.Source ? 0 : 1000) + m.Band;
    }

    /// <summary>
    /// R-h7-2 — removes a band marker. <b>The termination entry is REMOVED too, for bands ≥ 2</b>:
    /// §4.2 says an unmarked band is the <i>absence</i> of a marker rather than a marker with a
    /// default value, and <see cref="TerminationSet.Remove"/> is what expresses that.
    ///
    /// <para><b>R8B §3.3 — band 1 IS removable, on both sides, but its termination stays.</b> A
    /// fundamental marker used to refuse removal outright; now removing it only takes the VIEW away
    /// — <see cref="AddMarkerBand"/>'s own invariant ("adding a marker does not itself change the
    /// circuit") read backwards: removing one must not either. <see cref="TerminationSet.Remove"/>
    /// itself still refuses band 1 (nothing calls it for that band any more).</para>
    /// </summary>
    public bool RemoveMarkerBand(TerminationSideKind side, int band)
    {
        var marker = Markers.FirstOrDefault(m => m.Side == side && m.Band == band);
        if (marker is null) return false;

        if (band != 1)
            Terminations.Remove(side == TerminationSideKind.Source
                                    ? TerminationSide.Source : TerminationSide.Load,
                                band);
        Markers.Remove(marker);

        SyncMarkerSnapshotIntoFrame();
        RedrawRequested?.Invoke();
        DirtyChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// R-h9r2-10 — the context menu's "Remove L2": removes the marker and lets the band fall back to
    /// D9's unmarked near-short, as ONE frame request rather than a remove-then-set pair.
    /// <see cref="TerminationSet.Z"/> already answers <c>UnmarkedBandOhms</c> for any band
    /// <see cref="TerminationSet.Remove"/> has taken out — so "removed" and "set to Z = 1e-6" are the
    /// same internal state (§2's own point) and there is nothing left to write before requesting the
    /// re-solve — for bands ≥ 2. R8B §3.3: band 1 is removable too, but leaves its termination in
    /// place (see <see cref="RemoveMarkerBand"/>'s own remark), so removing S1/L1 here re-solves at
    /// an UNCHANGED circuit — only the marker/view goes away.
    /// </summary>
    public bool RemoveMarkerAndShort(HarmonicaMarker marker)
    {
        if (!RemoveMarkerBand(marker.Side, marker.Band)) return false;
        RequestScheduledFrame(dragging: false);
        return true;
    }

    /// <summary>R9A §1 — the context menu's "Add Load/Source Marker". Adds the band, makes it visible
    /// THIS instant (§1.2a), then asks for a frame so the strip gains its row and the intrinsic glyph
    /// appears. <b>The circuit does not change</b> — AddMarkerBand's own invariant — so the frame is a
    /// re-read of an unchanged state, not a correction.</summary>
    public HarmonicaMarker AddMarkerBandAndShow(TerminationSideKind side, int band)
    {
        var marker = AddMarkerBand(side, band);
        RequestScheduledFrame(dragging: false);
        return marker;
    }

    /// <summary>Markers ▸ Reset to defaults — back to S1 and L1 alone, at 50 Ω.</summary>
    public void ResetMarkers()
    {
        Terminations = new TerminationSet(Model.Settings.HarmonicCount);
        RebuildMarkersFromTerminations();
        DirtyChanged?.Invoke();
    }

    /// <summary>
    /// Writes a marker's termination as Γ — what a drag on a Smith panel produces.
    ///
    /// <para><b>Owner ruling, R3C follow-up, 2026-08-13 — a harmonic marker may be dragged outside the
    /// unit circle (an active, negative-real-part termination).</b> This used to clamp EVERY
    /// <c>|Γ| &gt; 0.999</c> down to exactly 0.999, which silently forbade any active marker at all —
    /// found to be unnecessary: <see cref="HarmonicaDataSet.ImpedanceOf"/>, which this hands off to,
    /// already nudges only the single true singularity (Γ = 1 exactly, an open circuit) and says so in
    /// its own doc comment — "<c>|Γ| &gt; 1</c> is left alone, because an active termination is a
    /// legitimate thing... to land on". The clamp here was strictly redundant with (and stricter than)
    /// what the callee already does correctly, so it is simply removed rather than narrowed.</para>
    /// </summary>
    public void SetMarkerGamma(HarmonicaMarker marker, Complex gamma)
        => SetMarkerImpedance(marker, HarmonicaDataSet.ImpedanceOf(gamma, Model.Settings.Z0));

    /// <summary>
    /// R-h9r2-8 — writes a marker's VSWR-circle radius, from a drag on its own circumference or from
    /// the menu's <c>Set…</c> dialog (brief-harmonicarf-r6b §1.2/§2.1).
    ///
    /// <para><b>No re-solve, unlike every other marker drag.</b> The overlay is a display annotation
    /// over an already-solved termination (§9's own framing) — it neither reads nor writes anything the
    /// circuit depends on, so there is no frame to request. <see cref="RedrawRequested"/> alone is
    /// enough to move the circle on screen; <see cref="DirtyChanged"/> marks the document unsaved, the
    /// same as every other <c>.charm</c>-persisted edit.</para>
    ///
    /// <para><b>Round 10 — NO clamping at all, in either direction.</b> The old floor
    /// (<c>Math.Max(MinVswr, vswr)</c>) forbade the whole <c>VSWR &lt; 1</c> / negative half of the
    /// family, which is where a circle dragged OUTSIDE the Smith chart lives — owner: "VSWR circles are
    /// restricted in value. They should not be... VSWR can be any value, except NaN or infinity."
    /// The only two values refused here are the two the owner named: a NaN is DROPPED (the marker keeps
    /// whatever it had — a circle that vanishes is worse than one that does not move), and an infinity
    /// becomes <see cref="HarmonicaVswrHandle.InfiniteVswr"/> with its sign.</para>
    ///
    /// <para><b>§2.1 — setting a value also ENABLES the circle</b> if it was off: typing a number into
    /// <c>Set…</c> and seeing nothing happen is the failure mode to avoid. A no-op for the drag path,
    /// which can only be reached while the circle is already on.</para>
    /// </summary>
    public void SetMarkerVswr(HarmonicaMarker marker, double vswr)
    {
        if (double.IsNaN(vswr)) return;
        marker.VswrValue = double.IsInfinity(vswr)
            ? Math.Sign(vswr) * HarmonicaVswrHandle.InfiniteVswr
            : vswr;
        marker.VswrEnabled = true;
        RedrawRequested?.Invoke();
        DirtyChanged?.Invoke();
    }

    /// <summary>R-h9r2-8's context-menu toggle — flips the overlay on/off with the same no-re-solve
    /// reasoning as <see cref="SetMarkerVswr"/>; <see cref="HarmonicaMarker.VswrValue"/> itself is
    /// untouched, so re-enabling shows the last value rather than resetting to the 2.0 default.</summary>
    public void ToggleMarkerVswrEnabled(HarmonicaMarker marker)
    {
        marker.VswrEnabled = !marker.VswrEnabled;
        RedrawRequested?.Invoke();
        DirtyChanged?.Invoke();
    }

    /// <summary>R-h9r2-9's context-menu toggle. Session state, like the field it flips — no
    /// <see cref="DirtyChanged"/>, and no re-solve: nothing about the CIRCUIT changes until the next
    /// drag actually lands somewhere different.</summary>
    public void ToggleMarkerSnapToGrid(HarmonicaMarker marker) => marker.SnapToGridEnabled = !marker.SnapToGridEnabled;

    // ── R-h7-3 — the §7.5 inputs ─────────────────────────────────────────────

    /// <summary>§7.5's input half, rebuilt from the current model. The strip renders this.
    ///
    /// <para><b>R3C follow-up</b> — when Vgs (not Idq) drives the bias, the Idq row shows the LIVE
    /// current that Vgs actually draws, read from this document's own <see cref="EnsureContext"/>.
    /// Calling it here is safe/cheap even though this is a property getter: <c>HarmonicaContext.
    /// Apply</c> is a no-op once its own <c>_model</c> already matches <see cref="Model"/> (an
    /// equality check, not a re-solve), so a strip refresh that hasn't touched the bias costs nothing
    /// beyond that check — the same reasoning that already lets <see cref="SolveFrame"/> call it every
    /// frame.</para>
    /// </summary>
    public IReadOnlyList<HarmonicaInput> Inputs
    {
        get
        {
            var ctx  = EnsureContext();
            var caps = Model.Dut.Capacitances;

            // R7D §3.3 — computed by HarmonicaSolver (ctx + the last published DataSet), never
            // re-derived here; a linear/absent capacitor needs none of this at all.
            double? cgs = caps.Cgs.IsNonlinear
                ? HarmonicaSolver.LinearizedCapacitanceFarads(ctx, Frame.Published, caps.Cgs.Coefficients!, DutCapacitanceKind.Cgs)
                : null;
            double? cdg = caps.Cdg.IsNonlinear
                ? HarmonicaSolver.LinearizedCapacitanceFarads(ctx, Frame.Published, caps.Cdg.Coefficients!, DutCapacitanceKind.Cdg)
                : null;
            double? cds = caps.Cds.IsNonlinear
                ? HarmonicaSolver.LinearizedCapacitanceFarads(ctx, Frame.Published, caps.Cds.Coefficients!, DutCapacitanceKind.Cds)
                : null;

            return HarmonicaInputs.Build(Model, Model.Bias.Idq is null ? ctx.DcDrainCurrentAmps : null,
                                         cgs, cdg, cds);
        }
    }

    /// <summary>What the last rejected input edit was wrong about, or null. Shown beside the strip
    /// rather than thrown — a live instrument that dies on a typo is not a live instrument.</summary>
    [ObservableProperty] private string? _inputError;

    /// <summary>How many times an input edit has reset the frame ladder. §6.8's own counter.</summary>
    public int ScheduleResetCount { get; private set; }

    /// <summary>
    /// Writes one §7.5 input and requests a fresh frame.
    ///
    /// <para><b>R-h7-3 — the value/structural split is decided by
    /// <see cref="CircuitModel.StructuralKey"/> and by nothing else.</b> A structural edit also calls
    /// <see cref="ResetSchedule"/>, per §6.8: the previous model's cost says nothing about this one's,
    /// and a ladder that carries a stale measurement across a rebuild degrades the wrong thing.</para>
    /// </summary>
    /// <returns>False when the text was rejected; <see cref="InputError"/> then says why.</returns>
    public bool ApplyInput(string key, string text)
    {
        var updated = HarmonicaInputs.Apply(Model, key, text, out string? error);
        if (updated is null) { InputError = error; return false; }

        InputError = null;
        bool structural = updated.StructuralKey != Model.StructuralKey;

        // K is the one input whose change invalidates a data structure outside the model:
        // TerminationSet fixes its band count at construction, so a copy-in would silently keep
        // bands the new K cannot address. Rebuild it, carrying every marked band that still fits.
        if (updated.Settings.HarmonicCount != Model.Settings.HarmonicCount)
            RetargetTerminations(updated.Settings.HarmonicCount);

        // R-h9b-6 — "a Z0 change must not move any impedance": TerminationSet is untouched, but every
        // marker's cached Gamma is a Z0-DERIVED value and has to be re-expressed against the new
        // reference, or the chart would silently keep showing the old Z0's Γ for the same impedance.
        if (updated.Settings.Z0 != Model.Settings.Z0)
            foreach (var m in Markers)
                m.Gamma = HarmonicaDataSet.GammaOf(
                    Terminations.Z(m.Side == TerminationSideKind.Source
                                       ? TerminationSide.Source : TerminationSide.Load, m.Band),
                    updated.Settings.Z0);

        Model = updated;

        // R3C follow-up — Idq⇄Vgs is solved INSIDE HarmonicaContext.Apply (SolveVgsForIdq), which
        // only ever runs on whatever context actually calls it. Without this, that would be a pool
        // worker's own throwaway context on the NEXT RequestFrame — the solved Vgs would exist for
        // one frame and never reach this document's own Model, so the Vgs row would still show
        // whatever it showed before the edit. Resolving through THIS document's own EnsureContext()
        // right here means the strip shows the solved value on the SAME edit, not one frame later.
        // Bias-only, never structural (K/DUT/etc. changes are not Vgs/Idq/Vds and skip this).
        if (key is HarmonicaInputs.KeyVgs or HarmonicaInputs.KeyIdq or HarmonicaInputs.KeyVds)
        {
            var ctx = EnsureContext();
            if (ctx.Model.Bias.Vgs != Model.Bias.Vgs)
                Model = Model with { Bias = Model.Bias with { Vgs = ctx.Model.Bias.Vgs } };
        }

        if (structural)
        {
            ResetSchedule();
            ScheduleResetCount++;
        }

        DirtyChanged?.Invoke();
        RequestScheduledFrame(dragging: false);
        return true;
    }

    /// <summary>
    /// R-h8-1 — writes a whole new DUT. <b>A sibling of <see cref="ApplyInput"/>'s structural path,
    /// not a second mechanism</b>: it reaches no further than <see cref="Model"/>, and the rebuild,
    /// the ladder reset and the fresh frame all fall out of the same
    /// <see cref="CircuitModel.StructuralKey"/> comparison R-h7-3 already made the only arbiter.
    /// Nothing here touches <c>HarmonicaContext</c>, <c>TerminationSet</c> or the scheduler.
    /// </summary>
    /// <returns>False when the new DUT is the one already loaded — a no-op, not a failure.</returns>
    public bool ApplyDut(DutSpec dut)
    {
        ArgumentNullException.ThrowIfNull(dut);

        var updated = Model with { Dut = dut };
        bool structural = updated.StructuralKey != Model.StructuralKey;
        if (!structural) return false;

        InputError = null;
        Model      = updated;

        ResetSchedule();
        ScheduleResetCount++;

        DirtyChanged?.Invoke();
        // The §7.5 input list is derived from the model, so a changed DUT changes it — and the strip
        // rebuilds its row because the row SHAPE moved, which is exactly the case H7's in-place
        // update path already falls back to a rebuild for.
        OnPropertyChanged(nameof(Inputs));
        RequestScheduledFrame(dragging: false);
        return true;
    }

    /// <summary>
    /// R-h9c-12 (R1C §6) — File ▸ Refresh DUT. Re-elaborates the SAME DUT unconditionally — the
    /// removed toolbar left elaboration to happen only on Set or here, never on a value change, a
    /// marker drag or a frame; this is the explicit "something on disk changed" escape hatch §6.1's
    /// own rule does not otherwise offer. <b>Always re-reads</b>: a cell or an external model can
    /// change between Set and Refresh with none of <c>DutSpec</c>'s own fields moving, which is
    /// exactly the case <see cref="CircuitModel.StructuralKey"/> is built to be blind to.
    /// </summary>
    public void RefreshDut()
    {
        EnsureContext().ForceRebuild();

        ResetSchedule();
        ScheduleResetCount++;

        DirtyChanged?.Invoke();
        OnPropertyChanged(nameof(Inputs));
        RequestForcedFrame();
    }

    /// <summary>
    /// R-h9c-12's own pooled path — a sibling of <see cref="RequestFrame"/> that forces every worker
    /// to re-elaborate rather than trust <see cref="CircuitModel.StructuralKey"/>. Kept separate
    /// rather than a flag on <see cref="RequestFrame"/> because an ordinary frame must NEVER take
    /// this branch by accident (§6.1's absolute rule) — the two call sites are the only two places
    /// this method's name may appear.
    /// </summary>
    private long RequestForcedFrame()
    {
        var opt   = new HarmonicaSolver.Options { IntrinsicPlane = IntrinsicPlane };
        var model = Model;
        var terms = Terminations.Clone();
        var marks = Markers.ToArray();

        // R-h9r2-1 — snapshotted on the UI thread, before submit, matching the pattern every other
        // pool-submitting method already follows for Model/Terminations/Markers.
        var prevPower = Frame.SmithPower;
        var prevEff   = Frame.SmithEfficiency;

        CancelNotice  = null;   // a new request supersedes any stop the user asked for
        IsSolving     = true;
        IsSolvingGrid = !opt.SkipContours;
        var onProgress    = GridProgressReporter(opt);
        var readoutFormat = ReadoutFormatLookup();
        return _pool.Submit((worker, ct) =>
        {
            var ctx = worker.ForceRebuildContext(model);
            return _solver.Solve(ctx, terms, marks, opt, worker.Grid, ct, onProgress, readoutFormat,
                                  prevPower, prevEff);
        });
    }

    /// <summary>
    /// R-h9b-12 — the DCIV Sweeps dialog's write-back. <b>Invalid input keeps the old trace</b>: a
    /// rejected candidate does not touch <see cref="Model"/> at all, so whatever family the panel is
    /// currently showing stays exactly as it was.
    /// </summary>
    /// <returns>False when the candidate fails <see cref="DcivFamily.IsValidOverride"/>.</returns>
    public bool ApplyDcivOverride(double vgsMin, double vgsMax, int vgsSteps,
                                  double vdsMin, double vdsMax, int vdsSteps)
    {
        if (!DcivFamily.IsValidOverride(vgsMin, vgsMax, vgsSteps, vdsMin, vdsMax, vdsSteps))
            return false;

        Model = Model with
        {
            Settings = Model.Settings with
            {
                DcivVgsMin = vgsMin, DcivVgsMax = vgsMax, DcivVgsSteps = vgsSteps,
                DcivVdsMin = vdsMin, DcivVdsMax = vdsMax, DcivVdsSteps = vdsSteps,
            },
        };
        DirtyChanged?.Invoke();
        RequestScheduledFrame(dragging: false);
        return true;
    }

    /// <summary>
    /// R-h9r2-18/18a — the Power Sweep dialog's write-back: Start/Stop/Step, the tickle
    /// (enabled + its absolute level), and <c>ExactCompressionSolve</c>, all together. <b>Validated
    /// BEFORE anything is written</b> (R-h9b-12's own rule): an invalid candidate never touches
    /// <see cref="Model"/>, so the sweep on screen stays exactly what it was rather than reverting
    /// after the fact. Not structural — see <see cref="CircuitModel.StructuralKey"/>, which this
    /// leaves untouched — so this is an ordinary value re-solve, no context rebuild.
    /// </summary>
    /// <returns>False when the range fails <see cref="PowerSweepValidation.IsValidRange"/> or the
    /// tickle fails <see cref="PowerSweepValidation.IsValidTickle"/>.</returns>
    public bool ApplyPowerSweepSettings(double start, double stop, double step,
                                        bool tickleEnabled, double tickleDbm, bool exactCompressionSolve,
                                        out int pointCount)
    {
        if (!PowerSweepValidation.IsValidRange(start, stop, step, out pointCount)) return false;
        if (tickleEnabled && !PowerSweepValidation.IsValidTickle(tickleDbm, start)) return false;

        Model = Model with
        {
            Settings = Model.Settings with
            {
                PinStartDbm = start, PinMaxDbm = stop, PinStepDbm = step,
                TickleEnabled = tickleEnabled, TickleDbm = tickleDbm,
                ExactCompressionSolve = exactCompressionSolve,
            },
        };
        DirtyChanged?.Invoke();
        RequestScheduledFrame(dragging: false);
        return true;
    }

    /// <summary>
    /// brief-harmonicarf-r6a §3 — the Advanced tab's contour-kernel controls (kernel / smooth /
    /// epsilon). <b>Validated BEFORE anything is written</b>, the same rule
    /// <see cref="ApplyPowerSweepSettings"/> follows: a rejected candidate never touches
    /// <see cref="Model"/>, so the surface on screen stays exactly what it was.
    ///
    /// <para>Not structural (<see cref="CircuitModel.StructuralKey"/> is untouched) — an ordinary
    /// value re-solve, which for the contour grid specifically means a re-FIT: <c>ContourGrid.Build</c>
    /// re-reads these three off <see cref="Model"/>.<c>Settings</c> at its own start (mirroring how it
    /// already re-reads <c>Z0</c>) and unconditionally drops its cached RBF factorization on every
    /// call, so the next frame's <c>Fit</c> re-factorizes against the new kernel/smooth/epsilon without
    /// re-running a single <see cref="PinSearch"/> — <see cref="RequestScheduledFrame"/> with
    /// <c>reuseUnchanged</c> is what keeps the Γ points themselves from being re-solved.</para>
    /// </summary>
    /// <param name="epsilon"><c>null</c> means Rbf2D's own scipy-style auto epsilon — never
    /// substituted for a number.</param>
    public bool ApplyContourSettings(RfCore.Loadpull.RbfKernel kernel, double smooth, double? epsilon)
    {
        if (!double.IsFinite(smooth) || smooth < 0) return false;
        if (epsilon is { } e && (!double.IsFinite(e) || e <= 0)) return false;

        Model = Model with
        {
            Settings = Model.Settings with
            {
                ContourKernel = kernel, ContourSmooth = smooth, ContourEpsilon = epsilon,
            },
        };
        DirtyChanged?.Invoke();

        // R-h7-12's own point-reuse path (the same one DragGridPoint's release uses) — nothing about
        // the terminations, side or harmonic moved, so every Γ point's PinSearch answer is reusable
        // as-is. ContourGrid.Build still drops the cached RBF factorization unconditionally (see this
        // method's own remarks), so the fit itself is NOT stale — only the expensive re-solve is
        // skipped.
        var plan = Scheduler.NextPlan(dragging: false);
        LastPlan = plan;
        RequestFrame(OptionsFor(plan, dragging: false) with { ReuseUnchangedGridPoints = true });
        return true;
    }

    /// <summary>Clears the override — back to <see cref="DcivFamily.DefaultKey"/>.</summary>
    public void ResetDcivOverride()
    {
        Model = Model with
        {
            Settings = Model.Settings with
            {
                DcivVgsMin = null, DcivVgsMax = null, DcivVgsSteps = null,
                DcivVdsMin = null, DcivVdsMax = null, DcivVdsSteps = null,
            },
        };
        DirtyChanged?.Invoke();
        RequestScheduledFrame(dragging: false);
    }

    // ── brief-harmonicarf-r6e §3/§4 — persisted axis limits + autoscale, one property per plot, ──
    // ── two surfaces each (a dialog's boxes/checkbox and the fly menu's Autoscale item) ───────────

    private static bool IsValidAxisPair(double lo, double hi)
        => double.IsFinite(lo) && double.IsFinite(hi) && hi > lo;

    /// <summary>The DCIV Sweeps dialog's Axis limits section. Validated BEFORE anything is written —
    /// the same "invalid input keeps the old picture" rule <see cref="ApplyDcivOverride"/> follows.
    /// No re-solve: this is a display-only window, never a sweep-range change.</summary>
    public bool ApplyDcivAxisLimits(double xMin, double xMax, double yMin, double yMax)
    {
        if (!IsValidAxisPair(xMin, xMax) || !IsValidAxisPair(yMin, yMax)) return false;

        Model = Model with
        {
            Settings = Model.Settings with { DcivXMin = xMin, DcivXMax = xMax, DcivYMin = yMin, DcivYMax = yMax },
        };
        DirtyChanged?.Invoke();
        RedrawRequested?.Invoke();
        return true;
    }

    /// <summary>The DCIV/loadline panel's own Autoscale — the checkbox in its dialog and the
    /// checkbox in its fly menu write this SAME property, so the two can never disagree.</summary>
    public void SetDcivAutoscale(bool value)
    {
        if (Model.Settings.DcivAutoscale == value) return;
        Model = Model with { Settings = Model.Settings with { DcivAutoscale = value } };
        DirtyChanged?.Invoke();
        RedrawRequested?.Invoke();
    }

    /// <summary>
    /// R-hui-6, owner-reported — "the axis shifts" when the DCIV/Loadline fly menu's "Locked" item is
    /// turned on; it should instead freeze exactly what is CURRENTLY on screen. A bare
    /// <c>SetDcivAutoscale(false)</c> is not that: while Autoscale is on, every REDRAW (not just a new
    /// published frame) re-autoscales live from <see cref="Frame"/>'s own data — <see
    /// cref="CaptureAxisWindows"/> only re-stores that fit back into <c>DcivXMin</c>/etc. on the NEXT
    /// published frame, so the stored value can trail what the screen is actually showing right now by
    /// however long since the last solve. Turning Autoscale off alone would then freeze at that STALE
    /// stored value — a visible jump. Fixed by computing the natural fit directly from THIS call's own
    /// <see cref="Frame"/> (the exact same data driving the current render) and writing it in the SAME
    /// model update that turns Autoscale off, so there is no window where a stale value could apply.
    /// </summary>
    public void LockDcivAxes()
    {
        var s = Model.Settings;
        var w = NaturalDcivWindow(Frame, s, RenderTheme);
        var next = w.Width > 0 && w.Height > 0
            ? s with
            {
                DcivAutoscale = false,
                DcivXMin = w.X, DcivXMax = w.X + w.Width, DcivYMin = w.Y, DcivYMax = w.Y + w.Height,
            }
            : s with { DcivAutoscale = false };
        Model = Model with { Settings = next };
        DirtyChanged?.Invoke();
        RedrawRequested?.Invoke();
    }

    /// <summary>The Power Sweep Axes dialog's write-back — X, left Y (gain) and right Y (efficiency).</summary>
    public bool ApplyPowerSweepAxisLimits(double xMin, double xMax, double yMin, double yMax,
                                          double y2Min, double y2Max)
    {
        if (!IsValidAxisPair(xMin, xMax) || !IsValidAxisPair(yMin, yMax) || !IsValidAxisPair(y2Min, y2Max))
            return false;

        Model = Model with
        {
            Settings = Model.Settings with
            {
                PowerSweepXMin = xMin, PowerSweepXMax = xMax,
                PowerSweepYMin = yMin, PowerSweepYMax = yMax,
                PowerSweepY2Min = y2Min, PowerSweepY2Max = y2Max,
            },
        };
        DirtyChanged?.Invoke();
        RedrawRequested?.Invoke();
        return true;
    }

    public void SetPowerSweepAutoscale(bool value)
    {
        if (Model.Settings.PowerSweepAutoscale == value) return;
        Model = Model with { Settings = Model.Settings with { PowerSweepAutoscale = value } };
        DirtyChanged?.Invoke();
        RedrawRequested?.Invoke();
    }

    /// <summary>R-hui-6 — the Power Sweep fly menu's own "Locked", same fix and same reasoning as
    /// <see cref="LockDcivAxes"/>.</summary>
    public void LockPowerSweepAxes()
    {
        var s = Model.Settings;
        var (w, w2) = NaturalPowerSweepWindow(Frame, s, RenderTheme);
        var next = w.Width > 0 && w.Height > 0
            ? s with
            {
                PowerSweepAutoscale = false,
                PowerSweepXMin = w.X, PowerSweepXMax = w.X + w.Width,
                PowerSweepYMin = w.Y, PowerSweepYMax = w.Y + w.Height,
                PowerSweepY2Min = w2.Y, PowerSweepY2Max = w2.Y + w2.Height,
            }
            : s with { PowerSweepAutoscale = false };
        Model = Model with { Settings = next };
        DirtyChanged?.Invoke();
        RedrawRequested?.Invoke();
    }

    /// <summary>
    /// The SAME Power Sweep Axes dialog, opened while the panel is showing the Time Domain view
    /// (§4) — writes the SEPARATE Time Domain limit set, never the power-sweep one, so switching
    /// modes cannot corrupt the other mode's axes.
    /// </summary>
    public bool ApplyTimeDomainAxisLimits(double xMin, double xMax, double yMin, double yMax,
                                          double y2Min, double y2Max)
    {
        if (!IsValidAxisPair(xMin, xMax) || !IsValidAxisPair(yMin, yMax) || !IsValidAxisPair(y2Min, y2Max))
            return false;

        Model = Model with
        {
            Settings = Model.Settings with
            {
                TimeDomainXMin = xMin, TimeDomainXMax = xMax,
                TimeDomainYMin = yMin, TimeDomainYMax = yMax,
                TimeDomainY2Min = y2Min, TimeDomainY2Max = y2Max,
            },
        };
        DirtyChanged?.Invoke();
        RedrawRequested?.Invoke();
        return true;
    }

    public void SetTimeDomainAutoscale(bool value)
    {
        if (Model.Settings.TimeDomainAutoscale == value) return;
        Model = Model with { Settings = Model.Settings with { TimeDomainAutoscale = value } };
        DirtyChanged?.Invoke();
        RedrawRequested?.Invoke();
    }

    /// <summary>R-hui-6 — the Time Domain fly menu's own "Locked", same fix and same reasoning as
    /// <see cref="LockDcivAxes"/>.</summary>
    public void LockTimeDomainAxes()
    {
        var s = Model.Settings;
        var (w, w2) = NaturalTimeDomainWindow(Frame, s, RenderTheme);
        var next = w.Width > 0 && w.Height > 0
            ? s with
            {
                TimeDomainAutoscale = false,
                TimeDomainXMin = w.X, TimeDomainXMax = w.X + w.Width,
                TimeDomainYMin = w.Y, TimeDomainYMax = w.Y + w.Height,
                TimeDomainY2Min = w2.Y, TimeDomainY2Max = w2.Y + w2.Height,
            }
            : s with { TimeDomainAutoscale = false };
        Model = Model with { Settings = next };
        DirtyChanged?.Invoke();
        RedrawRequested?.Invoke();
    }

    /// <summary>
    /// Moves the marker set onto a new harmonic count. A band above the new K is DROPPED — with its
    /// marker — rather than clamped: clamping would put two markers on one band, and the file format
    /// has no way to express that.
    ///
    /// <para><b>Prunes the live marker list rather than rebuilding it, and that is the whole fix for
    /// Round 11 §3.</b> This used to call <see cref="RebuildMarkersFromTerminations"/>, whose §4.2 rule
    /// is "S1/L1 are always present" — so merely editing HB Order made an S1 marker appear on a
    /// document that deliberately had none. R8B §3 is the rule that actually governs the source side
    /// (S1/S2 start with NO marker and are turned on from Add Source Marker), and the two rules
    /// disagree; the load path is where the S1-is-always-there rule is REALLY needed, because a loaded
    /// <c>.charm</c>'s marker set genuinely has to be reconstructed from its terminations and there is
    /// nothing else to read it from. A K change is not that case — every marker the user wants already
    /// exists in <see cref="Markers"/>, so the answer is to remove what no longer fits and touch
    /// nothing else. Keeping the surviving marker INSTANCES also preserves the session state hanging
    /// off them (<see cref="TopmostMarker"/>, a VSWR circle, a drag in progress), which a wholesale
    /// rebuild silently dropped.</para>
    /// </summary>
    private void RetargetTerminations(int harmonicCount)
    {
        var next = new TerminationSet(harmonicCount);
        foreach (var side in new[] { TerminationSide.Source, TerminationSide.Load })
            foreach (int band in Terminations.MarkedBands(side).ToArray())
                if (band <= harmonicCount)
                    next.Set(side, band, Terminations.Z(side, band));

        Terminations = next;

        foreach (var dropped in Markers.Where(m => m.Band > harmonicCount).ToArray())
        {
            if (ReferenceEquals(TopmostMarker, dropped)) TopmostMarker = null;
            Markers.Remove(dropped);
        }

        HarmonicCountChanged?.Invoke();
        RedrawRequested?.Invoke();
    }

    // ── solving ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the context only when the STRUCTURE changed (§6.1). A value change — a termination, a
    /// bias, a drive level — mutates in place; going through a rebuild would be roughly 1000× the
    /// cost of the thing being changed and is explicitly forbidden.
    /// </summary>
    private HarmonicaContext EnsureContext()
    {
        if (_ctx is null)
        {
            _ctx = HarmonicaContext.Create(Model);
            return _ctx;
        }
        _ctx.Apply(Model);          // structural change → rebuild; value change → mutate
        return _ctx;
    }

    /// <summary>How many times the context has been rebuilt. §6.1's own gate.</summary>
    public int ContextRebuildCount => _ctx?.RebuildCount ?? 0;

    /// <summary>Tier C's gate — how many times the DCIV family was actually computed.</summary>
    public int DcivComputeCount => _solver.DcivComputeCount;

    /// <summary>HB solves the last frame cost.</summary>
    public int LastSolveCount => _solver.LastSolveCount;

    /// <summary>brief-harmonicarf-r4 §5.3's own counter (`HarmonicaSolver.LeverOneDeltaGammaThreshold`)
    /// — how many frames read lever 1 (the previous frame's converged spectrum as the Pin-ladder seed)
    /// as disabled because the largest single-band Γ move since the last frame exceeded the threshold.
    /// Exposed here so §1's diagnostics overlay can show it without reaching into the solver directly.
    /// </summary>
    public int Lever1DisabledCount => _solver.Lever1DisabledCount;

    /// <summary>Round 11 §1's own counter — how many frames dropped the carried-over seed state
    /// because the STRUCTURE changed (see <c>HarmonicaSolver</c>'s own field). Exposed here for the
    /// same reason <see cref="Lever1DisabledCount"/> is: so the hedge's hit rate is readable without
    /// reaching into the solver.</summary>
    public int SolverStructuralSeedResetCount => _solver.StructuralSeedResetCount;

    /// <summary>
    /// Solves one frame and publishes it. <b>Never throws</b> — a live instrument that dies on a bad
    /// parameter is not a live instrument; the failure lands in <see cref="SolveError"/> and the
    /// previous frame stays on screen.
    /// </summary>
    public void SolveFrame(HarmonicaSolver.Options? options = null)
    {
        CancelNotice  = null;   // a new request supersedes any stop the user asked for
        IsSolving = true;
        try
        {
            var ctx = EnsureContext();
            var opt = (options ?? new HarmonicaSolver.Options()) with { IntrinsicPlane = IntrinsicPlane };
            var frame = _solver.Solve(ctx, Terminations, [.. Markers], opt, readoutFormat: ReadoutFormatLookup(),
                                      previousPower: Frame.SmithPower, previousEfficiency: Frame.SmithEfficiency);
            Frame      = frame with { PowerSweep = frame.PowerSweep with { XUnit = PowerSweepXUnit } };
            SolveError = null;
        }
        catch (Exception ex)
        {
            SolveError = ex.Message;
        }
        finally
        {
            IsSolving = false;
        }
    }

    // ── R-h45-8 / R-h45-9 — the pooled, latest-wins path ─────────────────────

    private readonly SolvePool<HarmonicaFrame> _pool = new();

    /// <summary>The pool this document's frames run on. Exposed so a test can read its counters.</summary>
    public SolvePool<HarmonicaFrame> Pool => _pool;

    /// <summary>
    /// Submits a frame to the solve pool and returns its sequence number. <b>The UI thread never
    /// solves</b> (§6.7) — it renders the most recent completed result, and a newer request
    /// supersedes an in-flight one rather than queueing behind it.
    ///
    /// <para>The context comes from the WORKER, not from <see cref="EnsureContext"/>: each worker
    /// owns its own, because neither <c>HarmonicaContext</c> nor <c>ContourGrid</c> is thread-safe
    /// (D2). The solver itself IS shared, deliberately — tier C's DCIV cache lives on it and must be
    /// computed once, not once per worker.</para>
    /// </summary>
    /// <param name="gridPointOverride">
    /// R-h9r2-4, Option 1 — while a single grid point is being dragged, the grid itself is frozen
    /// (R-h9r2-2 forces every drag to <c>SkipContours</c>), so without this the dragged point's own
    /// glyph would sit still until release. Splices <c>(Index, Gamma)</c> into the CARRIED
    /// <c>GridPoints</c> list for display only — cheaper than giving the grid-point drag its own
    /// live single-point solve, and it is what "contours stay frozen until release either way" means
    /// in practice: the polylines/levels/optimum carried forward are untouched, only the one dot's
    /// drawn position moves.
    /// </param>
    public long RequestFrame(HarmonicaSolver.Options? options = null,
                             (int Index, Complex Gamma)? gridPointOverride = null)
    {
        var opt   = (options ?? new HarmonicaSolver.Options()) with { IntrinsicPlane = IntrinsicPlane };
        var model = Model;
        var terms = Terminations.Clone();
        var marks = Markers.ToArray();

        // R-h9r2-1 — snapshotted on the UI thread, before submit, so a grid-less frame this request
        // produces can carry the currently-displayed layer forward.
        var prevPower = ApplyGridPointOverride(Frame.SmithPower,      gridPointOverride);
        var prevEff   = ApplyGridPointOverride(Frame.SmithEfficiency, gridPointOverride);

        CancelNotice  = null;   // a new request supersedes any stop the user asked for
        IsSolving     = true;
        IsSolvingGrid = !opt.SkipContours;
        var onProgress    = GridProgressReporter(opt);
        var readoutFormat = ReadoutFormatLookup();
        return _pool.Submit((worker, ct) =>
        {
            var ctx = worker.EnsureContext(model);
            return _solver.Solve(ctx, terms, marks, opt, worker.Grid, ct, onProgress, readoutFormat,
                                  prevPower, prevEff);
        });
    }

    /// <summary>See <see cref="RequestFrame"/>'s <c>gridPointOverride</c> parameter.</summary>
    private static SmithPanelData? ApplyGridPointOverride(SmithPanelData? panel, (int Index, Complex Gamma)? o)
    {
        if (panel is null || o is null) return panel;
        var (index, gamma) = o.Value;
        if (index < 0 || index >= panel.GridPoints.Count) return panel;

        var points = panel.GridPoints.ToArray();
        points[index] = points[index] with { Gamma = gamma };
        return panel with { GridPoints = points };
    }

    /// <summary>
    /// §3's own guard: a frame with <c>SkipContours</c> sweeps no grid and must not report progress
    /// against one — the caller (<see cref="RequestFrame"/>, <see cref="RequestInverseFrame"/>) would
    /// otherwise have to remember the same check twice.
    /// </summary>
    private Action<int, int>? GridProgressReporter(HarmonicaSolver.Options opt)
        => opt.SkipContours ? null : (done, total) => GridSolveProgress?.Invoke(done, total);

    /// <summary>
    /// Publishes a frame the pool completed. The caller marshals to the UI thread; this method
    /// assumes it is already there, which is the only place a published frame may be assigned.
    ///
    /// <para>R-h6-4 — publishing is also where the frame's measured cost is fed back to the ladder.
    /// The two were separate in H5 because nothing was driving a loop; now that a drag is, a frame
    /// whose cost is never recorded is a ladder that can never degrade.</para>
    /// </summary>
    public void PublishFrame(HarmonicaFrame frame)
    {
        if (frame.Inverse is { } outcome) ApplyInverseOutcome(outcome);
        if (frame.ConjugateMatch is { } match) ApplyConjugateMatch(match);

        Frame         = frame with { PowerSweep = frame.PowerSweep with { XUnit = PowerSweepXUnit } };
        SolveError    = null;
        CancelNotice  = null;
        IsSolving     = false;
        IsSolvingGrid = false;

        // The render cost is the PREVIOUS frame's, by one frame — the draw this frame provokes has
        // not happened yet. Said plainly rather than hidden: over a drag it is the right number, and
        // the alternative (recording after the draw) would put the ladder a frame behind on the
        // stage it is least able to influence.
        RecordFrameCost(frame.Timing with { RenderMs = LastRenderMs });
    }

    /// <summary>
    /// How long the canvas took to draw the last frame, in milliseconds. Written by the view after
    /// each draw; read by <see cref="PublishFrame"/> so the ladder sees a whole frame rather than
    /// only its solve half.
    /// </summary>
    public double LastRenderMs { get; set; }

    /// <summary>
    /// What the status strip shows (R-h6-5). The scheduler's own message outranks nothing and is
    /// never suppressed: D4's whole point is that a model which cannot hold the target is TOLD
    /// about, never silently stuttered at.
    /// </summary>
    public string? StatusMessage
    {
        get
        {
            if (SolveError is { Length: > 0 } err) return $"solve failed: {err}";
            if (CancelNotice is { Length: > 0 } stop) return stop;
            if (InverseMessage is { Length: > 0 } inv) return inv;
            return Scheduler.StatusMessage;
        }
    }

    /// <summary>
    /// Stops the solve in flight — the progress bar's right-click ▸ Cancel (owner, 2026-08-19). A grid
    /// frame is the one thing in harmonicaRF a user can be left waiting on: a dense grid on a slow DUT
    /// is seconds per frame, and until now the only way out was to wait for it.
    ///
    /// <para><b>The "solving" state is cleared HERE rather than on an event</b>, because a cancelled
    /// job raises none — <c>SolvePool</c> counts it superseded and stays quiet, which is exactly what
    /// keeps latest-wins cheap on a drag. The displayed frame is left alone: the previous answer is
    /// still the last true one, and blanking the panels would throw away a correct picture to report
    /// a stop.</para>
    ///
    /// <para>A job that reaches its end before the token is checked still publishes, and that is
    /// fine — <see cref="PublishFrame"/> clears the notice along with the flags, so the line does not
    /// claim a cancel that the frame on screen contradicts.</para>
    /// </summary>
    public void CancelSolve()
    {
        if (!IsSolving && !IsSolvingGrid) return;

        Pool.CancelCurrent();
        IsSolving     = false;
        IsSolvingGrid = false;
        CancelNotice  = "solve cancelled";
    }

    /// <summary>
    /// Set by <see cref="CancelSolve"/> and cleared by the next published frame. Outranks the
    /// scheduler's own line in <see cref="StatusMessage"/> but not a solve error, which is the more
    /// urgent of the two.
    /// </summary>
    [ObservableProperty] private string? _cancelNotice;

    partial void OnCancelNoticeChanged(string? value) => OnPropertyChanged(nameof(StatusMessage));

    /// <summary>Reports a pool failure without killing the document — same contract as <see cref="SolveFrame"/>.</summary>
    public void PublishFailure(Exception ex)
    {
        SolveError    = ex.Message;
        IsSolving     = false;
        IsSolvingGrid = false;
    }

    // ── R-h45-10 — the scheduled path ────────────────────────────────────────

    private readonly System.Diagnostics.Stopwatch _wall = System.Diagnostics.Stopwatch.StartNew();
    private FrameScheduler? _scheduler;

    /// <summary>
    /// The frame scheduler, fed this document's own wall clock. Created lazily so a test can install
    /// a synthetic-clock scheduler of its own before the first frame — which is what makes §6.8's
    /// policy testable through the document rather than only in isolation.
    /// </summary>
    public FrameScheduler Scheduler
    {
        get => _scheduler ??= new FrameScheduler(() => _wall.Elapsed.TotalMilliseconds);
        set => _scheduler = value;
    }

    /// <summary>
    /// Requests the frame the SCHEDULER says is affordable, rather than one the caller picked. This
    /// is the entry point a drag uses: pass <c>dragging: true</c> while the pointer is down and
    /// <c>false</c> on release, and freeze-and-snap falls out of §6.8's own ladder.
    /// </summary>
    public long RequestScheduledFrame(bool dragging)
    {
        var plan = Scheduler.NextPlan(dragging);
        LastPlan = plan;
        return RequestFrame(OptionsFor(plan, dragging));
    }

    /// <summary>
    /// One place a <see cref="FramePlan"/> becomes solver options, so the scheduled path and the
    /// inverse-drag path cannot drift apart about what a rung means.
    /// </summary>
    /// <param name="dragging">
    /// R-h9r2-2 — a drag ALWAYS skips the grid, regardless of which ladder rung the scheduler picked.
    /// §1.1's own finding is that live contour generation during a drag is too slow to be worth
    /// having at all (report 2), and Finding A / R-h9r2-1's carry-forward is what keeps the previous
    /// contour layer on screen instead of publishing empty lists while this is in effect. The
    /// ladder's CoarseRaster/CoarseGrid rungs still exist (a released drag can still be mid-recovery
    /// from one), but nothing dragging=true ever asks for them to run a grid.
    /// </param>
    private HarmonicaSolver.Options OptionsFor(FramePlan plan, bool dragging = false) => new()
    {
        Rings            = plan.Rings,
        Spokes           = plan.Spokes,
        RasterResolution = plan.RasterResolution,
        SkipContours     = dragging || plan.SkipContours,
        Quality          = plan.Quality,
        AtPavlDbm        = PlacedCursorPinDbm,
        EfficiencyMetric = EfficiencyMetric,
        Levels           = ContourLevels,
        GridSide         = GridSide,
        GridHarmonic     = GridHarmonic,
        // R-h7-11 — an IMPORTED or user-dragged grid supersedes the ring set entirely. It is an
        // arbitrary scatter, which is exactly what ContourGrid was built for (§6.4); the ladder's own
        // Rings/Spokes then describe nothing and are ignored rather than silently re-applied.
        //
        // CONSEQUENCE, stated rather than discovered: §6.8's CoarseGrid rung becomes a no-op while a
        // custom grid is installed — the scheduler can still coarsen the raster and freeze contours,
        // but it cannot thin a scatter it did not generate. Dropping points from a grid the user
        // imported or hand-placed would silently answer a different question from the one they asked.
        GammaGrid        = CustomGrid,
        // brief-harmonicarf-r6b §2.2 — layered on top of whatever GammaGrid resolves to, never
        // replacing it.
        AddedGridPoints  = AddedGridPoints.Count > 0 ? [.. AddedGridPoints] : null,
    };

    /// <summary>
    /// Finding B (§1.1) — whether <paramref name="side"/>/<paramref name="band"/> is the plane/band
    /// the contour grid currently sweeps (§6.5's document-wide selectors). <c>ContourGrid</c>'s own
    /// <c>_reusableAgainst</c> state key deliberately excludes the swept band's OWN termination value
    /// — "the band the grid sweeps is overwritten per point and says nothing about what a held point
    /// was solved at" — so a release that only moved THAT band's termination is provably a contour
    /// no-op: nothing the grid would recompute could have changed.
    /// </summary>
    private bool IsSweptBand(TerminationSideKind side, int band)
    {
        var mapped = side == TerminationSideKind.Source ? TerminationSide.Source : TerminationSide.Load;
        return mapped == GridSide && band == GridHarmonic;
    }

    /// <summary>How far Γ must move, since the last frame actually SUBMITTED for this (side, band),
    /// before a mid-drag frame is worth solving — brief-harmonicarf-r4 §5.4: "if the termination point
    /// doesn't change, the point should not run, because that result is already rendered." Set below
    /// what a Smith panel or the readout strip can show a user (a marker glyph a few pixels across on
    /// a panel a few hundred pixels wide already can't distinguish Γ movement smaller than ~1e-3, and
    /// every readout in this file rounds to at most 4 decimal digits) — 1e-4 is an order of magnitude
    /// under the coarser of the two, so this only ever catches genuinely sub-visible jitter (a
    /// stationary hand, a trackpad's own noise floor), never a real repositioning.</summary>
    private const double DragNoOpGammaTolerance = 1e-4;

    /// <summary>The (side, band, Γ) of the last mid-drag OR release frame actually submitted to the
    /// solve pool for a marker drag — null until the first one. Compared against on every subsequent
    /// mid-drag frame so a pointer-move event that landed within
    /// <see cref="DragNoOpGammaTolerance"/> of it can be skipped without ever touching the pool.</summary>
    private (TerminationSideKind Side, int Band, Complex Gamma)? _lastSubmittedDragTermination;

    /// <summary>§5.4's own gate — how many mid-drag marker frames were skipped because Γ had not moved
    /// past <see cref="DragNoOpGammaTolerance"/> since the last one actually solved. A counter, not a
    /// stopwatch, this repo's own convention (<c>Retries</c>/<c>LayerBRebuilds</c>' precedent) for
    /// making a no-re-solve path's own hit rate visible rather than inferred.</summary>
    public int NoOpDragFrameSkipCount { get; private set; }

    // ── brief-harmonicarf-r5 §3 — conflate-and-pace the marker-drag SOLVE submission ────────
    //
    // §3's own finding: SolvePool's latest-wins (D3) cancels the previous frame the instant a new one
    // is submitted, and a real pointer delivers 100-1000 events/sec against a 9-14 ms solve — every
    // mid-drag solve was being cancelled before it could complete, so nothing published until the
    // pointer slowed or stopped ("starvation", not "lag"). The fix is NOT a change to SolvePool
    // (guardrail 2 — latest-wins stays the pool's own policy for everything else); it is this call
    // site submitting at most one mid-drag solve at a time and conflating everything in between.
    //
    // "Is a mid-drag solve still outstanding" is answered from the POOL's own LastCompletedSequence
    // rather than from a private flag a completion callback has to remember to clear — a private flag
    // would wedge forever the moment a caller drains the pool without also routing its Completed event
    // back through this class (every headless test that calls RequestFrameOnMarkerRelease directly,
    // without wiring Pool.Completed at all, does exactly that). LastCompletedSequence only advances
    // when a job actually PUBLISHES (never merely superseded) and sequence numbers are pool-global and
    // monotonic, so "our in-flight seq is still greater than the last one that published" is true
    // exactly while it is genuinely outstanding, self-corrects the instant it (or anything newer)
    // finishes, and needs no cooperation from the caller to stay accurate.
    private long? _dragInFlightSeq;
    private bool _dragResubmitPending;
    private (TerminationSideKind Side, int Band)? _dragPendingTarget;

    private bool DragSolveInFlight => _dragInFlightSeq is { } seq && seq > _pool.LastCompletedSequence;

    /// <summary>
    /// R-h9r2-3 — the single routing point for a marker drag's RELEASE, used identically by
    /// <c>HarmonicaGesture.Apply</c>'s <c>ExtrinsicMarker</c> branch and by
    /// <see cref="DragIntrinsicGlyph"/>. R-h9r2-2's <see cref="OptionsFor"/> already forces
    /// <c>SkipContours = true</c> for every <c>dragging: true</c> request; this covers the moment the
    /// pointer comes up. When the released band IS the swept plane/band (Finding B, above), the grid
    /// is skipped there too — carrying the pre-drag contour layer forward (R-h9r2-1) instead of
    /// paying for a re-solve that would publish the identical result.
    ///
    /// <para><b>§5.4 — a mid-drag frame whose Γ has not actually moved is skipped before it ever
    /// reaches the solve pool</b>, since the frame already on screen is exactly the answer it would
    /// publish. Release is NEVER skipped by this — a real, non-degraded solve always runs on release
    /// regardless of how small the final move was, matching <c>DragGridPoint</c>'s own "mid-drag is
    /// free, release is real" shape.</para>
    ///
    /// <para><b>brief-harmonicarf-r5 §3 — a mid-drag call that finds a solve already outstanding
    /// CONFLATES rather than submits.</b> It records the (side, band) to resubmit for and returns -1
    /// (the same "nothing was submitted" sentinel §5.4's no-op uses) without touching the pool — so
    /// SolvePool's own cancel-before-submit (D3) never fires against a mid-drag job, and every mid-drag
    /// solve that DOES start is allowed to finish and publish. See <see cref="OnPoolSettled"/> for the
    /// other half.</para>
    /// </summary>
    public long RequestFrameOnMarkerRelease(TerminationSideKind side, int band, bool dragging)
    {
        if (dragging)
        {
            var marker = Markers.FirstOrDefault(m => m.Side == side && m.Band == band);
            if (marker is not null && _lastSubmittedDragTermination is { } last &&
                last.Side == side && last.Band == band &&
                (marker.Gamma - last.Gamma).Magnitude < DragNoOpGammaTolerance)
            {
                NoOpDragFrameSkipCount++;
                return -1;
            }

            if (DragSolveInFlight)
            {
                // A mid-drag solve is already running — conflate into it rather than cancelling it.
                // The marker's own Γ (already written by SetMarkerGamma before this call) is what the
                // eventual resubmission will read, so no target value needs to be stored here.
                _dragResubmitPending = true;
                _dragPendingTarget   = (side, band);
                return -1;
            }

            long seq = RequestScheduledFrame(dragging: true);
            _dragInFlightSeq = seq;
            if (marker is not null) _lastSubmittedDragTermination = (side, band, marker.Gamma);
            return seq;
        }

        // Release is never paced — a real, full-quality solve always runs, and any mid-drag solve
        // still outstanding is superseded by it exactly as latest-wins already does for every other
        // submitter. Reset the pacing state here too so a conflated move from THIS gesture can never
        // leak into the next one.
        _dragInFlightSeq     = null;
        _dragResubmitPending = false;
        _dragPendingTarget   = null;

        if (IsSweptBand(side, band))
        {
            var plan = Scheduler.NextPlan(dragging: false);
            LastPlan = plan;
            var releaseMarker = Markers.FirstOrDefault(m => m.Side == side && m.Band == band);
            if (releaseMarker is not null) _lastSubmittedDragTermination = (side, band, releaseMarker.Gamma);
            return RequestFrame(OptionsFor(plan, dragging: false) with { SkipContours = true });
        }

        var seqFull = RequestScheduledFrame(dragging: false);
        var m2 = Markers.FirstOrDefault(mk => mk.Side == side && mk.Band == band);
        if (m2 is not null) _lastSubmittedDragTermination = (side, band, m2.Gamma);
        return seqFull;
    }

    /// <summary>
    /// brief-harmonicarf-r5 §3 — the other half of conflate-and-pace: submits a conflated move THE
    /// MOMENT it can, rather than waiting for the next pointer event (which may never come, if the
    /// user is holding the pointer still while the picture catches up). Meant to be called from
    /// wherever a pool completion or failure is marshalled to the UI thread — <c>HarmonicaView</c> in
    /// the live app — right after publishing it; <paramref name="seq"/> is accepted for symmetry with
    /// that call site but not otherwise consulted, because <see cref="DragSolveInFlight"/> already
    /// answers "is our own submission still outstanding" from the pool's own
    /// <c>LastCompletedSequence</c> rather than from seq equality — which is what lets this be a no-op
    /// on every publish that has nothing to do with a drag (the common case) without needing to know
    /// this call's seq matches anything in particular.
    /// </summary>
    public void OnPoolSettled(long seq)
    {
        _ = seq;
        if (!_dragResubmitPending || DragSolveInFlight) return;

        _dragResubmitPending = false;
        var (side, band) = _dragPendingTarget!.Value;
        _dragPendingTarget = null;
        RequestFrameOnMarkerRelease(side, band, dragging: true);
    }

    // ── the Γ grid (§6.4 / R-h7-11) ──────────────────────────────────────────

    /// <summary>§7.2's per-chart efficiency metric. DE is the default.</summary>
    [ObservableProperty] private GridMetric _efficiencyMetric = GridMetric.DrainEfficiency;

    /// <summary>D8 — contour level count, auto with override.</summary>
    [ObservableProperty] private int _contourLevels = 10;

    /// <summary>§6.5 — which plane the contour grid sweeps. See <c>HarmonicaSolver.Options.GridSide</c>
    /// for why this is document-wide rather than per chart.</summary>
    [ObservableProperty] private TerminationSide _gridSide = TerminationSide.Load;

    /// <summary>§6.5 — which harmonic band the contour grid sweeps.</summary>
    [ObservableProperty] private int _gridHarmonic = 1;

    /// <summary>
    /// The Γ grid, when it is NOT the scheduler's ring set: an imported <c>.gam</c>, or a ring set
    /// with a point dragged. Null means "let the ladder choose", which is the ordinary state.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<Complex>? _customGrid;

    /// <summary>
    /// brief-harmonicarf-r6b §2.2 — Γ points added via the marker menu's <c>Add Point</c>/<c>Add
    /// Points to VSWR</c>, layered ON TOP of <see cref="CustomGrid"/> (or the ring/spoke preset when
    /// that is null) rather than replacing it — see <see cref="HarmonicaSolver.Options.
    /// AddedGridPoints"/>'s own doc comment for why this is a separate list. Persists in the
    /// <c>.charm</c> (<see cref="ToCharmJson"/>/<see cref="LoadCharm"/>); cleared by
    /// <see cref="ResetGrid"/> and by <see cref="SetGridPreset"/>, per the owner's own ruling that
    /// the preset must always describe exactly what is on screen.
    /// </summary>
    public ObservableCollection<Complex> AddedGridPoints { get; } = [];

    /// <summary>The Grid menu's ring presets. Clears any custom grid — a preset and an imported
    /// scatter are alternatives, and keeping both would leave the menu lying about what is drawn.
    /// §2.2's own added points are cleared too — "the preset must always describe exactly what is on
    /// screen".</summary>
    public void SetGridPreset(int rings, int spokes)
    {
        CustomGrid = null;
        AddedGridPoints.Clear();
        Scheduler.SetGridPreset(rings, spokes);
        ResetSchedule();
        DirtyChanged?.Invoke();
        RequestScheduledFrame(dragging: false);
    }

    /// <summary>Grid ▸ Reset grid — back to the ladder's own ring set, with no added points.</summary>
    public void ResetGrid()
    {
        CustomGrid = null;
        AddedGridPoints.Clear();
        Scheduler.SetGridPreset(null, null);
        ResetSchedule();
        DirtyChanged?.Invoke();
        RequestScheduledFrame(dragging: false);
    }

    /// <summary>
    /// brief-harmonicarf-r6b §2.2 — the marker menu's <c>Add Point</c>: appends the marker's own Γ to
    /// <see cref="AddedGridPoints"/> and re-solves. A full re-solve of the WHOLE grid, not just the
    /// new point — the honest fallback (§2.2's own text: "either is acceptable — say which you did").
    /// Adding a point moves the node set, which already invalidates <c>ContourGrid</c>'s own
    /// factorization cache by construction (its own <c>_factor</c>/<c>_factorMask</c> note), so
    /// nothing extra is needed there.
    /// </summary>
    public void AddGridPoint(Complex gamma)
    {
        AddedGridPoints.Add(gamma);
        DirtyChanged?.Invoke();
        ResetSchedule();
        RequestScheduledFrame(dragging: false);
    }

    /// <summary>
    /// brief-harmonicarf-r6b §2.3 — the marker menu's <c>Add Points to VSWR</c>: 12 points uniformly
    /// spaced in θ on the marker's OWN VSWR locus, through the same <see cref="AddGridPoint"/> path
    /// (one re-solve for the whole batch, not 12). §1.2's unclamped drag means some of these can land
    /// outside the unit circle — a legitimate active termination, not filtered out; a point the Pin
    /// search cannot converge at comes back a hole, same as any other.
    /// </summary>
    public void AddGridPointsOnVswrCircle(HarmonicaMarker marker)
    {
        var pts = RfCore.Loadpull.LoadpullSurface.VswrLocus(
            marker.Gamma, marker.VswrValue, RfCore.Loadpull.SurfacePlane.Gamma,
            new Complex(Model.Settings.Z0, 0.0), nPoints: 12);
        foreach (var p in pts) AddedGridPoints.Add(p);

        DirtyChanged?.Invoke();
        ResetSchedule();
        RequestScheduledFrame(dragging: false);
    }

    // ── R-h7-12 — dragging ONE grid point ────────────────────────────────────

    private bool _draggingGridPoint;

    /// <summary>Whether a Γ grid point is being dragged.</summary>
    public bool IsGridPointDragging => _draggingGridPoint;

    /// <summary>
    /// Starts a grid-point drag. <b>The current grid is frozen into <see cref="CustomGrid"/> first</b>,
    /// because the ladder's ring set is generated per frame from (rings, spokes) and a point moved
    /// inside it would be regenerated away on the very next frame.
    /// </summary>
    public void BeginGridPointDrag(int index)
    {
        var points = Frame.SmithPower.GridPoints;
        if (index < 0 || index >= points.Count) return;

        CustomGrid ??= [.. points.Select(p => p.Gamma)];
        _draggingGridPoint = true;
    }

    /// <summary>
    /// One frame of a grid-point drag. <b>R-h9r2-4, Option 1</b> — chosen over giving the drag its
    /// own live single-point solve, because it is cheaper and R-h9r2-2 now forces every drag
    /// (grid-point drags included) to <c>SkipContours</c>, so there is no grid build in flight to
    /// attach a single-point solve to anyway:
    ///
    /// <para><b>Mid-drag</b> (<paramref name="dragging"/> true), <see cref="CustomGrid"/> is left
    /// UNTOUCHED — the contour layer <see cref="RequestFrame"/>'s <c>gridPointOverride</c> carries
    /// forward (R-h9r2-1) is exactly the pre-drag one, and the dragged point's own glyph moves live
    /// by splicing <paramref name="gamma"/> into the CARRIED <c>GridPoints</c> for display only.</para>
    ///
    /// <para><b>On release</b>, the point is committed into <see cref="CustomGrid"/> for real and
    /// resolved with <see cref="HarmonicaSolver.Options.ReuseUnchangedGridPoints"/> — R-h7-12's own
    /// point reuse, which is what keeps this to ~1 Γ sample rather than the whole grid.</para>
    ///
    /// <para>Γ is clamped just inside the unit circle: the grid sweeps a passive load termination and
    /// Γ = 1 is an open, whose impedance the closure cannot represent.</para>
    /// </summary>
    public long DragGridPoint(int index, Complex gamma, bool dragging)
    {
        if (CustomGrid is not { } grid || index < 0 || index >= grid.Count)
            return RequestScheduledFrame(dragging);

        double mag = gamma.Magnitude;
        if (mag > 0.995) gamma = gamma / mag * 0.995;

        if (!dragging)
        {
            var next = grid.ToArray();
            next[index] = gamma;
            CustomGrid  = next;
            DirtyChanged?.Invoke();

            var releasePlan = Scheduler.NextPlan(dragging: false);
            LastPlan = releasePlan;
            return RequestFrame(OptionsFor(releasePlan, dragging: false) with { ReuseUnchangedGridPoints = true });
        }

        // §2 (brief-harmonicarf-r3b) — a mid-drag grid-point frame changes NO circuit state: the
        // dragged Γ is a display-only edit to the already-published grid-point list (CustomGrid
        // itself stays untouched until release, exactly as before). R-h9r2-4 chose this shape
        // precisely so the gesture would be cheap, but routing it through RequestFrame still ran the
        // WHOLE tier-A power sweep at terminations the drag never touches — SkipContours only ever
        // skipped the grid build, never tier A. A gesture that changes no circuit state must cost no
        // HB solves at all, so this is moved off the solve pool entirely: splice the moved point into
        // the CURRENTLY PUBLISHED frame's grid-point lists on the UI thread and redraw — the same
        // no-re-solve shape as SetMarkerVswr/ToggleMarkerVswrEnabled, just for a grid point rather
        // than a marker overlay. Setting Frame (an ObservableProperty) raises RedrawRequested itself
        // (OnFrameChanged); DirtyChanged is NOT raised — nothing persisted has changed yet, matching
        // the pre-existing rule that only release marks the document dirty.
        Frame = Frame with
        {
            SmithPower      = ApplyGridPointOverride(Frame.SmithPower,      (index, gamma)) ?? Frame.SmithPower,
            SmithEfficiency = ApplyGridPointOverride(Frame.SmithEfficiency, (index, gamma)) ?? Frame.SmithEfficiency,
        };
        return -1;
    }

    /// <summary>Ends a grid-point drag.</summary>
    public void EndGridPointDrag() => _draggingGridPoint = false;

    /// <summary>How many Γ points the last frame kept rather than re-solved. R-h7-12's own gate.</summary>
    public int LastGridPointsReused => _solver.LastGridPointsReused;

    /// <summary>
    /// §1 (R1C) — Grid ▸ Solve Now. The removed toolbar's "Solve" button forced a full-quality
    /// re-solve at the full user grid and raster, bypassing the ladder (§6.8's rungs are what the
    /// scheduler picks on its own; this is the explicit override). Same mechanism, new home.
    /// </summary>
    public void SolveFullGrid() => RequestFrame(new HarmonicaSolver.Options
    {
        Rings = FrameScheduler.FullRings, Spokes = FrameScheduler.FullSpokes,
        RasterResolution = HarmonicaSolver.Options.FullRasterResolution,
    });

    /// <summary>
    /// R-h7-11 — installs an arbitrary Γ scatter (an imported <c>.gam</c>, or a dragged ring set).
    /// The frame ladder is reset: a different point count is a different cost, and §6.8's rule is
    /// that the previous grid's measurement says nothing about this one's.
    /// </summary>
    public void SetGammaGrid(IReadOnlyList<Complex> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        CustomGrid = [.. points];
        ResetSchedule();
        DirtyChanged?.Invoke();
    }

    /// <summary>The plan the last scheduled request was solved under, or null if none has run.</summary>
    public FramePlan? LastPlan { get; private set; }

    /// <summary>
    /// Feeds a completed frame's measured cost back to the scheduler. Separate from
    /// <see cref="PublishFrame"/> because the two have different owners: publishing is the view's
    /// job on the UI thread, and timing is whoever measured it.
    /// </summary>
    public void RecordFrameCost(FrameTiming timing)
    {
        if (LastPlan is { } plan) Scheduler.RecordFrame(plan, timing);
    }

    /// <summary>Resets the ladder. Called when the model changes structurally (§6.8) — the previous
    /// model's cost says nothing about this one's.</summary>
    public void ResetSchedule() => Scheduler.Reset();

    // ── R-h6-11 — the operating point the inverse solve is posed at ───────────

    /// <summary>
    /// §7.4's <i>snap to compression</i> mode. ON by default, which is what makes "set the load at
    /// compression" the thing you get without doing anything — R-h6-11's own example.
    /// </summary>
    [ObservableProperty] private bool _snapCursorToCompression = true;

    /// <summary>Where the user has put the power-sweep cursor, dBm available. Ignored while
    /// <see cref="SnapCursorToCompression"/> is set.</summary>
    [ObservableProperty] private double _cursorPinDbm;

    /// <summary>
    /// §1 (R1C) — Display ▸ Cursor Snap to Compression. R-h6-11's own toggle, relocated from the
    /// removed toolbar to a menu item since it had "nowhere else" per §1's own affordance check.
    /// Turning snap OFF freezes the cursor where it currently reads, so the operating point does not
    /// silently jump the instant the toggle changes.
    /// </summary>
    public void ToggleCursorSnap()
    {
        if (SnapCursorToCompression) CursorPinDbm = OperatingPointDbm;
        SnapCursorToCompression = !SnapCursorToCompression;
        RequestScheduledFrame(dragging: false);
    }

    /// <summary>
    /// R-h6-11's <i>re-converge at compression</i>. <b>DEFAULT OFF</b>, per §6.6: it is ~10× the cost
    /// and ill-conditioned where the gain curve is flat. When on, a converged inverse step is followed
    /// by a fresh compression search and, if the compression point moved, one more solve at the new
    /// drive — which discards the Jacobian, and is exactly why it costs what it costs.
    /// </summary>
    [ObservableProperty] private bool _reconvergeAtCompression;

    /// <summary>The cursor Pin to hand the solver, or null to let it use the compression point.</summary>
    public double? PlacedCursorPinDbm => SnapCursorToCompression ? null : CursorPinDbm;

    /// <summary>
    /// The drive an inverse solve is posed at: the power-sweep cursor's Pin, read off the last
    /// published frame so it is the SAME operating point the glyphs on screen were evaluated at.
    /// </summary>
    public double OperatingPointDbm
    {
        get
        {
            if (!SnapCursorToCompression) return CursorPinDbm;
            var ps = Frame.PowerSweep;
            return ps.CursorIndex >= 0 && ps.CursorIndex < ps.PinAvailDbm.Length
                ? ps.PinAvailDbm[ps.CursorIndex]
                : CursorPinDbm;
        }
    }

    // ── M2 — the inverse solve, kept per R8C §5.1 ("keep the code"), unreferenced from the drag ────
    // path (which now runs IntrinsicAbcd's closed form instead). RequestInverseFrame below is
    // consequently unreachable from this class too — nothing here still calls it — and stays only so
    // the inverse solve can be re-wired later without rewriting it. Explicit `= null` initializers so
    // the compiler does not read "no assignment anywhere" as a mistake.

    private InverseSolver?  _inverse = null;
    private HarmonicaMarker? _inverseMarker = null;
    private InverseBand[]   _inverseBands  = [];
    private Complex[]       _inverseTargets = [];

    /// <summary>R-h6-12's cache. Shared across pool workers, so it is guarded — the region depends on
    /// the model and the band, not on which worker happened to sample it.</summary>
    private readonly System.Threading.Lock _reachGate = new();
    private Reachability.Key? _reachKey;
    private ReachableRegion?  _reach;

    /// <summary>
    /// Open item 4, settled by measurement (see <c>InverseSolveCostTests</c>): sampling costs ~53 ms
    /// at the shipping density and is paid ONCE per drag, not per frame. That is under two frames of a
    /// 33 ms budget for the whole gesture, so it was AUTOMATIC while an intrinsic drag ran the inverse
    /// solve — this stays a property so a slow model can still be told not to.
    ///
    /// <para>R8C §5.3 — DEFAULTS OFF. The region shaded the set of intrinsic Γ the (retired) inverse
    /// solve could reach. Under the closed-form ABCD inversion every target is reachable except at the
    /// map's own pole, so the shading answers a question that no longer has an interesting answer. The
    /// sampler and <c>DrawReachableRegion</c> stay in place — nothing here is deleted, only defaulted
    /// off — in case the inverse solve is ever re-enabled.</para>
    /// </summary>
    [ObservableProperty] private bool _showReachableRegion = false;

    /// <summary>How many times the reachable region has actually been sampled. R-h6-12's "cached"
    /// claim, as a counter rather than a clock.</summary>
    public int ReachabilitySampleCount { get; private set; }

    /// <summary>The most recent inverse-solve failure, in words, or null. Surfaces in the status
    /// strip so a glyph that refuses to move says why (R-h6-9).</summary>
    [ObservableProperty] private string? _inverseMessage;

    /// <summary>Whether an intrinsic drag is in progress.</summary>
    public bool IsInverseDragging => _inverse is not null;

    /// <summary>The inverse solver this drag is running, or null. Exposed so a test can read its
    /// counters — the FD-refresh rate is a number this phase owes a report on.</summary>
    public InverseSolver? Inverse => _inverse;

    /// <summary>
    /// R8C §5.1 — the intrinsic drag no longer runs the inverse solve; §5.3's closed form needs no
    /// per-gesture setup. Kept (rather than removed) because <c>HarmonicaPointer</c> still calls it on
    /// grab, and because <see cref="InverseSolver"/>/<see cref="RequestInverseFrame"/> stay in the
    /// tree — the owner said "keep the code" — reachable only from their own tests now.
    /// </summary>
    public void BeginIntrinsicDrag(HarmonicaMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        InverseMessage = null;
    }

    /// <summary>R8C §5.3 — the pole-proximity refusal bound, ohms. See <see cref="DragIntrinsicGlyph"/>'s
    /// own remark for why this is a magnitude bound rather than a literal finiteness check.</summary>
    public const double PoleMagnitudeOhms = 1e9;

    /// <summary>
    /// R8C §5.3 — one frame of an intrinsic drag: the closed-form ABCD back-calculation
    /// (<see cref="IntrinsicAbcd"/>), on the UI thread, no solve. <see
    /// cref="CircuitModel.IntrinsicDragAllowed"/> is checked at GRAB time
    /// (<c>HarmonicaHitTest</c>'s Pass 2, which is what stops this being reachable at all when the
    /// predicate is false); this call trusts that rather than re-checking the full predicate.
    /// </summary>
    public long DragIntrinsicGlyph(HarmonicaMarker marker, Complex targetIntrinsicGamma, bool dragging)
    {
        ArgumentNullException.ThrowIfNull(marker);

        var side = marker.Side == TerminationSideKind.Source ? TerminationSide.Source : TerminationSide.Load;
        var zIntr = HarmonicaDataSet.ImpedanceOf(targetIntrinsicGamma, Model.Settings.Z0);
        var zExt  = IntrinsicAbcd.ExtrinsicFor(Model, side, marker.Band, zIntr);

        // The map's own pole (−C·Z_intr + A → 0): the requested intrinsic impedance is not producible
        // by any finite extrinsic termination. A drag target essentially never lands EXACTLY on the
        // pole (it is a single point on a continuous Γ plane), so the guard is a magnitude bound, not
        // literal double.IsFinite — a near-pole target still blows the denominator up to a huge but
        // technically finite double (measured: ~7e17 Ω one ULP off the true pole), which is just as
        // unusable a termination as an infinite one. PoleMagnitudeOhms is comfortably above any real
        // termination (tens to low thousands of ohms) and comfortably below where double precision
        // itself starts to lose meaning. Refuse the frame — leave the marker exactly where it was
        // (R-h6-9's precedent: a failed solve moves NOTHING, no partial application).
        if (!double.IsFinite(zExt.Real) || !double.IsFinite(zExt.Imaginary) ||
            zExt.Magnitude > PoleMagnitudeOhms)
        {
            InverseMessage = "That intrinsic target is not reachable by any finite termination here.";
            return RequestScheduledFrame(dragging);
        }

        InverseMessage = null;
        SetMarkerImpedance(marker, zExt);   // markers update LIVE
        return RequestFrameOnMarkerRelease(marker.Side, marker.Band, dragging);
    }

    /// <summary>R8C §5.1 — no-op; clears the status message a refused drag may have left.</summary>
    public void EndIntrinsicDrag()
    {
        InverseMessage = null;
    }

    /// <summary>
    /// Submits one inverse frame to the pool. <b>The UI thread never solves</b> — the inverse solve
    /// runs on a worker exactly as the forward one does, against that worker's own context, and the
    /// answer comes back on the frame for the view to apply.
    /// </summary>
    private long RequestInverseFrame(bool dragging, bool forceSkipContours = false)
    {
        var solver  = _inverse!;
        var targets = (Complex[])_inverseTargets.Clone();
        var bands   = _inverseBands;
        var plan    = Scheduler.NextPlan(dragging);
        LastPlan    = plan;

        var baseOptions = OptionsFor(plan, dragging);
        if (forceSkipContours) baseOptions = baseOptions with { SkipContours = true };
        var model  = Model;
        var marks  = Markers.ToArray();
        var band   = _inverseMarker is { } m
            ? new InverseBand(m.Side == TerminationSideKind.Source
                                  ? TerminationSide.Source : TerminationSide.Load, m.Band)
            : bands[0];
        bool wantReach = ShowReachableRegion;
        bool reconverge = ReconvergeAtCompression;
        var  solverOwn = solver;
        var  onProgress    = GridProgressReporter(baseOptions);
        var  readoutFormat = ReadoutFormatLookup();

        // R-h9r2-1 — same snapshot-before-submit convention as RequestFrame/RequestForcedFrame.
        var prevPower = Frame.SmithPower;
        var prevEff   = Frame.SmithEfficiency;

        CancelNotice  = null;   // a new request supersedes any stop the user asked for
        IsSolving     = true;
        IsSolvingGrid = !baseOptions.SkipContours;
        return _pool.Submit((worker, ct) =>
        {
            var ctx = worker.EnsureContext(model);

            var failure = solverOwn.IsStarted ? InverseFailure.None : solverOwn.Begin(ctx, ct);
            InverseSolveResult? step = null;
            if (failure == InverseFailure.None)
            {
                step = solverOwn.Step(ctx, targets, ct);
                failure = step.Failure;

                // R-h6-11's outer loop, default OFF. Re-find compression on the answer and, if the
                // drive moved, solve once more there. SetOperatingPoint discards the Jacobian, which
                // is where the ~10× comes from.
                if (reconverge && step.Converged)
                {
                    var solved = solverOwn.TerminationsFor(step.Gammas);
                    var sweep  = PinSearch.Run(ctx, solved);
                    if (sweep.AtCompression is { } atc &&
                        Math.Abs(atc.PavlDbm - solverOwn.PavlDbm) > 0.05)
                    {
                        solverOwn.SetOperatingPoint(atc.PavlDbm);
                        var again = solverOwn.Step(ctx, targets, ct);
                        if (again.Converged) { step = again; failure = InverseFailure.None; }
                    }
                }
            }

            var terms = solverOwn.TerminationsFor(step?.Gammas ?? [.. solverOwn.Current]);

            var reach = wantReach ? EnsureReachable(ctx, terms, band, solverOwn.PavlDbm, ct) : null;

            // The markers the WORKER draws against must carry the solved terminations, or the frame
            // would show the glyphs at their new positions and the markers at their old ones.
            if (step is { Converged: true })
                for (int i = 0; i < marks.Length && i < step.Gammas.Length; i++)
                    marks[i].Gamma = step.Gammas[i];

            var frame = _solver.Solve(ctx, terms, marks, baseOptions with { Reachable = reach },
                                      worker.Grid, ct, onProgress, readoutFormat, prevPower, prevEff);

            return frame with
            {
                Inverse = new InverseOutcome(step?.Converged ?? false, failure, bands,
                                             step?.Gammas ?? [.. solverOwn.Current],
                                             step?.Residual ?? double.NaN),
            };
        });
    }

    private ReachableRegion? EnsureReachable(HarmonicaContext ctx, TerminationSet terms,
                                             InverseBand band, double pavlDbm,
                                             System.Threading.CancellationToken ct)
    {
        var key = Reachability.KeyFor(ctx.Model, band, pavlDbm);
        lock (_reachGate)
        {
            if (_reachKey == key && _reach is not null) return _reach;
        }

        var sampled = Reachability.Sample(ctx, terms, band, pavlDbm, ct: ct);

        lock (_reachGate)
        {
            _reach    = sampled;
            _reachKey = key;
            ReachabilitySampleCount++;
        }
        return sampled;
    }

    /// <summary>
    /// Writes a converged inverse result into the terminations and the markers. Called from
    /// <see cref="PublishFrame"/>, i.e. on the UI thread, because these are UI-visible state.
    ///
    /// <para>R-h6-9 — a FAILED outcome carries the unchanged vector, so applying it is a no-op by
    /// construction rather than by a branch somebody has to remember to write.</para>
    /// </summary>
    private void ApplyInverseOutcome(InverseOutcome outcome)
    {
        if (outcome.Converged)
        {
            for (int i = 0; i < outcome.Bands.Length && i < outcome.Gammas.Length; i++)
            {
                var b = outcome.Bands[i];
                Terminations.Set(b.Side, b.Band,
                                 HarmonicaDataSet.ImpedanceOf(outcome.Gammas[i], Model.Settings.Z0));
                var marker = Markers.FirstOrDefault(m => m.Band == b.Band &&
                    (m.Side == TerminationSideKind.Source) == (b.Side == TerminationSide.Source));
                if (marker is not null) marker.Gamma = outcome.Gammas[i];
            }
            InverseMessage = null;
            DirtyChanged?.Invoke();
        }
        else
        {
            InverseMessage = outcome.Failure switch
            {
                InverseFailure.NotConverged =>
                    "That intrinsic target is not reachable from any extrinsic termination — nothing moved.",
                InverseFailure.HbFailed =>
                    "The harmonic-balance solve did not converge on the way there — nothing moved.",
                InverseFailure.ActiveSourceFundamental =>
                    "Reaching that target needs an active fundamental source termination, where " +
                    "available power is undefined — nothing moved.",
                InverseFailure.Singular =>
                    "The intrinsic map is locally degenerate here — nothing moved.",
                _ => null,
            };
        }
    }

    /// <summary>
    /// R9D §2 — writes S1 to conj(Zin) at the reported backoff and asks for a normal frame, which is what
    /// re-renders the marker and regenerates the iso-lines ("as per normal usage"). A NOT-FOUND outcome
    /// writes nothing and only sets the message — R-h6-9's rule ("nothing but a converged solve may write
    /// a termination") applies here for the same reason: a marker that lands somewhere the solve did not
    /// actually reach is worse than one that does not move.
    ///
    /// <para><b>Interaction with brief-harmonicarf-r9a §11:</b> that brief blanks the message line while
    /// a gesture is live. This message is posted from a menu click, never mid-drag, so the two do not
    /// collide.</para>
    /// </summary>
    private void ApplyConjugateMatch(ConjugateMatchOutcome match)
    {
        if (!match.Found) { InverseMessage = match.Reason; return; }

        var s1 = Markers.FirstOrDefault(m => m.Side == TerminationSideKind.Source && m.Band == 1);
        if (s1 is null) return;                       // the marker was removed between request and reply

        SetMarkerImpedance(s1, Complex.Conjugate(match.Zin));
        InverseMessage = $"S1 set to conj(Zin) = {HarmonicaReadoutFormatting.FormatZ(Complex.Conjugate(match.Zin), ReadoutFormat.RealImaginary)} " +
                         $"at {match.ActualBackoffDb:0.0} dB backoff (Pin {match.PinDbm:0.0} dBm).";
        RequestScheduledFrame(dragging: false);
    }

    /// <summary>R9D §2.5 — the S1 marker menu's "Match to Zin*" default backoff.</summary>
    public const double ConjugateMatchBackoffDb = 5.0;

    /// <summary>R9D §2.5 — the S1 marker's "Match to Zin*" command: a measurement-only frame
    /// (<c>SkipContours</c>) that reports the backoff Zin, followed by <see cref="ApplyConjugateMatch"/>'s
    /// own real re-solve once it lands. Two frames, one grid.</summary>
    public long RequestConjugateMatch(double backoffDb)
        => RequestFrame(OptionsFor(Scheduler.NextPlan(false), dragging: false) with
        {
            ConjugateMatchBackoffDb = backoffDb,
            SkipContours = true,
        });

    // ── R9D §3 — PA-class preset terminations ───────────────────────────────

    /// <summary>
    /// Markers ▸ Preset Terminations ▸ Class B / J / J* / F / F⁻¹ (§3.6). Writes ONLY the Load-side
    /// markers that already exist (§3.2 — a preset never CREATES a marker), computing each band's
    /// INTRINSIC target from <see cref="PaClassPresets"/> and transforming it to the extrinsic plane
    /// through <see cref="IntrinsicAbcd"/> (§3.3).
    ///
    /// <para><b>"Best effort", per §3.4.</b> A nonlinear capacitor is replaced by its LINEARIZED value
    /// (the same number the readout strip already shows, from <see cref="HarmonicaSolver.
    /// LinearizedCapacitanceFarads"/>) for the transform only — never written back to <see
    /// cref="Model"/> — and the transform then proceeds normally. Every OTHER refusal in
    /// <see cref="CircuitModel.IntrinsicDragAllowed"/>'s table (a non-SDD DUT, a non-absent Cdg, or a
    /// package that couples the input and output loops) writes the intrinsic values straight AT the
    /// extrinsic plane instead, and says so — never a solver, per the owner's own instruction.</para>
    /// </summary>
    public void ApplyPaClassPreset(PaClass paClass)
    {
        var loadMarkers = Markers.Where(m => m.Side == TerminationSideKind.Load).ToArray();
        if (loadMarkers.Length == 0) { InverseMessage = null; return; }

        double z0   = Model.Settings.Z0;
        var    caps = Model.Dut.Capacitances;

        CircuitModel transformModel = Model;
        string? linearizedFallbackNote = null;
        if (caps.Cgs.IsNonlinear || caps.Cdg.IsNonlinear || caps.Cds.IsNonlinear)
        {
            var ctx = EnsureContext();
            var (cgs, cgsFellBack) = LinearizeForTransform(ctx, caps.Cgs, DutCapacitanceKind.Cgs);
            var (cdg, cdgFellBack) = LinearizeForTransform(ctx, caps.Cdg, DutCapacitanceKind.Cdg);
            var (cds, cdsFellBack) = LinearizeForTransform(ctx, caps.Cds, DutCapacitanceKind.Cds);

            // R9D §3.4 — a model COPY, used for the transform only: substituting a linearized
            // capacitor into the document would change the circuit the engine solves, which is not
            // what "best effort" means.
            transformModel = Model with
            {
                Dut = Model.Dut with { Capacitances = new DutCapacitances
                {
                    Cgs = cgs, Cdg = cdg, Cds = cds, RgsOhms = caps.RgsOhms,
                } },
            };

            if (cgsFellBack || cdgFellBack || cdsFellBack)
                linearizedFallbackNote = "no linearized value was available for a nonlinear capacitor " +
                    "(nothing solved yet, or the intrinsic plane is not located) — its own C(V=0) " +
                    "coefficient was used for the transform instead.";
        }

        bool bestEffortAtExtrinsic = !CircuitModel.IntrinsicDragAllowed(transformModel, out string refusalReason);
        var poleFailures = new List<string>();

        foreach (var marker in loadMarkers)
        {
            var zIntr = PaClassPresets.IntrinsicLoad(paClass, marker.Band, z0);
            Complex zWrite;
            if (bestEffortAtExtrinsic)
            {
                zWrite = zIntr;
            }
            else
            {
                var zExt = IntrinsicAbcd.ExtrinsicFor(transformModel, TerminationSide.Load, marker.Band, zIntr);
                if (!double.IsFinite(zExt.Real) || !double.IsFinite(zExt.Imaginary))
                {
                    poleFailures.Add(marker.Name);
                    continue;                          // R9D §3.3 item 1 — left unchanged, never clamped.
                }
                zWrite = zExt;
            }
            SetMarkerImpedance(marker, zWrite);
        }

        var messages = new List<string>();
        if (bestEffortAtExtrinsic)
            messages.Add($"Preset applied at the EXTRINSIC plane — {refusalReason} " +
                         "The intrinsic terminations will differ.");
        if (poleFailures.Count > 0)
            messages.Add($"{string.Join(", ", poleFailures)} could not be transformed to the extrinsic " +
                         $"plane and {(poleFailures.Count == 1 ? "was" : "were")} left as " +
                         $"{(poleFailures.Count == 1 ? "it was" : "they were")}.");
        if (linearizedFallbackNote is not null) messages.Add(linearizedFallbackNote);

        InverseMessage = messages.Count > 0 ? string.Join(" ", messages) : null;
        RequestScheduledFrame(dragging: false);
    }

    /// <summary>R9D §3.4 — the strip's own "linearized, or fall back to C(V=0)" pattern
    /// (<see cref="Inputs"/>), reused verbatim for the transform. A linear/absent capacitor passes
    /// through untouched.</summary>
    private (DutCapacitance Cap, bool FellBack) LinearizeForTransform(
        HarmonicaContext ctx, DutCapacitance cap, DutCapacitanceKind kind)
    {
        if (!cap.IsNonlinear) return (cap, false);

        double? linearized = HarmonicaSolver.LinearizedCapacitanceFarads(ctx, Frame.Published, cap.Coefficients!, kind);
        bool haveLinearized = linearized is { } lf && double.IsFinite(lf);
        double farads = haveLinearized ? linearized!.Value : cap.Coefficients![0];
        return (new DutCapacitance { Farads = farads }, !haveLinearized);
    }

    // ── persistence ──────────────────────────────────────────────────────────

    public string ToCharmJson()
        => CharmIo.Write(Model, Terminations, Appearance, Layout,
                         [.. PickedTraces.Select(t => new CharmIo.CharmTrace(t.Spec, t.PanelId, t.Label))],
                         [.. Markers.Where(m => m.VswrEnabled)
                                    .Select(m => new CharmIo.CharmMarkerVswr(
                                        m.Side == TerminationSideKind.Source ? TerminationSide.Source : TerminationSide.Load,
                                        m.Band, m.VswrValue))],
                         [.. AddedGridPoints]);

    /// <summary>Loads a <c>.charm</c>. Unresolved references come back so the caller can offer to
    /// re-point them rather than losing the document.</summary>
    public IReadOnlyList<CharmIo.UnresolvedReference> LoadCharm(string json, string? baseDirectory)
    {
        var c = CharmIo.ReadAll(json, baseDirectory);

        // R-h9b-7 — the live toggle is kept in step with the persisted appearance on load, so a
        // reopened .charm shows grid points exactly as it was left rather than always at the default.
        ShowGridPoints = c.Appearance.ShowGridPoints ?? false;
        // brief-harmonicarf-r5 §1 — same "kept in step with the persisted appearance on load" rule.
        // The rolling window itself is NOT restored (it is session-only diagnostics, not document
        // state) — only the toggle's on/off is.
        ShowDiagnosticsOverlay = c.Appearance.ShowDiagnosticsOverlay ?? false;
        // brief-harmonicarf-r6d §4 — same "kept in step with the persisted appearance on load" rule.
        ShowPowerSweepTimeDomain = c.Appearance.ShowPowerSweepTimeDomain ?? false;

        Model = c.Model;
        _ctx  = null;                                   // structure may have changed entirely

        Terminations = c.Terminations;
        RebuildMarkersFromTerminations();

        // R-h9r2-8 — VSWR overlay state is per-marker session state that RebuildMarkersFromTerminations
        // just wiped (it rebuilds Markers wholesale); re-apply what was persisted, matched by Side/Band.
        foreach (var entry in c.Vswr)
        {
            var side = entry.Side == TerminationSide.Source ? TerminationSideKind.Source : TerminationSideKind.Load;
            var marker = Markers.FirstOrDefault(m => m.Side == side && m.Band == entry.Band);
            if (marker is not null)
            {
                marker.VswrEnabled = true;
                marker.VswrValue   = entry.Value;
            }
        }

        Appearance = c.Appearance;
        Layout     = c.Layout;

        // brief-harmonicarf-r6b §2.2 — added grid points persist in the .charm.
        AddedGridPoints.Clear();
        foreach (var p in c.AddedGridPoints) AddedGridPoints.Add(p);

        PickedTraces.Clear();
        foreach (var t in c.Traces)
            PickedTraces.Add(new HarmonicaPickedTrace(t.Spec, t.PanelId, t.Label));

        EditDisplay.Undo.Clear();       // the history described a document that is gone
        return c.Unresolved;
    }

    /// <summary>
    /// §4.2 — S1/L1 are always present; every other MARKED band becomes a marker. An unmarked band
    /// is not a marker with a default value, it is the absence of one, which is what makes "remove
    /// this marker" and "never had one" the same state on reload.
    /// </summary>
    private void RebuildMarkersFromTerminations()
    {
        Markers.Clear();
        foreach (var side in new[] { TerminationSideKind.Source, TerminationSideKind.Load })
        {
            var engineSide = side == TerminationSideKind.Source
                ? TerminationSide.Source : TerminationSide.Load;

            var bands = new SortedSet<int>(Terminations.MarkedBands(engineSide)) { 1 };
            foreach (int band in bands)
            {
                var m = new HarmonicaMarker(side, band);
                var z = Terminations.Z(engineSide, band);
                m.Gamma = HarmonicaDataSet.GammaOf(z, Model.Settings.Z0);
                Markers.Add(m);
            }
        }
        RedrawRequested?.Invoke();
    }

    // ── the fixture a brand-new document opens on ────────────────────────────

    /// <summary>
    /// R7B §3.8 — the owner's own variable form of Hero 2's GaN HEMT, replacing the folded-coefficient
    /// string this default used to carry. Substituting the constants back in reproduces the old
    /// <c>I[2,0]</c> string term for term — proved, not trusted, by
    /// <c>HarmonicaSddTextTests.DefaultModelEquationEquation_AgreesWithTheOldFoldedCoefficientForm</c>.
    /// </summary>
    private const string DefaultSddText =
        "Periphery_mm = 1.0\n" +
        "Sv = -0.837\n" +
        "Sc = 0.71\n" +
        "TV0 = 4.268\n" +
        "TC = 1.507\n" +
        "th = 0.001\n" +
        "a = 0.176\n" +
        "g = 0.089\n" +
        "lam = 0.0012\n" +
        "B = 1130\n" +
        "\n" +
        "I[1,0] = _v1/50\n" +
        "I[2,0] = Periphery_mm*(B*TC*tanh(_v2*a*(tanh(g*(TV0 - _v1 + _v2*th + Sc*ln(exp(-(Sv - _v1)/Sc) + 1)))+1))*ln(exp(-(2*TV0 - 2*_v1 +2*_v2*th + 2*Sc*ln(exp(-(Sv - _v1)/Sc) + 1))/TC) + 1) * (_v2*lam + 1))/2";

    /// <summary>
    /// A new harmonicaRF document opens on a real, converging device rather than an empty canvas —
    /// §1's whole claim is liveness, and a tool that opens showing nothing has to be configured
    /// before it can demonstrate anything. This is Hero 2's own GaN HEMT, self-contained and needing
    /// no globals.
    /// </summary>
    public static CircuitModel DefaultModel() => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            SddText    = DefaultSddText,
            Parameters = HarmonicaSddText.ToParameters(HarmonicaSddText.Parse(DefaultSddText, portCount: 2)),
        },
        Bias     = new BiasSpec { Vgs = -3.05, Vds = 48 },
        Settings = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = 2e9,
            // Round 10 (owner): the bias network is IDEAL — HarmonicaSettings' own 1 H / 1 F
            // defaults, no longer overridden to 1 µH / 1 nF here. A 1 µH choke is 12.57 kΩ at
            // 2 GHz, which shunts the source plane hard enough to be READ: the shipped device's
            // gate is a plain 50 Ω (I[1,0] = _v1/50) and Zin came back 49.9992 + j0.1989 Ω,
            // matching 50 ‖ jωL to twelve significant figures. The owner wants a clean 50, and an
            // ideal choke gives 49.999999998 + j2.0e-7. Same reasoning for the DC block, from the
            // other end — see BiasChokeHenries/DcBlockFarads' own doc comments, which already
            // record 1 H / 1 F as "the ideal-bias value (§4.4) and the default".
            Tol = 1e-8,
            CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = 34,
        },
    };
}
