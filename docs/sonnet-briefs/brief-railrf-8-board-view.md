# Brief 8 — the board view: the layout editor's canvas, and railRF's overlays on it

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail8-n`
**Area:** `src/Ui/RailRf/`, `src/Render/` · **Depends on:** 5, 7 · **Blocks:** 9
**Design note:** [`railrf.md`](../design/railrf.md) §11.6, §2.4, §2.9

---

## 0. What this brief delivers

`RailLayoutOverlay : ILayoutCanvasOverlay`, three map overlays, and **no navigation code at all.**

| Piece | File |
|---|---|
| `RailLayoutOverlay` | `src/Ui/RailRf/RailLayoutOverlay.cs` |
| `RailMapScene` + `RailMapRenderer` | `src/Render/Renderers/RailMap*.cs` — the drawing, below the firewall, so the window and a headless report draw alike |
| `RailMapTheme` | `src/Render/Renderers/RailMapTheme.cs` — `FromTheme(ColorTheme, ColorVariant)` + `Fallback`, on `LayoutRenderTheme`'s exact pattern |

Three overlays, switched by §11.3's tab strip (brief 7): **drop**, **|Z|** (brief 15 fills it), and
**class** (brief 4's trace-versus-mesh classification). The `copper` tab is no overlay at all — the bare
artwork.

---

## 1. `R-rail8-1` — the board view IS `LayoutCanvas`, and that is how the gestures are got

The owner instruction, §11.6:

> **the board in the centre of that window pans and zooms exactly as the layout editor does, by mouse and
> by keyboard.** Not "similarly" — the same gestures, the same step sizes, the same anchoring. Someone who
> has learned one of these windows has learned the other, and **a near-miss is worse than an absence
> because it is discovered by being wrong.**

The way to get that is not to re-implement it. railRF's board view is `src/Ui/Controls/LayoutCanvas.cs`
itself, and railRF's drawing arrives through `ILayoutCanvasOverlay` — the seam wBond already uses to draw
wires on that same control.

**Read `src/Ui/Controls/ILayoutCanvasOverlay.cs` in full before writing this.** Its own doc comment already
records why it is a seam and not a transparent sibling control (*"an unhandled event bubbles to ANCESTORS,
never sideways to a sibling underneath"*), and four of §11.6's five traps are written on its members.

What railRF adds is three overlays. What it adds to navigation is **nothing**:

| Gesture | Where it already lives |
|---|---|
| Wheel / two-finger scroll → zoom anchored on the world point under the cursor | `LayoutViewport.WithZoomAnchoredAt` |
| Middle-button drag, or Space + left drag → pan | `LayoutCanvas`'s pan latch |
| `F` → Zoom to Fit | `LayoutCanvas.ZoomToFit` over `LayoutViewport.ZoomToFit` |
| `Z` then a left drag → Zoom to Marquee, **one shot**, self-disarming | `ArmZoomBox` / `ZoomBoxMarquee` |
| Ctrl/⌘ `+` / `−` → one step about the centre, **main row and numeric keypad** | `ZoomIn` / `ZoomOut` |
| Arrow keys → pan 40 device pixels, ×5 with Shift | `CanvasArrowPan.ScreenStep` |
| `Escape` → disarm the magnifier, drop a half-drawn box | `DisarmZoomBox` |

**And the same four toolbar buttons**, in the same order and with the same tooltips the layout editor's
carry: Zoom to Fit, the Zoom Box magnifier (**which lights while armed**), Zoom Out, Zoom 1:1. The keyboard
is not the only route to any of these, and the buttons are where a user who has never read a tooltip finds
them.

---

## 2. The five traps, each already paid for once

### `R-rail8-2` — the overlay never touches the layout model, and repaints through `InvalidateOverlay`

From the seam's own contract. Nothing railRF draws enters the `.clay`, and a map repaint goes through
`LayoutCanvas.InvalidateOverlay` rather than invalidating `LayoutPathCache` — **which on a real board is
the difference between a repaint and a rebuild of half a million shapes.**

### `R-rail8-3` — coordinates are the canvas's own DATABASE UNITS, converted at this boundary and nowhere else

Also from the seam's contract, and `WBondSnap` is the worked example of an overlay that stores in another
unit and converts here. The nm↔DBU bridge **fails silently at the 1000 DBU/µm default** — that is a
recorded wBond finding — so any test of the conversion needs geometry off **both** axes and off a round
number.

### `R-rail8-4` — every keyboard gesture is gated on nothing else owning the keyboard, and the gate is WIDER here

§11.6 trap 1. `F` is an ordinary letter in a net name and `Z` is one in a part number; the layout canvas
already suppresses both while a label is being typed. But:

> **railRF's window has far more text fields than the layout editor does** — its whole left column is
> editable rows. The gate is therefore **wider here, not narrower**: no navigation key fires while focus is
> in any text-entry control.

This is the one place railRF must add to the canvas's own behaviour rather than inherit it. Do it by
widening the gate, not by handling keys in the overlay.

### `R-rail8-5` — the arrow keys are the case the instruction turns on

§11.6 trap 2, and it is the trap with a reason most people would not guess:

> They pan only when nothing is selected and nothing is being typed, because in every one of these editors
> an arrow key already nudges a selection and that gesture is the older one. In this window the competing
> claim is stronger still — **an arrow key inside a value field belongs to the caret, always** — so the pan
> is what an arrow key means on the board panel and nowhere else.
>
> It matters because **a trackpad has no middle button** and the two-finger scroll is spent on zoom: **on a
> laptop the arrow keys are the only pan there is.**

### `R-rail8-6` — every held-key latch is dropped on `LostFocus`

§11.6 trap 3, and the seam has an `OnFocusLost()` member for exactly this:

> Hold Space to pan, then click a button on the bottom bar; the key-up goes to the button and the canvas
> never sees it, so the latch stays set and from that moment **every left-drag is a pan with nothing on
> screen explaining it.** That was diagnosed once already — and in a window whose board panel is ringed by
> controls it is *more* likely here than it was there, not less.

The recorded symptom, from the wBond diagnosis, is *"marquee select stopped working in the layout view"* —
which named the wrong subsystem entirely. Implement `OnFocusLost` and drop every latch, whether or not its
release was ever seen.

### `R-rail8-7` — Zoom to Fit must include the overlay's own extent

§11.6 trap 4. `ContentBounds()` exists on the seam for exactly this, and its doc comment records the wBond
case: a document on an empty scratch layout fitted to an **empty** extent and landed at an arbitrary
default, with every wire off screen.

> A drop map is co-extensive with the copper, so this looks harmless — **until a legend, a source marker or
> a flagged-via callout sits outside the copper's own bbox and Zoom to Fit cuts it off.**

So `ContentBounds()` returns the union of the map, the legend, the source and load markers and the via
callouts — not the copper's bbox, which the canvas already has.

### `R-rail8-8` — the viewport is per document and it persists

A railRF document reopens where it was left, as a layout document does, through the mechanism already
tested.

---

## 3. The overlays

### `R-rail8-9` — the drop map is the headline, and it is a picture

§2.4: *"where the colour changes fastest is where the drop is, and a designer reads that in a second."*

- The artwork itself, **coloured by node voltage on a cold-to-hot scale**, from the source to the load.
- **A cursor reads out the absolute voltage anywhere on the net** — which is why brief 5 `R-rail5-2`
  requires the field to be interpolable between cells rather than sampled only at them.
- A legend carrying the scale, **inside the picture** (brief 9 `R-rail9-2` depends on this).
- Source and load markers at their resolved pads, and **flagged via transitions** (brief 6) called out.

### `R-rail8-10` — the map is OPAQUE PAINT, and this is a design constraint decided here

§11.7 point 2, and it is decided in the design note *"rather than discovered in an export"*:

> For every other copy the background is a *backdrop*, so honouring transparency is a matter of not
> painting it; **the stackup copy is the counter-example, and it is instructive** — its renderer uses the
> background colour as **paint**, to cut a drill hole, and simply not painting the bore did not make it
> transparent, it showed the dielectric behind it. A hole had to be cut in what was behind.
>
> **A railRF map must not acquire that shape**: the shading is opaque paint laid over the copper and the
> legend carries its own scale, so **nothing in the picture depends on the page's background being any
> particular colour.**

Write that as a rule in `RailMapRenderer`'s own comment. It is the kind of constraint that is free to
honour while the renderer is being written and expensive to retrofit after a background-dependent effect
has been used once.

### `R-rail8-11` — the classification overlay is a REQUIREMENT, not a diagnostic

§11.3, and §2.9 rule 2. It draws which copper railRF treated as a trace and which it meshed, **a region can
be forced either way from here**, and a forced region draws differently from an inferred one. The reason
string from brief 4 `R-rail4-3` is the hover readout.

§9: *"a wide supply polygon treated as a trace is optimistic, and the number looks entirely ordinary.
Drawing the classification … is required, not optional."*

### `R-rail8-12` — the tabs switch the OVERLAY, never the geometry

§11.3. One canvas, one `LayoutView`, four tab states. A tab that rebuilt the canvas would drop the viewport
and undo `R-rail8-8`.

---

## 4. `R-rail8-13` — the renderer lives in `src/Render`, below the firewall

So the window and a headless report draw with the same code (§5, and it is why RND-1 put that project below
the firewall). `RailMapScene` is a pure function of the result plus a viewport; `RailMapRenderer.Draw` paints
it and computes nothing. The **overlay** in `src/Ui` holds the pointer handling and calls into them.

Two silent fallbacks already fixed when `src/Render` was created, and both bite anything new added there:
fonts load through `Assembly.GetManifestResourceStream` and **not** Avalonia's `AssetLoader`, and a colour
theme resolves through `src/Render`'s own embedded `.ccolor` provider. A new `ColorRole` must be added to
the shipped `Default.ccolor` in `src/Render/Assets/Color/` **in the same change**, or it resolves to
nothing, silently.

---

## 5. Tests — `tests/Ui.Tests/RailRf/RailBoardViewTests.cs`

### The headline gate — §7, and it is a comparison rather than a checklist

> Every gesture in §11.6's table is driven on the railRF board view **and** on a layout editor view holding
> the same geometry, and **the two resulting viewports must be identical** — pan offsets, zoom, and the
> framing Zoom to Fit chose.
>
> **A list of features can be satisfied by an approximation of each one; asserting the same viewport
> cannot.**

One test per gesture, both views, viewport equality. That is the whole design of this gate and it is why it
is worth more than seven separate assertions about seven separate behaviours.

### The rest

- **`R-rail8-4`**: with focus in a left-column `InlineEditText`, `F`, `Z` and the arrows change **nothing** —
  the viewport is equal before and after, and the text field's caret moved.
- **`R-rail8-5`**: with focus on the board and nothing selected, an arrow key pans by `CanvasArrowPan.ScreenStep`;
  with something selected, it does not pan.
- **`R-rail8-6`**: press Space, move focus away, release Space elsewhere, then left-drag — **it is a marquee,
  not a pan.** This is the recorded bug, reproduced as a test.
- **`R-rail8-7`**: a layout whose overlay legend sits outside the copper bbox fits to the **union**. The
  negative: return the copper bbox from `ContentBounds()` and assert the legend is framed out — proving the
  gate has teeth.
- **`R-rail8-2`**: a map repaint does not invalidate `LayoutPathCache`, asserted on the cache's own counter.
  A path-cache rebuild counter that moves is the 500k-shape defect.
- **`R-rail8-3`**: a round trip through the overlay's coordinate conversion, on geometry **off both axes and
  off a round number**, at the 1000 DBU/µm default.
- **`R-rail8-10`**: the map renders identically on two different page backgrounds — nothing in it depends on
  the background being any particular colour.
- **`R-rail8-11`**: forcing a region changes its draw state and its `Forced` flag, and the override survives a
  document round trip.
- **`R-rail8-12`**: switching tabs leaves the viewport unchanged.
- **Determinism**: the same result rendered twice is byte-identical, which brief 9's clipboard gate and brief
  17's doc figures both depend on.

**No timing tests.** Assert counters and viewports.

---

## 6. Scope

- **No navigation code.** If this brief contains a pan, a zoom, a marquee or a fit implementation, it is
  wrong. `R-rail8-4`'s widened keyboard gate is the sole addition and it is a gate, not a gesture.
- **No clipboard.** Brief 9.
- **No |Z| map contents.** Brief 15 fills that overlay; brief 8 creates the tab and the empty overlay so the
  tab strip is complete and brief 15 is a fill rather than a re-layout.
- **No new canvas, no second control, no transparent sibling.** The seam's own doc comment explains why, at
  length.
- **No layout-model writes, ever.**

**On completion:** record findings in `src/Ui/RESOLVED.md`, and anything about `src/Render` in
`src/Render/RESOLVED.md`. Never in a CLAUDE.md.
