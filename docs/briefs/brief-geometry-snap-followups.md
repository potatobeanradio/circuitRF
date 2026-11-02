# Sonnet Brief — Geometry snap follow-ups: handle drags, self-exclusion, and the missing hover marker

Three reports against `brief-snap-distance-and-geometry-snap.md`. **All three trace to one file** and are
diagnosed to specific lines — do not re-derive the locations.

**Primary file:** `src/Ui/Layout/LayoutEditorViewModel.Snap.cs` — `UpdateSnapMarker` at **line 156**.

Gate command is plain `dotnet test`.

---

## 1. Handle drags never snap — the marker update bails out

**Line 160:**

```csharp
if (_handleDragKind != HandleDragKind.None || _scaleDragKind != ScaleDragKind.None)
{
    _currentSnapCandidate = null;
    return;
}
```

`UpdateSnapMarker` **returns early during any handle or scale drag**, so no candidate is ever found and
nothing can snap. That is exactly the owner's report: dragging a rect's edge or vertex does not snap to other
geometry.

**R-snpf-1. Geometry snap must govern the destination of a vertex or edge drag.** R-snp-10's own wording
already said so — *"during a drag, geometry snap governs the destination"* — it simply was not applied to the
handle path. Remove the blanket early-out and let the query run.

**R-snpf-2. What snaps differs by handle kind, and this is the part that will be got wrong:**

| Handle | What the snap constrains |
|---|---|
| **Vertex** | the vertex position itself — snap it straight to the candidate feature |
| **Edge** | the **perpendicular offset**. An edge drag moves the edge along one axis (L1c/L1d), so a candidate must be **projected onto that axis** — the edge lands where the feature projects, not at the feature |
| **Bulge** | **out of scope.** A bulge is a curvature control, not a position; do not snap it |
| **Scale** | **out of scope for now** — a scale drag moves many points at once and there is no single grab point to snap. Say so rather than half-implementing it |

Keep the early-out for the scale case; narrow it so vertex and edge fall through.

**R-snpf-3. Geometry snap overrides grid snap here too**, as everywhere else. The handle path currently snaps
to `SnapDbu`; when a geometry candidate is in range it wins, and grid snap applies only when none is.

## 2. The dragged shape attracts itself, at its old position

**Line 183–184:**

```csharp
// During an active snap-drag, exclude the shape being dragged — it must never attract itself.
int? exclude = _snapDragActive && !_snapDragOwnerIsInstance ? _snapDragOwnerIndex : null;
```

Two defects here, and they explain both halves of the symptom.

**R-snpf-4. Exclusion only applies when `_snapDragActive`.** A plain move-drag — started by clicking the shape
body rather than a snap marker — leaves `_snapDragActive == false`, so **nothing is excluded** and the dragged
shape's own corners, midpoints and centroid remain candidates. Exclude the dragged geometry on **every** drag
that moves geometry, regardless of how the drag began.

**R-snpf-5. `exclude` is a single `int?` and cannot express a multi-shape selection.** Dragging three shapes
excludes at most one. It must become a **set** of excluded shape indices — and instance indices too, since
`_snapDragOwnerIsInstance` shows instances are draggable and equally capable of attracting themselves.

**R-snpf-6. The "old position" symptom is the tell that candidates come from stored geometry.** `LayoutSnapQuery.FindCandidates`
is passed `Model`, so it reads committed shapes while the drag preview lives in `DragOverrides`. Exclusion
fixes the reported bug — but check whether **non-dragged** geometry is also being read staler than it should
be, and confirm the query sees the same geometry the renderer draws.

## 3. No marker appears on hover — the feature's central affordance is missing

The owner sees no glyph while merely moving the mouse over features. That is not cosmetic: **R-snp-8 — the
click is consumed by the marker rather than by hit-testing — is unreachable without it.** The marker is what
tells the user a click will grab that point, so §2.3's entire "grab point" role is currently inaccessible.

**R-snpf-7. `UpdateSnapMarker` must run on plain pointer-move, not only during a drag.** Inside this file the
only call site is line 115, a forced recompute after a toggle. Find the canvas pointer-move path and confirm it
reaches `UpdateSnapMarker` on **every** move while the mode is enabled and no drag is in progress. R-snp-16's
sub-pixel guard (line 174) already makes that cheap; it is designed for exactly this call rate.

**R-snpf-8. `_currentSnapCandidate` must be rendered in the hover state.** Check `LayoutRenderer.Snap.cs` —
if the marker is only drawn when a drag is active, the query could be running correctly and still show
nothing. Report which of the two it was: **not queried**, or **queried but not drawn**.

**R-snpf-9. Hover markers must appear over primitives, instances *and* cell contents**, per the owner. If
instances or nested cell geometry produce no candidates, that is a scope bug in `FindCandidates`, not a
rendering one — and it would also mean R-snp-13's cell-space transform path is not being exercised at all.

## 4. Guardrails

- Do not snap bulge handles or scale drags (R-snpf-2).
- Do not add a second exclusion mechanism — widen the existing parameter to a set (R-snpf-5).
- Do not remove R-snp-16's sub-pixel guard (line 174); hover depends on it staying cheap.
- Do not disable Alt-suppression (line 166) — R-snp-11 stands.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 5. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Hover marker (R-snpf-7/8/9)** — with snap on and no drag in progress, moving the cursor near a corner of a
   **primitive**, of an **instance**, and of geometry **inside a nested cell** shows the correct glyph in each
   case. Assert both that the query ran (`SnapQueryRunCount` increments) **and** that a marker was drawn.
3. **Vertex drag snaps (R-snpf-1/2)** — dragging a rect's corner onto another rect's corner lands it exactly
   on that corner, not on the grid.
4. **Edge drag snaps (R-snpf-2)** — dragging a rect's edge toward another shape's corner lands the edge on the
   corner's **projection onto the drag axis**; the edge stays straight and the perpendicular constraint holds.
5. **Bulge and scale unchanged** — a bulge drag and a scale drag behave exactly as before; assert no snap
   candidate is consulted.
6. **Override order (R-snpf-3)** — with a candidate in range the handle lands on the feature; with none it
   lands on the grid.
7. **Self-exclusion, any drag (R-snpf-4)** — a plain move-drag started from the shape **body** produces no
   candidates belonging to the dragged shape. This is the reported bug; assert it on the body-drag path
   specifically, since the marker-initiated path already worked.
8. **Multi-shape exclusion (R-snpf-5)** — dragging three selected shapes excludes all three; dragging a
   selected instance excludes that instance.
9. **No stale positions (R-snpf-6)** — mid-drag, no candidate sits at a dragged shape's pre-drag location.
10. **Cost unchanged** — `SnapQueryRunCount` per pointer move is still one at most, and the counters from
    R-snp-12/14 stay bounded now that hover runs continuously.

## 6. On completion

Record in `src/Ui/CLAUDE.md`: that **`UpdateSnapMarker`'s early-out at line 160 disabled snap for every handle
drag**, and that the vertex/edge/bulge/scale cases are deliberately different (R-snpf-2); that **exclusion was
gated on `_snapDragActive`**, so only marker-initiated drags excluded themselves, and that it is now a set
covering shapes and instances; and **which of R-snpf-8's two causes** the missing hover marker turned out to be
— not queried, or queried but not drawn.
