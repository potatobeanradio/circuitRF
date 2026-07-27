# Sonnet Brief — Phase L1j: richer, live Properties Inspector

**Design:** `docs/design/layout-view.md` §6.3 (editing), §1 R6 (live physical readout, unit-suffixed entry),
§3.1/§3.1a (primitives and holes). **Consumes** L1a–L1i.

**Sequencing warning.** This brief restructures `LayoutShapePropertiesViewModel`, and **L1h also edits that
file** (adding `FlattenTolDbu` to `CircleShape`/`RoundedRectShape` and widening the panel predicate). **Land
L1h first**, or expect a merge in that one file. L1i does not overlap.

Three additions and one rule:

1. Width and height for `Rect` and `RoundedRect`.
2. An editable, virtualized vertex list for `Polygon` / `Curve` / `Path`, last in the panel.
3. Consistent text-entry validation across every field.
4. **Everything updates live**, including during a drag.

---

## 1. Liveness — the important one, and it is nearly free

### 1.1 What is already right

`LayoutShapePropertiesViewModel.SetContext` already subscribes to **both** the things that matter:

```csharp
_vm.PropertyChanged += OnVmPropertyChanged;   // refreshes on Overlay changes
_vm.Model.Changed   += OnModelChanged;        // refreshes on committed edits
```

and `RebuildOverlay()` runs on **every pointer move** of a drag. So the panel is *already being told* to
refresh live. The trigger is not the problem.

### 1.2 What is wrong

`RefreshFromVm` reads the **committed model**:

```csharp
_selected = _vm.SelectedIndices
    .Where(i => i >= 0 && i < _vm.Model.Shapes.Count)
    .Select(i => _vm.Model.Shapes[i])     // ← committed geometry
    .ToList();
```

But a drag deliberately does **not** mutate the model — one gesture is one undo entry, pushed on release.
The pending geometry lives in `Overlay.DragOverrides`, an `IReadOnlyDictionary<int, LayoutShape>` populated
for both L1c's move drag and L1d's handle drag. The panel refreshes at the right moments and then re-reads
values that have not changed yet.

### 1.3 Fix

**R-L1j-1. The inspector reads *effective* geometry: the drag override for an index when one exists,
otherwise the committed shape.**

```csharp
private LayoutShape Effective(int i) =>
    _vm!.Overlay.DragOverrides.TryGetValue(i, out var preview) ? preview : _vm.Model.Shapes[i];
```

**This one change makes every existing field live at once** — corner radius while dragging a rounded rect's
radius handle, circle radius, path width, and the new width/height below — rather than special-casing the new
ones. Expose the accessor on `LayoutEditorViewModel` (e.g. `EffectiveShapeAt(int)` /
`EffectiveSelectedShapes()`) so the renderer, the inspector and the status readouts all read the same source
and cannot disagree mid-drag. That is the same one-source principle L1i applies to marquee preview-vs-commit.

**R-L1j-2. While a drag is in progress the inspector is read-only.** `ApplyToEach` mutates the objects in
`_selected` directly; during a drag those are throwaway preview clones, so a commit would write into geometry
that is about to be discarded. Fields display live values and are disabled for editing until the drag
commits. This is not a limitation anyone will notice — you cannot type in a text box while dragging a handle.

**R-L1j-3. A refresh never clobbers the field that currently has focus.** The panel now refreshes far more
often, so a live update landing mid-typing would reset the user's text. The view tells the VM which field has
focus (`GotFocus`/`LostFocus` in code-behind, per the UI firewall), and `RefreshFromVm` skips writing that one
property. `_isRefreshing` already guards the opposite direction; this is its mirror.

---

## 2. Width and height for `Rect` and `RoundedRect`

`RectShape` currently has **no** type-specific section at all — the panel shows `ShowRoundedRect`,
`ShowCircle`, `ShowPath`, `ShowLabel`, and nothing for `Rect`. So this fills a real hole.

- `ShowRectSize = _selected.All(s => s is RectShape or RoundedRectShape)` — both types, since a rounded rect
  is a rect with a corner radius. Place **above** the existing corner-radius field.
- **Width** = `X2 - X1`, **Height** = `Y2 - Y1`, formatted through `LayoutUnits.Format` in the display unit,
  blank when a multi-selection disagrees.

**R-L1j-4. Editing width or height keeps the minimum corner `(X1, Y1)` fixed and moves the far edge.**
Predictable, matches how the shape was drawn, and needs no anchor UI. State it in the field tooltip so it is
not a surprise.

**Also add position** — `X` and `Y` of the minimum corner, editable. Not in the original request, but the
panel is otherwise position-blind, and a size field without a position field means the user can resize a
shape but not place it numerically. Strike it if unwanted; it is three lines beside the size work.

**RoundedRect guard**: after a width or height change, clamp `CornerRadius` to half the shorter side and
report if it was clamped. Otherwise editing width down produces a geometrically invalid shape.

Zero or negative sizes are rejected with a reason (§4), not silently clamped.

---

## 3. The vertex list

Shown when **exactly one** `PolygonShape`, `CurveShape` or `PathShape` is selected. (`CurveShape` is included
because it is what a `PolygonShape` becomes under L1d's promotion rule — omitting it would make the list
vanish the moment a user curves an edge.) Hidden for multi-selections and every other type.

**Position: last in the panel**, as requested.

### 3.1 Rows

| Column | Editable | Notes |
|---|---|---|
| # | no | index within its ring |
| X | **yes** | `LayoutUnits` field |
| Y | **yes** | `LayoutUnits` field |
| Edge | no | `Line` / `Arc` / `Cubic` for `Curve` and `Path` — the *outgoing* edge. Read-only; conversion stays on L1d's edge context menu. |

**Holes (§3.1a).** A polygon may carry inner rings. Group the list with ring headers — **Outer (12)**,
**Hole 1 (8)** — with the outer ring first. Hole vertices are editable like any other. A flat list that
silently showed only the outer ring would misreport the shape.

Committing a vertex edit is one `ReplaceShapeCommand` (L1d's single geometry-edit command), so it undoes as
one entry and the promotion rule keeps working.

### 3.2 Virtualization — two traps, both silent

**R-L1j-5. The vertex list must not sit inside the inspector's outer `ScrollViewer`.** A virtualizing panel
given unbounded height measures every item and realizes all of them — virtualization degrades to nothing,
with no error and no visible symptom until someone selects a 20,000-vertex imported polygon and the UI
freezes. Put the list in a **bounded** region: a `Grid` row sized `*` (or a `MaxHeight`) with its **own**
`ScrollViewer`, so the fields above scroll independently of the list.

**R-L1j-6. Row view-models are materialized lazily, not built up-front.** Avalonia virtualizes *containers*,
not *items* — an `ObservableCollection<VertexRowViewModel>` still allocates one VM per vertex on every
selection change and every refresh. Back the list with an index-addressed
`IReadOnlyList<VertexRowViewModel>` that constructs a row on first access and caches only realized rows,
reading X/Y through to the effective shape (R-L1j-1). With container virtualization on top, a 20,000-vertex
polygon materializes roughly the ~30 rows actually on screen.

**Refresh must not rebuild the collection.** During a vertex drag, `RebuildOverlay` fires per pointer move;
replacing the items collection each time would thrash the panel and lose scroll position and focus. Instead
raise `PropertyChanged` on the **realized** rows only, so the dragged vertex's X/Y update live and nothing
else moves.

---

## 4. Text-entry validation

**Reuse the codebase's existing validation idiom** (the parameter editor and analysis editors already have
one) rather than inventing a second. Apply it uniformly to every field in the panel, old and new.

- **Parse** every dimension through `LayoutUnits.TryParse`: `2.9mm`, `115 mil`, `50u`, and a bare number read
  as the current display unit.
- **Commit** on Enter or focus-loss. **Escape reverts** to the last good value.
- **Invalid input** shows a visible invalid state and **keeps the user's text** so it can be corrected —
  it does not commit, does not throw, and is not silently discarded. Reverting on focus-loss while still
  invalid is correct; reverting *as you type* is not.
- **Out-of-range** gets a specific reason, not a generic rejection: *"Width must be greater than 0"*,
  *"Corner radius cannot exceed half the shorter side"*.
- **Multi-selection** shows blank where values differ; typing applies to all as one undo entry, which is
  already `ApplyToEach`'s behaviour.
- **Display** carries the unit suffix when unfocused.

---

## Scope guardrails (do NOT do in L1j)

- No new geometry operations — this is display and numeric editing of existing fields only.
- No edge conversion, no insert/remove vertex from the list (those stay on L1d's canvas context menu). The
  Edge column is read-only.
- No changes to `LayoutHitTest`, `LayoutFlattener`, or the renderer beyond exposing the effective-shape
  accessor.
- No spatial index, caching or LOD (L2). No instances (L3).
- Don't touch `src/Core`, `src/Engine`, `RfCore`, the schematic or the symbol editor.

## Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **Live during a handle drag (R-L1j-1)** — the headline requirement. Drag a `Rect` corner handle and
   assert the panel's Width and Height update on **every** pointer move, before release. Assert the same for
   a `Circle` radius drag and a `RoundedRect` corner-radius drag, which get liveness for free from the same
   change.
3. **Live during a move drag** — dragging a whole shape updates the position fields continuously.
4. **Live on commit paths** — undo, redo, a boolean op and a paste all refresh the panel.
5. **Read-only while dragging (R-L1j-2)** — commits are suppressed mid-drag; assert the model is unchanged
   if a commit is attempted, and that fields re-enable on release.
6. **Focus is never clobbered (R-L1j-3)** — with the caret in the Width field, trigger a refresh from another
   source and assert the in-progress text survives.
7. **Width/height editing (R-L1j-4)** — setting Width to `2.9mm` on a rect keeps `X1` fixed and moves `X2`;
   Height likewise. On a `RoundedRect`, shrinking the width below twice the corner radius clamps the radius
   and reports it.
8. **Vertex list visibility** — shown for exactly one `Polygon` / `Curve` / `Path`; hidden for a
   multi-selection, a `Rect`, a `Circle` and a `Label`.
9. **Vertex editing** — editing a row's X commits one `ReplaceShapeCommand`; undo restores the shape at its
   original index; the list reflects the change.
10. **Holes are listed** — a polygon with one hole shows both ring groups with correct counts, and editing a
    hole vertex works.
11. **Virtualization holds (R-L1j-5, R-L1j-6)** — select a 20,000-vertex polygon and assert the number of
    materialized row view-models stays in the tens, not the thousands. Assert the panel opens without a
    perceptible stall. This is the test that catches the unbounded-height mistake, which is otherwise
    invisible.
12. **Drag does not rebuild the list** — during a vertex drag, assert the items collection instance is
    unchanged and scroll position is preserved while the dragged row's values update.
13. **Validation** — every dimension field accepts `2.9mm` / `115 mil` / `50u` / a bare number; garbage shows
    the invalid state without committing or throwing; Escape reverts; a differing multi-selection shows blank
    and applies to all on commit as one undo entry.

## On completion

1. Add a "Phase L1j — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`. Call out explicitly:
   **R-L1j-1 — the inspector reads effective (drag-override-aware) geometry, which is what makes every field
   live and why the refresh plumbing needed no change**; **R-L1j-2** (read-only mid-drag, because `_selected`
   holds preview clones); **R-L1j-3** (never clobber the focused field); **R-L1j-4** (min corner is the anchor
   for size edits); and **R-L1j-5/6 — the two silent virtualization traps**, since both fail invisibly on
   small shapes and only bite on large imported geometry. Plus the test file names.
2. Report back.
