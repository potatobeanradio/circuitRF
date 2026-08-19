using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.WBond;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// The wBond editor's view-model: it owns the design, keeps the inductance current as the user edits,
/// and publishes the panel readout (wbond.md §6.8; brief-wbond-wbc WB-C2).
///
/// <h3>The one performance seam, and it is easy to lose</h3>
/// <para>There are two ways to bring the inductance up to date and they differ by two orders of
/// magnitude:</para>
/// <list type="bullet">
/// <item><b>A point move</b> — the drag case — goes through <see cref="IncrementalFill.MoveWires"/>:
///   2N−1 blocks, a rank-2 factor update, M triangular solves. <b>~5 ms at 600 wires.</b></item>
/// <item><b>A structural change</b> — adding or removing a wire or a point — needs a full
///   <see cref="WireMesh.Build"/> and a cold fill, because the flat filament layout gives each wire a
///   fixed span. <b>~150 ms at 600 wires.</b></item>
/// </list>
/// <para>Routing a drag down the structural path is invisible — the answer is identical — and it
/// turns a 60 fps editor into a 6 fps one. <see cref="RebuildCount"/> and
/// <see cref="IncrementalUpdateCount"/> exist so a test can assert which path was taken, because
/// nothing else can.</para>
/// </summary>
public sealed partial class WBondViewModel : ObservableObject
{
    private WBondDesign _design;
    private WireMesh _mesh;
    private IncrementalFill _fill;
    private readonly Stack<DesignSnapshot> _undo = new();
    private readonly Stack<DesignSnapshot> _redo = new();

    // Kept in lockstep with the two stacks above: entry N's EditSequence stamp, so a host holding
    // BOTH this history and the hosted Layout Editor's can answer "which did the user do last"
    // (WB39a). See CircuitRF.Ui.Commands.EditSequence.
    private readonly Stack<long> _undoStamps = new();
    private readonly Stack<long> _redoStamps = new();

    [ObservableProperty] private WireSelection _selection = new();

    /// <summary>
    /// What is being previewed right now — a live marquee's contents — or null when nothing is.
    ///
    /// <para><b>It lives here, on the shared view-model, rather than inside whichever canvas owns the
    /// gesture.</b> A marquee dragged in the profile view selects WIRES, and a wire is a thing both
    /// views draw; highlighting it in only the canvas the pointer happens to be over is half an
    /// answer. Publishing it once means either canvas can start a marquee and both show what it has
    /// caught, live, without either knowing the other exists.</para>
    ///
    /// <para><b>Never the committed selection.</b> It is dropped at release, and the release is what
    /// writes <see cref="Selection"/> — the L1i rule: the committed selection is also the base a
    /// Shift-marquee adds to, so a preview that wrote itself into it could never shrink again.</para>
    /// </summary>
    [ObservableProperty] private WireSelection? _previewSelection;

    /// <summary>
    /// What the canvases should DRAW as selected: the live preview while one is running, the committed
    /// selection otherwise. Every renderer reads this; nothing reads <see cref="Selection"/> to draw.
    /// </summary>
    public WireSelection EffectiveSelection => PreviewSelection ?? Selection;

    partial void OnPreviewSelectionChanged(WireSelection? value) =>
        OnPropertyChanged(nameof(EffectiveSelection));

    partial void OnSelectionChanged(WireSelection value) =>
        OnPropertyChanged(nameof(EffectiveSelection));

    [ObservableProperty] private WBondUnit _displayUnit = WBondUnit.Mil;

    /// <summary>
    /// <b>Which plane the profile view projects onto</b> — the toolbar's own setting (§6.2).
    ///
    /// <para>Null is AUTO: each wire projects onto its OWN chord, which is what makes wire angle and
    /// wire length stop being profile differences at all. A number fixes the plane — 0 for XZ, π/2
    /// for YZ, anything for a diagonal — and answers the other question, "what does this array look
    /// like from over there". It was previously derived from the geometry and merely LABELLED, which
    /// gave the user no way to ask for the other picture.</para>
    ///
    /// <para>The canvas, the hit test, the horizontal drag and the marquee all read this one value,
    /// so a point cannot render in one place and move in another.</para>
    /// </summary>
    /// <remarks>Defaults to YZ (owner, 2026-08-16) — <see cref="WBondViewState.DefaultProfileAxisDegrees"/>
    /// is the same number, stated once there and mirrored here so a view-model built without a
    /// document behind it opens on the same plane as one that was.</remarks>
    [ObservableProperty] private double? _profileAzimuthRadians =
        WBondViewState.DefaultProfileAxisDegrees * Math.PI / 180.0;

    /// <summary>The toolbar combo's text for <see cref="ProfileAzimuthRadians"/> — "Auto", "XZ", "YZ", or an angle.</summary>
    public string ProfileAxisText => ProfileAxisSetting.Format(ProfileAzimuthRadians);

    partial void OnProfileAzimuthRadiansChanged(double? value) => OnPropertyChanged(nameof(ProfileAxisText));

    /// <summary>
    /// Sets the profile plane from what the user typed or picked. Unparseable text is refused rather
    /// than silently reinterpreted — the view puts the combo back.
    /// </summary>
    public bool CommitProfileAxisText(string? text)
    {
        if (!ProfileAxisSetting.TryParse(text, out double? azimuth)) return false;

        ProfileAzimuthRadians = azimuth;
        return true;
    }

    /// <summary>Raised whenever the readout changes — the panel and the canvas both listen.</summary>
    public event Action? ReadoutChanged;

    /// <summary>Raised on any edit, so the document can mark itself dirty.</summary>
    public event Action? DirtyChanged;

    public WBondViewModel(WBondDesign? design = null)
    {
        _design = design ?? EmptyDesign();
        _mesh = WireMesh.Build(_design);
        _fill = IncrementalFill.Create(_mesh);

        RefreshCapacitance();
        Readout = PanelReadout.Build(_design, _mesh, _fill.Reduce(), _capacitance);
    }

    // ---------------------------------------------------------------- the two design-level settings

    /// <summary>
    /// Whether the readout includes capacitance to the reference plane — the toolbar's own toggle
    /// (wbond.md §3.7, §6.8).
    ///
    /// <para><b>This is the EDITOR'S setting, and it is not the placed component's parameter.</b> A
    /// wBond design open in the editor is not yet a component, and one document can be placed as
    /// several components with different settings. What this writes is
    /// <see cref="WBondDesign.IncludeCapacitance"/> — which is what a newly-placed component
    /// <i>inherits</i> as its <c>IncludeCapacitance</c> parameter default
    /// (<c>WBondPlacement.ApplyDesign</c>), exactly the relationship <c>GroundPlane</c> already
    /// has.</para>
    /// </summary>
    public bool IncludeCapacitance
    {
        get => _design.IncludeCapacitance;
        set
        {
            if (_design.IncludeCapacitance == value) return;
            _design.IncludeCapacitance = value;
            OnPropertyChanged();
            Republish();
        }
    }

    /// <summary>
    /// The overmold relative permittivity the capacitance is computed in
    /// (<see cref="WBondDesign.OvermoldEr"/>). 1 is air.
    ///
    /// <para><b>This one DOES cost a refill</b>, unlike <see cref="ReadoutFrequencyGHz"/> beside it:
    /// ε_r scales <b>P</b>, so the capacitance reduction has to be rebuilt. It is the same ~0.06–0.08 ×
    /// the inductance fill that <see cref="IncludeCapacitance"/> pays, and it is paid on a typed
    /// setting rather than per drag frame, so no ladder gates it.</para>
    ///
    /// <para>Like <see cref="IncludeCapacitance"/> this is the EDITOR'S setting, written onto the
    /// design — which is what a newly-placed component inherits as its <c>er</c> parameter default.
    /// Values below 1 are ignored rather than written: the design would then refuse to validate, and
    /// the panel's own prompt already declines them.</para>
    /// </summary>
    public double OvermoldEr
    {
        get => _design.OvermoldEr;
        set
        {
            if (!(value >= 1.0) || !double.IsFinite(value) || _design.OvermoldEr == value) return;
            _design.OvermoldEr = value;
            OnPropertyChanged();
            Republish();
        }
    }

    /// <summary>
    /// The frequency, in GHz, the panel quotes its effective inductance at.
    ///
    /// <para><b>A readout setting, never a simulation input</b> — see
    /// <see cref="WBondDesign.ReadoutFrequencyGHz"/>. Changing it costs one panel rebuild and
    /// <b>no</b> refill: the capacitance and the inductance are both frequency-independent, and only
    /// the small M × M network they feed is re-evaluated.</para>
    /// </summary>
    public double ReadoutFrequencyGHz
    {
        get => _design.ReadoutFrequencyGHz;
        set
        {
            if (!(value > 0.0) || _design.ReadoutFrequencyGHz == value) return;
            _design.ReadoutFrequencyGHz = value;
            OnPropertyChanged();
            PublishReadout();
        }
    }

    /// <summary>How many times the capacitance has been rebuilt.</summary>
    public int CapacitanceComputeCount { get; private set; }

    /// <summary>
    /// Whether a drag frame is allowed to skip the fill and the readout entirely.
    ///
    /// <para><b>This is the frame-rate guarantee</b> (owner, 2026-08-18: <i>"dragging 500 wires must
    /// always be fast, it should always take priority; we can give up frame rate on the inductance
    /// calculation if necessary"</i>). Set by the drag path from the quality ladder, it makes
    /// <see cref="CommitPointMove"/> a no-op: the geometry still moves and the canvas still redraws,
    /// but nothing is filled, factorised, reduced or published. The exact answer is computed once, on
    /// release.</para>
    ///
    /// <para><b>It has to live here rather than in the drag path, because not every drag frame's edit
    /// goes through the drag path's own commit.</b> A plain translate calls <c>WireEdits.Translate</c>
    /// and commits nowhere; an alt-drag calls <see cref="ScaleSelection"/> and a rotate calls
    /// <see cref="RotateSelectionAboutOwnEnd"/>, and BOTH of those commit internally. Gating only the
    /// drag path's own call would therefore have left alt-drag and rotate filling every frame however
    /// degraded the rung said it was — the rung would have protected the one gesture that did not need
    /// it.</para>
    /// </summary>
    public bool DeferFills { get; set; }

    /// <summary>
    /// Whether a drag frame may pay for a capacitance refresh.
    ///
    /// <para><b>Spent only out of MEASURED leftover budget</b>, which is what stops it ever being the
    /// reason a drag is slow: the drag path sets this only when the ladder is at its Exact rung AND
    /// the previous frame used less than half the budget. On a 500-wire drag the ladder never reaches
    /// Exact, so this is never true and the capacitance is touched exactly once, on release. Nothing
    /// predicts anything — the gate is a measurement of the frame that just happened.</para>
    ///
    /// <para><b>Why this is not the "capacitance in the drag loop" the brief forbade.</b> That rule
    /// rested on the premise that C is far less geometry-sensitive than L, so a stale C would be a
    /// second-order effect. <b>Measured 2026-08-18, that premise is false:</b> scaling a 20-wire
    /// array's loops by ×1.1 moves L by +8.0 % and C by −3.9 % — |dC/dL| ≈ 0.4, not ≈ 0 — and the two
    /// errors <b>compound rather than cancel</b>, because L_eff = L/(1 − ω²LC) rises with both. The
    /// visible result was the readout stepping 2 % to 15 % at the moment the button was released,
    /// with the size of the step set by how far the drag went. The brief's own escape clause names
    /// exactly this case and prescribes a rank update for <b>P</b>; this is the cheaper half of that,
    /// and it removes the step outright on every design whose frames fit the budget.</para>
    /// </summary>
    public bool RefreshCapacitanceDuringGesture { get; set; }

    private CapacitanceReduction? _capacitance;
    private bool _capacitanceStale;

    // ---------------------------------------------------------------- the deferred fill

    /// <summary>
    /// How a queued recompute gets back onto the UI thread.
    ///
    /// <para>A property so a <b>test</b> can capture the callback and run it on demand instead of
    /// pumping a dispatcher — the thing being asserted is that the geometry lands before the
    /// arithmetic, and a test that had to race a message loop could not state that.</para>
    /// </summary>
    public Action<Action> RecomputeScheduler { get; set; } =
        action => Avalonia.Threading.Dispatcher.UIThread.Post(
            action, Avalonia.Threading.DispatcherPriority.Background);

    private readonly HashSet<int> _pendingWires = [];
    private bool _recomputeQueued;

    /// <summary>True while a fill has been deferred and the panel's numbers are one edit behind.</summary>
    public bool HasPendingRecompute => _pendingWires.Count > 0;

    /// <summary>How many deferred fills have actually run. A drag must not increase this.</summary>
    public int DeferredRecomputeCount { get; private set; }

    /// <summary>
    /// Applies a point move whose fill may be too big for one frame: <b>the geometry is already in
    /// the design, so the canvas is told immediately and the matrix follows on the next idle.</b>
    ///
    /// <para><b>This is the undo path</b> (owner, 2026-08-18: <i>"Undo/Redo is still slow. The wires
    /// should move instantly when user performs an Undo after moving wires. The wires moving takes
    /// priority over the inductance calculation."</i>) — the same rule as the drag, applied to an
    /// edit that has no gesture to end. A drag can defer to mouse-up because there IS a mouse-up; an
    /// undo is a single instant, so the deferral has to be to the next frame instead.</para>
    ///
    /// <para><b>Small edits are not deferred</b>, and that matters: a deferral costs a frame of stale
    /// numbers, which is worth paying only when the alternative is a stalled canvas. The bound is
    /// <see cref="QualityLadder.FitsInOneFrame"/> — the same one the drag path uses, so there is one
    /// answer in the codebase to "is this fill too big".</para>
    /// </summary>
    public void CommitPointMoveAfterFrame(IReadOnlyList<int> movedWires)
    {
        ArgumentNullException.ThrowIfNull(movedWires);
        if (movedWires.Count == 0) return;

        if (QualityLadder.FitsInOneFrame(movedWires.Count, _mesh.WireCount))
        {
            CommitPointMove(movedWires);
            return;
        }

        foreach (int w in movedWires) _pendingWires.Add(w);

        // The canvas draws from the DESIGN, whose points have already moved — so this alone puts the
        // wires where the user expects them, with the panel still showing the previous numbers.
        PublishGeometryOnly();

        if (_recomputeQueued) return;
        _recomputeQueued = true;
        RecomputeScheduler(FlushPendingRecompute);
    }

    /// <summary>
    /// Runs a deferred fill now. Called by the scheduler, and available to anything that needs the
    /// matrix to be current before it reads it.
    /// </summary>
    public void FlushPendingRecompute()
    {
        _recomputeQueued = false;
        if (_pendingWires.Count == 0) return;

        // A drag opened in the meantime owns the fill; its own EndDrag will commit everything. Coming
        // back later is what stops the pending work being silently dropped on the floor here.
        if (DeferFills)
        {
            _recomputeQueued = true;
            RecomputeScheduler(FlushPendingRecompute);
            return;
        }

        var wires = _pendingWires.ToArray();
        _pendingWires.Clear();
        DeferredRecomputeCount++;

        CommitPointMove(wires);
    }

    /// <summary>
    /// Notifies without recomputing: the geometry moved, the numbers did not. Used by the deferred
    /// path so the canvas repaints on this frame and the matrix catches up on the next.
    /// </summary>
    private void PublishGeometryOnly()
    {
        OnPropertyChanged(nameof(Readout));
        ReadoutChanged?.Invoke();
        DirtyChanged?.Invoke();
    }

    /// <summary>
    /// Recomputes <b>P</b> and the array-basis capacitance — the whole ~0.06–0.08 × fill.
    ///
    /// <para><b>In the drag loop only while the frame budget allows it</b> — see
    /// <see cref="RefreshCapacitanceDuringGesture"/>, which the drag path sets from the quality
    /// ladder's own rung. There is no rank-update machinery for <b>P</b>: a full rebuild is ~36 ms at
    /// the 600-wire worst case and sub-millisecond on the designs anyone actually drags, so the
    /// ladder — which measures real frames rather than predicting them — is a better gate than a cost
    /// model would be.</para>
    ///
    /// <para><b>The original design held C frozen for the whole gesture, and that was wrong.</b> Its
    /// premise was that C is far less geometry-sensitive than L; measured, |dC/dL| ≈ 0.4 and the two
    /// errors compound, so the readout stepped 2–15 % at the moment the button was released.
    /// <see cref="RefreshCapacitanceDuringGesture"/> carries the numbers. When a frame genuinely
    /// cannot afford this the ladder degrades and <see cref="HoldReadout"/> freezes the panel — so a
    /// stale value is never shown rather than shown and later corrected.</para>
    /// </summary>
    private void RefreshCapacitance()
    {
        // A SINGULAR P IS NOT AN UNEVALUABLE EDIT, and that is why this is caught rather than thrown.
        // P is refilled and refactorised from scratch on every republish, while L's factor is
        // maintained incrementally and only the MOVED wires' rows are revisited — so a degenerate wire
        // that is not the one being dragged reaches this fill and nothing else. Rolling the user's
        // drag back over it would be wrong twice over: the drag did not cause it, and the inductance
        // it would discard is still perfectly well defined. So the capacitance goes away, exactly as
        // it does when IncludeCapacitance is off, and the reason is said once instead of per frame.
        //
        // Before this the exception left CapacitanceReduction, escaped Republish — which no guard
        // covered — and came out of OnPointerMoved, taking the application with it mid-drag
        // (owner, 2026-08-19: "pivot 0.000E+000 at wire 6").
        try
        {
            _capacitance = _design.IncludeCapacitance
                ? CapacitanceReduction.Create(_mesh, parallel: true)
                : null;
            _capacitanceRefusal = null;
        }
        catch (InvalidOperationException ex)
        {
            _capacitance = null;
            if (_capacitanceRefusal != ex.Message)
            {
                _capacitanceRefusal = ex.Message;
                Report(ex.Message);
            }
        }

        _capacitanceStale = false;
        CapacitanceComputeCount++;
    }

    /// <summary>
    /// Why the capacitance is missing from the readout, or null when it is not — read by the panel
    /// so a dropped capacitance is visible where it is missing and not only in a toolbar message that
    /// has since scrolled away.
    /// </summary>
    public string? CapacitanceRefusal => _capacitanceRefusal;

    private string? _capacitanceRefusal;

    public WBondDesign Design => _design;

    public WireMesh Mesh => _mesh;

    /// <summary>The live panel contents (R-wbc-7). Replaced wholesale on every edit.</summary>
    public PanelReadout Readout { get; private set; }

    /// <summary>How many full mesh rebuilds have happened. A drag must not increase this.</summary>
    public int RebuildCount { get; private set; }

    /// <summary>How many incremental updates have happened — the drag path.</summary>
    public int IncrementalUpdateCount { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// The <see cref="CircuitRF.Ui.Commands.EditSequence"/> stamp of the entry <see cref="Undo"/>
    /// would take next, or 0 when there is none.
    /// </summary>
    public long TopUndoStamp => _undoStamps.Count > 0 ? _undoStamps.Peek() : 0;

    /// <summary>The same, for the entry <see cref="Redo"/> would take next.</summary>
    public long TopRedoStamp => _redoStamps.Count > 0 ? _redoStamps.Peek() : 0;

    // ---------------------------------------------------------------- the two update paths

    /// <summary>
    /// The <b>drag path</b>: wire geometry moved, no points added or removed.
    ///
    /// <para>Call this from every pointer-move handler. It re-flattens only the moved wires, rank-2
    /// updates the factor, and republishes the readout — measured at ~5 ms for one wire at 600.</para>
    /// </summary>
    /// <param name="motion">
    /// <see cref="SelectionMotion.HorizontalRigidTranslation"/> when the whole selection moved rigidly
    /// in x/y — that lets the fill skip its intra-selection blocks. It is an optimisation and is only
    /// correct if the selection really did move that way.
    /// </param>
    public void CommitPointMove(IReadOnlyList<int> movedWires,
                                SelectionMotion motion = SelectionMotion.General)
    {
        ArgumentNullException.ThrowIfNull(movedWires);
        if (movedWires.Count == 0) return;

        // The frame-rate guarantee: the geometry has already moved and the canvas will redraw it, but
        // the fill, the reduction and the panel are all skipped. See DeferFills.
        if (DeferFills) return;

        // The rest of a gesture that is passing through a degenerate position. The MATRIX is kept
        // exactly up to date — it is well defined even where its factor is not — and the factor is
        // retried each frame, so the panel comes back the moment the wires separate rather than
        // staying frozen until the button comes up.
        if (_fillHeld)
        {
            _fill.MoveWiresUnfactored(movedWires, motion);

            if (!_fill.TryRefactor())
            {
                PublishGeometryOnly();
                return;
            }

            _fillHeld = false;
            OnPropertyChanged(nameof(ReadoutIsHeld));
            IncrementalUpdateCount++;
            Republish();
            return;
        }

        try
        {
            _fill.MoveWires(movedWires, motion);
        }
        catch (InvalidOperationException ex) { RefuseFill(ex, movedWires, motion); return; }

        IncrementalUpdateCount++;
        Republish();
    }

    /// <summary>
    /// A fill that failed: rolled back if it was a discrete edit, <b>held</b> if a gesture is open.
    ///
    /// <h3>Why a drag must not roll back</h3>
    /// <para>Owner, 2026-08-19: <i>"when I drag wires overtop of other wires, the dragged wires move
    /// back to their old position during the drag and my mouse is no longer overtop of the wires that
    /// I was dragging."</i> Passing one wire over another is an ordinary thing to do with a mouse, and
    /// for the instant the two coincide the inductance matrix is singular. Treating that instant as a
    /// failed EDIT undid the whole gesture underneath the cursor — the wires jumped back, and the drag
    /// carried on from a grab point that no longer had anything under it.</para>
    ///
    /// <para>A transient degeneracy is not an edit at all; it is a position the geometry is passing
    /// through. So the geometry keeps moving with the hand and only the NUMBERS stop, which is the
    /// same priority the quality ladder already applies when a frame cannot afford its fill —
    /// <i>"the geometry always moves and the canvas always redraws; the FILL is the only thing that
    /// can be skipped"</i>. What is held is only the FACTOR — the matrix is kept exact by
    /// <c>MoveWiresUnfactored</c>, which is what lets the panel recover mid-drag the moment the wires
    /// separate. <see cref="EndGesture"/> settles whatever is left, and rolls back only if the wires
    /// were dropped somewhere unevaluable.</para>
    /// </summary>
    private void RefuseFill(InvalidOperationException ex,
                           IReadOnlyList<int> movedWires, SelectionMotion motion)
    {
        if (!_inGesture)
        {
            RefuseEdit(ex);
            return;
        }

        _heldReason = ex.Message;

        _fillHeld = true;
        OnPropertyChanged(nameof(ReadoutIsHeld));

        // MoveWires threw part-way down its own loop, so the wires after the one that failed never got
        // their rows. Redo the whole move without the factor, and the matrix is exact from here on —
        // which is what the per-frame TryRefactor above needs in order to be able to recover at all.
        _fill.MoveWiresUnfactored(movedWires, motion);

        PublishGeometryOnly();
        Report("Readout paused while the wires pass through this position; it will update when you "
               + $"release. {ex.Message}");
    }

    /// <summary>
    /// True while a gesture is moving wires through a position whose matrices are singular: the wires
    /// follow the cursor, the panel's numbers are the ones from before it happened, and
    /// <see cref="EndGesture"/> settles it.
    /// </summary>
    public bool ReadoutIsHeld => _fillHeld;

    private bool _fillHeld;

    /// <summary>Why the fill was held, kept for the release that turns the hold into a refusal.</summary>
    private string? _heldReason;

    /// <summary>
    /// The <b>structural path</b>: a wire or a point was added or removed, so the flat filament layout
    /// no longer matches and the mesh must be rebuilt.
    ///
    /// <para>Deliberately separate from <see cref="CommitPointMove"/> rather than detected
    /// automatically: <see cref="WireMesh.RefreshWire"/> throws on a point-count change, which is the
    /// right behaviour, and a caller that silently fell back to a rebuild would make the expensive
    /// path invisible.</para>
    /// </summary>
    public void CommitStructuralChange()
    {
        WireMesh mesh;
        IncrementalFill fill;

        try
        {
            mesh = WireMesh.Build(_design);
            fill = IncrementalFill.Create(mesh);
        }
        catch (InvalidOperationException ex) { RefuseEdit(ex); return; }

        _mesh = mesh;
        _fill = fill;
        _fillHeld = false;   // a rebuild is by definition in step
        RebuildCount++;
        Republish();
    }

    /// <summary>
    /// Raised when an edit produced geometry the physics cannot evaluate, with the reason.
    /// The edit is rolled back; the design is never left in a state the panel cannot describe.
    ///
    /// <para><b>A refusal decided in the constructor is replayed to the first subscriber</b>, because
    /// the view attaches its handler after the view-model exists — so a design that arrives already
    /// unevaluable has no listener at the moment it is diagnosed, and the reason would otherwise be
    /// dropped on the floor.</para>
    /// </summary>
    public event Action<string>? EditRefused
    {
        add
        {
            _editRefused += value;
            if (_deferredRefusal is { } reason)
            {
                _deferredRefusal = null;
                value?.Invoke(reason);
            }
        }
        remove => _editRefused -= value;
    }

    private Action<string>? _editRefused;
    private string? _deferredRefusal;

    /// <summary>
    /// Reports a refusal that was decided BEFORE any edit was attempted, so there is nothing to roll
    /// back — the profile view's "this plane has no world coordinate to place a wire at" is the one
    /// case (owner, 2026-08-16). It reaches the same toolbar strip <see cref="RefuseEdit"/> uses,
    /// because a gesture that visibly does nothing has to say why wherever the reason comes from.
    /// </summary>
    public void ReportRefusal(string reason) => Report(reason);

    /// <summary>
    /// Raises <see cref="EditRefused"/>, or <b>holds the reason for the first subscriber</b> when
    /// there is none yet. The constructor computes the capacitance before any view has attached its
    /// handler, so a design that arrives already unevaluable is diagnosed with nobody listening —
    /// and without this the panel would come up silently missing its capacitance rows.
    /// </summary>
    private void Report(string reason)
    {
        if (_editRefused is null) _deferredRefusal = reason;
        else _editRefused(reason);
    }

    /// <summary>
    /// An edit that made the inductance matrix singular is UNDONE and reported, never thrown.
    ///
    /// <para>This is reachable from ordinary use, not a defensive nicety: a wire lying in the ground
    /// plane has zero loop inductance (its image cancels it exactly), and "straighten a wire whose
    /// feet are both on the plane" is one gesture. So is duplicating with zero pitch, which puts two
    /// wires on identical geometry. Left unguarded, the factorisation throws out of a pointer handler
    /// and takes the application with it.</para>
    ///
    /// <para>Rollback restores the most recent undo snapshot — which is the pre-edit state for a
    /// discrete edit, and the pre-gesture state for a drag, because a gesture pushes one. With no
    /// snapshot to restore (an edit made outside both) the geometry is left alone, so the readout is
    /// stale rather than wrong; the message says what happened either way.</para>
    ///
    /// <para><b>The mesh and the factor are rebuilt afterwards, and that is not belt-and-braces.</b>
    /// <c>IncrementalFill.MoveWires</c> is not transactional: it re-flattens the moved wires into the
    /// mesh and writes their rows into <b>L</b> BEFORE the rank-2 update discovers the matrix is
    /// singular and throws. So a "refused" edit had already left the degenerate geometry in the mesh
    /// and a half-applied rank-1 update in the factor — and the mesh is what
    /// <see cref="RefreshCapacitance"/> refills <b>P</b> from on every later frame, whether or not
    /// that wire is the one being dragged. That is how a refusal on one gesture became a hard crash
    /// on a later one (owner, 2026-08-19).</para>
    /// </summary>
    private bool _refusing;

    private void RefuseEdit(InvalidOperationException ex)
    {
        // Restore re-enters the commit path, so a snapshot that is ITSELF unevaluable would recurse
        // forever. One level only: report and stop.
        if (!_refusing && _undo.Count > 0)
        {
            _refusing = true;
            try
            {
                var snapshot = _undo.Pop();
                if (_undoStamps.Count > 0) _undoStamps.Pop();
                _inGesture = false;   // the gesture cannot continue from a state that was rolled back
                Restore(snapshot);
            }
            finally { _refusing = false; }
        }

        RebuildAfterFailedFill();
        Report(ex.Message);
    }

    /// <summary>
    /// Puts the mesh, the matrix and the factor back in step with the design after a fill threw
    /// part-way through. See <see cref="RefuseEdit"/> for why they can be out of step at all.
    ///
    /// <para>A full rebuild, on an error path that runs once per refusal — the incremental path has no
    /// way to undo a partial rank-2 update, and a factor carrying half of one is silently wrong
    /// rather than loudly broken, which is the worse failure.</para>
    ///
    /// <para>If the design it rebuilds from is <i>itself</i> unevaluable there is nothing better to
    /// fall back to, so the previous mesh is kept and the caller's message stands as the explanation.</para>
    /// </summary>
    private void RebuildAfterFailedFill()
    {
        try
        {
            var mesh = WireMesh.Build(_design);
            var fill = IncrementalFill.Create(mesh);
            _mesh = mesh;
            _fill = fill;
            RebuildCount++;
        }
        catch (InvalidOperationException)
        {
            // Nothing to swap in. Reported by the caller.
        }
    }

    /// <summary>
    /// Recomputes and republishes — <b>and it is a guarded region, not a bare call.</b>
    ///
    /// <h3>Why the guard has to be here and not only around the fill</h3>
    /// <para><see cref="CommitPointMove"/> and <see cref="CommitStructuralChange"/> each wrapped only
    /// their own <i>inductance</i> work in <see cref="RefuseEdit"/>, on the premise that a degenerate
    /// geometry shows up there first. <b>It does not always.</b> Two matrices are factorised on this
    /// path, and only one of them is rebuilt from scratch each time: <see cref="IncrementalFill"/>
    /// revisits <b>L</b> only for the wires that MOVED, while <see cref="RefreshCapacitance"/> refills
    /// <b>P</b> over the whole mesh. A wire left degenerate by an earlier refused edit is therefore
    /// invisible to every guard upstream and fatal here — and this is downstream of all of them, so
    /// the Cholesky breakdown escaped through <c>OnPointerMoved</c> and took the whole application
    /// down mid-drag (owner, 2026-08-19).</para>
    ///
    /// <para><see cref="RefreshCapacitance"/> now handles its own degeneracy, so in practice this
    /// catch is the backstop for the OTHER factorisation on the path — the array reduction's small
    /// inverse inside <see cref="PublishReadout"/>. It is kept because the lesson of the crash is
    /// precisely that "the fill would have caught it" is not a property anything enforces.</para>
    /// </summary>
    private void Republish()
    {
        try
        {
            // Inside a gesture the capacitance is refreshed only when the frame budget allows it; when
            // it does not it is marked stale, and EndGesture pays for it once. See
            // RefreshCapacitanceDuringGesture.
            if (_inGesture && !RefreshCapacitanceDuringGesture) _capacitanceStale = true;
            else RefreshCapacitance();

            PublishReadout();
            _publishRefused = false;
            _lastPublishRefusal = null;
        }
        catch (InvalidOperationException ex)
        {
            // ROLL BACK ONCE, THEN ONLY REPORT. RefuseEdit pops an undo snapshot, which is right when
            // the edit just made is what broke the geometry — and actively destructive when it is not.
            // A design that is ALREADY unevaluable refuses on every frame of an unrelated drag, and a
            // rollback per frame would unwind the whole undo stack, silently reverting work the user
            // never asked to lose. The flag says which case this is: it is cleared by the first
            // republish that succeeds, so it is set exactly while the current state cannot be
            // evaluated.
            if (_publishRefused)
            {
                // Same reason, same frame after frame — say it once rather than once per frame.
                if (_lastPublishRefusal != ex.Message)
                {
                    _lastPublishRefusal = ex.Message;
                    _editRefused?.Invoke(ex.Message);
                }
                return;
            }

            _publishRefused = true;
            _lastPublishRefusal = ex.Message;
            RefuseEdit(ex);
        }
    }

    /// <summary>True while the present geometry cannot be evaluated — see <see cref="Republish"/>.</summary>
    private bool _publishRefused;

    private string? _lastPublishRefusal;

    /// <summary>
    /// Rebuilds the panel from what is already computed — the M triangular solves and the small
    /// M × M network, with no fill and no factorisation of <b>P</b>.
    /// </summary>
    private void PublishReadout()
    {
        Readout = PanelReadout.Build(_design, _mesh, _fill.Reduce(), _capacitance);

        OnPropertyChanged(nameof(Readout));
        ReadoutChanged?.Invoke();
        DirtyChanged?.Invoke();
    }

    // ---------------------------------------------------------------- selection

    /// <summary>
    /// Selects every wire in the design and nothing else — no layout geometry, no partial points.
    ///
    /// <para><b>Deliberately separate from the layout editor's own Select All</b>, which selects
    /// every shape and instance. A wBond user reaching for "select all my wires" on a board covered
    /// in copper wants exactly the wires; making them do it by selecting everything and then
    /// deselecting the geometry is not a workflow.</para>
    /// </summary>
    /// <returns>The number of wires selected.</returns>
    public int SelectAllWires()
    {
        int n = _design.WireCount;
        Selection = new WireSelection { Wires = [.. Enumerable.Range(0, n)] };
        return n;
    }

    /// <summary>
    /// Replaces the wire selection with its complement: everything currently touched becomes
    /// unselected, and everything else becomes selected.
    ///
    /// <para><b>A partially-selected wire counts as selected</b> (via
    /// <see cref="WireSelection.TouchedWires"/>) and therefore drops out of the inverted set. The
    /// alternative — inverting point-by-point — would turn "I picked three vertices" into a
    /// selection of every other vertex in the design, which is not what anyone means by inverting a
    /// selection of wires.</para>
    /// </summary>
    /// <returns>The number of wires now selected.</returns>
    public int InvertWireSelection()
    {
        var touched = Selection.TouchedWires();
        var inverted = new HashSet<int>();

        for (int i = 0; i < _design.WireCount; i++)
            if (!touched.Contains(i))
                inverted.Add(i);

        Selection = new WireSelection { Wires = inverted };
        return inverted.Count;
    }

    /// <summary>Clears the wire selection, leaving any layout-geometry selection alone.</summary>
    public void ClearSelection() => Selection = new WireSelection();

    /// <summary>
    /// Selects every wire belonging to one array — the inductance panel's own double-click gesture
    /// (owner, 2026-08-16).
    ///
    /// <para>Membership is read from <see cref="WireMesh.ArrayOfWire"/> rather than by re-walking the
    /// design, because the mesh's flat wire index is the SAME index a <see cref="WireSelection"/>
    /// holds; deriving it a second way is how the panel and the canvas would come to disagree about
    /// which wires are in "G2".</para>
    /// </summary>
    /// <returns>The number of wires selected — zero for an index that names no array.</returns>
    public int SelectArray(int arrayIndex)
    {
        var wires = new HashSet<int>();
        for (int w = 0; w < _mesh.WireCount; w++)
            if (_mesh.ArrayOfWire[w] == arrayIndex) wires.Add(w);

        Selection = new WireSelection { Wires = wires };
        return wires.Count;
    }

    // ---------------------------------------------------------------- edits

    /// <summary>Nudges the current selection by one step (WB25).</summary>
    public void NudgeSelection(int dx, int dyOrDz, bool coarse, EditorView view)
    {
        if (Selection.IsEmpty) return;

        // In the PROFILE view the subject is the whole group — the same rule the drag and the alt-drag
        // follow there. See ProfileGroupSubject.
        var subject = view == EditorView.Profile ? ProfileGroupSubject(Selection) : Selection;

        PushUndo();
        long step = coarse ? WireEdits.CoarseNudgeNm : WireEdits.DefaultNudgeNm;
        WireEdits.Nudge(_design, subject, dx, dyOrDz, step, view);

        // A nudge moves whole points, never adds them — so it is the drag path.
        CommitPointMove([.. subject.TouchedWires()],
                        dyOrDz == 0 ? SelectionMotion.HorizontalRigidTranslation : SelectionMotion.General);
    }

    /// <summary>
    /// What a PROFILE-view edit actually moves: <b>the same point or segment on every wire of every
    /// array the selection touches</b> (owner, 2026-08-18: <i>"when I click drag a point/segment in
    /// Wire Profile view only 1 wire within the group moves. I want all the wires within that group to
    /// move."</i>).
    ///
    /// <h3>Why the group, and why only in this view</h3>
    /// <para>The profile view draws a group as ONE superimposed shape under one envelope band, and a
    /// bond group is one loop program on one bonder — reshaping one member and leaving its siblings
    /// behind is not a thing the machine can do. That is the same reasoning that already put alt-drag
    /// on the array here (WB24c, owner 2026-08-17: <i>"it needs to change ALL the wires in the group at
    /// once"</i>); a plain drag simply had not been moved with it, so the two gestures disagreed about
    /// what they were pointing at.
    ///
    /// <para>The LAYOUT view is deliberately unchanged: there each wire is drawn at its own place among
    /// the pads and a drag moves THAT wire onto THAT pad. Its members are not interchangeable.</para>
    ///
    /// <h3>What it does NOT promote</h3>
    /// <para><b>The <see cref="Selection"/> itself is left alone</b>, exactly as alt-drag leaves it —
    /// this is the subject of one edit, not a re-selection. The panel still reports the wire the user
    /// clicked, and the highlight still marks it.</para>
    ///
    /// <para>A sibling with too few points to have the named element is skipped rather than
    /// approximated. An array's members may legitimately differ in point count (§6.2), and guessing
    /// which of its points "corresponds" would move a wire somewhere nobody asked for.</para>
    /// </summary>
    public WireSelection ProfileGroupSubject(WireSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.IsEmpty) return selection;

        var wires = _design.AllWires().ToList();
        var subject = new WireSelection();

        // The arrays the selection touches, through the SAME flat-index-to-array mapping SelectArray
        // and ExpandToArrays use, so "the group" means one thing in this class.
        var siblings = new Dictionary<int, List<int>>();

        List<int> SiblingsOf(int wire)
        {
            if (wire < 0 || wire >= _mesh.WireCount) return [];
            int array = _mesh.ArrayOfWire[wire];

            if (siblings.TryGetValue(array, out var found)) return found;

            var list = new List<int>();
            for (int w = 0; w < _mesh.WireCount; w++)
                if (_mesh.ArrayOfWire[w] == array) list.Add(w);

            siblings[array] = list;
            return list;
        }

        foreach (int wire in selection.Wires)
            foreach (int sibling in SiblingsOf(wire))
                subject.Wires.Add(sibling);

        foreach (var point in selection.Points)
            foreach (int sibling in SiblingsOf(point.Wire))
                if (point.Point < wires[sibling].Points.Count)
                    subject.Points.Add(new PointRef(sibling, point.Point));

        foreach (var segment in selection.Segments)
            foreach (int sibling in SiblingsOf(segment.Wire))
                if (segment.Point < wires[sibling].Points.Count - 1)
                    subject.Segments.Add(new SegmentRef(sibling, segment.Point));

        return subject;
    }

    /// <summary>
    /// <b>The alt-drag primitive: scale the selection's span and height together, in one frame.</b>
    ///
    /// <para>Span and height are applied TOGETHER rather than a gesture declaring one axis and
    /// ignoring the other — a diagonal alt-drag means both, and the axis-declaration rule made the
    /// second half of the gesture silently inert (owner, 2026-08-16). Passing 1.0 for either factor
    /// leaves that quantity exactly alone, so a purely vertical or purely horizontal drag still does
    /// only what it looks like.</para>
    ///
    /// <para><b>The unit is the ARRAY, or the selection, and the caller says which</b> — see
    /// <paramref name="wholeArray"/>. It used to be the set of wires sharing a loop profile, which is
    /// where this went wrong: a design built from <c>WBondEmbedding.DefaultDesign</c> gave every array
    /// the same profile, so alt-dragging a wire in G1 moved G2's wires too (owner, 2026-08-17). Loop
    /// profiles no longer exist at all (2026-08-18); an array is the bond group the user is looking
    /// at, and that is the only grouping this gesture has ever meant.</para>
    ///
    /// <para>Scaling by FACTOR rather than to a common value is unchanged and still matters: an array
    /// whose wires deliberately have different spans — a fan-out from a common pad — keeps their
    /// ratios however many of them move.</para>
    /// </summary>
    /// <param name="moveOutputFoot">
    /// Which foot the span scale moves. The caller decides it from WHICH END the user grabbed — the
    /// pinned foot is the far one — matching the rotate tool's own rule (WB26a).
    /// </param>
    /// <param name="wholeArray">
    /// True to promote the selection to <b>every wire in each array it touches</b> — what the PROFILE
    /// view's alt-drag means (owner, 2026-08-17: <i>"it needs to change ALL the wires in the group at
    /// once"</i>), and what WB24c/D4 meant by "the whole bound array" before a profile was used as a
    /// proxy for one. That view draws a group as one superimposed shape under a single envelope band,
    /// and a bond group is one loop program on one bonder: reshaping one wire of it and leaving its
    /// siblings behind is not a thing the machine can do.
    ///
    /// <para>False for the LAYOUT view, where each wire is drawn at its own place among the pads and an
    /// alt-drag stretches THAT wire's span onto THAT pad — the wires of an array are not
    /// interchangeable there, because each one lands somewhere different.</para>
    /// </param>
    /// <returns>How many wires were rescaled.</returns>
    public int ScaleSelection(double spanFactor, double heightFactor, bool moveOutputFoot,
                              bool wholeArray = false)
    {
        var touched = TouchedWireList();
        if (touched.Count == 0) return 0;
        if (spanFactor == 1.0 && heightFactor == 1.0) return 0;

        if (wholeArray) touched = ExpandToArrays(touched);

        var wires = _design.AllWires().ToList();
        bool pushed = PushUndo();

        int moved = WireEdits.ScaleWires(
            touched.Select(i => wires[i]), heightFactor, spanFactor, moveOutputFoot);

        if (moved == 0)
        {
            if (pushed) DropUndoEntry();   // nothing happened; do not leave a no-op on the undo stack
            return 0;
        }

        CommitPointMove([.. touched]);
        return moved;
    }

    // ---------------------------------------------------------------- transforms (§6.4)

    /// <summary>
    /// Rotates every selected wire about ONE of its own ends (WB26a) — the fan-out gesture.
    ///
    /// <para><b>Each wire turns about its own pinned end, not about a shared pivot.</b> The two are
    /// genuinely different operations and both are wanted: this one spreads a ground array leaving a
    /// single paddle; <see cref="RotateSelectionRigidly"/> swings the whole selection as one body.
    /// Doing only the second and calling it "rotate" would make the array-authoring case unreachable.</para>
    /// </summary>
    /// <param name="pivotOnInputFoot">
    /// Which end stays fixed. The gesture decides this from which end the user grabbed — the pivot is
    /// the FURTHER one — so no mode switch is needed.
    /// </param>
    public int RotateSelectionAboutOwnEnd(double radians, bool pivotOnInputFoot, EditorView view)
    {
        var touched = TouchedWireList();
        if (touched.Count == 0) return 0;

        PushUndo();
        var wires = _design.AllWires().ToList();
        foreach (int index in touched)
            WireEdits.RotateAboutEndPoint(wires[index], pivotOnInputFoot, radians, view);

        CommitPointMove(touched);
        return touched.Count;
    }

    /// <summary>
    /// Maps every point of the named wires through <paramref name="mapXy"/>, leaving z alone —
    /// <b>and pushes NO undo entry of its own</b>.
    ///
    /// <para>The primitive behind the Layout Editor's own rotate and mirror over a wirebond cell
    /// (owner, 2026-08-17: <i>"map the rotate button command from the hosted layout … want this to
    /// work just like the layout primitives work"</i>). Those transforms are one rigid body over
    /// shapes, instances AND wires, and they have to be ONE undo entry — so the caller wraps this in
    /// its own <c>IUiCommand</c> on the LAYOUT's stack and this must not add a second entry to the
    /// wire stack. Every other edit here pushes its own, which is why this one says so twice.</para>
    ///
    /// <para>x and y only: an in-plane rotation or mirror of the layout says nothing about how high a
    /// wire loops, and carrying z through the map would flatten a loop into the plane the moment a
    /// user rotated a pad.</para>
    /// </summary>
    /// <returns>How many wires were transformed.</returns>
    public int MapWirePointsXy(IReadOnlyCollection<int> wireIndices, Func<long, long, (long X, long Y)> mapXy)
    {
        ArgumentNullException.ThrowIfNull(wireIndices);
        ArgumentNullException.ThrowIfNull(mapXy);
        if (wireIndices.Count == 0) return 0;

        var wires = _design.AllWires().ToList();
        var moved = new List<int>(wireIndices.Count);

        foreach (int index in wireIndices)
        {
            if (index < 0 || index >= wires.Count) continue;

            var wire = wires[index];
            for (int i = 0; i < wire.Points.Count; i++)
            {
                var p = wire.Points[i];
                var (x, y) = mapXy(p.X, p.Y);
                wire.Points[i] = new Point3(x, y, p.Z);
            }
            moved.Add(index);
        }

        if (moved.Count == 0) return 0;

        // A rigid map moves whole points and adds none, so this is the DRAG path — a rebuild here
        // would cost two orders of magnitude for an answer that is identical.
        //
        // ...and it is also the REDO path (TransformWiresCommand.Execute), where the wires must appear
        // instantly for the same reason they must during a drag. AfterFrame defers only when the fill
        // genuinely will not fit in a frame, so a drag frame — which is already gated by the ladder —
        // is unaffected.
        CommitPointMoveAfterFrame(moved);
        return moved.Count;
    }

    /// <summary>
    /// Rotates the whole selection rigidly about one shared pivot — the other half of WB26a.
    /// </summary>
    public int RotateSelectionRigidly(double radians, Point3 pivot, EditorView view)
    {
        var touched = TouchedWireList();
        if (touched.Count == 0) return 0;

        PushUndo();
        var wires = _design.AllWires().ToList();
        double cos = Math.Cos(radians), sin = Math.Sin(radians);

        foreach (int index in touched)
        {
            var wire = wires[index];
            foreach (int i in Selection.MovingPoints(index, wire.Points.Count))
            {
                var p = wire.Points[i];
                double dx = p.X - pivot.X, dy = p.Y - pivot.Y, dz = p.Z - pivot.Z;

                wire.Points[i] = view == EditorView.Layout
                    ? new Point3(pivot.X + (long)Math.Round(dx * cos - dy * sin),
                                 pivot.Y + (long)Math.Round(dx * sin + dy * cos), p.Z)
                    : new Point3(pivot.X + (long)Math.Round(dx * cos - dz * sin), p.Y,
                                 pivot.Z + (long)Math.Round(dx * sin + dz * cos));
            }
        }

        CommitPointMove(touched);
        return touched.Count;
    }

    /// <summary>
    /// Mirrors every selected wire about an axis-aligned plane.
    ///
    /// <para><paramref name="reverseTraversal"/> is surfaced to the user as a checkbox and defaults to
    /// true, because a mirrored wire's input should normally stay on the input side — and getting it
    /// wrong flips every mutual-inductance sign involving that wire (WB3), which is a
    /// plausible-looking wrong answer rather than a visible failure.</para>
    /// </summary>
    public int MirrorSelection(char axis, long aboutNm, bool reverseTraversal = true)
    {
        var touched = TouchedWireList();
        if (touched.Count == 0) return 0;

        PushUndo();
        var wires = _design.AllWires().ToList();
        foreach (int index in touched) WireEdits.Mirror(wires[index], axis, aboutNm, reverseTraversal);

        CommitPointMove(touched);
        return touched.Count;
    }

    /// <summary>Displaces interior points laterally with both feet pinned (§6.4).</summary>
    public int BendSelection(long dxNm, long dyNm, long dzNm)
    {
        var touched = TouchedWireList();
        if (touched.Count == 0) return 0;

        PushUndo();
        var wires = _design.AllWires().ToList();
        foreach (int index in touched) WireEdits.Bend(wires[index], dxNm, dyNm, dzNm);

        CommitPointMove(touched);
        return touched.Count;
    }

    /// <summary>
    /// Collapses interior points onto the chord, <b>keeping the point count</b> (2026-08-18: the
    /// reason used to be "so a profile can be re-applied"; with nothing to re-apply from, it is that
    /// a user who straightens by mistake can undo, and that the point count is their own choice).
    ///
    /// <para>That is what makes this the drag path rather than a structural change: the flat filament
    /// layout is untouched, so it costs an incremental update rather than a full rebuild.</para>
    /// </summary>
    public int StraightenSelection()
    {
        var touched = TouchedWireList();
        if (touched.Count == 0) return 0;

        PushUndo();
        var wires = _design.AllWires().ToList();
        foreach (int index in touched) WireEdits.Straighten(wires[index]);

        CommitPointMove(touched);
        return touched.Count;
    }

    /// <summary>Extends or shortens along each wire's own chord, from one end (§6.4).</summary>
    public int ExtendSelection(double factor, bool fromOutputFoot = true)
    {
        var touched = TouchedWireList();
        if (touched.Count == 0 || factor <= 0) return 0;

        PushUndo();
        var wires = _design.AllWires().ToList();
        foreach (int index in touched) WireEdits.ExtendAlongAxis(wires[index], factor, fromOutputFoot);

        CommitPointMove(touched);
        return touched.Count;
    }

    // ---------------------------------------------------------------- clipboard (§6.7)

    /// <summary>Serialises the selected wires, or null when nothing whole is selected.</summary>
    public string? CopySelection() => WBondClipboard.Copy(_design, Selection);

    /// <summary>
    /// Pastes a clipboard payload, offset so the copies are visibly distinct from their originals,
    /// and selects the result.
    ///
    /// <para>Structural — new wires change the flat filament layout — so it is one rebuild and one
    /// undo entry for the whole paste, not one per wire.</para>
    /// </summary>
    /// <returns>How many wires were added; 0 for a foreign or empty clipboard.</returns>
    public int PasteWires(string? clipboardText, long dxNm, long dyNm, long dzNm = 0)
    {
        if (WBondClipboard.TryParse(clipboardText) is not { } payload) return 0;

        int before = _design.AllWires().Count();
        bool pushed = PushUndo();

        // The indices the paste ACTUALLY created, resolved before the commit — a paste is not an
        // append, because each wire rejoins an array of its own name and so may land in the middle of
        // the flat order. See WBondClipboard.Paste's own note for the owner-reported bug this is.
        var pasted = WBondClipboard.Paste(_design, payload, dxNm, dyNm, dzNm);
        int added = pasted.Count;

        if (added == 0)
        {
            if (pushed) DropUndoEntry();
            return 0;
        }

        CommitStructuralChange();

        // The refusal path rolls the design back, so "did it survive" is asked of the design itself
        // rather than assumed from the paste having run. It also invalidates the indices above, which
        // is why nothing is selected when it fires.
        if (_design.AllWires().Count() != before + added) return 0;

        Selection = new WireSelection { Wires = [.. pasted] };

        return added;
    }

    /// <summary>
    /// Pastes at the first multiple of <paramref name="pitchNm"/> <b>across the wires' own
    /// direction</b> at which no pasted wire would land exactly on a wire already in the design.
    ///
    /// <h3>The bug (owner, 2026-08-16)</h3>
    /// <para><i>"When I copy and then paste a wire, a new wire appears 5 mil in +y. Good. However, if
    /// I paste the same wire again, I get an error: 'The inductance matrix is not positive…' and a 3rd
    /// wire does not appear."</i> Paste applied one FIXED offset, so the second paste of an unchanged
    /// clipboard placed a wire exactly on top of the first paste's. Two wires on identical geometry
    /// have identical filaments, the mutual equals the self, and the matrix is singular — the refusal
    /// was correct, and the placement was the bug.</para>
    ///
    /// <para><b>The step runs ACROSS the wires, not along a fixed axis</b> (owner, 2026-08-16: "pasting
    /// a north-south wire uses the wrong dimension for the offset"). A bond array is pitched
    /// perpendicular to its wires — that is what a pitch IS — so an east/west wire steps in y and a
    /// north/south wire steps in x, and a wire at 37° steps at 37°+90° rather than being forced onto
    /// an axis it does not lie on. Stepping along a fixed axis slid a north/south copy END-TO-END with
    /// its original instead of beside it.</para>
    ///
    /// <para><b>The pitch governs PLACEMENT, never the clipboard's own spacing</b> (the owner's own
    /// distinction): the wires arrive with whatever spacing they were copied with, and the whole batch
    /// is translated as one body.</para>
    ///
    /// <para>Occupancy is tested on the FEET — the two bonded ends. Two wires sharing both feet
    /// exactly are the degenerate case this exists to avoid, whatever their loops do in between.</para>
    /// </summary>
    /// <returns>How many wires were added; 0 for a foreign or empty clipboard.</returns>
    public int PasteWiresAtFreePitch(string? clipboardText, long pitchNm)
    {
        if (WBondClipboard.TryParse(clipboardText) is not { } payload) return 0;

        var (dx, dy) = FreePasteOffset(payload, pitchNm);
        return PasteWires(clipboardText, dx, dy);
    }

    /// <summary>
    /// How far, and in which direction, a paste has to step to miss everything already there. Always
    /// at least one pitch, so a paste is visibly a paste even when the design is empty.
    /// </summary>
    internal (long Dx, long Dy) FreePasteOffset(WBondClipboard.Payload payload, long pitchNm)
    {
        ArgumentNullException.ThrowIfNull(payload);

        long pitch = pitchNm > 0 ? pitchNm : WireEdits.CoarseNudgeNm;
        var (ux, uy) = PasteDirection(payload);

        var occupied = new HashSet<(long, long, long, long, long, long)>();
        foreach (var wire in _design.AllWires())
        {
            if (wire.Points.Count < 2) continue;
            var a = wire.Points[0];
            var b = wire.Points[^1];
            occupied.Add((a.X, a.Y, a.Z, b.X, b.Y, b.Z));
        }

        (long Dx, long Dy) At(int step) =>
            ((long)Math.Round(pitch * step * ux), (long)Math.Round(pitch * step * uy));

        for (int step = 1; step <= MaxPasteProbeSteps; step++)
        {
            var (dx, dy) = At(step);
            bool clash = false;

            foreach (var entry in payload.Wires)
            {
                int n = entry.Points.Length;
                if (n < 6 || n % 3 != 0) continue;

                var key = (entry.Points[0] + dx, entry.Points[1] + dy, entry.Points[2],
                           entry.Points[n - 3] + dx, entry.Points[n - 2] + dy, entry.Points[n - 1]);

                if (occupied.Contains(key)) { clash = true; break; }
            }

            if (!clash) return (dx, dy);
        }

        // Nothing free within the probe range — hand back the last candidate rather than refusing.
        // The physics guard still catches a genuine coincidence and rolls the paste back with a
        // message, which is strictly better than silently doing nothing here.
        return At(MaxPasteProbeSteps);
    }

    /// <summary>
    /// The unit direction a paste steps in: <b>perpendicular, in the layout plane, to the mean chord
    /// azimuth of the wires being pasted</b>.
    ///
    /// <para>Canonicalised to point east where it can and north otherwise, so the copy lands to the
    /// RIGHT of a north/south wire and ABOVE an east/west one rather than behind or below it. Both
    /// perpendiculars are equally valid geometrically; only one of them is the direction a user reads
    /// as "the next wire along".</para>
    ///
    /// <para>Falls back to +y for a payload with no usable chord — every wire's feet coincident in xy,
    /// which is degenerate geometry that has no across-direction to speak of.</para>
    /// </summary>
    private static (double X, double Y) PasteDirection(WBondClipboard.Payload payload)
    {
        // Summed as VECTORS, not as angles: averaging 179° and −179° as numbers gives 0°, which is the
        // direction perpendicular to both of them.
        double sx = 0, sy = 0;

        foreach (var entry in payload.Wires)
        {
            int n = entry.Points.Length;
            if (n < 6 || n % 3 != 0) continue;

            double dx = entry.Points[n - 3] - entry.Points[0];
            double dy = entry.Points[n - 2] - entry.Points[1];

            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 0) continue;

            // Folded onto a half-plane first: a wire and its reverse are the same LINE, and two
            // anti-parallel members of one array must not cancel each other out of the mean.
            if (dx < 0 || (dx == 0 && dy < 0)) { dx = -dx; dy = -dy; }

            sx += dx / length;
            sy += dy / length;
        }

        double norm = Math.Sqrt(sx * sx + sy * sy);
        if (norm <= 0) return (0.0, 1.0);   // no usable chord — the old fixed +y

        // Rotate the chord direction a quarter turn: (x, y) → (−y, x).
        double px = -sy / norm, py = sx / norm;

        // …then face it east, or north when it is purely vertical. An east/west chord (1, 0) gives
        // (0, 1) — +y, which is what paste already did and must keep doing. A north/south chord
        // (0, 1) gives (−1, 0), which is flipped here to +x.
        if (px < -1e-12 || (Math.Abs(px) <= 1e-12 && py < 0)) { px = -px; py = -py; }

        return (px, py);
    }

    /// <summary>
    /// How many pitches a paste will walk before giving up. Bounded because the search is over a
    /// user-supplied pitch that could be a nanometre; a thousand steps is far past any real bond
    /// array and costs nothing.
    /// </summary>
    private const int MaxPasteProbeSteps = 1000;

    /// <summary>The selected wires, bounds-checked once so every transform above can trust the list.</summary>
    private List<int> TouchedWireList()
    {
        int count = _design.AllWires().Count();
        return [.. Selection.TouchedWires().Where(i => i >= 0 && i < count)];
    }

    /// <summary>
    /// Promotes a set of wire indices to <b>every wire in each ARRAY any of them belongs to</b> — the
    /// profile view's "the whole group at once" (see <see cref="ScaleSelection"/>'s <c>wholeArray</c>).
    ///
    /// <para>Through <c>WireMesh.ArrayOfWire</c>, which is the same flat-index-to-array mapping
    /// <see cref="SelectArray"/> uses, so "the group" means one thing in this class.</para>
    /// </summary>
    private List<int> ExpandToArrays(IReadOnlyCollection<int> seed)
    {
        var arrays = new HashSet<int>();
        foreach (int wire in seed)
            if (wire >= 0 && wire < _mesh.WireCount) arrays.Add(_mesh.ArrayOfWire[wire]);

        if (arrays.Count == 0) return [.. seed];

        var expanded = new List<int>();
        for (int wire = 0; wire < _mesh.WireCount; wire++)
            if (arrays.Contains(_mesh.ArrayOfWire[wire])) expanded.Add(wire);

        return expanded;
    }

    /// <summary>Reverses every selected wire's current direction (WB26b / D7).</summary>
    public int ReverseSelection()
    {
        var wires = _design.AllWires().ToList();
        var touched = Selection.TouchedWires().Where(i => i >= 0 && i < wires.Count).ToList();
        if (touched.Count == 0) return 0;

        PushUndo();
        foreach (int index in touched) wires[index].Reverse();

        // Reversing does not change the point COUNT, so it is the drag path — and it negates exactly
        // those wires' off-diagonal mutuals, which the readout must show.
        CommitPointMove(touched);
        return touched.Count;
    }

    /// <summary>
    /// The loop height a newly-created wire is arched to when the caller does not say — the shipped
    /// default (<c>WBondEmbedding.DefaultWire.LoopHeightMils</c>), which is what the design's default
    /// loop profile used to supply.
    /// </summary>
    public static long DefaultNewWireLoopHeightNm { get; } =
        WBondUnits.ToNm(WBondEmbedding.DefaultWire.LoopHeightMils, WBondUnit.Mil);

    /// <summary>
    /// Adds a wire between two points, arched on the seed shape (§6.4).
    ///
    /// <para>Returns the new wire's flat index. <b>Structural</b> — a new wire changes the flat
    /// filament layout, so it costs a rebuild; that is why creation is a click-click gesture rather
    /// than something that fires per pointer move.</para>
    ///
    /// <para><b>Which array a new wire joins is now stated, not inferred</b> (2026-08-18). It used to
    /// be answered by loop-profile identity — the first array referencing the design's first profile —
    /// which is why the layout drop path had to invent a uniquely-named throwaway profile purely to
    /// force a NEW array. <paramref name="arrayName"/> says it outright.</para>
    ///
    /// <para>Array membership is what the reduction sums over (§3.4), so a wire in no array would be
    /// drawn, measured, and absent from every published inductance — silently. There is therefore
    /// always an array.</para>
    /// </summary>
    /// <param name="arrayName">
    /// The array to join, created when the design has no array of that name — which is how a caller
    /// asks for a NEW group: pass <see cref="NextArrayName"/>.
    ///
    /// <para>Null means "the group already on screen": the FIRST array, or a new one when the design
    /// has none. That is what the two interactive draw tools want — an array IS a pin pair on the
    /// generated symbol, so making one per drawn wire would grow the symbol every time a user draws.</para>
    /// </param>
    public int AddWire(Point3 start, Point3 end, long diameterNm, string material,
                       string? arrayName = null, int points = 7, long? loopHeightNm = null)
    {
        PushUndo();

        var array = arrayName is null
            ? _design.Arrays.FirstOrDefault()
            : _design.Arrays.FirstOrDefault(
                a => string.Equals(a.Name, arrayName, StringComparison.OrdinalIgnoreCase));

        if (array is null)
        {
            array = new WireArray { Name = arrayName ?? NextArrayName() };
            _design.Arrays.Add(array);
        }

        array.Wires.Add(LoopShape.CreateSeedWire(
            start, end, diameterNm, material,
            loopHeightNm ?? DefaultNewWireLoopHeightNm, Math.Max(3, points)));

        CommitStructuralChange();
        return _design.AllWires().Count() - 1;
    }

    /// <summary>The first free <c>G&lt;n&gt;</c> — what a caller asks for a brand-new group with.</summary>
    public string NextArrayName()
    {
        for (int n = 1; ; n++)
        {
            string candidate = "G" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!_design.Arrays.Any(a => string.Equals(a.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    /// <summary>Duplicates a wire with pitch (WB26) — structural, and one rebuild for the whole batch.</summary>
    public int DuplicateWithPitch(int sourceWire, long pitchX, long pitchY, int count)
    {
        var wires = _design.AllWires().ToList();
        if (sourceWire < 0 || sourceWire >= wires.Count) return 0;

        bool pushed = PushUndo();

        IReadOnlyList<Wire> made;
        try
        {
            made = WireEdits.DuplicateWithPitch(_design, wires[sourceWire], pitchX, pitchY, count);
        }
        catch (ArgumentException ex)
        {
            // The primitive already refuses a pitch that would stack copies on the source. Surfacing
            // that refusal is the editor's job — letting it escape would take down the dialog that
            // asked for it.
            if (pushed) DropUndoEntry();
            _editRefused?.Invoke(ex.Message);
            return 0;
        }

        // ONE rebuild for the whole batch, which is the entire point of WB26.
        CommitStructuralChange();
        return made.Count;
    }

    // ---------------------------------------------------------------- undo

    /// <summary>
    /// A snapshot of every wire's points — enough to undo any edit in this class.
    ///
    /// <para>Points only, not the whole design: it is what every edit here touches, and cloning the
    /// point lists is O(N·points) and allocation-light, where a `.wBond` round trip would be
    /// milliseconds of JSON per undo push.</para>
    /// </summary>
    private sealed record DesignSnapshot(ArraySnapshot[] Arrays, Point3[][] Points);

    /// <summary>
    /// One array's identity and MEMBERSHIP, by wire reference.
    ///
    /// <para>Holding the <see cref="Wire"/> objects themselves is what makes a deletion undoable: the
    /// deleted wire is still alive in the snapshot, so restoring membership puts the same object back
    /// rather than a reconstruction of it. A wire ADDED after the snapshot is simply absent from it
    /// and therefore disappears on undo, with no bookkeeping either way.</para>
    /// </summary>
    private sealed record ArraySnapshot(string Name, Wire[] Wires);

    private DesignSnapshot Capture()
    {
        var wires = _design.AllWires().ToList();
        return new DesignSnapshot(
            [.. _design.Arrays.Select(a => new ArraySnapshot(a.Name, [.. a.Wires]))],
            [.. wires.Select(w => w.Points.ToArray())]);
    }

    private bool _inGesture;

    /// <summary>
    /// Opens a gesture: every edit until <see cref="EndGesture"/> collapses into ONE undo entry.
    ///
    /// <para>A live alt-drag applies a scale per frame, sixty times a second. Without this, one drag
    /// would leave sixty undo entries and Ctrl+Z would walk back through the drag a frame at a time
    /// instead of undoing it — the same collapse harmonicaRF's own Edit Display drag already does, for
    /// the same reason.</para>
    /// </summary>
    public void BeginGesture()
    {
        if (_inGesture) return;
        PushUndo();
        _inGesture = true;
        _fillHeld = false;   // a new gesture starts from a matrix that is in step
    }

    /// <summary>
    /// Closes a gesture. Safe to call when none is open.
    ///
    /// <para><b>This is where the capacitance is paid for</b> (wbond.md §4.4): a drag leaves it stale
    /// on purpose, and the commit is the one moment it is rebuilt. A gesture that changed no geometry
    /// rebuilds nothing.</para>
    /// </summary>
    public void EndGesture()
    {
        _inGesture = false;

        if (_fillHeld)
        {
            SettleHeldFill();
            return;
        }

        if (_capacitanceStale) Republish();
    }

    /// <summary>
    /// Ends a gesture that spent part of its life in the held state (see <see cref="RefuseFill"/>):
    /// rebuild once from wherever the wires actually landed, and roll back only if that is still a
    /// place the physics cannot evaluate.
    ///
    /// <para>A full rebuild rather than an incremental one, because the held period deliberately
    /// stopped maintaining the factor — there is no increment left to apply, and the matrix and its
    /// factor are both out of step with the mesh.</para>
    /// </summary>
    private void SettleHeldFill()
    {
        _fillHeld = false;
        OnPropertyChanged(nameof(ReadoutIsHeld));

        // The held frames kept the matrix exact, so the only thing missing is a factor — no rebuild.
        if (_fill.TryRefactor())
        {
            Republish();
            return;
        }

        // The wires were DROPPED on a degenerate position, not merely dragged across one. Now the
        // gesture IS an edit, and now undoing it is the right answer — at the moment the button came
        // up, where it does not move anything out from under the cursor.
        RefuseEdit(new InvalidOperationException(_heldReason ?? "The wires were left on a position " +
            "whose inductance matrix is singular; the move has been undone."));
    }

    /// <summary>
    /// Removes the entry <see cref="PushUndo"/> just added, for an edit that turned out to change
    /// nothing. Stamp and snapshot come off together — a stamp left behind would make this history
    /// look more recently edited than it is, and Ctrl+Z would come here instead of to the layout's.
    /// </summary>
    private void DropUndoEntry()
    {
        _undo.Pop();
        if (_undoStamps.Count > 0) _undoStamps.Pop();
    }

    /// <summary>Pushes an undo entry, unless a gesture is open. Returns whether it actually pushed.</summary>
    private bool PushUndo()
    {
        if (_inGesture) return false;
        _undo.Push(Capture());
        _undoStamps.Push(CircuitRF.Ui.Commands.EditSequence.Next());
        _redo.Clear();
        _redoStamps.Clear();
        return true;
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Capture());
        // The entry keeps the stamp it was recorded with — undo moves a cursor through history
        // rather than adding to it.
        _redoStamps.Push(_undoStamps.Count > 0 ? _undoStamps.Pop() : 0);
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Capture());
        _undoStamps.Push(_redoStamps.Count > 0 ? _redoStamps.Pop() : 0);
        Restore(_redo.Pop());
    }

    /// <summary>
    /// Puts the design back to a snapshot: array membership first, then points.
    ///
    /// <para><b>Membership is restored BEFORE points, and the order matters.</b> The point arrays are
    /// indexed by flat <see cref="WBondDesign.AllWires"/> order, which is the concatenation of the
    /// arrays' own membership — so restoring points against the CURRENT membership after a structural
    /// edit would write each wire's points onto whichever wire now happens to sit at that index.</para>
    ///
    /// <para>This is what makes add, delete, paste, merge and move-between-groups undoable. The
    /// previous version captured points alone and could only drop TRAILING wires, so a deletion or a
    /// group move survived Ctrl+Z — found by a test of the group move, and it was never specific to
    /// that edit.</para>
    /// </summary>
    private void Restore(DesignSnapshot snapshot)
    {
        bool structural = MembershipDiffers(snapshot);

        // Rebuild the arrays wholesale from the snapshot. New WireArray objects are fine — nothing
        // holds one across an undo; the profile view and the context menu both address arrays by
        // index, and the wires inside are the SAME objects.
        if (structural)
        {
            _design.Arrays.Clear();
            foreach (var a in snapshot.Arrays)
                _design.Arrays.Add(new WireArray { Name = a.Name, Wires = [.. a.Wires] });
        }

        var wires = _design.AllWires().ToList();
        int limit = Math.Min(wires.Count, snapshot.Points.Length);

        // Only the wires whose points actually CHANGED are refilled. Undoing a one-wire move used to
        // recompute all N rows of the matrix, because the restore handed every index to the fill
        // whether it had moved or not — at 500 wires that is the whole matrix for one wire's worth of
        // edit, and it is most of why an undo felt slower than the drag that preceded it.
        var changed = new List<int>();

        for (int i = 0; i < limit; i++)
        {
            if (wires[i].Points.Count != snapshot.Points[i].Length) structural = true;

            if (!structural && !PointsDiffer(wires[i].Points, snapshot.Points[i])) continue;

            wires[i].Points.Clear();
            wires[i].Points.AddRange(snapshot.Points[i]);
            changed.Add(i);
        }

        // A structural restore invalidates every flat index an outstanding selection holds.
        if (structural) Selection = new WireSelection();

        if (structural) CommitStructuralChange();
        else CommitPointMoveAfterFrame(changed);
    }

    /// <summary>Whether a wire's live points differ from the snapshot's, so it needs restoring at all.</summary>
    private static bool PointsDiffer(List<Point3> live, Point3[] snapshot)
    {
        if (live.Count != snapshot.Length) return true;
        for (int i = 0; i < snapshot.Length; i++)
            if (live[i] != snapshot[i]) return true;
        return false;
    }

    /// <summary>True when the arrays, their names, or their membership differ from the snapshot.</summary>
    private bool MembershipDiffers(DesignSnapshot snapshot)
    {
        if (_design.Arrays.Count != snapshot.Arrays.Length) return true;

        for (int a = 0; a < _design.Arrays.Count; a++)
        {
            var live = _design.Arrays[a];
            var snap = snapshot.Arrays[a];

            if (!string.Equals(live.Name, snap.Name, StringComparison.Ordinal)) return true;
            if (live.Wires.Count != snap.Wires.Length) return true;

            // By REFERENCE: two different wires with identical geometry are still a structural change.
            for (int w = 0; w < live.Wires.Count; w++)
                if (!ReferenceEquals(live.Wires[w], snap.Wires[w])) return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A minimal valid design, so a blank editor has something to draw and validate.
    ///
    /// <para>Delegates to <see cref="WBondEmbedding.DefaultDesign"/> rather than building its own:
    /// a freshly-dropped schematic component starts from the same design, and two definitions of
    /// "what a new wBond is" would drift the first time either changed.</para>
    /// </summary>
    private static WBondDesign EmptyDesign() => WBondEmbedding.DefaultDesign();
}
