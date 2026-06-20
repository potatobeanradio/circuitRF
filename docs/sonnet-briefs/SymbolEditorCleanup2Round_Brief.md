# circuitRF — Symbol Editor Cleanup Round 2 (Claude Code / Sonnet)

Ten items: ports source-of-truth, live resize/drag rendering + live inspector, Sine resolution, remove
HalfWave, add ExponentialTaper, arrow-key nudging, graceful input handling, toolbar glyphs, and a Thick stroke
tier. Sub-gated; report+STOP between layers. Firewall: SymbolModel / SymbolGeometry / EditableSymbol stay
framework-free (role enums + plain numbers, no Skia/Avalonia). `dotnet build`/`dotnet test` green each layer.

## Design decision — Ports source of truth (read first; drives Layer 1)
The CELL is the source of truth for how many ports a component type has (from the engine reference /
ComponentTypeRegistry). The symbol only draws WHERE the ports go. So the editable Ports text field is wrong —
it lets the author set a number that can silently disagree with the cell.
- **Cell-bound symbol:** PortCount comes FROM the cell; display it READ-ONLY. Keep the "unmapped ports" warning
  (cell says N ports, author placed fewer pins → warn) — it's genuinely useful.
- **Orphan / scratch symbol:** no cell → no external port count. The effective port count is simply the number
  of pins drawn; there's nothing to be "unmapped" against, so NO unmapped warning (matches the user's
  observation).
Resolution: REMOVE the editable Ports field. Wire the cell's port count in (read-only) when the symbol is
opened from a cell; for orphans, port count = pin count and no warning.

## Read first (verified on disk)
- Views/Content/SymbolEditorView.axaml — toolbar (primitive buttons are TextBlocks), the Ports `TextBox`
  (`PortCountBox`), the Stroke Normal/Thin buttons.
- ViewModels/SymbolEditorViewModel.cs — `PortCount` (editable), `NextUnmappedPortIndex`, `ComputeUnmappedPorts`,
  the Tool enum (has HalfWave), draw builders, resize/drag state, OnKeyDown.
- Schematic/EditableSymbol.cs — `PortCount { get; set; }`; orphan vs cell-bound not currently distinguished.
- Schematic/SymbolModel.cs — primitive records incl. `SinePrimitive`(Cx,Cy,Amp,Cycles,Length,Axis),
  `HalfWavePrimitive`, `SymbolStrokeTier { Normal, Thin }`.
- SymbolGeometry.cs — BboxOf/HitTest/Translate/Rotate/StrokeTierOf per primitive.
- SymbolEditorRenderer.cs — primitive rendering (Sine point generation lives here); stroke-tier → px width.
- ViewModels/Properties/SymbolPrimitiveInspectorViewModel.cs (+ View) — per-primitive inspector; the live-update
  target (items 3).
- The schematic's IValueConverter that returns `AvaloniaProperty.UnsetValue` from ConvertBack for invalid input
  — find it (grep ConvertBack/UnsetValue) and REUSE it for item 8.

## LAYER 1 — Remove editable Ports field; cell is source of truth
1. **View:** remove the `PortCountBox` `TextBox` from SymbolEditorView.axaml. For a CELL-BOUND symbol, show the
   port count as a READ-ONLY TextBlock ("Ports: N"); for an orphan, either hide it or show "Ports: <pinCount>"
   (read-only).
2. **Editor open paths:** when a symbol is opened FROM A CELL, supply the cell's port count to the editor
   (read-only). The cell's port count comes from the component type / .ccell — resolve it where the editor is
   opened for a cell view (OpenOrActivateSymbol when the .csym lives under a cell, and the cell-context New
   Symbol). For orphan/scratch symbols, mark the symbol as having no external port count.
   - Add to EditableSymbol a notion of "external port count" vs orphan, e.g. `int? ExternalPortCount` (null =
     orphan). When non-null, that's the authority and the warning uses it; when null, port count = Pins.Count
     and no warning.
   - Keep `PortCount` for serialization compatibility if needed, but it is NO LONGER author-editable from the
     field. (Alpha: if dropping the persisted PortCount, bump the .csym format_version — reject-on-mismatch, no
     migration. If keeping it as a written-but-derived value, document that.)
3. **VM:** remove `OnPortCountChanged` author-edit path; `ComputeUnmappedPorts` returns empty when orphan
   (ExternalPortCount null), else compares mapped pins against ExternalPortCount.
**Gate:** open a cell symbol → "Ports: N" read-only, unmapped warning shows if pins < N; open an orphan/scratch
symbol → no editable field, no unmapped warning; drawing pins doesn't error. Report.

## LAYER 2 — Live resize: render the primitive live, no outline box
On gripper resize, render the PRIMITIVE itself live (scaled as you drag), and DO NOT draw the outline preview
box (`ResizePreviewBb`). Currently `OnPointerMoved` tracks `_resizeLiveX1/Y1` and the overlay draws
`ResizePreviewBb`. Change so the live preview shows the actual primitive at the in-progress scale:
- During resize, build a transient scaled copy of the primitive (apply the same scale the commit uses:
  `ResizeSymbolPrimitiveCommand`'s sx/sy about the top-left anchor) and render THAT as the in-progress
  primitive (like the draw-tools' `InProgressPrimitive`), instead of the bbox outline.
- Remove `ResizePreviewBb` from the overlay/renderer (or stop populating it).
**Gate:** grab a primitive's resize handle and drag → the shape itself scales live, no dashed box; release
commits the same shape; undo restores. Report.

## LAYER 3 — Live inspector updates on drag & resize
While dragging or resizing a primitive (and moving pins), the Properties inspector values must update LIVE (not
only on release). The inspector reads from the selected primitive/pin; during a live op the model isn't mutated
until commit, so push the in-progress values:
- During drag/resize/pin-move, update the inspector's displayed values from the live transform (the same
  transient values used for the live render). Simplest: have the inspector VM observe an "in-progress
  transform" the editor VM exposes, or refresh the inspector from the live preview each `RebuildOverlay`.
- On release/commit, the inspector reflects the committed values (already happens).
**Gate:** select a primitive, watch the inspector, drag it → X/Y (and W/H on resize) update continuously;
release → final values stick; pin move → pin X/Y update live. Report.

## LAYER 4 — Sine resolution as a function of cycles (+ PtsPerCycle)
The Sine glyph looks jaggy at high Cycles because point count is fixed. Add `PtsPerCycle` to `SinePrimitive`
(editable; sensible default, e.g. 16–24) and generate points as `ceil(Cycles * PtsPerCycle)` (clamp to a sane
min/max) in the renderer's sine point generation.
- SymbolModel: add `int PtsPerCycle` to `SinePrimitive` (default e.g. 20). (Alpha: bump .csym format_version if
  it changes the serialized shape.)
- Renderer: sine sample count = `max(8, ceil(Cycles * PtsPerCycle))`.
- Inspector: expose PtsPerCycle as an editable int field for a selected Sine.
**Gate:** a 1-cycle sine looks smooth; a 10-cycle sine is smooth (not jaggy); editing PtsPerCycle changes the
resolution live. Report.

## LAYER 5 — Remove HalfWave primitive + toolbar button
A half-wave is a Sine with 0.5 cycles, so HalfWave is redundant.
- Remove the HalfWave toolbar button from SymbolEditorView.axaml.
- Remove `Tool.HalfWave` from the Tool enum and all its branches (IsTwoPointDragTool, BuildTwoPointPrimitive
  HalfWave case, etc.).
- Remove `HalfWavePrimitive` from SymbolModel + its geometry/render/hit-test handling.
- (Alpha: any existing .csym with a HalfWave won't load — acceptable under no-migration; bump format_version.
  Since this is alpha with no shipped symbols using it, just remove cleanly.)
**Gate:** no HalfWave button; Sine with Cycles=0.5 produces the half-wave shape; build has no HalfWave
references. Report.

## LAYER 6 — Add ExponentialTaper primitive
A microstrip exponential taper glyph. Parameters: `W1` (start width), `W2` (end width), `L` (length), `Filled`
(default false), `NumPts` (resolution, default e.g. 24). Drawn left→right over length L; the top and bottom
edges follow an exponential profile so width transitions from W1 to W2; the glyph is a CLOSED region.
- SymbolModel: add `ExponentialTaperPrimitive` (record) with Cx,Cy (or an origin), W1, W2, L, Filled, NumPts,
  ColorRole, StrokeTier. Width at position x∈[0,L]: `w(x) = W1 * (W2/W1)^(x/L)` (exponential taper); half-width
  above/below the centerline. Build a closed polygon: top edge left→right at +w(x)/2, bottom edge right→left at
  −w(x)/2, sampled at NumPts.
- Geometry: BboxOf (from W1,W2,L), HitTest (closed-region/edge), Translate, RotateBy90About, StrokeTierOf.
- Renderer: stroke the closed outline; fill when Filled.
- VM: add `Tool.ExponentialTaper`; two-point drag sets L (horizontal extent) and a default W1/W2 (or W1=W2=drag
  height, then editable); add to IsTwoPointDragTool + BuildTwoPointPrimitive. Default Filled=false.
- Inspector: editable W1, W2, L, NumPts, Filled for a selected taper.
- Toolbar: add a button (glyph in Layer 9).
**Gate:** draw an ExponentialTaper → authentic exponential microstrip taper, closed region; toggling Filled
fills it; editing W1/W2/L/NumPts updates it; rotate/resize/undo work. Report.

## LAYER 7 — Arrow keys nudge selected primitives & pins
Arrow keystrokes move the current selection. Primitives nudge by the fine grid (p=5, or 1 with a modifier if you
like — keep v1 simple: one grid step). PINS always stay on the connection grid P=100 (nudge by 100).
- In OnKeyDown, handle Left/Right/Up/Down: if primitives selected → MoveSymbolPrimitivesCommand by ±step;
  if pins selected → MoveMultipleSymbolPinsCommand by ±100 (stay on grid). Both undoable; can move together.
- Respect IsLocked (no-op when locked).
**Gate:** select a primitive → arrows nudge it by one grid step (undoable); select a pin → arrows move it by
100, staying on grid; mixed selection moves both. Report.

## LAYER 8 — Graceful input handling for ALL primitive fields
Many inspector text fields fail ungracefully on bad input. Apply the same fix already used elsewhere: a single
IValueConverter that returns `AvaloniaProperty.UnsetValue` from ConvertBack for null/invalid input (Avalonia
treats UnsetValue as "skip this source update" — no error, no crash).
- Find the existing converter (grep ConvertBack + UnsetValue). REUSE it.
- Apply it to EVERY editable numeric field in the primitive/pin inspector (all primitive types: Line, Rect,
  RoundedRect, Circle, Ellipse, Arc, Sine{...,PtsPerCycle}, Polygon/polyline coords, Quad/Cubic control points,
  Text fontsize/anchor, ExponentialTaper W1/W2/L/NumPts, pin X/Y/Port). Invalid input → no update, field
  reverts on next refresh; valid → applies.
**Gate:** type garbage ("abc", "", "--", out-of-range) into each primitive's fields → no crash, no red error
adorner, the model keeps its last valid value; valid input applies. Spot-check several primitive types. Report.

## LAYER 9 — Toolbar primitive glyphs (replace text labels)
Replace the primitive button TEXT (Line, PLine, Rect, …) with small RENDERED GLYPHS of the primitive (like the
Library Palette tiles). Keep glyphs SMALL — no larger than the current text labels. Follow HIG.
- Render each primitive's glyph (reuse the palette's symbol-glyph rendering approach / SymbolEditorRenderer at
  tiny scale, or hand-built SVG/Path glyphs). Specific asks:
  - Line → a segment angled slightly DOWN from left to right.
  - PLine → a simple 4-point polyline glyph.
  - Others → a representative mini-glyph of each shape (Rect, RRect, Circle, Ellipse, Arc, Triangle, Polygon,
    QBez, CBez, Sine, ExponentialTaper).
  - **Text tool stays its Material icon** (FormatText) — do NOT glyph it.
- Keep the active-tool accent styling and tooltips. Don't grow the toolbar height.
**Gate:** toolbar shows mini primitive glyphs (Text still its icon), each no bigger than the old labels, active
state + tooltips intact, no scrollbar growth. Report.

## LAYER 10 — Add Thick stroke tier
Add `Thick` to `SymbolStrokeTier` (so { Normal, Thin, Thick }) as a thickness option for all primitive lines.
- SymbolModel: add `Thick` to the enum.
- Renderer: map Thick → a heavier px width; SymbolGeometry.StrokeTierOf / hit-test extra-tolerance handles it.
- Toolbar: add a "Thick" button next to Normal/Thin (same SetCurrentStrokeTierCommand pattern).
- Inspector: the per-primitive Stroke ComboBox includes Thick.
- (Alpha: bump .csym format_version if the serialized enum set changes; reject-on-mismatch.)
**Gate:** select Thick → new primitives draw thicker; the inspector Stroke ComboBox offers Normal/Thin/Thick;
existing primitives can be set to Thick. Report.

## Acceptance
Ports field removed (cell = source of truth, read-only display + unmapped warning only when cell-bound); live
resize renders the primitive (no outline box); inspector updates live on drag/resize/pin-move; Sine resolution
scales with cycles via PtsPerCycle; HalfWave removed; ExponentialTaper added (closed, Filled, NumPts); arrow
keys nudge prims (grid) and pins (P-grid); all primitive inspector fields fail gracefully via the UnsetValue
converter; toolbar shows mini primitive glyphs (Text keeps its icon); Thick stroke tier added everywhere.
Firewall green; build/test green; no regression to existing draw/select/move/rotate/save.

## Guardrails
- SymbolModel/SymbolGeometry/EditableSymbol stay framework-free.
- Cell is the port-count authority; orphan port count = pin count, no warning. Don't reintroduce an editable
  port field.
- Live resize/drag render the real primitive (transient scaled/translated copy), not an outline box; commit via
  the existing commands; undo exact.
- REUSE the existing UnsetValue IValueConverter for ALL primitive fields — don't invent per-field validation.
- Toolbar glyphs stay small (≤ current label size); Text tool keeps its Material icon; no toolbar scrollbar.
- Alpha format_version: bump + reject-on-mismatch for any .csym shape change (PtsPerCycle, HalfWave removal,
  ExponentialTaper, Thick); never migrate.
- Sub-gate; report+STOP between layers; don't batch.
- Update docs/design/symbol-editor.md (ports authority; ExponentialTaper; PtsPerCycle; Thick; arrow nudge;
  glyph toolbar) and standard-library-symbols.md if the primitive set is referenced there.

*Exit: the Symbol Editor draws ports per the cell (no editable count), renders live resize/drag with a live
inspector, has smooth cycle-scaled sines, drops HalfWave, gains ExponentialTaper + a Thick stroke, supports
arrow-key nudging, fails input gracefully everywhere, and shows mini primitive glyphs on the toolbar.*
