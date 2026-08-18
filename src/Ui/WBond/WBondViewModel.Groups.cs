using CircuitRF.WBond;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// Group-scoped edits — the operations the profile view's own context menu offers (wbond.md §6.4a).
///
/// <para><b>Every one of these applies to a whole ARRAY, not to the selection.</b> That is the
/// distinction that makes the profile view's menu worth having separately from the toolbar: the
/// profile view draws one curve per array (§6.4), so the thing under the pointer there IS a group,
/// and "set the loop height" means setting it for the group you are looking at. The toolbar's own
/// transforms stay selection-scoped and are unchanged.</para>
///
/// <para><b>Every operation is wire-by-wire, and there is no bound-versus-free case any more</b>
/// (2026-08-18). Loop profiles are gone: a wire's points are the only truth about its shape, so each
/// member is read, transformed and written on its own terms. That is what the owner asked for —
/// <i>"each array is generally its own shape and I want flexibility for user to change each wire
/// within the array"</i> — and it is also simply fewer paths.</para>
/// </summary>
public sealed partial class WBondViewModel
{
    /// <summary>The arrays, in the order the profile view draws them.</summary>
    public IReadOnlyList<WireArray> Arrays => _design.Arrays;

    /// <summary>Name of the array at <paramref name="arrayIndex"/>, or null when out of range.</summary>
    public string? ArrayNameAt(int arrayIndex) =>
        arrayIndex >= 0 && arrayIndex < _design.Arrays.Count ? _design.Arrays[arrayIndex].Name : null;

    // ---------------------------------------------------------------- reads

    /// <summary>
    /// The shape a group can be copied from — read straight off its first wire's own geometry
    /// (<see cref="LoopShape.Read"/>), with that wire's own loop height.
    ///
    /// <para><b>Reading rather than looking anything up is now the only path</b>, and it is the one
    /// that always worked: it makes Copy Coordinates work on a hand-drawn or imported group, which is
    /// the case a user most wants to lift a shape out of.</para>
    ///
    /// <para>The loop height reported is the WIRE's own max z minus min z (the definition — see
    /// <see cref="Wire.LoopHeightNm"/>), NOT its peak above the chord. Those differ whenever the feet
    /// sit at different z, and writing the shape back with the wrong one would silently flatten an
    /// asymmetric loop.</para>
    /// </summary>
    public (IReadOnlyList<ShapePoint> Shape, long LoopHeightNm)? ShapeForGroup(int arrayIndex)
    {
        if (arrayIndex < 0 || arrayIndex >= _design.Arrays.Count) return null;

        var wire = _design.Arrays[arrayIndex].Wires.FirstOrDefault();
        if (wire is null || wire.Points.Count < 2) return null;

        return (LoopShape.Read(wire), Math.Max(1, wire.LoopHeightNm));
    }

    // ---------------------------------------------------------------- writes

    /// <summary>
    /// Sets every wire in the group to <paramref name="heightNm"/> of loop height,
    /// <b>preserving each wire's authored X-Y path</b>.
    ///
    /// <para><b>This used to straighten a hand-routed wire, and that was a real bug</b> (2026-08-18).
    /// The old path read each wire's shape, set a height on it and stamped it back — and stamping a
    /// shape writes X and Y by linear interpolation between the feet, so a wire taken around an
    /// obstacle came back as a plain planar arc. The same wire's <c>LoopHeight_G1</c> controlling
    /// parameter has preserved its path since 2026-08-17, so the editor and the netlist disagreed
    /// about the same wire. Both now go through
    /// <see cref="WireEdits.SetLoopHeightPreservingPath"/>.</para>
    ///
    /// <para>A dead-straight wire is <b>refused</b> rather than arched, because there is no rise to
    /// scale and nothing honest to do — that is the primitive's own rule and it is not papered over
    /// here.</para>
    /// </summary>
    public int SetGroupLoopHeight(int arrayIndex, long heightNm)
    {
        if (heightNm <= 0) return 0;
        if (Group(arrayIndex) is not { } array) return 0;

        bool pushed = PushUndo();
        int changed = 0;

        foreach (var wire in array.Wires)
            if (WireEdits.SetLoopHeightPreservingPath(wire, heightNm)) changed++;

        // Height changes move points without adding them, so this is the cheap drag path.
        if (changed > 0) CommitPointMove([.. FlatIndices(arrayIndex)]);
        else if (pushed) DropUndoEntry();

        return changed;
    }

    /// <summary>Sets every wire in the group to <paramref name="spanNm"/> of foot-to-foot span.</summary>
    public int SetGroupSpan(int arrayIndex, long spanNm)
    {
        if (spanNm <= 0) return 0;
        if (Group(arrayIndex) is not { } array) return 0;

        PushUndo();
        int changed = 0;

        foreach (var wire in array.Wires)
        {
            double currentNm = wire.ChordLengthMetres() * WBondUnits.NmPerMetre;
            if (currentNm <= 0) continue;

            WireEdits.ScaleSpan(wire, spanNm / currentNm, moveOutputFoot: true);
            changed++;
        }

        if (changed > 0) CommitPointMove([.. FlatIndices(arrayIndex)]);
        return changed;
    }

    /// <summary>Sets every wire in the group to <paramref name="diameterNm"/>.</summary>
    public int SetGroupDiameter(int arrayIndex, long diameterNm)
    {
        if (diameterNm <= 0) return 0;
        if (Group(arrayIndex) is not { } array) return 0;

        PushUndo();
        foreach (var wire in array.Wires) wire.DiameterNm = diameterNm;

        // Diameter is the filament radius: it changes self-inductance and the internal impedance, so
        // the matrix must be refilled rather than incrementally updated.
        CommitStructuralChange();
        return array.Wires.Count;
    }

    /// <summary>Sets every wire in the group to <paramref name="material"/> (a <see cref="WireMaterial"/> name).</summary>
    public int SetGroupMaterial(int arrayIndex, string material)
    {
        if (string.IsNullOrWhiteSpace(material)) return 0;
        if (Group(arrayIndex) is not { } array) return 0;

        PushUndo();
        foreach (var wire in array.Wires) wire.Material = material;

        // Material governs σ, which is R and the internal-impedance term — not the external L. The
        // structural path is still the honest one: the readout's R/ωL column changes.
        CommitStructuralChange();
        return array.Wires.Count;
    }

    /// <summary>
    /// Rotates the whole group rigidly about its own centroid in the layout plane.
    ///
    /// <para>Rigid about the group centroid, rather than each wire about its own foot, because a
    /// bond group is a physical fan whose wires' relative placement is the thing being preserved —
    /// rotating each wire independently would shear the group apart.</para>
    /// </summary>
    public int RotateGroup(int arrayIndex, double radians)
    {
        if (Group(arrayIndex) is not { } array || array.Wires.Count == 0) return 0;

        var indices = FlatIndices(arrayIndex).ToList();
        if (indices.Count == 0) return 0;

        long sumX = 0, sumY = 0;
        int n = 0;
        foreach (var wire in array.Wires)
            foreach (var p in wire.Points) { sumX += p.X; sumY += p.Y; n++; }

        if (n == 0) return 0;
        var pivot = new Point3(sumX / n, sumY / n, 0);

        // Route through the SAME selection-scoped rotation the toolbar uses, so the two can never
        // disagree about how a rotation is applied — only about what it is applied to.
        var saved = Selection;
        Selection = new WireSelection { Wires = [.. indices] };
        int moved = RotateSelectionRigidly(radians, pivot, EditorView.Layout);
        Selection = saved;

        return moved;
    }

    /// <summary>Reverses every wire in the group (WB26b — this flips each one's mutual-coupling sign).</summary>
    public int ReverseGroup(int arrayIndex)
    {
        if (Group(arrayIndex) is not { } array) return 0;

        var saved = Selection;
        Selection = new WireSelection { Wires = [.. FlatIndices(arrayIndex)] };
        int n = ReverseSelection();
        Selection = saved;

        return n;
    }

    /// <summary>
    /// Mirrors the group's loop shape end-for-end — a crest at 30% of the span moves to 70%.
    ///
    /// <para><b>Not the same as reversing.</b> See <see cref="LoopShape.Flip"/>: reversing changes
    /// which foot is the input and therefore every mutual sign; flipping changes only where the crest
    /// sits. They are adjacent menu items precisely because they look alike and are not.</para>
    /// </summary>
    public int FlipGroup(int arrayIndex)
    {
        if (Group(arrayIndex) is not { } array || array.Wires.Count == 0) return 0;

        bool pushed = PushUndo();
        int changed = 0;

        foreach (var wire in array.Wires)
        {
            if (wire.Points.Count < 2) continue;

            // A flip IS an X-Y operation — the crest moves along the span — so writing the mirrored
            // shape back between the same two feet is exactly right here, unlike a loop-height change.
            var flipped = LoopShape.Flip(LoopShape.Read(wire));
            LoopShape.Write(wire, wire.Points[0], wire.Points[^1], flipped, wire.LoopHeightNm);
            changed++;
        }

        if (changed > 0) CommitPointMove([.. FlatIndices(arrayIndex)]);
        else if (pushed) DropUndoEntry();

        return changed;
    }

    /// <summary>
    /// Gives every wire in the group the supplied shape — the Paste half of §6.4a.
    ///
    /// <para><b>A one-shot stamp, and nothing is installed on the design</b> (2026-08-18). The shape
    /// used to be stored as the group's own named profile with every wire bound to it; that persistent
    /// link is exactly what the owner rejected. Each wire keeps its own feet and gets the pasted
    /// shape's z-versus-span; nothing afterwards remembers where the shape came from.</para>
    /// </summary>
    public int ApplyShapeToGroup(int arrayIndex, IReadOnlyList<ShapePoint> shape, long loopHeightNm)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (Group(arrayIndex) is not { } array || array.Wires.Count == 0) return 0;

        PushUndo();

        int changed = 0;
        foreach (var wire in array.Wires)
        {
            if (wire.Points.Count < 2) continue;
            LoopShape.Write(wire, wire.Points[0], wire.Points[^1], shape, loopHeightNm);
            changed++;
        }

        // The pasted shape may carry a different point count, which changes the filament layout —
        // so this is structural, not a point move.
        CommitStructuralChange();
        return changed;
    }

    /// <summary>
    /// Merges another design's wires into this one, keeping group identity — the Import Wires path
    /// (wbond.md §9.4).
    ///
    /// <para>Wires join an EXISTING group of the same name rather than creating a duplicate, so
    /// importing a bond list drawn elsewhere lands its GND wires in this design's GND group. Group
    /// identity travels by name; that is the whole reason the DXF layer carries the name rather than
    /// an index.</para>
    ///
    /// <para>The reference layout is untouched — this adds wires, it does not replace a design.</para>
    /// </summary>
    /// <returns>The number of wires added.</returns>
    public int MergeWires(WBondDesign incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (incoming.WireCount == 0) return 0;

        PushUndo();
        int added = 0;

        foreach (var source in incoming.Arrays)
        {
            var target = _design.Arrays
                .FirstOrDefault(a => string.Equals(a.Name, source.Name, StringComparison.OrdinalIgnoreCase));

            if (target is null)
            {
                target = new WireArray { Name = source.Name };
                _design.Arrays.Add(target);
            }

            foreach (var wire in source.Wires)
            {
                target.Wires.Add(wire);
                added++;
            }
        }

        // New wires change the filament layout, so the matrix is refilled rather than patched.
        if (added > 0) CommitStructuralChange();
        return added;
    }

    /// <summary>
    /// Deletes every wire the selection touches, and removes any group left empty by it — see
    /// <see cref="PruneEmptyGroups"/> for why the appealing alternative (keep the pin) is not
    /// available at this layer.
    /// </summary>
    /// <returns>The number of wires removed.</returns>
    public int DeleteSelectedWires()
    {
        var touched = Selection.TouchedWires();
        if (touched.Count == 0) return 0;

        // Deleting EVERY wire is allowed (owner, 2026-08-16: "make it support 0 wires") — the design
        // comes back with no wires and, through PruneEmptyGroups, no arrays either. See
        // WBondDesign.Validate for why an EMPTY array is still refused while NO arrays is not.
        PushUndo();

        // Walk the flat index space once, keeping what is not selected — rebuilding each array in
        // place, so the surviving wires keep their order and their group.
        int flat = 0;
        int removed = 0;

        foreach (var array in _design.Arrays)
        {
            var keep = new List<Wire>(array.Wires.Count);
            foreach (var wire in array.Wires)
            {
                if (touched.Contains(flat)) removed++;
                else keep.Add(wire);
                flat++;
            }

            array.Wires.Clear();
            array.Wires.AddRange(keep);
        }

        PruneEmptyGroups();

        // Every remaining flat index has shifted; an outstanding selection would now point at the
        // wrong wires, so it is dropped rather than silently re-pointed.
        Selection = new WireSelection();
        CommitStructuralChange();

        return removed;
    }

    // ---------------------------------------------------------------- single-wire edits

    /// <summary>
    /// Moves one point of one wire — the Properties Inspector's coordinate list (§6.9).
    ///
    /// <para>The DRAG path: a point move changes no filament count, so the matrix is patched
    /// incrementally rather than refilled.</para>
    /// </summary>
    public bool SetWirePoint(int wireIndex, int pointIndex, Point3 value)
    {
        if (_design.AllWires().ElementAtOrDefault(wireIndex) is not { } wire) return false;
        if (pointIndex < 0 || pointIndex >= wire.Points.Count) return false;
        if (wire.Points[pointIndex] == value) return false;

        PushUndo();
        wire.Points[pointIndex] = value;
        CommitPointMove([wireIndex]);
        return true;
    }

    /// <summary>
    /// Sets ONE wire's loop height — max z minus min z, per the definition (§3.0) — keeping every
    /// X and Y exactly as authored.
    ///
    /// <para>The same primitive the whole-group command and the <c>LoopHeight_G1</c> controlling
    /// parameter use, so a wire cannot end up with a different shape depending on which of the three
    /// asked for the height.</para>
    /// </summary>
    public bool SetWireLoopHeight(int wireIndex, long heightNm)
    {
        if (heightNm <= 0) return false;
        if (_design.AllWires().ElementAtOrDefault(wireIndex) is not { } wire) return false;
        if (wire.Points.Count < 2) return false;
        if (wire.LoopHeightNm == heightNm) return false;   // no-op: no undo entry

        bool pushed = PushUndo();

        if (!WireEdits.SetLoopHeightPreservingPath(wire, heightNm))
        {
            if (pushed) DropUndoEntry();   // dead straight: no rise to scale, nothing honest to do
            return false;
        }

        CommitPointMove([wireIndex]);
        return true;
    }

    /// <summary>
    /// Sets ONE wire's foot-to-foot span. The output foot moves; the input foot stays put.
    ///
    /// </summary>
    public bool SetWireSpan(int wireIndex, long spanNm)
    {
        if (spanNm <= 0) return false;
        if (_design.AllWires().ElementAtOrDefault(wireIndex) is not { } wire) return false;

        double currentNm = wire.ChordLengthMetres() * WBondUnits.NmPerMetre;
        if (currentNm <= 0) return false;

        PushUndo();
        WireEdits.ScaleSpan(wire, spanNm / currentNm, moveOutputFoot: true);
        CommitPointMove([wireIndex]);
        return true;
    }

    /// <summary>
    /// Moves one wire into another group, creating the group when the name is new.
    ///
    /// <para>A group is a named terminal (§3.4) — moving a wire between groups is a statement about
    /// which pin it lands on, so the reduction, the mutual matrix and the panel all follow. Structural
    /// for that reason.</para>
    ///
    /// <para><b>The selection is RE-POINTED to the wire's new flat index rather than dropped</b>,
    /// because the caller is the Properties panel and the user is still looking at this wire. Every
    /// other structural edit here drops the selection; this one would blank the panel mid-edit.</para>
    /// </summary>
    public bool MoveWireToGroup(int wireIndex, string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName)) return false;
        if (_design.AllWires().ElementAtOrDefault(wireIndex) is not { } wire) return false;

        var source = _design.Arrays.FirstOrDefault(a => a.Wires.Contains(wire));
        if (source is null) return false;
        if (string.Equals(source.Name, groupName, StringComparison.OrdinalIgnoreCase)) return false;

        PushUndo();

        var target = _design.Arrays
            .FirstOrDefault(a => string.Equals(a.Name, groupName, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            target = new WireArray { Name = groupName.Trim() };
            _design.Arrays.Add(target);
        }

        source.Wires.Remove(wire);
        target.Wires.Add(wire);

        // A source left empty is REMOVED — see PruneEmptyGroups. Leaving it makes the array-basis
        // reduction singular, so the whole move would be refused and rolled back.
        PruneEmptyGroups();
        CommitStructuralChange();

        int newIndex = _design.AllWires().ToList().IndexOf(wire);
        Selection = newIndex >= 0 ? new WireSelection { Wires = [newIndex] } : new WireSelection();

        return true;
    }

    /// <summary>
    /// Moves EVERY wire the selection touches into one group, creating it when the name is new —
    /// the "Group Wires As…" command (owner, 2026-08-16).
    ///
    /// <para><b>One structural rebuild for the whole batch, and one undo entry</b>, which is the
    /// reason this is not a loop over <see cref="MoveWireToGroup"/> at the call site: that would cost
    /// N mesh rebuilds and leave N entries on the undo stack, so Ctrl+Z would walk the regrouping
    /// back one wire at a time.</para>
    ///
    /// <para>The wires are collected BEFORE anything moves. Membership is what the flat index space
    /// is built from, so resolving indices as the arrays change under them would move the wrong
    /// wires — the same ordering trap <see cref="Restore"/> already documents.</para>
    ///
    /// <para><b>A source group left empty is REMOVED</b>, and it has to be: <c>WBondDesign.Validate</c>
    /// refuses an array with no wires outright — "an empty array makes the mapping matrix
    /// rank-deficient and the array-basis inductance singular". The appealing alternative (a group is
    /// a named terminal, so keep the pin) is not available at this layer; leaving one behind makes the
    /// whole edit unevaluable, so the refusal fires and the regroup silently rolls back. See
    /// <see cref="PruneEmptyGroups"/>.</para>
    /// </summary>
    /// <returns>How many wires actually changed group.</returns>
    public int MoveWiresToGroup(IEnumerable<int> wireIndices, string groupName)
    {
        ArgumentNullException.ThrowIfNull(wireIndices);
        if (string.IsNullOrWhiteSpace(groupName)) return 0;

        string name = groupName.Trim();
        var all = _design.AllWires().ToList();

        // Resolve to wire OBJECTS first — see the remarks.
        var moving = wireIndices
            .Where(i => i >= 0 && i < all.Count)
            .Select(i => all[i])
            .Distinct()
            .Where(w => _design.Arrays.FirstOrDefault(a => a.Wires.Contains(w)) is { } src
                        && !string.Equals(src.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (moving.Count == 0) return 0;

        PushUndo();

        var target = _design.Arrays
            .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            target = new WireArray { Name = name };
            _design.Arrays.Add(target);
        }

        foreach (var wire in moving)
        {
            _design.Arrays.FirstOrDefault(a => a.Wires.Contains(wire))?.Wires.Remove(wire);
            target.Wires.Add(wire);
        }

        PruneEmptyGroups();
        CommitStructuralChange();

        // Re-pointed rather than dropped: the user is looking at these wires and expects them to stay
        // highlighted where they landed — the same courtesy MoveWireToGroup extends to its one wire.
        var moved = _design.AllWires().ToList();
        var selection = new WireSelection();
        foreach (var wire in moving)
        {
            int index = moved.IndexOf(wire);
            if (index >= 0) selection.Wires.Add(index);
        }
        Selection = selection;

        return moving.Count;
    }

    /// <summary>Every group name in the design, in the order the profile view draws them.</summary>
    public IReadOnlyList<string> GroupNames => [.. _design.Arrays.Select(a => a.Name)];

    /// <summary>
    /// Drops every group that has no wires left in it.
    ///
    /// <para><b>Not tidiness — a validity rule.</b> <c>WBondDesign.Validate</c> rejects an empty
    /// array, because the array-basis reduction maps wires onto groups and a group with no wires is a
    /// zero row: the mapping matrix is rank-deficient and the reduced inductance singular. Every edit
    /// that can empty a group therefore has to call this, or the edit is refused and rolled back the
    /// moment the mesh is rebuilt — which is how it reads to the user: the command appears to do
    /// nothing and reports a physics error it has no way to connect to what they did.</para>
    ///
    /// <para><b>The LAST group is pruned too</b> (owner, 2026-08-16: "make it support 0 wires"). It
    /// used to be kept, on the grounds that a design needs at least one array — so deleting the only
    /// wire left one empty array behind, which is precisely the state the paragraph above says is
    /// invalid, and the user got the mapping-matrix sentence for pressing Delete. A design with no
    /// wires has no groups either: a group is a named terminal, and there is nothing to terminate.</para>
    /// </summary>
    private void PruneEmptyGroups()
    {
        for (int i = _design.Arrays.Count - 1; i >= 0; i--)
            if (_design.Arrays[i].Wires.Count == 0) _design.Arrays.RemoveAt(i);
    }

    /// <summary>The group a flat wire index belongs to, or null when the index names no wire.</summary>
    public string? GroupNameOfWire(int flatIndex)
    {
        if (flatIndex < 0) return null;

        int flat = 0;
        foreach (var array in _design.Arrays)
        {
            if (flatIndex < flat + array.Wires.Count) return array.Name;
            flat += array.Wires.Count;
        }
        return null;
    }

    /// <summary>
    /// A group name not yet in use — <c>G1</c>, <c>G2</c>, … — for seeding the "New Group…" prompt so
    /// the user is offered a valid answer rather than an empty box.
    /// </summary>
    public string SuggestGroupName() => NextArrayName();

    /// <summary>Sets one wire's diameter. Structural: the filament radius is in the self term.</summary>
    public bool SetWireDiameter(int wireIndex, long diameterNm)
    {
        if (diameterNm <= 0) return false;
        if (_design.AllWires().ElementAtOrDefault(wireIndex) is not { } wire) return false;
        if (wire.DiameterNm == diameterNm) return false;

        PushUndo();
        wire.DiameterNm = diameterNm;
        CommitStructuralChange();
        return true;
    }

    /// <summary>Sets one wire's material. Structural: σ drives R and the internal impedance.</summary>
    public bool SetWireMaterial(int wireIndex, string material)
    {
        if (string.IsNullOrWhiteSpace(material)) return false;
        if (_design.AllWires().ElementAtOrDefault(wireIndex) is not { } wire) return false;
        if (string.Equals(wire.Material, material, StringComparison.Ordinal)) return false;

        PushUndo();
        wire.Material = material;
        CommitStructuralChange();
        return true;
    }

    /// <summary>Removes the whole group and every wire in it.</summary>
    public int DeleteGroup(int arrayIndex)
    {
        if (Group(arrayIndex) is not { } array) return 0;

        PushUndo();
        int n = array.Wires.Count;
        _design.Arrays.RemoveAt(arrayIndex);

        // Every flat wire index above the removed group has shifted — an outstanding selection would
        // now point at the wrong wires, so it is dropped rather than silently re-pointed.
        Selection = new WireSelection();
        CommitStructuralChange();

        return n;
    }

    // ---------------------------------------------------------------- helpers

    private WireArray? Group(int arrayIndex) =>
        arrayIndex >= 0 && arrayIndex < _design.Arrays.Count ? _design.Arrays[arrayIndex] : null;

    /// <summary>Flat <see cref="WBondDesign.AllWires"/> indices belonging to one array.</summary>
    public IEnumerable<int> FlatIndices(int arrayIndex)
    {
        int flat = 0;
        for (int a = 0; a < _design.Arrays.Count; a++)
        {
            foreach (var _ in _design.Arrays[a].Wires)
            {
                if (a == arrayIndex) yield return flat;
                flat++;
            }
        }
    }

}
