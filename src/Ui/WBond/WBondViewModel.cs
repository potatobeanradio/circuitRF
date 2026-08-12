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

    [ObservableProperty] private WireSelection _selection = new();

    [ObservableProperty] private WBondUnit _displayUnit = WBondUnit.Mil;

    /// <summary>Raised whenever the readout changes — the panel and the canvas both listen.</summary>
    public event Action? ReadoutChanged;

    /// <summary>Raised on any edit, so the document can mark itself dirty.</summary>
    public event Action? DirtyChanged;

    public WBondViewModel(WBondDesign? design = null)
    {
        _design = design ?? EmptyDesign();
        _mesh = WireMesh.Build(_design);
        _fill = IncrementalFill.Create(_mesh);
        Readout = PanelReadout.Build(_design, _mesh, _fill.Reduce());
    }

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

        try
        {
            _fill.MoveWires(movedWires, motion);
        }
        catch (InvalidOperationException ex) { RefuseEdit(ex); return; }

        IncrementalUpdateCount++;
        Republish();
    }

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
        RebuildCount++;
        Republish();
    }

    /// <summary>
    /// Raised when an edit produced geometry the physics cannot evaluate, with the reason.
    /// The edit is rolled back; the design is never left in a state the panel cannot describe.
    /// </summary>
    public event Action<string>? EditRefused;

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
    /// snapshot to restore (an edit made outside both) the geometry is left alone and the previous
    /// mesh kept, so the readout is stale rather than wrong; the message says what happened either
    /// way.</para>
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
                _inGesture = false;   // the gesture cannot continue from a state that was rolled back
                Restore(snapshot);
            }
            finally { _refusing = false; }
        }

        EditRefused?.Invoke(ex.Message);
    }

    private void Republish()
    {
        Readout = PanelReadout.Build(_design, _mesh, _fill.Reduce());
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

    // ---------------------------------------------------------------- edits

    /// <summary>Nudges the current selection by one step (WB25).</summary>
    public void NudgeSelection(int dx, int dyOrDz, bool coarse, EditorView view)
    {
        if (Selection.IsEmpty) return;

        PushUndo();
        long step = coarse ? WireEdits.CoarseNudgeNm : WireEdits.DefaultNudgeNm;
        WireEdits.Nudge(_design, Selection, dx, dyOrDz, step, view);

        // A nudge moves whole points, never adds them — so it is the drag path.
        CommitPointMove([.. Selection.TouchedWires()],
                        dyOrDz == 0 ? SelectionMotion.HorizontalRigidTranslation : SelectionMotion.General);
    }

    /// <summary>Alt + vertical drag on a profile curve — scales the whole bound array (WB24a/c).</summary>
    public int ScaleProfileHeight(string profileName, double factor)
    {
        var profile = _design.ProfileByName(profileName);
        if (profile is null) return 0;

        PushUndo();
        int moved = WireEdits.ScaleBoundWires(_design, profile, heightFactor: factor, spanFactor: 1.0);
        if (moved > 0) CommitPointMove([.. BoundWireIndices(profileName)]);
        return moved;
    }

    /// <summary>Alt + horizontal drag on a profile curve — scales span by FACTOR across the array (WB24b/c).</summary>
    public int ScaleProfileSpan(string profileName, double factor)
    {
        var profile = _design.ProfileByName(profileName);
        if (profile is null) return 0;

        PushUndo();
        int moved = WireEdits.ScaleBoundWires(_design, profile, heightFactor: 1.0, spanFactor: factor);
        if (moved > 0) CommitPointMove([.. BoundWireIndices(profileName)]);
        return moved;
    }

    /// <summary>
    /// Detaches every wire in the selection from its profile (D5), returning how many were detached —
    /// the number the "N wires detached" toast reports.
    /// </summary>
    public int DetachSelection()
    {
        var wires = _design.AllWires().ToList();
        int detached = 0;

        bool pushed = PushUndo();
        foreach (int index in Selection.TouchedWires())
        {
            if (index < 0 || index >= wires.Count) continue;
            if (wires[index].ProfileBinding is null) continue;
            ProfileEnvelope.Detach(wires[index]);
            detached++;
        }

        if (detached > 0) { DirtyChanged?.Invoke(); ReadoutChanged?.Invoke(); }
        else if (pushed) _undo.Pop();   // nothing happened; do not leave a no-op on the undo stack

        return detached;
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
    /// Collapses interior points onto the chord, <b>keeping the point count</b> so a profile can be
    /// re-applied and return the wire exactly where it was.
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

    /// <summary>
    /// Re-applies each selected wire's bound profile, restoring the loop a straighten collapsed.
    /// A free (detached) wire is skipped — it has no profile to restore from.
    /// </summary>
    public int ReapplyProfileToSelection()
    {
        var touched = TouchedWireList();
        if (touched.Count == 0) return 0;

        var wires = _design.AllWires().ToList();
        var applied = new List<int>();

        // Pushed-or-not is tracked rather than assumed: inside an open gesture PushUndo deliberately
        // does nothing, and popping then would discard somebody else's entry.
        bool pushed = PushUndo();

        foreach (int index in touched)
        {
            var wire = wires[index];
            if (wire.ProfileBinding is null) continue;
            if (_design.ProfileByName(wire.ProfileBinding) is not { } profile) continue;

            profile.ApplyTo(wire, wire.Points[0], wire.Points[^1]);
            applied.Add(index);
        }

        if (applied.Count > 0) CommitPointMove(applied);
        else if (pushed) _undo.Pop();

        return applied.Count;
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

        int added = WBondClipboard.Paste(_design, payload, dxNm, dyNm, dzNm);
        if (added == 0)
        {
            if (pushed) _undo.Pop();
            return 0;
        }

        CommitStructuralChange();

        // The refusal path rolls the design back, so "did it survive" is asked of the design itself
        // rather than assumed from the paste having run.
        if (_design.AllWires().Count() != before + added) return 0;

        var selection = new WireSelection();
        for (int i = before; i < before + added; i++) selection.Wires.Add(i);
        Selection = selection;

        return added;
    }

    /// <summary>The selected wires, bounds-checked once so every transform above can trust the list.</summary>
    private List<int> TouchedWireList()
    {
        int count = _design.AllWires().Count();
        return [.. Selection.TouchedWires().Where(i => i >= 0 && i < count)];
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
    /// Adds a wire between two points, using a loop profile to generate the whole loop (§6.4).
    ///
    /// <para>Returns the new wire's flat index, or −1 when the design has no profile and none could be
    /// made. <b>Structural</b> — a new wire changes the flat filament layout, so it costs a rebuild;
    /// that is why creation is a click-click gesture rather than something that fires per pointer
    /// move.</para>
    ///
    /// <para>The wire joins the first array bound to that profile, and one is created when there is
    /// none. Array membership is what the reduction sums over (§3.4), so a wire in no array would be
    /// drawn, measured, and absent from every published inductance — silently.</para>
    /// </summary>
    public int AddWire(Point3 start, Point3 end, long diameterNm, string material,
                       string? profileName = null, int pointsIfProfileCreated = 7)
    {
        var profile = profileName is null
            ? _design.Profiles.FirstOrDefault()
            : _design.ProfileByName(profileName);

        if (profile is null)
        {
            profile = LoopProfile.BallBond(WBondUnits.ToNm(20.0, WBondUnit.Mil), pointsIfProfileCreated);
            _design.Profiles.Add(profile);
        }

        PushUndo();

        var array = _design.Arrays.FirstOrDefault(
            a => string.Equals(a.Profile, profile.Name, StringComparison.OrdinalIgnoreCase));

        if (array is null)
        {
            array = new WireArray { Name = NextArrayName(), Profile = profile.Name };
            _design.Arrays.Add(array);
        }

        array.Wires.Add(profile.CreateWire(start, end, diameterNm, material));

        CommitStructuralChange();
        return _design.AllWires().Count() - 1;
    }

    private string NextArrayName()
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
            if (pushed) _undo.Pop();
            EditRefused?.Invoke(ex.Message);
            return 0;
        }

        // ONE rebuild for the whole batch, which is the entire point of WB26.
        CommitStructuralChange();
        return made.Count;
    }

    private IEnumerable<int> BoundWireIndices(string profileName)
    {
        var wires = _design.AllWires().ToList();
        for (int i = 0; i < wires.Count; i++)
            if (string.Equals(wires[i].ProfileBinding, profileName, StringComparison.OrdinalIgnoreCase))
                yield return i;
    }

    // ---------------------------------------------------------------- undo

    /// <summary>
    /// A snapshot of every wire's points and binding — enough to undo any edit in this class.
    ///
    /// <para>Points only, not the whole design: it is what every edit here touches, and cloning the
    /// point lists is O(N·points) and allocation-light, where a `.wBond` round trip would be
    /// milliseconds of JSON per undo push.</para>
    /// </summary>
    private sealed record DesignSnapshot(
        ArraySnapshot[] Arrays, Point3[][] Points, string?[] Bindings, long[] ProfileHeights);

    /// <summary>
    /// One array's identity and MEMBERSHIP, by wire reference.
    ///
    /// <para>Holding the <see cref="Wire"/> objects themselves is what makes a deletion undoable: the
    /// deleted wire is still alive in the snapshot, so restoring membership puts the same object back
    /// rather than a reconstruction of it. A wire ADDED after the snapshot is simply absent from it
    /// and therefore disappears on undo, with no bookkeeping either way.</para>
    /// </summary>
    private sealed record ArraySnapshot(string Name, string? Profile, Wire[] Wires);

    private DesignSnapshot Capture()
    {
        var wires = _design.AllWires().ToList();
        return new DesignSnapshot(
            [.. _design.Arrays.Select(a => new ArraySnapshot(a.Name, a.Profile, [.. a.Wires]))],
            [.. wires.Select(w => w.Points.ToArray())],
            [.. wires.Select(w => w.ProfileBinding)],
            [.. _design.Profiles.Select(p => p.LoopHeightNm)]);
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
    }

    /// <summary>Closes a gesture. Safe to call when none is open.</summary>
    public void EndGesture() => _inGesture = false;

    /// <summary>Pushes an undo entry, unless a gesture is open. Returns whether it actually pushed.</summary>
    private bool PushUndo()
    {
        if (_inGesture) return false;
        _undo.Push(Capture());
        _redo.Clear();
        return true;
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Capture());
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Capture());
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
                _design.Arrays.Add(new WireArray { Name = a.Name, Profile = a.Profile, Wires = [.. a.Wires] });
        }

        var wires = _design.AllWires().ToList();
        int limit = Math.Min(wires.Count, snapshot.Points.Length);

        for (int i = 0; i < limit; i++)
        {
            if (wires[i].Points.Count != snapshot.Points[i].Length) structural = true;
            wires[i].Points.Clear();
            wires[i].Points.AddRange(snapshot.Points[i]);
            wires[i].ProfileBinding = snapshot.Bindings[i];
        }

        for (int i = 0; i < Math.Min(_design.Profiles.Count, snapshot.ProfileHeights.Length); i++)
            _design.Profiles[i].LoopHeightNm = snapshot.ProfileHeights[i];

        // A structural restore invalidates every flat index an outstanding selection holds.
        if (structural) Selection = new WireSelection();

        if (structural) CommitStructuralChange();
        else CommitPointMove([.. Enumerable.Range(0, wires.Count)]);
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
            if (!string.Equals(live.Profile, snap.Profile, StringComparison.Ordinal)) return true;
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
