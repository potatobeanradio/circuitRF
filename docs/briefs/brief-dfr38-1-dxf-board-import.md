# Brief DFR38-1 — a board drawn as DXF outlines imports as copper and vias

**Series:** designer feedback round 38. · **Tag:** `R-dfr38-1-<n>`
**Area:** `src/Design/Layout/Interchange/` (`DxfImport`, a new `DxfCopperLoops`), `src/Design/Layout/LayoutLayerMapping.cs`, `src/Cli/LayoutConvert.cs`, `src/Ui/ViewModels/WorkspaceViewModel.cs` and the Layer Mapping dialog.
Findings go in `src/Design/RESOLVED.md`. No new geometry kernel: Clipper2 is in `src/Design` and `DrillViaPairing` already makes vias from drills.

## The gap

Vendors publish evaluation boards as DXF: a drawing of outlines, one DXF layer per purpose. The Layer Mapping dialog can already say what each layer is, and a correct mapping (`PATTERN` to Top Copper, `HOLE` to Drill, `PARTS` to Silk Top) still gives a picture that cannot be simulated:

- A `LINE` mapped to a conductor stays a zero-width `PathShape`, one per `LINE`, so nothing is filled and nothing can become copper.
- A `CIRCLE` mapped to Drill stays a `CircleShape`, so there are no vias.
- The dialog has no **Ignore**, and every row it cannot match starts on **Add to technology**. Its choices for `PTN`, `HIDDEN`, `SHELL` and `0` all add layers the user did not ask for.
- Every `BLOCK` becomes a cell, whether or not model space inserts it. A sample board gives 53 cells, of which 3 are used, and the dialog's counts add up every block: `PATTERN` reads 354 shapes where model space holds 196.

The sample file has 194 `LINE`s and 2 closed `POLYLINE`s on `PATTERN`, 87 `CIRCLE`s and 5 `INSERT`s on `HOLE`, 2 `CIRCLE`s on `PARTS`, a 4-`LINE` frame on `SHELL`, and 1 `INSERT` on `PTN`. It declares no `$INSUNITS`. Copper loops share vertices to about 1e-12 mm, and four line ends meet no other end: `LINE`s at the board edge overlap collinearly, so a loop there closes only after the overlap is resolved.

## Decisions for the owner before building

**D1. A new command, or the existing one?** Recommended: the existing **File ▸ Import ▸ DXF**, with the mapping deciding. A layer mapped to a stackup conductor is filled, a layer mapped to a via or drill layer becomes vias, and any other layer stays outlines as today. The user has already said what each layer is, so nothing is guessed, and a mechanical drawing whose layers are added to the technology imports exactly as it does now.

**D2. Which side of a loop is copper?** A file of bare outlines does not say. Recommended: even-odd by nesting, outermost loop filled, shown in the dialog's preview with an **Invert** per layer. On the sample the pours and the line are copper, and the part-number digits may be copper or a clearance, so the reading needs checking against the board as published.

**D3. May an unclosed loop be closed by a straight line?** Recommended: only within a stated tolerance (default 1 µm, editable), and anything still open is listed by coordinates and left as an outline. A guessed closure across a real gap silently shorts two nets.

## Requirements

- **R-dfr38-1-1** The Layer Mapping dialog gains an **Ignore** action, for DXF imports. Its rows and counts are what model space reaches. Same wording as the picture import's roles. A layer the technology's `DxfLayerName` alias does not name is not guessed from its name.
- **R-dfr38-1-2** Only what model space reaches is imported, with `INSERT`s flattened. No cell for an unused `BLOCK`, and Messages states how many were skipped.
- **R-dfr38-1-3** A layer mapped to a conductor chains `LINE`, `ARC` and open `POLYLINE` entities into loops: overlapping collinear segments are resolved first, then ends are joined within D3's tolerance. Loops become `PolygonShape`s with holes by D2. `CIRCLE`s and closed polylines are loops already.
- **R-dfr38-1-4** A layer mapped to a via or drill layer makes a plated through via from each `CIRCLE`, from the top to the bottom of the stack, through `DrillViaPairing`. A non-circular entity on the layer is reported, not guessed at.
- **R-dfr38-1-5** A layer mapped to `Outline` closes to the board outline. The units prompt stays as it is today.
- **R-dfr38-1-6** The dialog previews the result through `CircuitRF.Render` before anything is written, and Cancel creates nothing.
- **R-dfr38-1-7** The same operation from the CLI, with no second implementation: `convert x.dxf -o dir --tech <id> --layer-map "PATTERN=Top Copper,HOLE=Drill,PARTS=ignore"`. With a `--layer-map`, a layer it does not name is a refusal listing the layers, as `convert` refuses an unstated Excellon format. With none, `convert` behaves as it does today.
- **R-dfr38-1-8** Docs: `reference/layout-editor.md` and `reference/cli.md`, in present tense.

## Gates

- A synthetic DXF committed under `testdata/`, with a pour holding a clearance, a line in it, two `LINE`s overlapping at the edge and one real gap: one polygon with one hole plus one polygon, the gap reported by coordinates, circles as vias, an unused block leaving no cell.
- `check` on the imported cell finds no error, and an edge port on the line reaches `em --dry-run` with a return.
- An import that maps nothing to a conductor or a drill layer is unchanged, byte for byte.
- UiSnapshot of the dialog and the preview on the synthetic file: PNG looked at, and listed in the note.

## Not in this brief

Multi-layer boards drawn as one DXF per layer, and text entities as copper. Vendor data stays out of the repo, so the sample file is not committed.
