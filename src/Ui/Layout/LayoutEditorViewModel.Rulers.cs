using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Layout;
using CircuitRF.Ui.Renderers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// In-design ruler annotations — docs/design/layout-view.md §9B. The <see cref="Tool.Ruler"/> tool's
/// two-click placement, the <b>third selection channel</b>, the endpoint drag, and the Clear-All
/// gesture. A partial-class extension of <see cref="LayoutEditorViewModel"/>, following the same
/// convention as <c>.Instances.cs</c>/<c>.Snap.cs</c>/<c>.Bitmaps.cs</c>.
///
/// <para><b>Why a third channel rather than a fourth shape type</b> (§9B.1): a ruler is deliberately
/// not a <see cref="LayoutShape"/>, so it cannot ride <c>_selectedIndices</c>. What that costs is this
/// file plus a handful of hooks in the main VM; what it buys is that no code walking
/// <see cref="LayoutView.Shapes"/> — GDSII, Gerber, Excellon, PCB, booleans, offset, flatten, DRC, the
/// MoM extractors — can ever leak an annotation into a manufacturing file, structurally rather than by
/// a maintained exclusion list. See <see cref="RulerAnnotation"/>'s own doc comment.</para>
///
/// <para><b>Move, nudge, delete and copy are UNIFIED with the other two channels</b>, exactly as
/// shapes and instances already are: the shared <c>BeginMoveDrag</c>/<c>CommitMoveDrag</c>/
/// <c>DeleteSelection</c>/<c>NudgeSelection</c> in the main file fold a ruler command into the same
/// <see cref="CompositeCommand"/>, so a mixed selection still moves and deletes as one undo entry.
/// Only what is genuinely ruler-specific — placement, the endpoint drag, Clear All — lives here.</para>
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    // ── Selection (the third channel) ─────────────────────────────────────────────────────────────

    private readonly List<int> _selectedRulerIndices = [];

    public IReadOnlyList<int> SelectedRulerIndices => _selectedRulerIndices;

    /// <summary>Mirrors <c>SetSelection</c>/<c>SetInstanceSelection</c> exactly —
    /// <paramref name="clearOtherKind"/> false for Shift/Ctrl add-toggle, true for a replace.</summary>
    private void SetRulerSelection(IEnumerable<int> indices, bool clearOtherKind = true)
    {
        var distinct = new List<int>();
        foreach (var i in indices)
            if (i >= 0 && i < Model.Rulers.Count && !distinct.Contains(i))
                distinct.Add(i);

        _selectedRulerIndices.Clear();
        _selectedRulerIndices.AddRange(distinct);

        if (clearOtherKind)
        {
            if (_selectedIndices.Count > 0) { _selectedIndices.Clear(); _pickedVertexIndex = null; }
            if (_selectedInstanceIndices.Count > 0) _selectedInstanceIndices.Clear();
        }

        // DELIBERATELY NOT _cycleCache.Clear() — this method is what the overlap cycle CALLS to move
        // between kinds, so clearing the stack here would destroy the very thing being walked. It cost
        // the wrap-around: ruler -> shape -> ruler worked, and the fourth click then rebuilt the stack
        // and sat on the ruler forever. SetSelection has never cleared it, for the same reason; a
        // selection arriving from anywhere ELSE invalidates the cache at its own call site instead
        // (SelectAll, DeselectAll, Delete, the marquee commit, and every model mutation).

        SelectionStatusText = ComputeSelectionStatus();
        RebuildOverlay();
    }

    private string ComputeRulerSelectionStatus()
    {
        if (_selectedRulerIndices.Count == 0) return "";
        if (_selectedRulerIndices.Count == 1)
        {
            var r = Model.Rulers[_selectedRulerIndices[0]];
            return $"Ruler · {r.FormatLength(r.DistanceDbu, DisplayUnit, Model.DbuPerMicron)}"
                 + $" {UnitSuffix(DisplayUnit)}";
        }
        return $"{_selectedRulerIndices.Count} rulers";
    }

    /// <summary>Drops any ruler index the model no longer has — the ruler half of the main file's
    /// own post-mutation index pruning, so an undo that removed rulers cannot leave the third channel
    /// pointing past the end of the list.</summary>
    private bool PruneRulerSelection()
        => _selectedRulerIndices.RemoveAll(i => i < 0 || i >= Model.Rulers.Count) > 0;

    private void ApplyRulerClickSelection(int hitIndex, bool shift, bool ctrl)
    {
        if (ctrl)
        {
            SetRulerSelection(_selectedRulerIndices.Contains(hitIndex)
                ? _selectedRulerIndices.Where(i => i != hitIndex)
                : _selectedRulerIndices.Append(hitIndex), clearOtherKind: false);
        }
        else if (shift)
        {
            SetRulerSelection(_selectedRulerIndices.Contains(hitIndex)
                ? _selectedRulerIndices
                : _selectedRulerIndices.Append(hitIndex), clearOtherKind: false);
        }
        else
        {
            bool totalMulti = _selectedIndices.Count + _selectedInstanceIndices.Count + _selectedRulerIndices.Count > 1;
            if (!(totalMulti && _selectedRulerIndices.Contains(hitIndex)))
                SetRulerSelection([hitIndex]);
        }
    }

    // ── The zoom rulers are measured against ──────────────────────────────────────────────────────

    /// <summary>
    /// The most recent device-pixels-per-DBU the canvas reported.
    ///
    /// <para><b>A <see cref="RulerSizeMode.Fixed"/> ruler's painted extent is a function of the
    /// zoom</b> — that IS the mode — so hit-testing its readout, and measuring it for Zoom-to-Fit,
    /// need the same number the renderer used. The canvas already hands it over on every press
    /// (<c>zoomPxPerDbu</c>) and, as its reciprocal, on every move (<c>pixelDbu</c>); this caches
    /// whichever arrived last so a path that gets neither (the context menu, a headless test) still
    /// measures against something real rather than zero.</para>
    /// </summary>
    internal double LastZoomPxPerDbu { get; private set; }

    internal void NoteZoomPxPerDbu(double zoomPxPerDbu)
    {
        if (zoomPxPerDbu > 0) LastZoomPxPerDbu = zoomPxPerDbu;
    }

    // ── The Show Rulers view toggle (R-rul-1) ─────────────────────────────────────────────────────

    /// <summary>R-rul-1: rulers are governed by exactly one thing besides their own existence — this
    /// per-document toggle. <b>View state, deliberately not persisted in the <c>.clay</c></b>: it says
    /// what this session wants to look at, not what the design contains. Export leaves rulers ON
    /// regardless (see <c>LayoutRenderOptions.ShowRulers</c>).</summary>
    [ObservableProperty] private bool _showRulers = true;

    partial void OnShowRulersChanged(bool value) => Model.NotifyChanged();

    // ── The Ruler tool (§9B.5) ────────────────────────────────────────────────────────────────────

    /// <summary>The first click's (already snapped) endpoint while a two-click placement is in
    /// progress, or null when the tool is armed but nothing has been clicked yet.</summary>
    private (long X, long Y)? _rulerFirstPoint;

    /// <summary>The live cursor point of the placement in progress — snapped exactly as the second
    /// click will be, so the previewed number is the one that will be committed.</summary>
    private (long X, long Y) _rulerCursor;

    /// <summary>True while the Ruler tool has one endpoint down — read by the main file's Escape
    /// contract so a half-placed ruler counts as an operation in progress.</summary>
    internal bool RulerPlacementInProgress => _rulerFirstPoint is not null;

    /// <summary>
    /// A ruler press (§9B.5 R-rul-8): the first sets the first endpoint, the second commits. <b>The
    /// tool stays armed</b> — measuring is something a user does several times in a row.
    /// </summary>
    private void HandleRulerToolPress(double wx, double wy, KeyModifiers mods, long snapTolDbu)
    {
        var pt = SnapRulerPoint(wx, wy, mods, snapTolDbu, _rulerFirstPoint);

        if (_rulerFirstPoint is not { } first)
        {
            _rulerFirstPoint = pt;
            _rulerCursor = pt;
            RebuildOverlay();
            return;
        }

        _rulerFirstPoint = null;

        // §9B.5: "a ruler whose two endpoints coincide after snapping is discarded rather than
        // committed" — a zero-length ruler reads as a rendering glitch, not as a measurement.
        if (first.X == pt.X && first.Y == pt.Y) { RebuildOverlay(); return; }

        var ruler = new RulerAnnotation
        {
            X1 = first.X, Y1 = first.Y, X2 = pt.X, Y2 = pt.Y,
            // R-rul-4: Fixed, 11 pt. Purpose 1 (the temporary measurement) is the common one and the
            // one where a bad default is immediately annoying.
            SizeMode = RulerSizeMode.Fixed,
            TextSizePt = DefaultRulerTextSizePt,
            TextHeightDbu = DefaultRulerTextHeightDbu(),
        };
        Execute(new AddRulerCommand(Model, ruler));
        RebuildOverlay();
    }

    private void HandleRulerToolMove(double wx, double wy, KeyModifiers mods, long snapTolDbu)
    {
        _rulerCursor = SnapRulerPoint(wx, wy, mods, snapTolDbu, _rulerFirstPoint);
        RebuildOverlay();
    }

    internal const double DefaultRulerTextSizePt = 11.0;

    /// <summary>The world height a ruler switched to <see cref="RulerSizeMode.Scaled"/> starts at —
    /// seeded from the same label-height default the Label tool uses, so a ruler and a label authored
    /// in the same document read at the same size. Stored at placement (never left at zero) so
    /// switching modes and back is reversible from the first moment (§9B.7).</summary>
    private long DefaultRulerTextHeightDbu() => Math.Max(1, _labelHeightDbu);

    /// <summary>
    /// The snap stack for a ruler endpoint (R-rul-9/R-rul-10), unchanged from what a <c>Path</c>
    /// vertex gets — grid snap plus the geometry-snap query, with the same marker feedback. This is
    /// what makes the measurement TRUSTWORTHY: an endpoint landing 3 DBU short of the corner reports
    /// a number that is wrong in a way nobody notices.
    ///
    /// <para><b>Geometry snap outranks the Shift constraint</b>, deliberately — a snapped endpoint is
    /// a stronger statement of intent than a held modifier.</para>
    ///
    /// <para><b>Shift is <see cref="AngleMode.Deg45"/> here, passed explicitly, NOT the document's own
    /// <see cref="LayoutView.AngleMode"/></b> (R-rul-10). A Manhattan document is a statement about
    /// manufacturable artwork, and the diagonal gap between two Manhattan traces is exactly the
    /// measurement you most want to take.</para>
    /// </summary>
    private (long X, long Y) SnapRulerPoint(double wx, double wy, KeyModifiers mods, long snapTolDbu,
                                            (long X, long Y)? from)
    {
        UpdateSnapMarker((long)Math.Round(wx), (long)Math.Round(wy), mods, Math.Max(snapTolDbu, 0), 1);
        if (_snapCandidateIsRealTarget && _currentSnapCandidate is { } target) return (target.X, target.Y);

        if (from is { } first && (mods & KeyModifiers.Shift) != 0)
            return LayoutSnapping.ConstrainAndSnap(first.X, first.Y, wx, wy, AngleMode.Deg45, Model.SnapDbu, false);

        return LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspend: false);
    }

    /// <summary>The live two-click preview (R-rul-8: "the number is visible BEFORE committing"), or
    /// null when no placement is in progress.</summary>
    private RulerAnnotation? BuildRulerPreview()
    {
        if (_rulerFirstPoint is not { } first) return null;
        return new RulerAnnotation
        {
            X1 = first.X, Y1 = first.Y, X2 = _rulerCursor.X, Y2 = _rulerCursor.Y,
            SizeMode = RulerSizeMode.Fixed,
            TextSizePt = DefaultRulerTextSizePt,
            TextHeightDbu = DefaultRulerTextHeightDbu(),
        };
    }

    /// <summary>Abandons a half-placed ruler. Called from <c>CancelDrawOp</c>, which is what Escape
    /// already runs for every drawing tool — R-rul-8's "the ruler needs no new escape logic, only to
    /// be one of them."</summary>
    private void CancelRulerPlacement() => _rulerFirstPoint = null;

    // ── Selection press / endpoint drag (§9B.6) ───────────────────────────────────────────────────

    private (int Index, bool Second, RulerAnnotation Original, RulerAnnotation Preview)? _rulerEndpointDrag;

    internal bool RulerEndpointDragActive => _rulerEndpointDrag is not null;

    /// <summary>
    /// Grabs a ruler ENDPOINT, and nothing else. Returns true when the press was consumed.
    ///
    /// <para><b>Selecting a ruler is deliberately NOT here any more</b> — it is a
    /// <see cref="LayoutPick"/> in the click's own pick stack, so a click on a ruler that is already
    /// selected cycles down to whatever lies beneath it (owner, 2026-08-27), exactly as overlapping
    /// shapes have done since L1c. What stays here is the endpoint grab, because that is a HANDLE
    /// gesture: it belongs to a ruler the user has already selected and it must beat the cycle for
    /// the same reason <see cref="TryHandleSelectPressOnHandles"/> beats it for a shape — a press on
    /// a handle must not disturb the selection.</para>
    ///
    /// <para>Only offered on a lone selected ruler, since endpoint handles render only for a
    /// single-ruler selection (§9B.6) and a gesture with no visible handle is one the user cannot
    /// know is available.</para>
    /// </summary>
    private bool TryBeginRulerEndpointDrag(long px, long py, bool shift, bool ctrl, long tolDbu)
    {
        if (shift || ctrl) return false;
        if (_selectedRulerIndices.Count != 1 || _selectedIndices.Count != 0 || _selectedInstanceIndices.Count != 0)
            return false;

        if (LayoutRulerHitTest.Hit(Model, px, py, tolDbu, LastZoomPxPerDbu) is not { } hit) return false;
        if (hit.Index != _selectedRulerIndices[0]) return false;
        if (hit.Part is not (LayoutRulerHitTest.RulerPart.Endpoint1 or LayoutRulerHitTest.RulerPart.Endpoint2))
            return false;

        var original = Model.Rulers[hit.Index];
        _rulerEndpointDrag = (hit.Index, hit.Part == LayoutRulerHitTest.RulerPart.Endpoint2,
                              original.Clone(), original.Clone());
        _selectPressWX = px; _selectPressWY = py;
        return true;
    }

    /// <summary>Live endpoint drag — moves that endpoint alone and re-measures, through the same snap
    /// stack placement uses so a re-aimed endpoint is as trustworthy as a placed one.</summary>
    private void UpdateRulerEndpointDrag(double wx, double wy, KeyModifiers mods, long snapTolDbu)
    {
        if (_rulerEndpointDrag is not { } drag) return;

        var anchor = drag.Second ? (drag.Original.X1, drag.Original.Y1) : (drag.Original.X2, drag.Original.Y2);
        var pt = SnapRulerPoint(wx, wy, mods, snapTolDbu, anchor);

        var preview = drag.Original.Clone();
        if (drag.Second) { preview.X2 = pt.X; preview.Y2 = pt.Y; }
        else             { preview.X1 = pt.X; preview.Y1 = pt.Y; }

        _rulerEndpointDrag = (drag.Index, drag.Second, drag.Original, preview);
        RebuildOverlay();
    }

    /// <summary>One <see cref="ReplaceRulerCommand"/>, or nothing at all when the endpoint did not
    /// actually move — an empty undo entry for a click that changed nothing is worse than none.</summary>
    private void CommitRulerEndpointDrag()
    {
        if (_rulerEndpointDrag is not { } drag) return;
        _rulerEndpointDrag = null;

        var before = drag.Original;
        var after = drag.Preview;
        bool moved = before.X1 != after.X1 || before.Y1 != after.Y1 || before.X2 != after.X2 || before.Y2 != after.Y2;
        bool degenerate = after.X1 == after.X2 && after.Y1 == after.Y2;

        if (moved && !degenerate)
            Execute(new ReplaceRulerCommand(Model, drag.Index, before, after));

        RebuildOverlay();
    }

    private void CancelRulerEndpointDrag()
    {
        _rulerEndpointDrag = null;
    }

    // ── Context menu (R-rul-12) ───────────────────────────────────────────────────────────────────

    /// <summary>Topmost ruler under the click, within tolerance — mirrors
    /// <see cref="FindBitmapForContextMenu"/>'s shape exactly, so a right-click finds a ruler the same
    /// way it finds a bitmap.</summary>
    public int? FindRulerForContextMenu(double wx, double wy, long tolDbu)
        => LayoutRulerHitTest.Hit(Model, (long)Math.Round(wx), (long)Math.Round(wy), tolDbu, LastZoomPxPerDbu)
            is { } hit ? hit.Index : null;

    /// <summary>Selects one ruler and nothing else — what the context menu's <c>Edit…</c> does before
    /// opening the modal, so the dialog edits the ruler that was right-clicked.</summary>
    public void SelectRuler(int index)
    {
        _cycleCache.Clear();   // not from the click stack — see SetRulerSelection's note
        SetRulerSelection([index]);
    }

    /// <summary>Selects a set of rulers and nothing else — the non-pointer selection path, used by
    /// the Select-All-of-a-kind case and by tests that need a multi-ruler selection without
    /// synthesising a marquee drag.</summary>
    public void SelectRulers(System.Collections.Generic.IEnumerable<int> indices)
    {
        _cycleCache.Clear();   // not from the click stack — see SetRulerSelection's note
        SetRulerSelection(indices);
    }

    public void DeleteRuler(int index)
    {
        if (index < 0 || index >= Model.Rulers.Count) return;
        Execute(new DeleteRulersCommand(Model, [index]));
        SetRulerSelection([]);
    }

    // ── Ctrl+K / Cmd+K — clear every ruler (R-rul-13) ─────────────────────────────────────────────

    /// <summary>R13a: enabled only when there is at least one ruler, with a stated reason when not —
    /// never a silent no-op.</summary>
    public LayoutCommandAvailability ClearAllRulersAvailability => Model.Rulers.Count > 0
        ? LayoutCommandAvailability.Enabled
        : LayoutCommandAvailability.Disabled("Clear All Rulers: this layout has no rulers.");

    /// <summary>Removes every ruler as ONE undo entry, with no confirmation prompt — the operation is
    /// undoable, and a prompt on an undoable action trains people to dismiss prompts.</summary>
    [RelayCommand]
    public void ClearAllRulers()
    {
        if (!ClearAllRulersAvailability.CanExecute) return;
        Execute(new ClearAllRulersCommand(Model));
        SetRulerSelection([]);
    }

    // ── Property edits from the Properties Inspector / the Edit… modal ────────────────────────────

    /// <summary>
    /// The ruler-side sibling of <c>LayoutShapePropertiesViewModel.ApplyToEach&lt;T&gt;</c>
    /// (R-rul-11a) — one <see cref="SetShapeFieldCommand{T}"/> per actually-changing selected ruler,
    /// folded into a single <see cref="CompositeCommand"/> so ten rulers change as ONE undo entry.
    ///
    /// <para><b>This ~20 lines is the one concrete cost of §9B.1</b> and is paid here rather than by
    /// reopening that decision: the existing <c>ApplyToEach</c> is typed
    /// <c>Func&lt;LayoutShape, T&gt;</c>, and a ruler is deliberately not a <see cref="LayoutShape"/>.
    /// <see cref="SetShapeFieldCommand{T}"/> itself is reused verbatim — its body only touches the
    /// view for <c>NotifyChanged</c> and mutates through a caller-supplied closure.</para>
    ///
    /// <para>Lives on the editor VM rather than on the panel so the context-menu <c>Edit…</c> modal
    /// and the docked inspector commit through one path and cannot disagree.</para>
    /// </summary>
    public void ApplyToEachRuler<T>(string description, Func<RulerAnnotation, T> getter,
                                    Action<RulerAnnotation, T> setter, T newValue)
    {
        if (_selectedRulerIndices.Count == 0) return;

        IUiCommand? combined = null;
        foreach (var index in _selectedRulerIndices)
        {
            if (index < 0 || index >= Model.Rulers.Count) continue;
            var ruler = Model.Rulers[index];
            var old = getter(ruler);
            if (Equals(old, newValue)) continue;

            var captured = ruler;
            IUiCommand cmd = new SetShapeFieldCommand<T>(Model, description, old, newValue, v => setter(captured, v));
            combined = combined is null ? cmd : new CompositeCommand(combined, cmd);
        }

        if (combined is not null) Execute(combined);
    }

    /// <summary>Test seam over <c>BuildPickStack</c> — the ordered list of everything under a click,
    /// which is what overlap cycling walks. Asserting it directly beats inferring the order from a
    /// sequence of clicks, and it is the one place a ruler's precedence over geometry is decided.</summary>
    internal IReadOnlyList<LayoutPick> PickStackForTest(long px, long py, long tolDbu)
        => BuildPickStack(px, py, tolDbu);

    /// <summary>The currently selected rulers, in selection order — what the Properties Inspector
    /// reads to decide a shared value or a blank.</summary>
    public IReadOnlyList<RulerAnnotation> SelectedRulers() =>
        _selectedRulerIndices.Where(i => i >= 0 && i < Model.Rulers.Count)
                             .Select(i => Model.Rulers[i]).ToList();

    /// <summary>Drag-override-aware, mirroring <c>EffectiveSelectedShapes</c> (R-L1j-1): while a drag
    /// is live the inspector must show what the user is currently aiming, not the stored value the
    /// model still holds. Covers both a single-endpoint drag and a whole-ruler move.</summary>
    public IReadOnlyList<RulerAnnotation> EffectiveSelectedRulers()
    {
        var overrides = Overlay.RulerDragOverrides;
        if (_rulerEndpointDrag is null && overrides is not { Count: > 0 }) return SelectedRulers();

        return _selectedRulerIndices
            .Where(i => i >= 0 && i < Model.Rulers.Count)
            .Select(i =>
            {
                if (_rulerEndpointDrag is { } drag && i == drag.Index) return drag.Preview;
                return overrides is not null && overrides.TryGetValue(i, out var moved) ? moved : Model.Rulers[i];
            })
            .ToList();
    }
}
