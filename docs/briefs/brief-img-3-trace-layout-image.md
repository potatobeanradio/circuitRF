# Brief IM-3 — Trace a layout picture

**Series:** `brief-img-0-overview.md` (D4, D5, D6, D7, D8, D14, D16) · **Tag:** `R-im3-<m>`
**Depends on:** IM-2
**Area:** new `src/Design/Layout/Recognition/Image/` (`ImageTrace.cs`, `ImageScale.cs`, `ImageLayerMap.cs`,
`ImageTracePresets.cs`), `src/Design/Layout/Recognition/LandPatternMatch.cs` (a scale search over it), design note §6

---

## 1. Goal

A layout picture becomes `LayoutShapes` on technology layers, at a stated scale, sitting exactly on the picture. This
is the whole of **Create Layout from Image** and the front half of a layout picture made into a schematic.

## 2. Requirements

**R-im3-1 — The entry point.** `ImageTrace.Trace(ImageTraceInput, RunControl?) → ImageTraceResult`, pure (posts
nothing, writes nothing), and `ImageTrace.Run(input, target, options)` which traces and then writes — the split
`ArtworkRecognition.Recognize`/`Run` already has. The input: the `ImageSource`, the technology, the scale (or its
absence), the layer map (or *Auto*), a scope rectangle in pixels (or the whole picture), and `ImageTraceOptions`.

**R-im3-2 — Scale (D6).** `ImageScale.Candidates(input)` returns every piece of scale evidence found, each with its
kind, its metres per pixel and its support:
- **Placement**: a placed `BitmapShape` gives `W/pixelWidth` (and checks `H/pixelHeight` — an aspect that disagrees by
  more than 1 % is reported, since the bitmap was stretched).
- **Two points**: picked pixel points plus a distance with an SI unit (`1.6 mm`, `62 mil`); a bare number is a refusal.
- **Parts**: two-pad pairs in the copper (or in a pad/paste cluster) searched against `SmtCaseTable` with
  `LandPatternMatch`'s own fit, over a log-spaced scale sweep refined around each peak; a candidate's support is the
  number of pairs it fits within 3 %. Reported with the cases it found ("4 × 0603, 2 × 0402").
- **Impedance**: a picked trace, a stated Z0 and the technology → W through `LineCalculator`, divided by the trace's
  measured pixel width.
- **Resolution metadata**: offered only.
`ImageScale.Choose` picks per D6: placement when the target is that layout; else a stated two-point or impedance
scale; else parts with support ≥ 3; else **no scale**, which is a refusal for a traced write and a *waiting* state in the
dialog. The chosen one and its evidence go into the provenance and the report.

**R-im3-3 — Resolution in the report.** One line: *1 px = 25.4 µm; a 300 µm trace is read to ± 4 %* — the half-pixel
edge accuracy (R-im1-7) as a width uncertainty at the narrowest traced line. No prose beyond that.

**R-im3-4 — Layers (D7).** `ImageLayerMap.Auto(clusters, technology)`: background = the cluster with the most border
contact (ties: the lightest); *Drill* = a cluster whose components are ≥ 80 % circles (R-im1-8) inside other copper; the
remaining clusters by descending area onto the technology's copper layers top first; a cluster whose components are
thin strokes with text-like rows is *Silkscreen*; a single remaining cluster → the top copper. Where two copper colours
overlap (a viewer drawing a semi-transparent layer over another), the overlap colour is its own cluster and maps to
**both** layers — detected as the colour within ΔE 12 of the alpha-mix of two others. The map is a list the user edits;
an edited map is never replaced by a re-run.

**R-im3-5 — Shapes.** Per mapped cluster: coverage contours (R-im1-7), holes kept, simplification and snapping as the
options say (defaults: 0.35 px, 3°, 45° snapping on), despeckle below the minimum feature (default 2 px²), converted
through the `PixelFrame` to DBU and placed so the traced layout lies on the picture: at the placed bitmap's rect, or
with the picture's top-left at the origin. Circles become circles; **drill circles become the technology's via shape on
its via layer** (the same shape the Gerber/Excellon import writes for a plated hole), a drill cluster with no via
layer in the technology is reported and written on no layer. *Board outline* becomes the outline layer's path.
Silkscreen is traced as filled shapes **and** kept as skeleton strokes for IM-4.

**R-im3-6 — The underlay (D5).** The result carries the source as a locked `BitmapShape` at 35 % opacity at exactly
the traced frame, on the technology's first documentation-purpose layer when it has one, else on the layer the most
copper was traced to (reported — hiding that layer hides the picture). Not written when the setting is off, or when the
target is the layout the picture already sits in (the bitmap is already there).

**R-im3-7 — Targets (D14).** *New cell* (with `CellCreate`, layout view, the technology reference, the kept picture
per R-im2-4); *into the layout the bitmap sits in* — returned as an edit the GUI applies as **one** undo step through
a `CompositeCommand` of the editor's `AddShapeCommand`s, never a file write; AS D4's replace rule for a cell whose layout carries this
command's `ImageSource` block. The CLI's `convert` target is IM-11's.

**R-im3-8 — Presets.** `ImageTracePresets` saves and reads a named layer map + options in
`<UserStateDirectory>/image-presets/` — matched by cluster colours (ΔE ≤ 8 each), so a preset offers itself when a
picture from the same source is dropped. Never stored in a workspace.

**R-im3-9 — The report** (`RecognitionReport`'s classes, new ones added): clusters found and how each was mapped,
shapes per layer, specks removed, edges snapped, circles fitted, the scale and its evidence, the resolution line, and
anything ignored. A trace is refused only for no scale (on a write), no technology, or **no shape on any copper layer**
— *the picture has no copper-coloured regions at the mapped colours*.

## 3. Not in this phase
Recognition of the traced copper as a circuit (IM-4); the dialog (IM-5); photographs (IM-13).

## 4. Gates (minimal tests, run only these classes)
In-memory pictures drawn by the test from a known layout (rectangles, a 45° jog, a circle, two layers with a
translucent overlap):
- `ImageTraceGeometryTests` — at a stated scale every edge lands within ½ px (in DBU) of the drawn one; the 45° jog is
  exact; the overlap colour lands on both layers; a drill circle becomes a via.
- `ImageScaleTests` — four 0603 pairs and two 0402 pairs drawn at 25 µm/px infer 25 µm/px ± 1 %; two pairs alone give
  no scale; a placed bitmap's rect wins when the target is its layout; a bare-number distance is refused.
- `ImageLayerMapTests` — background by border contact; a user-edited map survives a re-run; a preset is offered for a
  picture of the same colours.
- `ImageTraceTargetTests` — into-layout returns one edit and writes no file; the underlay sits on the traced frame.
