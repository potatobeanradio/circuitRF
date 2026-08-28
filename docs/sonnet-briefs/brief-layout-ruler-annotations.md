# Sonnet Brief — Ruler annotations in the Layout editor

**Design:** `docs/design/layout-view.md` **§9B** (all of it), plus §1.3 (display units are free), §2.2
(chrome colours are roles, layer colours are literal), §4 (`.clay`), §6.1/§6.2 (tools, selection),
§6.4 (clipboard), §8 (interchange). Read §9B first and in full — this brief implements it and does not
re-argue it.

**One sentence:** a two-point measurement the user places *inside* the layout, which reads out the
distance between its endpoints, persists in the `.clay`, and comes out in a slide and in a DXF.

**The single most important structural fact:** a ruler is **not** a `LayoutShape` and never enters
`LayoutView.Shapes`. §9B.1 gives the reasoning. If you find yourself adding a `RulerShape` to the
`JsonDerivedType` list on `LayoutShape`, stop and re-read it.

**Sequencing.** Self-contained; touches the layout model, persistence, renderer, VM, view, properties
inspector, clipboard and the DXF writer. No dependency on unlanded work.

---

## 1. What already exists — read this first

Nearly every mechanism this needs is already built for something else. Copy the pattern; do not invent.

| Piece | Where |
|---|---|
| **The second selection channel** — `SelectedInstanceIndices` beside `SelectedIndices`, with its own hit-test/drag/delete/copy/inspector plumbing across 8 files | `Layout/LayoutEditorViewModel*.cs`, `LayoutOverlay.cs`, `Renderers/LayoutRenderer.cs`, `ViewModels/LayoutShapePropertiesViewModel.cs` |
| **Tool arming + Escape** — `Tool` enum, `SetActiveToolCommand`, `OnActiveToolChanged`, and the Escape contract ("disarm to Select, then deselect") | `LayoutEditorViewModel.cs` 676-700, 2670-2710 |
| **Toolbar button markup** — `Classes.ToolActive` + `EnumEqualsBool` + `CommandParameter`, zero code-behind | `Views/Layout/LayoutEditorView.axaml` 132-185 |
| **A single-click tool with no drag** (closest analogue for the commit path) | `CommitViaPlacement`, `LayoutEditorViewModel.cs` ~723 |
| **Snap** — grid (`LayoutSnapping.SnapPoint`) and geometry (`LayoutSnapQuery`, pins/corners/intersections/midpoints/centroids) with marker feedback | `Layout/LayoutSnapping.cs`, `LayoutSnapQuery.cs`, `LayoutEditorViewModel.Snap.cs` |
| **Context-menu find-then-offer** — the exact shape `FindRulerForContextMenu` should copy | `FindBitmapForContextMenu`, `LayoutEditorViewModel.Bitmaps.cs` 76 |
| **Context-menu build seam** — one XAML `ContextMenu`, rebuilt per `Opening`; never construct one | `Controls/LayoutCanvas.cs` 843-935 |
| **Multi-selection property editing** — a field shows the shared value or blanks when they differ; committing folds one `SetShapeFieldCommand<T>` per item into a `CompositeCommand`, i.e. ONE undo entry | `ApplyToEach<T>` 2024, `FormatSharedDbu`, the `xs.Count == 1 ? xs[0] : null` tri-state pattern (e.g. `OnLabelStyleValueChanged` 646) |
| **Undo commands** with restore-at-original-index | `Commands/Layout/AddShapeCommand.cs`, `DeleteShapesCommand.cs`, `SetShapeFieldCommand.cs` |
| **Additive `.clay` fields** omitted when empty, no `FormatVersion` bump | `ClayFile.Pins`, `ClayFile.DrcWaivers`, `Design/Layout/LayoutPersistence.cs` 43-52 |
| **Adding a colour role** — constant → `ColorRole.All` → light+dark in `ColorTheme.BuiltIn` → `LayoutRenderTheme` | `Theming/ColorRole.cs` 27-31/224-252, `ColorTheme.cs` 71-88/166-182, `Renderers/LayoutRenderTheme.cs` |
| **Screen-space drawing over world geometry** (constant-pixel dots/ticks at any zoom) | `LayoutRenderer.PCellHandles.cs`, `LayoutRenderer.Drc.cs` |
| **World-space text with real font metrics** | `LayoutRenderer.MeasureLabelWorldBbox` 1132, `DrawLabelText` 2357, `LayoutTextOutline.ResolveTypeface` |
| **Clipboard graphic export** — builds a throwaway `LayoutView` and calls `LayoutRenderer.Draw` | `Clipboard/LayoutClipboard.cs` `BuildTransientView` 210, `ExportOptions` 228, `ComputeSelectionBounds` 168 |
| **DXF: extra non-technology layers** — the seam wBond wires already use | `DxfWriter.WriteLayerTable(extraLayerNames)` 403, `DxfWireIo.LayerNames` 58 |
| **DXF: blocks, entity headers, TEXT** | `DxfWriter.WriteBlockRecordTable` 509, `WriteBlockHeader/Footer` 588/603, `WriteEntityHeader` 621, `WriteText` 1155 |
| **DXF: the empty `DIMSTYLE` table** whose comment this brief retires | `DxfWriter.WriteDimstyleTable` 497 |

---

## 2. Model + persistence (`src/Design`)

`RulerAnnotation` and `RulerSizeMode` exactly as §9B.2 declares them, in `Design/Layout/LayoutModel.cs`.
`LayoutView.Rulers` is a `List<RulerAnnotation>` initialised empty, sitting beside `Shapes` and
`Instances`.

- **No `Layer` field** (R-rul-1). Not "a `Layer` we ignore" — absent.
- `Style` is the existing `LabelFontStyle` (R-rul-2), not a new enum.
- `ClayFile.Rulers` is `List<RulerAnnotation>?`, written only when non-empty, **no `FormatVersion`
  bump** — mirror `Pins`/`DrcWaivers` exactly, including their doc-comment convention.
- Mutations go through `LayoutView.NotifyChanged`, but rulers do **not** enter `SpatialIndex`
  (§9B.11: tens of rulers, not 500,000 — a linear scan is the right-sized tool). Pass `LayoutChangeInfo.Full`; do not invent a
  ruler-specific `LayoutChangeKind`.
- `LayoutFlattener` does not carry rulers up (§9B.7). Rulers are cell-local.

## 3. Rendering (`src/Ui/Renderers`)

A `LayoutRenderer.Rulers.cs` partial, drawn **after every layer and after instances**, before the
transient overlay. Rulers always paint on top; nothing about a layer affects them.

Per ruler: the measurement line, an end tick at each endpoint, and the readout block at the midpoint —
distance, then the Δx/Δy line when `ShowComponents`, then the caption when non-empty (§9B.4).

- **`RulerSizeMode.Fixed`** — text at `TextSizePt` device points, line and ticks at constant device
  pixels, both independent of `vp.Zoom`. **`Scaled`** — text at `TextHeightDbu` world height, exactly
  as `DrawLabelText` handles a `LabelShape`, line weight scaling with it.
- **Text is always upright** regardless of the ruler's angle, and offset perpendicular so it never
  overlaps the line (§9B.4).
- Every length string comes from `LayoutUnits.Format(dbu, view.DisplayUnit, view.DbuPerMicron)`
  (R-rul-6). No second formatter, no hard-coded unit.
- Colours from two new roles, `Layout.RulerAnnotationLine` / `Layout.RulerAnnotationText`, projected
  into `LayoutRenderTheme` (§9B.8). A selected ruler additionally takes `Layout.Selection`.
- Expose a **`MeasureRulerScreenBox` / `MeasureRulerWorldBbox` pair** — the renderer is the only thing
  that knows the font metrics, and hit-test (§5), `ContentBounds`, Zoom-to-Fit and
  `ComputeSelectionBounds` (§7) all need the painted extent. One implementation, several callers; this
  is the mistake `MeasureLabelWorldBbox`'s own doc comment records.

**Also add a `ShowRulers` flag to `LayoutRenderOptions`** (default true) — the View toggle of R-rul-1.
Export mode leaves it **on**: rulers are document content, not overlay state.

## 4. The tool (`src/Ui/Layout`)

Add `Ruler` to `LayoutEditorViewModel.Tool` and a toolbar button mirroring the existing markup
(`MaterialIcon` `Kind="Ruler"` or `TapeMeasure`). Two-click placement, live preview of the *whole*
ruler including its readout, tool stays armed after a commit (§9B.5).

- Escape: **no new code** beyond being a tool — `OnKeyDown`'s existing "cancel the draw op, return to
  `Tool.Select`" branch already covers it. Verify rather than duplicate.
- Both endpoints go through the existing grid + geometry snap stack unchanged (R-rul-9), with the same
  snap markers.
- Shift locks the second endpoint to horizontal / vertical / 45° (R-rul-10). **Not** `AngleMode` —
  §9B.5 says why. **Geometry snap outranks the Shift constraint** when a snap feature is in tolerance.
- A ruler whose endpoints coincide after snapping is discarded, not committed.

## 5. Selection and editing

Third channel `SelectedRulerIndices`, mirroring `SelectedInstanceIndices` — find every site that
handles that one and handle rulers alongside.

- Hit-test: the line within tolerance, either endpoint, **and the readout text box** (R-rul-11).
  Rulers hit-test **above** all geometry. New `LayoutRulerHitTest`, linear scan.
- Marquee selects rulers whose whole line is enclosed, on §6.2's existing enclose/crossing rule.
- Drag moves a whole ruler; dragging one endpoint moves that endpoint and re-measures live. Endpoint
  handles render only for a single-ruler selection.
- Delete removes selected rulers.
- **Properties Inspector** gains a Ruler section (mirror the Bitmap section's structure in
  `LayoutShapePropertiesViewModel`): both endpoints, size mode, **one** size field whose label and unit
  follow the mode (R-rul-3 — never two fields with one inert), style, caption, Δ toggle, and the
  measured distance read-only.
- **Multi-selection editing is required, and is the existing mechanism (R-rul-11a).** Select ten rulers,
  type one text size, all ten change as ONE undo entry. Follow `ApplyToEach<T>` exactly: shared value or
  blank when they differ, tri-state (null = differs) for the mode/style combos and the Δ checkbox,
  `FormatSharedDbu` for the dimension fields, one command per ruler folded into a `CompositeCommand`.
  - It needs an **`ApplyToEachRuler<T>` sibling** (~20 lines) because the existing `ApplyToEach` is typed
    `Func<LayoutShape, T>` and a ruler is deliberately not a `LayoutShape`. This is the one concrete cost
    of §9B.1 — pay it rather than reopening that decision.
  - **`SetShapeFieldCommand<T>` itself is reusable verbatim**: its body only touches the view for
    `NotifyChanged` and mutates through a caller-supplied closure, so it is already generic over "a field
    on something this `LayoutView` owns". **Widen its doc comment** to say so rather than copying it.
    (Renaming it to `SetLayoutFieldCommand<T>` is a 13-reference change across 3 files if a reviewer
    prefers the honest name — optional, not required by this brief.)
  - **Mixed size modes (R-rul-3a):** when the selection's `SizeMode` differs, the mode combo shows its
    mixed state and the size field is **disabled with the R13a reason**, because the one field means
    points in `Fixed` and a world length in `Scaled`. Never write a number into a mixed-mode selection.
- **Context menu**: `FindRulerForContextMenu` → `Edit…` and `Delete`. `Edit…` opens the same property
  set as a modal. Items disabled with a stated reason, never hidden (R13a).
- **`Ctrl+K` / `Cmd+K`** clears every ruler as ONE undo entry, no prompt (R-rul-13). Register both
  gestures in `WorkspaceWindow.axaml` alongside the existing pairs. `Ctrl+Shift+K` is Check Design
  Rules — leave it alone.
- Commands in `src/Ui/Commands/Layout/`: `AddRulerCommand`, `MoveRulersCommand`, `ReplaceRulerCommand`,
  `DeleteRulersCommand`, `ClearAllRulersCommand`. Same restore-at-original-index discipline as
  `AddShapeCommand`, and the same `lock (view.RenderLock)` around every mutation.

## 6. Clipboard — internal

`LayoutFragment.Payload.Rulers`, translated on copy and on placement by the same `Translate` used for
shapes and instances, and **rescaled with them** when the destination has a different `DbuPerMicron`
(R-L1f-2) — a pasted ruler must measure the same physical distance. No layer reconciliation applies;
rulers have no layer.

`CanCopySelection` grows a `SelectedRulerIndices.Count > 0` term.

## 7. Clipboard — the PowerPoint path

`BuildTransientView` copies `payload.Rulers` into `view.Rulers`: one line, and the ruler then appears in
the PDF, SVG and bitmap flavours with no export-specific rendering.

**`ComputeSelectionBounds` is the one genuinely subtle part (R-rul-16).** A `Fixed` ruler's world extent
depends on the export scale, which depends on the bounds. Resolve in **exactly two passes**: pass 1
unions the line endpoints and every `Scaled` ruler's measured text; pass 2 measures `Fixed` text at pass
1's scale and unions that. Monotone, so a second pass cannot make it worse. Do not iterate to a fixed
point; do not skip pass 2. That method's own doc comment records the ports-cropped-off-the-page bug this
is the same family as — read it before editing it.

## 8. DXF export — real `DIMENSION` entities

§9B.10 is the specification; follow R-rul-18 / 18a / 18b / 18c precisely. Summary of the work:

1. **`WriteDimstyleTable` stops writing zero records.** One record per distinct (text height, font
   style) pair actually used, named `CIRCUITRF_1`, `CIRCUITRF_2`, … **Update its comment** — it
   currently states the table is empty *because* this codebase never creates a dimension entity, and
   that premise is exactly what this brief retires. No `DSTYLE` XDATA overrides.
2. **One anonymous `*D#` block per ruler**, through the existing `WriteBlockRecordTable` /
   `WriteBlockHeader` / `WriteBlockFooter` path, holding the extension lines, the dimension line with
   its ticks, and the readout `TEXT`.
3. **One `DIMENSION` entity per ruler** on the `RULER` layer, subclasses `AcDbDimension` +
   `AcDbAlignedDimension`, groups per §9B.10's table (`70` = `1 | 32`).
4. **`RULER` layer** through the existing `extraLayerNames` seam.
5. **Caption / Δ ride in group `1`**, always beginning `<>` so the measurement stays live; omit group
   `1` entirely when there is neither. **Never write the formatted distance as literal text.**
6. **`Fixed` resolves once at export** to `extentsDiagonal × TextSizePt / NominalViewportDiagonalPt`,
   the constant stated once in code, and the Messages note says so.
7. **Import: skip `DIMENSION`.** `*D#` blocks are already skipped by the importer's anonymous-block
   rule — verify, do not re-implement.

**GDSII, Gerber, Excellon and `.kicad_pcb` writers are not touched at all.** They walk `Shapes`; rulers
are not in `Shapes`; there is nothing to exclude. If you find yourself adding an exclusion to one of
them, the model went in the wrong place — go back to §2.

## 9. Scope guardrails

- **Never** add `RulerShape` to `LayoutShape`'s `JsonDerivedType` list.
- No chained/angular/radial dimensions, no leader or callout arrows (§9B.11).
- No DRC interaction: no tolerance, no pass/fail, no rule binding.
- Rulers are never a boolean/offset/flatten operand, never meshed, never in the spatial index, never a
  snap *target*, never net-aware.
- No ruler round-trip on DXF import.
- Rulers in a sub-cell do not render through an instance placement.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.
- **No new timing benchmark tests** — assert counters and structure, not wall-clock.

## 10. Gate (acceptance)

1. Build green (`TreatWarningsAsErrors=true`); `dotnet test tests/Ui.Tests` and
   `dotnet test tests/Firewall.Tests` green, plus `tests/RfCore.Tests` if `src/Design` changed. Read
   `TestResults/last-run.trx` for any failure — **do not re-run the suite to find out what broke.**
2. **Round-trip** — a `.clay` with rulers serializes, reloads and compares equal (both size modes, both
   `TextSizePt` and `TextHeightDbu` preserved across a mode switch, caption and Δ flag preserved). An
   existing ruler-free `.clay` reloads and **re-serializes byte for byte**, `FormatVersion` unchanged.
3. **Not geometry** — a layout with rulers exports to GDSII, Gerber, Excellon and `.kicad_pcb` producing
   **byte-identical** output to the same layout with `Rulers` cleared. This is the load-bearing test;
   assert it on all four.
4. **DXF is a real dimension** — the exported file contains one `DIMENSION` per ruler with both subclass
   markers, a resolvable group `3` `DIMSTYLE` reference, a `*D#` block whose entities' owner handles
   point at their own `BLOCK_RECORD`, and a `RULER` layer table record. A ruler with a caption carries
   group `1` beginning `<>`; a bare ruler carries no group `1`. **Validate with a non-circuitRF reader**
   (R-rul-18c) — a round-trip through our own `DxfReader` proves nothing about conformance.
5. **DXF import** of that same file produces **no** rulers and no stray `*D#` structure.
6. **Snap** — with geometry snap on, a ruler endpoint placed near a rect corner lands *exactly* on the
   corner and the readout reports the exact corner-to-corner distance. With Shift held and no snap
   feature in range, the second endpoint lies exactly on 0/45/90°. With Shift held **and** a snap
   feature in tolerance, snap wins.
7. **Undo/redo** — place, move, drag one endpoint, edit each property, delete, and `Ctrl+K`: each is
   exactly one undo entry and each round-trips. `Ctrl+K` on a document with 5 rulers restores all 5 in
   one Ctrl+Z, and is disabled with a reason at zero rulers.
8. **Escape** disarms the Ruler tool to `Tool.Select` mid-placement with nothing committed.
9. **Fixed vs Scaled** — render the same ruler at two zooms an octave apart: `Fixed` text measures the
   same device height at both, `Scaled` measures double. Off-screen render, measured, not eyeballed.
10. **Display unit** — switching the document mm → mil re-renders every readout in mil with no stored
    field changing.
11. **Selection** — clicking the readout text selects the ruler; a ruler over a trace is selected in
    preference to the trace; marquee enclosure selects it; endpoint handles appear only for a single
    selection.
12. **Clipboard** — copy a selection of shapes *and* a ruler, paste into another layout at a different
    `DbuPerMicron`: the ruler survives and reports the same physical distance. Copy into
    **PowerPoint/Keynote**: the ruler, its readout and its caption appear in the vector graphic. A
    `Fixed`-mode ruler's text is fully inside the page (R-rul-16) — assert the bounds, not just that it
    pasted.
13. **Multi-edit (R-rul-11a)** — draw 10 rulers at 11 pt, select all 10, set the text size to 16: all 10
    change and **one** Ctrl+Z restores all 10 to 11 pt. Repeat for style, caption and the Δ toggle. With
    a selection of mixed values a field reads blank, not the first ruler's value. With **mixed size
    modes** the size field is disabled with its reason; setting the mode across the selection first then
    enables it (R-rul-3a).
14. **Theme** — both new roles appear in the Color Theme Settings dialog with light and dark defaults,
    and changing either repaints rulers without a document reload.
15. **Cell-local** — a cell containing rulers, placed as an instance in a parent, renders no rulers in
    the parent; Flatten Hierarchy produces none.

## 11. On completion

**Write the findings to `src/Ui/RESOLVED.md`. Do not touch any `CLAUDE.md`** — not to add a changelog
entry, not to note a convention, not "just one line". `CLAUDE.md` is standing project memory and is kept
small on purpose; completion notes belong in the sibling `RESOLVED.md`, and if one does not exist where
you need it, create it.

Record: **§9B.1** (a ruler is not a `LayoutShape`, and that the manufacturing writers therefore needed no
exclusions — the property gate 3 pins); the **third selection channel**; **R-rul-3** (one size field, not
two with one inert) and **R-rul-3a** (mixed size modes disable the field rather than guess a unit);
**R-rul-11a** (multi-selection editing reuses `ApplyToEach`, and the `ApplyToEachRuler` sibling that
§9B.1 costs); **R-rul-16** (the two-pass bounds, and why one pass crops); **R-rul-18** (a real
`DIMENSION`, and the `DIMSTYLE` comment that had to be retired); **R-rul-18b** (`Fixed` has no meaning in
DXF and is resolved against the drawing extents); and the test file names.
