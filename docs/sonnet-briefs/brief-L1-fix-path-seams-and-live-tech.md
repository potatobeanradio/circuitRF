# Sonnet Brief — L1 fix: Path outline seams, and live technology editing

Two owner-reported items. Independent of each other and of L1d; do them together, they are both small.

---

## 1. `Path` renders internal seam lines at every bend

### Symptom

A `PathShape` with bends shows thin lines *inside* the trace at each vertex — visual artifacts that do not
correspond to any geometry.

### Root cause

`LayoutRenderer.BuildPathOutline` builds the trace outline with Skia's stroker:

```csharp
strokeForFill.GetFillPath(centerline, outline);
```

`GetFillPath` does **not** produce a single merged outline. Skia's stroker emits **one contour per segment**
plus a wedge per join, overlapping each other at every bend. That is fine for the **fill** — `DrawLayer`
issues one `canvas.DrawPath(shapePath, fillPaint)` with the default `Winding` fill type, so overlaps
composite exactly once — but `DrawLayer` then does:

```csharp
strokeBatch.AddPath(shapePath);
...
canvas.DrawPath(strokeBatch, strokePaint);
```

The hairline stroke traces **every contour edge in the path**, including the internal boundaries where the
segment quads and join wedges abut. Those internal boundaries are the seams the user is seeing. The bug is
therefore specific to `PathShape`, and specific to the stroke; single-contour shapes (`Rect`, `Circle`,
`RoundedRect`, non-self-intersecting `Polygon`/`Curve`) are unaffected.

### Fix

Resolve the overlapping contours into a simple path before returning, in `BuildPathOutline`:

```csharp
var outline = new SKPath();
strokeForFill.GetFillPath(centerline, outline);

// Skia's stroker emits one overlapping contour per segment plus a wedge per join. Filling that is
// fine (Winding composites once), but hairline-stroking it draws every internal contour boundary —
// the seam artifacts at bends. Simplify unions them into a single outer contour (plus any genuine
// holes), so the stroke traces only the real silhouette.
var simplified = new SKPath();
if (outline.Simplify(simplified)) { outline.Dispose(); return simplified; }
simplified.Dispose();
return outline;   // degenerate input — fall back rather than dropping the trace
```

Use the simplified path for **both** the fill and the stroke; do not keep two versions.

**Performance note for L2.** `Simplify` is an `SkPathOps` call and is meaningfully more expensive than path
construction. It is fine at L1 scale, where paths are rebuilt per frame anyway, but it must ride along with
the per-shape path cache L2 introduces — recomputing it every frame for thousands of traces would not meet
§5.1. Add a `// L2: cache with the shape path` comment at the call site.

### Do NOT "unify" this with Clipper2 later

L1e adds Clipper2 offsetting, which will also produce path outlines — for booleans, DRC (§9A) and Gerber
export (§8). It is tempting to then delete this code and use the Clipper2 outline everywhere. **Don't.**
Clipper2 works on *flattened* geometry, so a curved trace's outline would become polygonal, throwing away
the adaptive, zoom-correct curve tessellation §3.2 R9c specifies for display. Two outlines for two purposes
is correct here:

- **Display** — Skia stroker + `Simplify`, curves stay curves.
- **Geometry** (booleans, DRC, export) — Clipper2 offset on the flattened centerline, exact and integer.

Write that distinction into the method's doc comment so it survives.

### Tests

- **No internal seams**: render a 3-segment `PathShape` with a 90° bend into an off-screen `SKBitmap` and
  assert that a scanline crossing the interior of the bend contains **no** stroke-colored pixels between the
  two silhouette edges. Without the fix this fails; with it, it passes.
- **Silhouette preserved**: the simplified outline's bounds equal the unsimplified outline's bounds.
- **Degenerate input** (2 identical points, single segment, zero width) does not throw and does not drop the
  shape.

---

## 2. Technology edits should apply live, and closing dirty should prompt

### What the owner asked for

Editing a `.ctech` — colour, `Visible`, `Selectable`, anything — should be reflected in open layouts
**immediately**, without pressing Save. Save becomes purely "write to disk". Closing the editor with unsaved
changes must prompt.

### Design

The plumbing is almost entirely in place. L0c already delivers a `Technology` to open layouts via
`TechnologyCache.TechnologyChanged` → `WorkspaceViewModel` → `LayoutEditorViewModel.ApplyTechResolution`.
The only thing that is file-gated is *when* the cache's value changes. And `TechEditorViewModel` already
funnels **every** committed edit through exactly two methods — `CommitEdit` and `RestoreSnapshot` (undo/redo).
So this is a small, well-localised change.

**Add a live override to `TechnologyCache`:**

```csharp
public void SetLive(string absPath, Technology tech);  // installs an override, raises TechnologyChanged
public void ClearLive(string absPath);                 // drops it; next Get() reloads from disk, raises
```

`Get(absPath)` returns the live override when one exists, otherwise the existing file-backed cached value.
Consumers need no change at all — they already react to `TechnologyChanged`.

**Wire the editor:**

| Event | Action |
|---|---|
| `CommitEdit` (any committed edit) | `SetLive(FilePath, clone)` |
| `RestoreSnapshot` (undo / redo) | `SetLive(FilePath, clone)` |
| Save succeeds | write the file, then `ClearLive(FilePath)` — disk and working copy now agree, so no override is needed |
| Close, user chooses **Don't Save** | `ClearLive(FilePath)` — open layouts revert to the on-disk technology |
| Close, user chooses **Save** | save path above |
| Close, user chooses **Cancel** | nothing changes; the override stays |
| Document disposed for any other reason while dirty | `ClearLive(FilePath)` |

**R-fix-1. `SetLive` stores a deep clone, never `Working` itself.** Two reasons, both real: the editor keeps
mutating `Working` in place, so consumers would observe half-applied edits mid-frame; and `RestoreSnapshot`
**replaces** the `Working` reference entirely, so any consumer holding the old object would silently stop
updating after the first undo. Clone with the mechanism already used for snapshots —
`TechPersistence.Deserialize(TechPersistence.Serialize(Working))`. `CommitEdit` already serializes for the
undo entry, so this is one extra deserialize per committed edit on a small object.

**Coalesce, don't throttle.** Edits commit on focus-loss / Enter / dialog-OK / checkbox toggle, not per
keystroke, so the natural rate is already low. But a multi-selection apply can fire many `CommitEdit`s in one
gesture — post the `SetLive` through the dispatcher and collapse duplicates within a frame so the canvas
repaints once, not N times.

**The L0c invariant still holds, and matters more now.** A technology change must **never** re-seed an open
layout's `DisplayUnit` or `SnapDbu`. Those are the document's own state. With updates now streaming
continuously, a regression here would silently fight the user mid-edit. Keep the existing behaviour and keep
its test.

### Close prompt

Verify — and fix if absent — that `TechDocument` participates fully in `ConfirmCloseDockable` /
`PromptSaveBeforeClose` with **Save / Don't Save / Cancel**, exactly as `LayoutDocument` does, including on
application quit and on workspace switch. This was an L0d gate item; the owner's report suggests at least one
path (probably tab close) misses it.

### One interaction to guard

**"Reload Technology"** in the project tree calls `Invalidate` and reloads from disk. With a live override
present, that would silently discard unsaved editor changes. Make it prompt ("Discard unsaved changes to
*name*?") when a live override exists for that path, and only clear on confirmation.

### Tests

- **Live propagation**: with a layout open and its `.ctech` open, change a layer's colour in the editor —
  **without saving** — and assert the layout document received a new `Technology` whose `LayerDef` carries
  the new colour. Same for toggling `Visible` and `Selectable`.
- **Deep clone (R-fix-1)**: after `SetLive`, mutate `Working` directly and assert the value a consumer holds
  is unchanged; then undo and assert the consumer *does* receive the restored value.
- **Discard reverts**: edit without saving, close with "Don't Save", assert open layouts are back to the
  on-disk technology.
- **Save clears the override**: after Save, `Get` returns the file-backed value and it equals what was saved.
- **Units are never re-seeded**: with a layout whose `DisplayUnit`/`SnapDbu` differ from the technology's
  defaults, stream several live technology edits and assert both are untouched.
- **Close prompts**: a dirty `TechDocument` cannot be closed silently; Cancel keeps it open **and** keeps the
  live override in force.
- **Reload guard**: "Reload Technology" on a path with a live override prompts, and cancelling leaves the
  override intact.

---

## Guardrails

Fix only these two items. No changes to the tool state machine, hit-testing, the flattener, or anything in
L1d's scope. Do not add Clipper2 (L1e).

## On completion

Add an "L1 fix (path seams + live tech)" entry at the top of `src/Ui/CLAUDE.md` recording: **(1)** that
`GetFillPath` returns overlapping per-segment contours, that this is invisible when filling and visible when
hairline-stroking, that `Simplify` is the fix, and the **deliberate** display-vs-geometry outline split that
L1e must not collapse; **(2)** the live-override mechanism, the deep-clone rule and why `RestoreSnapshot`
makes it non-optional, and the discard/save/reload semantics.
