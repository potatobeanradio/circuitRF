# Sonnet Brief — Phase L1i: live marquee selection feedback

**Design:** `docs/design/layout-view.md` §6.2 (selection). **Consumes L1c** (marquee, hit-testing, selection).
**Independent of L1h** — it touches the marquee drag path only, and L1h touches the context menu, the
properties panel and a new Scale command. If both are in flight, they should not collide; if they do, L1h
wins and this rebases.

Small, self-contained fix. One owner report.

## Symptom

While dragging a selection marquee, nothing highlights. The selection appears only on release, so the user
cannot see what they are about to select until it is already selected.

## Root cause

`LayoutEditorViewModel.CommitMarquee` is called **only** from `HandleSelectRelease`. The drag-move handler
does just this:

```csharp
case SelectDragKind.Marquee:
    _marqueeCurX = px; _marqueeCurY = py;
    RebuildOverlay();
    break;
```

`RebuildOverlay` draws the marquee rectangle but computes no hits, so no shape is ever highlighted mid-drag.

## Fix

**R-L1i-1. Extract the hit computation and commit it to one function, then call it from both the move handler
and the release handler.**

Split `CommitMarquee` into:

```csharp
// Pure: no mutation of _selectedIndices, no side effects.
private List<int> ComputeMarqueeSelection(long curX, long curY);
```

containing everything `CommitMarquee` does today **except** the final `SetSelection` — the visible/selectable
filter, the `leftToRight` enclose-vs-crossing test, and the Shift/Ctrl combination against
`_marqueeBaseSelection`. `CommitMarquee` then becomes `SetSelection(ComputeMarqueeSelection(x, y))`, and the
move handler calls the same function to produce a **preview**.

**One predicate, two callers.** This is the point of the refactor: if the preview computed hits differently
from the commit, the highlight would lie about the outcome, and the two would drift the first time either was
touched.

**R-L1i-2. The preview does not mutate `_selectedIndices`.** Store it in a separate `_marqueePreview` list.
The modifier combination is computed *against* `_marqueeBaseSelection`, so a preview that wrote into the real
selection would corrupt the base it is derived from on the very next pointer move.

**R-L1i-3. Highlight the computed set, not the raw hits.** `ComputeMarqueeSelection` already folds in the
Shift (add) and Ctrl (toggle) semantics, so its result *is* the prospective final selection. Highlighting
exactly that makes the preview literally what-you-see-is-what-you-get, including the case where Ctrl-dragging
over an already-selected shape will *deselect* it — which should visibly un-highlight as the marquee reaches
it. That behaviour is the owner's "unhighlight" and it falls out of this rule rather than needing its own
code.

**Rendering.** Give the renderer a single "effective highlight" list — `_marqueePreview` while a marquee drag
is active, `_selectedIndices` otherwise — so there is one highlight path rather than a committed one and a
preview one. Preview shapes use the same accent as a committed selection; do not invent a second style, since
the whole point is to show the outcome.

**R-L1i-4. The marquee rectangle shows which mode is active.** Now that the highlight updates live, dragging
back across the press point flips enclose ↔ crossing mid-gesture and the highlight visibly changes — which is
confusing without a matching cue. Draw the marquee **solid** for left-to-right (enclose) and **dashed** for
right-to-left (crossing), the standard CAD affordance. It is nearly free here and it is what makes the
distinction learnable rather than mysterious.

**Status readout.** Update the metadata-bar readout live during the drag — *"4 shapes"* — matching L1c's
`n of m` convention. Clear it on release or cancel.

**Escape mid-marquee** clears the preview and leaves `_selectedIndices` untouched. Verify this still holds.

## Performance

The recompute runs on the UI thread on every pointer move and currently scans all shapes.

- **Skip the recompute when the marquee rectangle has not changed** by at least one device pixel. Pointer
  moves arrive far faster than the rectangle meaningfully changes.
- **Keep it allocation-light**: reuse a scratch `List<int>` across moves rather than allocating per move, and
  do not allocate in the `LayoutGeometry.BboxOf` loop.
- Add `// L2: query the spatial index instead of scanning all shapes` at the loop. L2's R-tree makes this
  cheap; do **not** build an index here.

## Scope guardrails (do NOT do in L1i)

- Marquee drag only. No changes to click selection, overlap cycling, move drag, or the hit stack.
- No spatial index (L2). No changes to `LayoutHitTest` or `LayoutGeometry`.
- No new selection modes (lasso, layer-filtered marquee, select-by-type).
- Do not change the enclose/crossing semantics themselves — only make them visible.
- Don't touch `src/Core`, `src/Engine`, `RfCore`, the schematic or the symbol editor.

## Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **Live highlight** — during a marquee drag, a shape entering the rectangle highlights before release, and
   one leaving it un-highlights.
3. **Preview equals outcome (R-L1i-1)** — for a set of drags across all three modifier states, the preview
   list at the moment of release is **identical** to the committed selection. Assert against the same
   function, not a reimplementation.
4. **Modifiers preview correctly (R-L1i-3)** — plain drag previews the hits alone; Shift previews
   base ∪ hits; Ctrl previews base XOR hits, so dragging over an already-selected shape visibly
   **un-highlights** it.
5. **Base selection is not corrupted (R-L1i-2)** — a long drag with many pointer moves under Shift produces
   the same result as a single move to the same endpoint. This fails if the preview writes into
   `_selectedIndices`.
6. **Direction flip mid-drag** — press, drag left (crossing, dashed), then drag right past the press point
   (enclose, solid): the highlight set and the rectangle style both update live and the committed result
   matches the final direction.
7. **Escape** mid-marquee leaves the selection exactly as it was before the press, and clears the preview.
8. **Hidden and non-selectable layers are never previewed**, matching the commit filter.
9. **No recompute when the rectangle is unchanged** — assert the compute function is not called for a pointer
   move that does not move the rectangle by a device pixel.
10. **Screen-pixel coverage** — drive at least one full marquee gesture from **screen** coordinates through
    the canvas conversion, at a realistic default viewport (the standing rule from the L1 fix round: world-
    coordinate tests cannot catch screen→world bugs).

## On completion

1. Add a "Phase L1i — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`. Call out: **the single
   `ComputeMarqueeSelection` shared by preview and commit** and why that matters, that **the preview reflects
   the modifiers** so Ctrl-drag visibly un-highlights, that **the preview never writes to `_selectedIndices`**,
   and the **solid = enclose / dashed = crossing** cue.
2. Report back.
