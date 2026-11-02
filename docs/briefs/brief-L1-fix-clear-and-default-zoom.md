# Sonnet Brief — L1 fix: unclipped `SKCanvas.Clear` and the nonsense default zoom

Two independent root causes, both found by reading the code rather than by rendering. Neither is a
compositing mystery and neither needs a screenshot to confirm. **Read this whole brief before changing
anything** — the second bug explains why the first "fix round" appeared to do nothing.

---

## Bug 1 — the toolbar (and both rulers, and the metadata bar) are wiped by the canvas

### Root cause

`src/Ui/Renderers/LayoutRenderer.cs` line 100:

```csharp
canvas.Clear(theme.Background);
```

**Avalonia hands an `ICustomDrawOperation` the whole render-surface canvas.** `ICustomDrawOperation.Bounds`
is used for invalidation and hit-testing — it does **not** clip Skia. `SKCanvas.Clear` fills the entire
current clip region, so with no clip in force this wipes the whole window, including every sibling already
painted.

`LayoutEditorView.axaml` paints in declaration order: `ToolbarBorder` (Dock=Top) → metadata `Border`
(Dock=Bottom) → `HRuler` → `VRuler` → `LayoutCanvas` → placeholder. The canvas is next to last, so its
`Clear` destroys all four controls painted before it.

### Why the symbol editor is fine, and why the harness was right

`SymbolEditorRenderer.Draw` calls `canvas.Clear` too — line 32. It gets away with it for exactly one reason:

```
src/Ui/Views/Content/SymbolEditorView.axaml:534
    ClipToBounds="True"
```

`SymbolEditorCanvas` sets `ClipToBounds="True"`; `LayoutCanvas` (LayoutEditorView.axaml:247) does not. With
`ClipToBounds`, Avalonia pushes a clip before rendering the control, which constrains `Clear` to the control's
own rectangle. Without it, `Clear` is unbounded. That single attribute is the entire difference between the
two editors — not the `WrapPanel`↔`ScrollViewer` swap, and not a Grid-vs-DockPanel choice.

**The headless harness was correct and should not have been discarded.** It placed `SymbolEditorCanvas` next
to a plain toolbar *without* `ClipToBounds="True"` and reproduced the symptom every time — because in that
configuration the known-good canvas has the same real bug. The harness was reporting a true positive; the
conclusion "the harness has a defect" threw away the one piece of evidence that was accurate. Please restore
the harness rather than deleting it, and re-read `src/Ui/CLAUDE.md`'s note about it.

### Why it flickers on hover exactly as reported

Hovering a toolbar button invalidates only that button's small rect. The canvas does not intersect it, so the
canvas is not re-rendered and the button paints normally — the toolbar "appears". Any pointer move over the
canvas calls `InvalidateVisual()` (`LayoutCanvas.OnPointerMoved`), forcing a full repaint in which the
unclipped `Clear` wipes the toolbar again. "Invisible until hover, invisible again on exit" is the exact
signature of this bug.

### Fix

**Do both.** The renderer fix is the real one, because it does not depend on a caller remembering an
attribute; the AXAML fix is correct anyway and matches the symbol editor.

1. **`LayoutRenderer.Draw`** — replace the bare `Clear` with an explicit clip, and hold it for the whole
   render so no later drawing can escape the control's box either:

   ```csharp
   canvas.Save();
   try
   {
       canvas.ClipRect(SKRect.Create(0, 0, (float)vp.Width, (float)vp.Height));
       canvas.DrawRect(SKRect.Create(0, 0, (float)vp.Width, (float)vp.Height),
                       new SKPaint { Color = theme.Background, Style = SKPaintStyle.Fill });
       // ... grid, layers, ghost — the existing body ...
   }
   finally { canvas.Restore(); }
   ```
   Keep the existing inner `Save`/`Concat(matrix)`/`Restore` nested inside this one. Cache the background
   paint rather than allocating per frame.

2. **`LayoutRulerRenderer.Draw`** — line 24 has the identical unclipped `Clear`. Same fix. Both rulers are
   painted before the canvas, so they were wiping the toolbar too, and were then wiped by the canvas.

3. **`LayoutEditorView.axaml`** — add `ClipToBounds="True"` to `LayoutCanvas` (line 247) and to both
   `LayoutRulerControl`s, matching `SymbolEditorView.axaml:534`.

4. **`SymbolEditorRenderer` / `SchematicRenderer`** — leave them alone in this brief, but note in the
   completion write-up that they carry the same latent unclipped `Clear` and are protected only by a caller
   attribute. That is worth its own small hardening pass later; do not scope-creep into it now.

### Regression test — no Avalonia required

This is fully testable headlessly, and the harness confusion is precisely why it should be:

> Create an `SKSurface` **larger** than the viewport (e.g. 400×300 surface, 200×150 viewport). Fill the whole
> surface with a sentinel color. Call `LayoutRenderer.Draw` with `vp.Width = 200`, `vp.Height = 150`. Assert
> every pixel **outside** the 200×150 rect is still the sentinel color.

Add the same test for `LayoutRulerRenderer`. These two tests pin the contract directly — "a layout renderer
never writes outside its viewport rect" — with no compositor, no window, and nothing to distrust.

---

## Bug 2 — the default zoom makes the first shape impossible to draw

Not a placeholder bug. The `IsEmpty` / `PropertyChanged` wiring added in the last round is correct and should
stay; it never fired because **no shape was ever created**.

### Root cause

`LayoutViewport.Default(width, height, zoom = 1.0)` — and in this codebase **zoom is device pixels per DBU**
(confirmed by `LayoutCanvas.Zoom1To1`, which computes `1.0 / dbuPerUnit`).

At the default `DbuPerMicron = 1000`, 1 DBU = 1 nm, so `zoom = 1.0` means **1 screen pixel per nanometre**.
A new empty layout therefore shows a window roughly **1.5 µm wide**.

Now bring in the snap grid. The PCB starter technology's `DefaultSnapDbu` is 1 mil = **25,400 DBU**. The
entire visible canvas is ~1,500 DBU across — about 6% of one snap step. Every pointer position in the whole
window snaps to the same grid coordinate, so in `BuildTwoPointShape`:

```csharp
if (x1 == x2 || y1 == y2) return null;
```

…every drag returns `null`. No shape, ever. `IsEmpty` stays true, the placeholder never hides, and the
symptom presents as "drawing doesn't appear / stuck on the placeholder."

On the MMIC starter tech (5 DBU snap) a shape *is* created, but it is a few hundred nanometres across — which
is why this looked intermittent rather than systematic.

**Why every test passed**: the headless tests call `OnPointerPressed(wx, wy, …)` with world coordinates
directly, bypassing `ScreenToWorld` and the default viewport entirely. The bug lives exactly in the gap those
tests skip over.

### Fix

1. **Make the default viewport physically meaningful.** `LayoutViewport.Default` must not hardcode
   `zoom = 1.0`. Give it the information to choose: pass the `LayoutView` (or its `SnapDbu` and
   `DbuPerMicron`) and frame a sensible physical span — **suggest ~200 snap steps across the viewport width**,
   clamped into `[MinZoom, MaxZoom]`, with a fallback span when `SnapDbu <= 0`. That yields ~20 mm across for
   the PCB tech and ~1 µm for MMIC at a 5 nm snap — both immediately drawable, and the grid is visible at
   §5's 8-pixel threshold rather than sub-pixel.
   Audit every `Default(...)` call site; the empty-layout path in `ZoomToFitInternal` is the important one.

2. **Never let a real drag silently produce nothing.** Keep rejecting a genuinely zero-length drag, but if the
   **unsnapped** drag is non-degenerate while the **snapped** result collapses, expand the degenerate axis to
   one snap step instead of returning `null`. A deliberate small drag should make a minimum-size shape; the
   one thing it must not do is nothing at all, with no feedback. Apply to `Rect`, `RoundedRect` and `Circle`.

3. **Remove the auto-fit-on-first-shape hack** in `LayoutCanvas.OnModelChanged`. It was added to compensate
   for the broken default zoom, and once the default is sane it actively hurts: every first shape yanks the
   viewport to frame that one shape. `OnLayoutUpdated`'s initial fit for a layout that *loads* with content is
   correct and stays; delete the `Model.Changed` branch and leave the plain `InvalidateVisual()`.

### Regression tests

- **Default viewport is drawable** — for **both** starter technologies: build a default viewport at a
  realistic canvas size (say 1200×800), convert two screen points ~300 px apart through `ScreenToWorld`, snap
  them as the tool does, and assert the result is **non-degenerate**. This is the test that would have caught
  the bug.
- **End-to-end through screen coordinates** — drive the tool state machine with *screen* points via the
  canvas's conversion (not world coordinates) on a PCB-tech layout at the default viewport, and assert a
  `RectShape` lands in `Model.Shapes` and `IsEmpty` goes false.
- **Minimum-size fallback** — a 3-px drag at a zoom where that is under one snap step yields a one-snap-step
  shape, not `null`.
- **Grid is visible at the default viewport** for both starter techs (pitch ≥ the 8-px threshold).

---

## Guardrails

- Fix **only** these two causes plus their tests. No refactoring of the tool state machine, no changes to
  `LayoutHitTest`/`LayoutFlattener` (L1c), no touching the schematic or symbol renderers beyond the note in
  Bug 1 §Fix item 4.
- **Do not delete the headless harness.** It was right.
- Correct `src/Ui/CLAUDE.md`: remove any claim that the `WrapPanel` swap or a Grid-vs-DockPanel choice was
  involved, and remove the claim that the harness is defective.

## On completion

Add an "L1 fix — COMPLETE" entry at the top of `src/Ui/CLAUDE.md` recording, in one paragraph each:
**(1)** that `ICustomDrawOperation.Bounds` does not clip Skia, that an unclipped `SKCanvas.Clear` wipes the
whole surface, that `ClipToBounds="True"` is what was silently protecting the symbol editor, and that layout
renderers now clip themselves; **(2)** that **zoom is device-pixels-per-DBU**, so a default of 1.0 meant 1 px
per nanometre and made the PCB snap grid larger than the entire viewport — with the note that world-coordinate
unit tests structurally cannot catch screen→world bugs, which is why the two new tests go through
`ScreenToWorld`.
