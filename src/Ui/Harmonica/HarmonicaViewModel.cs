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

    /// <summary>The most recently solved frame. The panels render this and nothing else.</summary>
    [ObservableProperty] private HarmonicaFrame _frame = HarmonicaFrame.Empty;

    /// <summary>Non-null when the last solve failed. Shown in the status strip rather than thrown —
    /// a live tool that throws on a bad parameter is unusable.</summary>
    [ObservableProperty] private string? _solveError;

    [ObservableProperty] private bool _isSolving;

    /// <summary>§7.3's plane toggle — ONE toggle, moving the DCIV family and the loadline together.</summary>
    [ObservableProperty] private bool _intrinsicPlane = true;

    /// <summary>§7.4's click-to-cycle X-axis unit.</summary>
    [ObservableProperty] private PowerSweepXUnit _powerSweepXUnit = PowerSweepXUnit.PoutDbm;

    /// <summary>D11 — iso-line labels default OFF. The default setting is also the fast one.</summary>
    [ObservableProperty] private bool _showIsoLineLabels;

    /// <summary>Raised whenever the frame, the theme or a marker changes and the canvas must repaint.</summary>
    public event Action? RedrawRequested;

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
        marker.Gamma = (z - 50.0) / (z + 50.0);
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

    /// <summary>Markers ▸ Reset to defaults — back to S1 and L1 alone, at 50 Ω.</summary>
    public void ResetMarkers()
    {
        Terminations = new TerminationSet(Model.Settings.HarmonicCount);
        RebuildMarkersFromTerminations();
        DirtyChanged?.Invoke();
    }

    /// <summary>Writes a marker's termination as Γ — what a drag on a Smith panel produces.</summary>
    public void SetMarkerGamma(HarmonicaMarker marker, Complex gamma)
    {
        // Γ = 1 is an open; the impedance is infinite. Nudge off the rim rather than producing a
        // non-finite Z that would take the whole solve down.
        double mag = gamma.Magnitude;
        if (mag > 0.999) gamma = gamma / mag * 0.999;
        SetMarkerImpedance(marker, 50.0 * (Complex.One + gamma) / (Complex.One - gamma));
    }

    // ── R-h7-3 — the §7.5 inputs ─────────────────────────────────────────────

    /// <summary>§7.5's input half, rebuilt from the current model. The strip renders this.</summary>
    public IReadOnlyList<HarmonicaInput> Inputs => HarmonicaInputs.Build(Model);

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

        Model = updated;

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
            var frame = _solver.Solve(ctx, Terminations, [.. Markers], opt);
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
    public long RequestFrame(HarmonicaSolver.Options? options = null)
    {
        var opt   = (options ?? new HarmonicaSolver.Options()) with { IntrinsicPlane = IntrinsicPlane };
        var model = Model;
        var terms = Terminations.Clone();
        var marks = Markers.ToArray();

        IsSolving = true;
        return _pool.Submit((worker, ct) =>
        {
            var ctx = worker.EnsureContext(model);
            return _solver.Solve(ctx, terms, marks, opt, worker.Grid, ct);
        });
    }

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

        Frame      = frame with { PowerSweep = frame.PowerSweep with { XUnit = PowerSweepXUnit } };
        SolveError = null;
        IsSolving  = false;

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
        SolveError = ex.Message;
        IsSolving  = false;
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
        return RequestFrame(OptionsFor(plan));
    }

    /// <summary>One place a <see cref="FramePlan"/> becomes solver options, so the scheduled path and
    /// the inverse-drag path cannot drift apart about what a rung means.</summary>
    private HarmonicaSolver.Options OptionsFor(FramePlan plan) => new()
    {
        Rings            = plan.Rings,
        Spokes           = plan.Spokes,
        RasterResolution = plan.RasterResolution,
        SkipContours     = plan.SkipContours,
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
    /// One frame of a grid-point drag: move sample <paramref name="index"/> to
    /// <paramref name="gamma"/> and re-solve <b>only that point</b> (R-h7-12).
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

        var next = grid.ToArray();
        next[index] = gamma;
        CustomGrid  = next;
        DirtyChanged?.Invoke();

        // The scheduler still owns the rung (R-h6-4); what this adds is the reuse flag, which is
        // what makes the frame cost one Γ sample rather than the whole grid.
        var plan = Scheduler.NextPlan(dragging);
        LastPlan = plan;
        return RequestFrame(OptionsFor(plan) with { ReuseUnchangedGridPoints = true });
    }

    /// <summary>Ends a grid-point drag.</summary>
    public void EndGridPointDrag() => _draggingGridPoint = false;

    /// <summary>How many Γ points the last frame kept rather than re-solved. R-h7-12's own gate.</summary>
    public int LastGridPointsReused => _solver.LastGridPointsReused;

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
            .Select(b => HarmonicaDataSet.GammaOf(Terminations.Z(b.Side, b.Band)))
            .ToArray();

        _inverseTargets = [.. Markers.Select(m => m.GammaIntrinsic)];
        _inverseMarker  = marker;
        _inverse        = new InverseSolver(Terminations, _inverseBands, start,
                                            new InverseSolveOptions { PavlDbm = OperatingPointDbm });
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

        return RequestInverseFrame(dragging);
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
    private long RequestInverseFrame(bool dragging)
    {
        var solver  = _inverse!;
        var targets = (Complex[])_inverseTargets.Clone();
        var bands   = _inverseBands;
        var plan    = Scheduler.NextPlan(dragging);
        LastPlan    = plan;

        var baseOptions = OptionsFor(plan);
        var model  = Model;
        var marks  = Markers.ToArray();
        var band   = _inverseMarker is { } m
            ? new InverseBand(m.Side == TerminationSideKind.Source
                                  ? TerminationSide.Source : TerminationSide.Load, m.Band)
            : bands[0];
        bool wantReach = ShowReachableRegion;
        bool reconverge = ReconvergeAtCompression;
        var  solverOwn = solver;

        IsSolving = true;
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
                                      worker.Grid, ct);

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
                Terminations.Set(b.Side, b.Band, HarmonicaDataSet.ImpedanceOf(outcome.Gammas[i]));
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
                         [.. PickedTraces.Select(t => new CharmIo.CharmTrace(t.Spec, t.PanelId, t.Label))]);

    /// <summary>Loads a <c>.charm</c>. Unresolved references come back so the caller can offer to
    /// re-point them rather than losing the document.</summary>
    public IReadOnlyList<CharmIo.UnresolvedReference> LoadCharm(string json, string? baseDirectory)
    {
        var c = CharmIo.ReadAll(json, baseDirectory);

        Model = c.Model;
        _ctx  = null;                                   // structure may have changed entirely

        Terminations = c.Terminations;
        RebuildMarkersFromTerminations();

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
                m.Gamma = (z - 50.0) / (z + 50.0);
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
