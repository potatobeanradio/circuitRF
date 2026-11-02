# Brief: two Schematic Editor selection/edit fixes

Two small, independent changes. Each is self-contained; land and verify separately.

---

## Change 1 — R→L (crossing) rubber-band selects the WHOLE net's wires

**Current:** A right-to-left (crossing) rubber-band selects each object the rect intersects. For wires
it selects only the wires the rect physically crossed. `SchematicHitTest.TestRect` already has an
`ExpandCrossing` step, but it only seeds from **component** hits (port → connected wire → next
component); it does not expand from a wire to the rest of its net.

**Wanted:** When a crossing rubber-band touches any wire, expand the selection along electrical
connectivity to include **every wire on that net** (so all of the net's wire segments are selected),
even wires the rect only partially touched or didn't touch. Crossing (R→L) only — Window (L→R) is
unchanged. Components/canvas objects continue to be selected exactly as today.

**Representation:** select whole wires (`HitKind.Wire` → wire ids), the same way the rubber-band
already represents wire selection. A wire is one electrical node end-to-end, so a fully-selected wire
highlights all its segments — that is "all wire segments on the net." We are **not** adding per-segment
(`SelectedWireSegments`) entries, and we are **not** pulling in the components attached to the net (the
request is about wires).

**Why no VM change:** both `HandleSelectDrag` (live preview) and `FinishRubberBand` (release) call
`SchematicHitTest.TestRect(…, SelectMode.Crossing)` → `ExpandCrossing`. Putting the expansion in
`ExpandCrossing` makes both the live highlight and the committed selection include the whole net, and
leaves Window mode untouched (ExpandCrossing runs only for Crossing).

### 1a. `src/Ui/Schematic/NetExtractor.cs` — add a public net-traversal helper

Add this public method (e.g. right after `FindNodeLabel`). It reuses the existing private `UnionFind`
and `AddGeometricUnions`, so it matches extraction connectivity exactly (shared vertices, T-junctions,
dot crossings):

```csharp
    /// <summary>
    /// Returns every wire id on the same electrical node(s) as <paramref name="seedWireIds"/>: the full
    /// set of wires connected via shared vertices, T-junctions, and dot crossings (the same geometric
    /// connectivity as extraction). Seed ids that exist in the model are always included. A wire is one
    /// node end-to-end (AddGeometricUnions chains a wire's vertices into a single root), so this returns
    /// the connected-wire set for the touched net. Used by the crossing rubber-band to grab a whole net
    /// from a single touched wire.
    /// </summary>
    public static HashSet<string> ConnectedWireIds(SchematicEditModel model, IEnumerable<string> seedWireIds)
    {
        var result = new HashSet<string>();
        foreach (var id in seedWireIds)
            if (model.FindWire(id) is not null) result.Add(id);
        if (result.Count == 0 || model.Wires.Count == 0) return result;

        double gs = model.GridSize;
        (long, long) QK(double x, double y) => ((long)Math.Round(x / gs), (long)Math.Round(y / gs));

        var uf = new UnionFind();
        AddGeometricUnions(model, QK, uf);

        // A wire's first-vertex root identifies its net node; wires sharing a node share the root.
        var seedRoots = new HashSet<(long, long)>();
        foreach (var id in result)
        {
            var w = model.FindWire(id);
            if (w is null || w.Points.Count == 0) continue;
            var k = QK(w.Points[0].X, w.Points[0].Y);
            if (uf.Contains(k)) seedRoots.Add(uf.Find(k));
        }
        if (seedRoots.Count == 0) return result;

        foreach (var w in model.Wires)
        {
            if (w.Points.Count == 0) continue;
            var k = QK(w.Points[0].X, w.Points[0].Y);
            if (uf.Contains(k) && seedRoots.Contains(uf.Find(k)))
                result.Add(w.Id);
        }
        return result;
    }
```

### 1b. `src/Ui/Schematic/SchematicHitTest.cs` — expand wires to whole nets in `ExpandCrossing`

In `ExpandCrossing`, immediately **before** `return result;`, add the whole-net wire expansion. Seeds =
every wire already in the result (the rect's wire hits plus any wires the existing component-chain
expansion added):

```csharp
        // Whole-net wire expansion (crossing only): a crossing select touching ANY wire grabs every
        // wire on the same electrical node — shared vertices, T-junctions, dot crossings — so the
        // entire net's wire segments are selected, not just the wires the rect physically crossed.
        var wireSeeds = result.Where(h => h.Kind == HitKind.Wire).Select(h => h.Id).ToList();
        if (wireSeeds.Count > 0)
            foreach (var wid in NetExtractor.ConnectedWireIds(editModel, wireSeeds))
                if (selected.Add(wid))
                    result.Add(new HitResult(HitKind.Wire, wid));

        return result;
```

(`selected` and `result` already exist in `ExpandCrossing`; `selected` is the `HashSet<string>` of ids
already chosen, so the `selected.Add(wid)` guard prevents duplicates.)

**Perf note:** `ExpandCrossing` runs on every pointer-move during a crossing drag (live preview) and on
release. `ConnectedWireIds` builds a union-find + one `ComputeConnectivityGeometry()` pass — O(N) — per
call. This is consistent with the existing `ExpandCrossing` cost (it already iterates all
components×ports×wires per call), and it gives live net-highlight feedback during the drag. If profiling
on a very large schematic shows this is too heavy per frame, the expansion can later be gated to the
release path only (`FinishRubberBand`) — but keep it in `ExpandCrossing` for now (single source, live
feedback, Window mode untouched).

### Change 1 verification
- Draw a net spanning several wires joined by corners/T-junctions. R→L (crossing) rubber-band through
  **one** wire of it → **all** wires on that net become selected (whole net highlighted), including
  wires outside the rect. Dragging/deleting then acts on the whole net.
- L→R (Window) rubber-band over the same partial area → unchanged (only fully-enclosed objects).
- Two wires on opposite pins of a component (R1.pin1 vs R1.pin2) are **different** nets → crossing one
  does not select the other (the union-find does not union across component bodies).
- A crossing rect over a component still selects the component (and its existing component-chain
  expansion) as before; now its wires also pull in their full nets.

---

## Change 2 — Esc fully cancels a net-label edit (label must NOT move) and closes the edit box

**Root cause:** `WorkspaceWindow.axaml` has a Window-level binding
`<KeyBinding Gesture="Escape" Command="{Binding DisarmPlacementCommand}"/>`. That binding marks the
Escape key **Handled** before the inline `TextBox`'s bubble `KeyDown` (`OnInlineEditKeyDown`, a normal
handler) can run — so the box's own Escape branch never fires. The box therefore stays open; when focus
later leaves it, the deferred path `OnInlineEditLostFocus → MaybeDismissInlineEdit →
CommitAndDismissInlineEdit → CommitInlineEdit` runs, and for a label being edited via node-search
(`_inlineEditMoveLabel == true`) that executes `MoveNetLabelAnchorCommand` — **moving the label**.
(The VM's `CancelInlineEdit` never moves a label; the move only comes from this unwanted commit.)

**Fix:** intercept Escape in `OnViewKeyDownTunnel`. That handler is registered
`RoutingStrategies.Tunnel, handledEventsToo: true` (see the SchematicView constructor), so it fires even
though the Window KeyBinding marked the event handled — it is the codebase's established interception
point for exactly this "Window KeyBinding ate the key" problem. When the inline box is focused, Escape
there synchronously cancels (kind → None) and closes the box, which makes the deferred commit a
guaranteed no-op (kind is None and the box is already invisible). Enter and typing still fall through to
the TextBox.

### `src/Ui/Views/Content/SchematicView.axaml.cs` — `OnViewKeyDownTunnel`

Replace the opening guard lines:
```csharp
        if (!IsKeyboardFocusWithin) return;               // focus not inside this view — skip
        if (InlineEditBox.IsKeyboardFocusWithin) return;  // inline TextBox owns its own Esc/Enter
        var vm = Vm;
        if (vm is null) return;
```
with:
```csharp
        if (!IsKeyboardFocusWithin) return;               // focus not inside this view — skip

        // The inline edit box owns its own typing and Enter. Escape is special: the Window-level
        // Escape KeyBinding (DisarmPlacementCommand) marks the event Handled before the TextBox's
        // bubble KeyDown (OnInlineEditKeyDown) can run, so the box's own Escape branch never fires —
        // leaving the box open and letting the deferred LostFocus commit MOVE a net label. This tunnel
        // handler is registered handledEventsToo:true, so intercept Escape HERE to guarantee a full
        // cancel (the net label must not move) and to close the box. Other keys fall through to the box.
        if (InlineEditBox.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Escape && Vm is not null)
            {
                Vm.CancelInlineEdit();   // kind → None: any deferred MaybeDismissInlineEdit/Commit is a no-op
                DismissInlineEditBox();  // IsVisible = false → MaybeDismissInlineEdit early-returns
                Vm.SetSelectTool();
                e.Handled = true;
            }
            return;                       // box owns Enter + typing; only Escape needs handling here
        }

        var vm = Vm;
        if (vm is null) return;
```

Leave the rest of `OnViewKeyDownTunnel` and all of `OnInlineEditKeyDown` unchanged. (`OnInlineEditKeyDown`'s
Escape branch is now redundant but harmless — keep it as a belt-and-suspenders; Enter still commits
through it since Enter is not a Window KeyBinding.)

### Change 2 verification
- Double-click a wire that is on a net which already has a label elsewhere → the inline box opens
  pre-filled with the message "Net already named '…' — editing here moves the label to this wire" →
  press **Esc** → the label does **not** move, the box closes, the tool returns to Select. (Before the
  fix, the label jumped to the clicked wire.)
- Double-click an existing net label, change the text, press **Esc** → no rename, box closes, label
  unchanged.
- Double-click a wire to create a new label, type, press **Esc** → no label created, box closes.
- **Enter** still commits in every case (create / rename / move) exactly as before.
- Escape when **not** inline-editing (canvas) still cancels the active tool / clears selection as before
  (the non-inline path of `OnViewKeyDownTunnel` is unchanged).
- Undo after a committed net-label move still works (unchanged).

---

## Notes
- Change 1 is pure selection behavior; no model mutation, no undo entry, no persistence impact.
- Change 2 changes only key routing in the View; no VM/model/persistence change. `MoveNetLabelAnchorCommand`
  and the commit path are untouched — they simply no longer run on Esc.
- The two changes touch different files except none overlap, so they can land in either order.
