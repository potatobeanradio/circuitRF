# circuitRF — Symbol Editor cleanup, round 2 (Claude Code / Sonnet)

Round 1 reported these done but they did NOT fully land — verified on disk. The inspector is mostly **read-only
TextBlocks** (only X/Y and Text commit; W/H, R, Rx/Ry, Stroke, polyline points, pin fields are display-only),
there is **no Sine/Arc/Line/etc. type-specific field set**, the **selection highlight is still a dashed bbox**
(renderer `DrawSelectionOverlay` still draws `SKRect`), **Rotate is about origin (0,0)** (swings prims out of
view), the **port-count is a NumericUpDown**, and grid-snap / pin-select need runtime confirmation. This round
is more specific + HIG-sensitive. Sub-gated; **report and STOP between layers**; **instrument-first** for the
two "is it even working" items. Firewall green; every mutation undoable (notify Execute AND Undo); pins stay on
P; **Properties pane is ~300 px wide — spend width sparingly**.

## Read first (verified types/locations)
- `ViewModels/SymbolPrimitiveInspectorViewModel.cs` — today: editable X/Y + Text only; `SetPrimView` switch;
  `PolylineCoordRowViewModel` (get-only props — NOT editable). This is the bulk of the work.
- `Views/Properties/SymbolPrimitiveInspectorView.axaml` — TextBlocks for W/H/R/Rx/Ry/Stroke/points/pin.
- `ViewModels/SymbolEditorViewModel.cs` — `GridSnap`+`SnapToP` (art) / `SnapToConnectionGrid` (pins);
  `PinToolPress`/`HitTestPin` (Pin tool only); `RotateSelectionCommand` wiring (R key + toolbar); `PortCount`.
- `Commands/Symbol/RotateSelectionCommand.cs` + `SymbolGeometry.RotateBy90` — rotates about (0,0).
- `Renderers/SymbolEditorRenderer.cs` `DrawSelectionOverlay` — STILL a dashed `SKRect` + fill (round-1 Layer 8
  did not change it). The schematic wire-selection highlight to mirror: `SchematicRenderer` wire-select paint.
- `SymbolGeometry.cs` — `HitTest` (per-edge for most prims — OK), `BboxOf`, `StrokeTierOf`, `RotateBy90`,
  `TranslateBy`. `SymbolModel.cs` — primitive types + their fields (Sine: Cx,Cy,Amp,Cycles,Length,Axis;
  Arc: Cx,Cy,R,StartDeg,SweepDeg; Line: X1,Y1,X2,Y2; etc.; `SineAxis` enum; `SymbolStrokeTier` enum).
- `Views/Content/SymbolEditorView.axaml` — toolbar `ScrollViewer` (must NOT show a scrollbar — Layer 6),
  port-count `NumericUpDown` (Layer 5).

## Spine
- **Inspector = the real editor for every primitive.** Every field of the selected primitive is EDITABLE and
  commits via an undoable command with live re-render. No read-only geometry fields.
- **Mirror the schematic** for the selection highlight (outline stroke, translucent accent) — replace the bbox
  rectangle in the renderer.
- **Undoable + exact restore** for rotate (translate-to-anchor compensation must round-trip).
- **HIG + 300 px budget:** label + field rows; spinners ONLY where they fit; never force horizontal scroll.
- **Scope fence:** the 6 items below only. No new tools, no connectivity/schematic changes.

## LAYER 1 — INSTRUMENT: grid-snap + pin-select (confirm before fixing)
Two "does it even work" reports. Add temporary stderr logging, run, report — NO fix yet.
1. **Grid snap:** log in `SnapToP` (`v`, `GridSnap`, returned value) and at draw-commit the final point. Draw a
   line with snap ON then OFF at a normal zoom. Report whether values land on multiples of 5 when ON. (If
   snapping works but is invisible at the test zoom, that's the finding — say so.)
2. **Pin select/move:** log in `PinToolPress` (`hit` index), `HitTestPin` (nearest dist), `OnPointerMoved`
   pin branch, and the release commit. With the **Pin tool active**, click a pin and drag. Report whether the
   hit registers and the move commits. (Note: pins are a Pin-tool concept — confirm whether the user expects
   pin select under the **Select** tool too; if so that's a real gap to fix in Layer 4.)

**Layer 1 gate:** report both logs + the diagnosis for each. No code changes yet.

## LAYER 2 — full editable primitive inspector (the bulk) — HIG, 300 px
Make EVERY field of the selected primitive editable + live + undoable. Add a generic
`SetSymbolPrimitiveFieldCommand` (or per-type commands) that sets a named field and `NotifyChanged()`; Undo
restores the prior value. Replace ALL display-only TextBlocks with edit controls. Per type, surface (label →
field), commit on Enter/focus-loss, debounce live re-render:
- **Line:** X1,Y1,X2,Y2; Stroke (ComboBox).
- **Polyline / Polygon:** the **editable** point list (Layer 3); Stroke; (Polygon: Filled checkbox).
- **Rect / RoundedRect:** Cx,Cy,W,H (RoundedRect: + Radius); Stroke; Filled.
- **Circle:** Cx,Cy,R; Stroke; Filled.
- **Ellipse:** Cx,Cy,Rx,Ry; Stroke; Filled.
- **Arc:** Cx,Cy,R,StartDeg,SweepDeg; Stroke.
- **Sine:** Cx,Cy,Amp,Cycles,Length,**Axis (ComboBox Horizontal/Vertical)**,Stroke.
- **HalfWave:** Cx,Cy,Amp,Length,Axis(ComboBox),Stroke.
- **Quad/Cubic Bézier:** all control points (P0,Ctrl/C1,C2,P2/P3); Stroke.
- **Text:** Content, AnchorX, AnchorY, FontSize, FontStyle (already editable — keep), Align (ComboBox).
- **Stroke** is a **ComboBox** of `SymbolStrokeTier` for every stroked primitive (not a TextBlock).
- **Pin (Layer 4):** PortIndex (editable int), X, Y (editable, P-snapped).
HIG/width: a compact two-up grid (`Lbl  [field]  Lbl  [field]`) for paired coords; single column for the rest;
`FontSize=11`, tight padding. **Spinners (NumericUpDown) only where they fit** — for paired X/Y/W/H use plain
`TextBox` (numeric) without spinners to save width; reserve `NumericUpDown` for single-field rows (R, Radius,
Cycles) where there's room. Never let the pane force a horizontal scrollbar at 300 px.

**Layer 2 gate:** select each primitive type → all its fields show as editable controls → editing any field
updates the canvas live and is undoable → no horizontal scroll at 300 px. Walk through ALL types. Report the
per-type matrix.

## LAYER 3 — editable polyline/polygon point list
`PolylineCoordRowViewModel` currently has get-only X/Y. Make each row's X and Y **editable** (`[ObservableProperty]`),
committing an undoable point-edit command (move that vertex) with live re-render. Keep the virtualized
`ListBox` (lazy) so large polylines stay cheap. Compact rows: `[i]  [X]  [Y]` — numeric TextBoxes, no spinners
(width). Editing a row moves only that vertex; Undo restores it.

**Layer 3 gate:** a polyline's points list is editable; changing a coordinate moves that vertex live + undoably;
large polylines don't lag. Report.

## LAYER 4 — pin select/move + editable pin properties (driven by Layer 1 finding)
Based on Layer 1: ensure a pin can be **selected and dragged** (with the Pin tool, and — if the user expects
it — under Select too; implement per the Layer 1 diagnosis). Then make the inspector's pin fields editable:
- **PortIndex** (editable int → `RemapSymbolPinCommand`, already exists),
- **X, Y** (editable → `MoveSymbolPinCommand`, P-snapped on commit).
Pins MUST stay on the connection grid P. Live re-render; undoable.

**Layer 4 gate:** a pin selects + drags (live, snaps to P, undoable); its Port/X/Y are editable in the
inspector and commit undoably on P. Report (state which tool(s) select a pin).

## LAYER 5 — port-count: plain integer box (no spinner)
Replace the **Number-of-Ports `NumericUpDown`** (next to Save/Save As in `SymbolEditorView.axaml`) with a plain
`TextBox` that accepts integers only (reject non-digits; clamp ≥ 0; empty → 0). Two-way bind to
`ViewModel.PortCount`. No up/down buttons.

**Layer 5 gate:** port count is a plain integer text box; typing a non-integer is rejected; value drives
`PortCount`. Report.

## LAYER 6 — selection highlight = outline stroke (mirror schematic wire) + no toolbar scrollbar
1. **Highlight (the round-1 miss):** in `SymbolEditorRenderer.DrawSelectionOverlay`, REPLACE the dashed `SKRect`
   + fill with a **translucent System-Accent stroke drawn on the primitive's own outline geometry** + small
   padding — exactly like the schematic wire-selection highlight. Read `SchematicRenderer`'s wire-select paint
   and mirror its alpha/width/style. Build each selected primitive's path (reuse the draw path used by
   `DrawSymbol`) and stroke it at `strokeWidth + pad` in accent@~30–40%. **Text keeps a (correct) box**
   highlight. This pairs with the existing per-edge `HitTest` so the highlight matches the hit region.
2. **Toolbar scrollbar:** enforce **no horizontal OR vertical scrollbar** on the toolbar. With Font controls
   already removed, set the toolbar `ScrollViewer` to `HorizontalScrollBarVisibility=Disabled` (or drop the
   ScrollViewer and let it clip / wrap) so a scrollbar never appears. Confirm at narrow and default widths.

**Layer 6 gate:** selecting a primitive outlines its actual shape in translucent accent (no rectangle);
matches the schematic wire look; the toolbar shows no scrollbars at any width. Report with a description.

## LAYER 7 — rotate about bottom-left anchor, exact-restore undo
Change rotation to pivot about the selection's **bottom-most, left-most coordinate** (min-X, max-Y in
screen-y-down = the visual bottom-left of the selection bbox), so prims rotate in place instead of swinging
about origin.
- Compute the anchor from the selection bbox (min X, max Y). Rotate each primitive's points about that anchor
  (translate to anchor → `RotateBy90` about origin → translate back), OR add a `RotateBy90About(prim, ax, ay)`
  to `SymbolGeometry`.
- **Undo must restore exact original coordinates** — capture the anchor at Execute time and use the SAME anchor
  for the inverse in Undo (three 90° steps about that anchor, or store/restore). Round-trip must be exact (no
  drift); if floating error accumulates, snapshot original point arrays and restore them on Undo.
- Applies to the toolbar Rotate button and the `R` key (both call the command). Pins rotate about the same
  anchor and re-snap to P.

**Layer 7 gate:** rotating a selection keeps it in place (pivots at its bottom-left), 4× rotate returns to
start, and Undo restores exact original coordinates (assert in a headless test). Report.

## Acceptance
1. Inspector: every field of every primitive type is editable, live, undoable; Sine shows
   Cx/Cy/Amp/Cycles/Length/Axis(ComboBox)/Stroke; Stroke is a ComboBox everywhere; polyline points editable;
   pin Port/X/Y editable; no horizontal scroll at 300 px.
2. Selection highlight is an outline stroke (translucent accent, schematic-wire style), not a bbox.
3. Rotate pivots at the selection's bottom-left and Undo restores exact coordinates.
4. Port-count is a plain integer box (no spinner); toolbar has no scrollbars.
5. Grid-snap and pin select/move confirmed working (per Layer 1) or fixed.
6. `dotnet build`/`dotnet test` green; firewall green; every edit undoable; pins on P.

## Guardrails
- **Instrument-first** for grid-snap + pin-select; don't "fix" what already works — confirm at runtime first.
- **Every inspector field editable + undoable + live** — no read-only geometry display this round.
- **Mirror the schematic wire-selection highlight**; don't invent a new style.
- **Rotate undo must be exact** — snapshot original coordinates if needed.
- **300 px budget / HIG:** spinners only where they fit; numeric TextBoxes for paired coords; no horizontal
  scroll; no toolbar scrollbars.
- Sub-gate every layer; report+STOP between each; don't batch.
- Update `docs/design/symbol-editor.md`: full editable inspector (per-type field sets + Stroke ComboBox +
  editable polyline points + pin fields), outline selection highlight, rotate-about-bottom-left with exact
  undo, integer port-count box, no toolbar scrollbars.

*Exit: the inspector edits every primitive precisely (typed values, Stroke ComboBox, editable points, Sine
params), selection outlines the real shape, rotate pivots sensibly with exact undo, and the toolbar/port-count
are clean — no read-only fields, no stray scrollbars, no origin-swing rotation.*
