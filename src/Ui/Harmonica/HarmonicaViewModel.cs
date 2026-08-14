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

        // §4.2 — S1 and L1 are ALWAYS present. S2…/L2… are added and removed from a menu (H7);
        // a band with no marker is 1e-6 Ω, which TerminationSet already treats as the unmarked state.
        Markers.Add(new HarmonicaMarker(TerminationSideKind.Source, 1));
        Markers.Add(new HarmonicaMarker(TerminationSideKind.Load,   1));
        SetMarkerImpedance(Markers[0], new Complex(25, 0));
        SetMarkerImpedance(Markers[1], new Complex(80, 10));

        // R-h9r2-1 (§2) — SUPERSEDES R-h9b-14's "sensible starting impedances" for S2/L2/L3. A new
        // document's default marker set is still S1, S2, L1, L2, L3 (AddMarkerBand refuses a band
        // above Terminations.HarmonicCount, asserted rather than relied on: DefaultModel's K=3 is
        // what makes all five fit) — but S2/L2/L3 now default to the SAME unmarked-band epsilon
        // TerminationSet itself already uses for "no marker at all" (Z = 1e-6 Ω), not a distinct
        // hand-picked value. S1 and L1 are UNCHANGED (25 Ω and 80+j10 Ω) — they are always present
        // and were never part of this brief's complaint.
        //
        // A LOADED .charm is completely unaffected: this only ever runs in the constructor's own
        // default-model path, never on load — RebuildMarkersFromTerminations (the load path) replaces
        // Markers wholesale from whatever TerminationSet the file actually carried.
        if (Terminations.HarmonicCount < 3)
            throw new InvalidOperationException(
                $"the default marker set needs harmonic bands 1..3, but this model's HarmonicCount is " +
                $"only {Terminations.HarmonicCount}");

        var unmarked = new Complex(TerminationSet.UnmarkedBandOhms, 0);
        SetMarkerImpedance(AddMarkerBand(TerminationSideKind.Source, 2), unmarked);
        SetMarkerImpedance(AddMarkerBand(TerminationSideKind.Load,   2), unmarked);
        SetMarkerImpedance(AddMarkerBand(TerminationSideKind.Load,   3), unmarked);
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
            ? f : ReadoutFormat.RealImaginary;
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
    partial void OnFrameChanged(HarmonicaFrame value)   => RedrawRequested?.Invoke();
    partial void OnLayoutChanged(CharmLayout value)     { RedrawRequested?.Invoke(); DirtyChanged?.Invoke(); }

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
        return marker;

        static int Rank(HarmonicaMarker m)
            => (m.Side == TerminationSideKind.Source ? 0 : 1000) + m.Band;
    }

    /// <summary>
    /// R-h7-2 — removes a band marker. <b>The termination entry is REMOVED too</b>, not reset:
    /// §4.2 says an unmarked band is the <i>absence</i> of a marker rather than a marker with a
    /// default value, and <see cref="TerminationSet.Remove"/> is what expresses that. Band 1 refuses,
    /// on both sides — it is the fundamental.
    /// </summary>
    public bool RemoveMarkerBand(TerminationSideKind side, int band)
    {
        if (band == 1) return false;

        var marker = Markers.FirstOrDefault(m => m.Side == side && m.Band == band);
        if (marker is null) return false;

        Terminations.Remove(side == TerminationSideKind.Source
                                ? TerminationSide.Source : TerminationSide.Load,
                            band);
        Markers.Remove(marker);

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
    /// re-solve. Refuses band 1 exactly as <see cref="RemoveMarkerBand"/> already does, on both sides.
    /// </summary>
    public bool RemoveMarkerAndShort(HarmonicaMarker marker)
    {
        if (!RemoveMarkerBand(marker.Side, marker.Band)) return false;
        RequestScheduledFrame(dragging: false);
        return true;
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
    /// R-h9r2-8 — writes a marker's VSWR-circle radius, from a drag on its handle.
    ///
    /// <para><b>No re-solve, unlike every other marker drag.</b> The overlay is a display annotation
    /// over an already-solved termination (§9's own framing) — it neither reads nor writes anything the
    /// circuit depends on, so there is no frame to request. <see cref="RedrawRequested"/> alone is
    /// enough to move the circle on screen; <see cref="DirtyChanged"/> marks the document unsaved, the
    /// same as every other <c>.charm</c>-persisted edit.</para>
    /// </summary>
    public void SetMarkerVswr(HarmonicaMarker marker, double vswr)
    {
        marker.VswrValue = HarmonicaVswrHandle.VswrOf(HarmonicaVswrHandle.RhoOf(vswr));
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
        => HarmonicaInputs.Build(Model, Model.Bias.Idq is null ? EnsureContext().DcDrainCurrentAmps : null);

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

    /// <summary>
    /// Moves the marker set onto a new harmonic count. A band above the new K is DROPPED — with its
    /// marker — rather than clamped: clamping would put two markers on one band, and the file format
    /// has no way to express that.
    /// </summary>
    private void RetargetTerminations(int harmonicCount)
    {
        var next = new TerminationSet(harmonicCount);
        foreach (var side in new[] { TerminationSide.Source, TerminationSide.Load })
            foreach (int band in Terminations.MarkedBands(side).ToArray())
                if (band <= harmonicCount)
                    next.Set(side, band, Terminations.Z(side, band));

        Terminations = next;
        RebuildMarkersFromTerminations();
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

    /// <summary>
    /// Solves one frame and publishes it. <b>Never throws</b> — a live instrument that dies on a bad
    /// parameter is not a live instrument; the failure lands in <see cref="SolveError"/> and the
    /// previous frame stays on screen.
    /// </summary>
    public void SolveFrame(HarmonicaSolver.Options? options = null)
    {
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

        Frame         = frame with { PowerSweep = frame.PowerSweep with { XUnit = PowerSweepXUnit } };
        SolveError    = null;
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
            if (InverseMessage is { Length: > 0 } inv) return inv;
            return Scheduler.StatusMessage;
        }
    }

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

    /// <summary>The Grid menu's ring presets. Clears any custom grid — a preset and an imported
    /// scatter are alternatives, and keeping both would leave the menu lying about what is drawn.</summary>
    public void SetGridPreset(int rings, int spokes)
    {
        CustomGrid = null;
        Scheduler.SetGridPreset(rings, spokes);
        ResetSchedule();
        DirtyChanged?.Invoke();
        RequestScheduledFrame(dragging: false);
    }

    /// <summary>Grid ▸ Reset grid — back to the ladder's own ring set.</summary>
    public void ResetGrid()
    {
        CustomGrid = null;
        Scheduler.SetGridPreset(null, null);
        ResetSchedule();
        DirtyChanged?.Invoke();
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
        Rings = 5, Spokes = 12,
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

    // ── M2 — the inverse solve, wired to the pool ────────────────────────────

    private InverseSolver?  _inverse;
    private HarmonicaMarker? _inverseMarker;
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
    /// 33 ms budget for the whole gesture, so it is AUTOMATIC rather than opt-in — this stays a
    /// property so a slow model can still be told not to.
    /// </summary>
    [ObservableProperty] private bool _showReachableRegion = true;

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
    /// Starts an intrinsic drag on <paramref name="marker"/>'s glyph.
    ///
    /// <para><b>Every marked band becomes an unknown, and every OTHER glyph's present value becomes a
    /// target</b> (R-h6-6). The other targets are frozen HERE rather than re-read each frame: re-reading
    /// would let each frame's own answer become the next frame's requirement, and the constraint
    /// "these do not move" would quietly become "these may drift as far as one frame at a time
    /// allows".</para>
    /// </summary>
    public void BeginIntrinsicDrag(HarmonicaMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);

        _inverseBands = [.. Markers.Select(m => new InverseBand(
            m.Side == TerminationSideKind.Source ? TerminationSide.Source : TerminationSide.Load,
            m.Band))];

        if (_inverseBands.Length == 0) { _inverse = null; return; }

        var start = _inverseBands
            .Select(b => HarmonicaDataSet.GammaOf(Terminations.Z(b.Side, b.Band), Model.Settings.Z0))
            .ToArray();

        _inverseTargets = [.. Markers.Select(m => m.GammaIntrinsic)];
        _inverseMarker  = marker;
        _inverse        = new InverseSolver(Terminations, _inverseBands, start,
                                            new InverseSolveOptions
                                            {
                                                PavlDbm = OperatingPointDbm,
                                                Z0      = Model.Settings.Z0,
                                            });
        InverseMessage  = null;
    }

    /// <summary>
    /// One frame of an intrinsic drag: put <paramref name="marker"/>'s glyph at
    /// <paramref name="targetGamma"/> and hold every other glyph where it was.
    /// </summary>
    public long DragIntrinsicGlyph(HarmonicaMarker marker, Complex targetGamma, bool dragging)
    {
        if (_inverse is null) return RequestScheduledFrame(dragging);

        int idx = Markers.IndexOf(marker);
        if (idx >= 0 && idx < _inverseTargets.Length) _inverseTargets[idx] = targetGamma;

        // R-h9r2-3 — applies identically to the extrinsic case: on release, if the dragged marker's
        // own band is the plane/band the contour grid currently sweeps (Finding B), the grid is
        // skipped there too rather than re-solved for a result the swept band's own value could not
        // have changed.
        bool forceSkip = !dragging && IsSweptBand(marker.Side, marker.Band);
        return RequestInverseFrame(dragging, forceSkip);
    }

    /// <summary>Ends the drag. The solver — and with it the Jacobian — is dropped; the next drag
    /// starts from a fresh FD build, because the operating point it was measured at is gone.</summary>
    public void EndIntrinsicDrag()
    {
        _inverse       = null;
        _inverseMarker = null;
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

    // ── persistence ──────────────────────────────────────────────────────────

    public string ToCharmJson()
        => CharmIo.Write(Model, Terminations, Appearance, Layout,
                         [.. PickedTraces.Select(t => new CharmIo.CharmTrace(t.Spec, t.PanelId, t.Label))],
                         [.. Markers.Where(m => m.VswrEnabled)
                                    .Select(m => new CharmIo.CharmMarkerVswr(
                                        m.Side == TerminationSideKind.Source ? TerminationSide.Source : TerminationSide.Load,
                                        m.Band, m.VswrValue))]);

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
    /// A new harmonicaRF document opens on a real, converging device rather than an empty canvas —
    /// §1's whole claim is liveness, and a tool that opens showing nothing has to be configured
    /// before it can demonstrate anything. This is Hero 2's own GaN HEMT with its coefficients folded
    /// into the equation text, so the document is self-contained and needs no globals.
    /// </summary>
    public static CircuitModel DefaultModel() => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/50",
                ["I[2,0]"] = "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2",
            },
        },
        Bias     = new BiasSpec { Vgs = -3.05, Vds = 48 },
        Settings = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = 2e9,
            BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-8,
            CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = 34,
        },
    };
}
