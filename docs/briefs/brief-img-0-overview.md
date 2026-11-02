# Brief series — Create from Image (IM-1 … IM-12, IM-14; optional IM-13)

**Status:** written, not started · **Date:** 2026-10-10 · **Decisions:** locked (§3), owner-reviewed
2026-10-10 — the four the owner settled are marked ◆ and recorded in §6
**Requirement tag:** `R-im<n>-<m>` (phase n, requirement m).
**Design note to be written by IM-1:** `docs/design/image-to-circuit.md` — this overview is its source.
**Builds on:** the whole Create Schematic from Artwork series (`brief-artsch-0-overview.md`, decisions D1–D20;
`src/Design/Layout/Recognition/`, `src/Ui/Recognition/`, `src/Cli/Recognize.cs`, `docs/design/artwork-to-schematic.md`),
the bitmap primitives (`BitmapShape` in `src/Design/Layout/LayoutModel.cs`, `EditableBitmap` in
`src/Design/Schematic/CanvasObjects.cs`, `LayoutEditorViewModel.Bitmaps.cs`), the drawn netlist
(`src/Design/Schematic/NetlistSchematic.cs` — it already takes position hints), AS-10's stroke-glyph reader
(`src/Design/Layout/Recognition/Silkscreen/`), the interchange `convert` pipeline (`src/Cli/LayoutConvert.cs`,
`src/Design/Layout/Interchange/`), the clipboard (`src/Ui/Clipboard/`), the Project Tree's Known File actions
(`src/Ui/ViewModels/ProjectTree/ITreeActions.cs`) and the MCP server's image content (`src/Cli/Serve/McpServer.cs`).

---

## 0. What the owner asked for (paraphrased)

Create Schematic from Artwork turns copper into a schematic. The owner wants the same idea with a **bitmapped
picture** as the source:

- **Create Schematic from Image** — the picture may be a *schematic* (a datasheet's application circuit, a
  screenshot from another tool) or a *layout* (a board or die picture), and either way the result is a schematic.
- **Create Layout from Image** — a layout picture becomes layout geometry.
- Best effort, like the artwork command: the user cleans up a little afterwards.
- **Reuse the Create Schematic from Artwork dialog**; the image is dropped onto it or browsed for (`.png` and the
  other common bitmap formats).
- **Right-click a placed image** (the schematic and layout bitmap primitives) to run the command on it.
- **Project Tree:** an image under Known Files offers the command in its context menu.
- **Edit ▸ Paste Image as Schematic… / Paste Image as Layout…** take the system clipboard's picture; greyed out
  when the clipboard holds no bitmap.
- **Graceful failure**: many pictures will hold no schematic or layout at all.
- **CLI and MCP** as well.
- **UX is the priority**: close to drag-and-drop for most users, with the conversion tweakable by advanced users.

---

## 1. Is it feasible? (the answer the series is built on)

**Yes, for the pictures most users will actually have, and with no machine learning and no new native dependency.**

| Picture | Expectation | Why |
|---|---|---|
| A layout rendered by a tool (a Gerber viewer or layout editor screenshot, a die plot, a figure in an application note) | **Good.** Copper comes back to within ½ pixel; the rest is the artwork pipeline, which already works. | Flat colours, sharp edges, rectilinear geometry. Colour separation + sub-pixel contours + snapping is classical, well understood image processing. |
| A machine-drawn schematic (a datasheet circuit, another tool's screenshot, a circuitRF export) | **Good topology, partial values.** Wires, junctions and the common RF symbols read reliably; designators and values read when the text is clean, and become tunable variables when not. | One stroke width, straight orthogonal wires, a small symbol vocabulary. Text is the weak part, and the artwork series already decided what to do with an unknown value (a global variable with a `tune` entry). |
| A photograph of a real board | **Partial at best** — optional phase IM-13. | Perspective, glare, solder mask over the copper, parts covering their pads. |
| A hand-drawn schematic | **Not a goal.** It may work on a neat drawing; it is not tested. | Lines are not straight, symbols are not standard. |

What makes it cheap is that **the hard back halves already exist**:
- A layout picture → (new) traced copper → the **existing** artwork recognition → schematic.
- A schematic picture → (new) a `TestBench` → the **existing** `NetlistSchematic.Build` → `.csch`.
- An unknown value → the **existing** global-variable-plus-`tune` rule (AS D10), so a picture whose text could not be
  read still gives a circuit the user can tune straight away.
- Text → the **existing** stroke-glyph matcher (AS-10), fed with the centre lines of a skeletonised picture instead
  of a Gerber pen's.

The one thing an image never states is **scale**. A layout picture has no millimetres in it; D6 treats that the way
the repo already treats an unstated Excellon format — never a silent guess.

---

## 2. The phases

| Phase | Brief | What it delivers | Depends on |
|---|---|---|---|
| IM-1 | `brief-img-1-raster-core.md` | `src/Design/Imaging/`: decode, colour separation, coverage, threshold, components, skeleton, contours, segments, circles; the design note | — |
| IM-2 | `brief-img-2-image-source-and-kind.md` | `ImageSource` (file, clipboard bytes, placed bitmap), what kind of picture it is, the refusals, where the picture is kept | IM-1 |
| IM-3 | `brief-img-3-trace-layout-image.md` | A layout picture → layout shapes on technology layers: scale, layer mapping, polygons, vias, the underlay | IM-2 |
| IM-4 | `brief-img-4-layout-image-to-schematic.md` | Traced layout → the existing artwork recognition; silkscreen read from the picture | IM-3 |
| IM-5 | `brief-img-5-dialog.md` | **The dialog** — the artwork dialog generalised to a source pane; image canvas with overlay; scale and layer tools; drop, browse, paste | IM-4 |
| IM-6 | `brief-img-6-entry-points.md` | Design menu, Edit ▸ Paste Image as…, right-click on a placed bitmap, Project Tree image files | IM-5 |
| IM-7 | `brief-img-7-schematic-wires-and-regions.md` | A schematic picture → wires, junctions, crossings, text regions, symbol regions | IM-2 |
| IM-8 | `brief-img-8-schematic-symbols.md` | Symbol regions → component kinds, orientations and pins | IM-7 |
| IM-9 | `brief-img-9-text-and-values.md` | Designators, values, net labels and port names read from the picture; association; teaching | IM-7 (IM-8 for association) |
| IM-10 | `brief-img-10-schematic-emit.md` | The circuit, an "as drawn" drawing, the underlay, ports; the dialog's schematic-picture mode | IM-8, IM-9, IM-5 |
| IM-11 | `brief-img-11-cli-and-mcp.md` | `convert` reads images; `recognize` reads images; the overlay picture; MCP | IM-4 (layout half), IM-10 (schematic half) |
| IM-12 | `brief-img-12-acceptance-docs-example.md` | Round trips through circuitRF's own renderer and through an independent drawer; user pages; example | IM-6, IM-10, IM-11 |
| IM-13 | `brief-img-13-board-photos.md` | **Optional, late.** Photographs: perspective, white balance, solder-mask separation | IM-3 |
| IM-14 | `brief-img-14-sparameter-plots-to-touchstone.md` | S-parameter plots (rectangular, Smith, polar) → Touchstone: axes, traces, markers, missing phase and entries, the plot mode of the dialog, CLI/MCP | IM-2, IM-5, IM-9, IM-11 |

**Ordering is UX-first.** IM-1 → IM-6 is a complete, shippable feature for layout pictures (to a layout and to a
schematic) with every entry point the owner asked for, so the owner can use and judge the UX before the schematic-
picture reader exists. IM-7 → IM-9 can run in parallel with IM-3 → IM-6. The layout half of IM-11 can land any time
after IM-4.

---

## 3. Locked decisions

### D1 — No machine learning, no native dependency, all in-house
Decoding goes through **SkiaSharp**, which is already below the firewall (`SKCodec`: PNG, JPEG, BMP, GIF first
frame, WebP). Every algorithm is written in-house under MIT: colour clustering, adaptive threshold, connected
components, distance transform, skeleton, marching squares, line and circle fitting, template matching. No trained
model is shipped. The symbol classifier and the text reader each sit behind an interface (`IImageSymbolClassifier`,
`IImageTextReader`) so a later extension can add one without touching the pipeline. ◆ **Owner: in-house only.** An
operating system's own text-recognition service is **deferred**, not rejected — `IImageTextReader` is where it would
plug in; a native OCR library would need the owner's approval as a native dependency.

### D2 — One implementation, below the firewall
As AS D2. Everything that decides anything lives in `src/Design/Imaging/` (pixels → geometry, no circuit knowledge),
`src/Design/Layout/Recognition/Image/` (picture → layout) and `src/Design/Schematic/Recognition/` (picture →
circuit). `src/Ui` holds the dialog, the overlay canvas and the entry points only. The GUI, the CLI and the MCP tool
call the same entry points, and IM-11's gate is byte identity between them.

### D3 — The pipelines
```
picture (file │ clipboard │ placed bitmap)
   └─ IM-2  ImageSource → decoded, oriented, flattened onto white; kind = schematic │ layout │ none
        │
        ├─ layout picture ── IM-3  trace → LayoutShapes on technology layers (+ the underlay)
        │                        ├─ Make Layout    → .clay  (new cell, or into the layout the bitmap sits in)
        │                        └─ Make Schematic → IM-4 → RecognitionInput → ArtworkRecognition (AS-3…AS-6, unchanged)
        │
        └─ schematic picture ─ IM-7 wires/regions → IM-8 symbols → IM-9 text → IM-10 TestBench → NetlistSchematic.Build
```
**A schematic picture does not make a layout directly.** That is Create Schematic from Image followed by the existing
Update Layout from Schematic (L5); the dialog says so in one line when the user picks Layout for a schematic picture.

### D4 — The picture is kept with the result, never embedded
Documents reference images by path (the bitmap primitives' rule). The source picture is **copied into the target
cell folder** as `<cell>.source.<ext>` (a pasted picture is written as PNG), and the provenance records the original
file name — not its path (no personal paths in a workspace) — its SHA-256, its pixel size and the options used. A
re-run compares the hash.

### D5 — The result sits on its source
The written schematic or layout carries the source picture as a **locked underlay at 35 % opacity** — an
`EditableBitmap` behind the schematic, a `BitmapShape` behind the layout — placed so every recognised symbol, wire and
copper shape lies on the thing it was read from. Cleaning up is then comparing in place, not flipping between
windows. The underlay is an ordinary bitmap: the user hides, fades or deletes it like any other; a bitmap is already
excluded from export, DRC and EM. A setting (Settings ▸ Recognition ▸ *Keep the source picture under the result*,
default on) turns it off.

### D6 — Scale is never a silent guess ◆
A layout picture needs metres per pixel, and a wrong one is off by orders of magnitude while looking fine.
Evidence, strongest first:
1. **The placement of the bitmap it came from**, when the command was started on a placed `BitmapShape` and the target
   is *that* layout — tracing onto the picture where it lies is what the gesture means.
2. **Two points the user picked and a distance they typed**, with an SI unit (a bare number is refused, as `render
   --window` refuses one).
3. **Parts on the picture**: two-pad land patterns matched against `SmtCaseTable` at a common scale; the scale is
   *inferred* when at least three pairs agree within 3 % (the AS-4 matcher, run over a scale search).
4. **A line of stated impedance**: the user clicks a trace and types its Z0; the technology gives W.
5. **The file's resolution metadata** — offered, never chosen: a screenshot's 72/96/144 dpi describes a screen.

◆ **Owner: an inferred scale is an acceptable default in the GUI, provided it is one click away from being changed.**
The GUI fills the scale from the strongest evidence it has and says which; with only (5) or nothing, **Create waits**
and the scale row is the one highlighted. The CLI needs `--scale`, and `--scale auto` is an explicit answer that is
refused (listing the candidates it found) when (3) is not met.

### D7 — Layers come from colour, mapped by the user
A layout picture is separated into at most eight colour clusters (CIELAB, deterministic k-means, clusters closer than
ΔE 10 merged). Each cluster is a row: swatch, share of the picture, and a layer — a technology layer, *Drill*,
*Board outline*, *Silkscreen*, or *Ignore*. Defaults: the cluster touching most of the picture's border is
*Background*; the rest map to the technology's copper layers in order of area; a single-colour drawing maps to the top
copper. A mapping can be **saved as a preset** (user state, not the workspace), because pictures from one source
always use the same colours. CLI `--layer-map "#c87533=TOP,#2e8b57=ignore"`.

### D8 — The technology is the user's
A picture says nothing about the stackup. The traced layout and the recognised schematic name the technology the user
chose — the workspace default unless changed — and the report says once that the stackup was not read from the
picture (AS §14's "the stackup is the user's", louder).

### D9 — What a schematic picture can hold
Recognised: **R, L, C, ground, port / terminal / connector, transmission line** (a box or a coax drawing), **short
and open stubs**, **diode**, and **multi-pin devices** (a transistor, an amplifier triangle, an IC box) — which are
**cut out as in AS D9**: each pin on a wire becomes a Port, so the result is the networks around the device as the
device sees them. A two-pin symbol nothing matches becomes a part of kind `?` (generated as C, as AS does). Both the
US and IEC drawings of R and L, and the common variants of C and ground, are templates (IM-8).

### D10 — Text
Designators, values, net labels and port names only — never prose. The reader is the AS-10 stroke matcher on the
picture's skeleton, with the Hershey glyph set widened to lower case, `.`, `/`, `=`, `°`, `µ`, `Ω`, and a **grammar**:
a word is accepted only as a designator, a value with its multiplier and unit, a net name or a port name. A value that
does not read becomes a **global variable with a `tune` entry** (AS D10, same names `<Refdes>_<Param>`, same
transparent initial values). **Learn These Glyphs** (AS-10) works on picture text too.

### D11 — Wire conventions
A T-junction always connects. A four-way crossing connects only where there is a **junction dot** (option: *Never* /
*With a dot* (default) / *Always*). Two wires with the same net label connect. A wire end within half a stroke gap of a
pin connects. Dangling wire ends are listed.

### D12 — The drawing keeps the picture's arrangement ◆ (owner: yes)
The schematic is drawn **as the picture is drawn**: each symbol at its position and rotation (snapped to the grid),
each wire along its route (snapped), at a scale where the median recognised pin span equals the symbol's own. This is
`NetlistSchematic.Build` with **exact** placement hints (it already takes ordering hints from AS-6) — not a second
schematic writer. *Tidy* (the existing automatic arrangement) is the alternative, one combo away.

### D13 — Ports and analysis
Ports come from port symbols, terminal circles, connector symbols, and net labels that name a port (`RFIN`, `RF_OUT`,
`IN`, `OUT`, `P1`…); else from wires that end, dangling, at the left and right extremes of the drawing. **A schematic
picture with no port is not refused**: the schematic is written without its S-parameter analysis and the report says
so — unlike artwork, a drawn circuit is useful before it has ports. The analysis, when written, is AS D15's.

### D14 — Where the result goes ◆ (owner: over the placed bitmap)
- **Default: a new cell**, name prompted, default from the file name (sanitised by `NameValidator`), else
  `pasted_image_<n>`. A layout picture made into a schematic writes **both** views into that cell — the traced
  layout and the schematic — so AS-8's Show in Artwork works on it and the underlay is on both.
- **Started on a placed bitmap**: the default target is the document it sits in, **over the bitmap** — the traced
  copper into that layout, or the recognised schematic into that schematic beside nothing but its picture. It is one
  undo step through the editor's own commands, not a file write, so AS D4's "never replace" rule is not engaged.
- AS D4's replace rule is unchanged for every file write.

### D15 — CLI and MCP: no new verb
- **Picture → layout is an import**: `convert` gains an image reader (`--from image`, inferred from content), so
  `convert board.png board.clay` and every other target format (`.gds`, Gerber, …) fall out of `convert`'s one-import,
  one-export rule.
- **Picture → schematic is a recognition**: `recognize` takes a picture (`--image-kind auto|schematic|layout`).
- Both take the dialog's options as flags; `--overlay out.png` writes the annotated picture; the MCP tools return the
  overlay as image content, so an agent with vision **sees what was read** and corrects it through the parts CSV or by
  editing the written `.cnl` — the format is the contract.

### D16 — Graceful failure
A picture is refused only when: it cannot be decoded (the refusal names the formats read, and says *export it as
PNG*); it is smaller than 64 px on a side; **nothing in it is a drawing** (IM-2's kind is *none* — the sentence says
what was seen: *a photograph-like picture with no straight line work*, *a page of text*, *a blank picture*); a layout
picture has no scale (D6); or nothing at all was recognised. Each refusal says what to do next. In the GUI the user may
override the kind (*Read it as a schematic anyway*). **Everything else is a finding**, reported with a count (AS D16).
Pictures above 50 megapixels are reduced to 50 with a note.

### D17 — Determinism
The same picture with the same options gives the same bytes on every platform: seeded clustering, no
order-dependent parallelism, no system font in any decision. IM-11's GUI-vs-CLI byte identity and IM-12's committed
fixtures depend on it.

### D18 — Non-goals for this series
Hand-drawn schematics (not tested), vector sources (PDF, SVG, EMF — a much better source than their rasterisation, and
a different series), multi-page documents, handwriting, any trained model, stackup inference, coupled lines (as AS
D18), placing an imported device model into the recognised schematic, and — until IM-13 — photographs.

---

## 4. UX principles

- **One gesture to a result.** Drop a picture on the dialog (or paste, or right-click a placed one): it is read at
  once with every option on Auto, the overlay shows what was read, and **Create** is the only click left — unless the
  picture is a layout with no scale evidence, in which case the scale row is the one thing asking.
- **The picture is the centre of the dialog.** What was read is drawn over it (wires, parts, text, copper by layer,
  unknowns in amber). Clicking an overlay item selects its row; selecting a row highlights its overlay item.
- **The artwork dialog's parts, not a second dialog.** Target, parts table, report strip, options row and the
  Export/Import Parts buttons are the AS-8 controls, extracted and shared (IM-5).
- **Advanced is an expander, never a wizard.** Threshold, minimum feature, snap angles, crossing rule, clustering,
  presets — collapsed by default, each re-running the read live (debounced, cancellable).
- **No explanatory prose under readouts.** Values, a short status, a note only where there is no value.
- **Failure is shown on the picture.** A picture with nothing in it is shown dimmed with one sentence and the
  override links — never an error dialog.

---

## 5. Testing rules and housekeeping

As the artwork series (`brief-artsch-0-overview.md` §4, §5), plus:
- **Pictures for tests are generated in memory** with SkiaSharp wherever a claim allows; the few committed picture
  fixtures (IM-12) are small PNGs produced once by a generator in `tests/` using **only fonts circuitRF ships**, so
  every platform reads the same bytes.
- **No screenshot of any commercial tool is ever committed**, and no fixture, folder or test name may say where a
  field picture came from. Field pictures live under the git-ignored `testdata/image-sources/` as `FixtureFact`s.
- Image-processing claims are asserted on **geometry and counters** (an edge within ½ px, a count of components),
  never on time.

---

## 6. Owner decisions (2026-10-10, paraphrased)

1. **D1** — Text is read in-house only. Operating-system text recognition is deferred for now.
2. **D6** — A scale inferred from agreeing land patterns may be the GUI's default, as long as changing it is one click
   away (the scale combo, R-im5-4). The CLI keeps `--scale auto` as an explicit answer.
3. **D12** — The schematic is drawn as the picture draws it; *Tidy* stays an option.
4. **D14** — Started on a placed bitmap, the result is drawn over the top of that bitmap, in the same document, as one
   undo step.
