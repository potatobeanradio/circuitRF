# Brief IM-11 — Pictures on the command line and through MCP

**Series:** `brief-img-0-overview.md` (D2, D6, D7, D15, D16, D17) · **Tag:** `R-im11-<m>`
**Depends on:** IM-4 (the layout half), IM-10 (the schematic half) — the two halves may land separately
**Area:** `src/Cli/LayoutConvert.cs` (an image reader), `src/Cli/Recognize.cs` (picture input), `src/Cli/Serve/
ToolCatalog.cs` + `McpServer.cs` (tool parameters, overlay image content), new `src/Design/Imaging/ImageOverlay.cs`
(the annotated picture, drawn below the firewall), `docs/design/cli.md` (§26 extended, a new section for the image
reader), `tests/Ui.Tests/Cli/`

---

## 1. Goal

Everything the dialog does, headlessly, by the same functions, with no new verb — and an agent that can **see** what
was read.

## 2. Requirements

**R-im11-1 — `convert` reads pictures (D15).** `--from image`, also inferred from content (`DocumentKinds`, IM-2). The
reader is `ImageTrace.Trace`; it lands on a cell folder plus a technology like every other reader, so every target
format follows from `convert`'s one-import, one-export rule. Flags:
- `--scale <len>/px` (`25.4um/px`), `--dpi <n>` (stated by the caller, so it is an answer, not metadata),
  `--scale-points x0,y0,x1,y1=<len>` (pixel coordinates), `--scale auto` (parts evidence; refused below support 3, the
  refusal **listing every candidate** with its support and the flags that would answer it);
- `--tech <id|path>` (headless there is no open workspace; `convert`'s existing rule for writing a `.ctech` of its own
  applies when none is given, and a picture's default stackup is the workspace-creation default);
- `--layer-map "#rrggbb=<layer>|drill|outline|silk|ignore,…"`, `--preset <name>`; with neither, IM-3's automatic map,
  **printed** so the caller can pin it next time;
- `--region x0,y0,x1,y1` in pixels (`px` suffix required — the units rule of `render --window`);
- `--min-feature`, `--simplify`, `--snap-deg`, `--snap45 on|off`, `--underlay on|off`.
No scale → exit 1 with the D6 sentence. A `.clay` target is a directory (AUT-12).

**R-im11-2 — `recognize` reads pictures.** A picture path is accepted where a layout is; `--image-kind
auto|schematic|layout`. A layout picture takes every R-im11-1 flag plus every existing `recognize` flag (it is
`ImageRecognition`); a schematic picture takes `--crossings never|dot|always`, `--arrange drawn|tidy`,
`--connect x,y` / `--disconnect x,y` (repeatable, pixel points, IM-10 R-im10-8), `--threshold`, `--parts`/
`--parts-out` (pixel-coordinate CSV). Read-only by default, `-o x.cnl`, `--into new:<name>`, `--replace` — all exactly
as AS-7. A picture of kind *None* exits 1 with IM-2's sentence and the `--image-kind` flag that would override it.

**R-im11-3 — The overlay picture.** `--overlay out.png` (both verbs) writes the source picture with what was read drawn
over it — IM-5's overlay classes, unknowns in amber, each part labelled with its refdes and value, each port with its
number. Drawn by `ImageOverlay` in `src/Design/Imaging` with SkiaSharp and circuitRF's shipped font, so the GUI canvas
and the CLI draw the same marks from the same result (the canvas draws them live; the file is the same picture
flattened). Written even when the run is refused after reading, because that is when it is most useful.

**R-im11-4 — `--json`.** `result.recognize` gains `image` (kind, confidence, evidence, pixel size, reduction), `scale`
(chosen, evidence, candidates), `layers` (the map as applied), and for a schematic picture `symbols`, `words`
(read and unread with boxes), `crossings`. `convert --json` gains the same `image`/`scale`/`layers`.

**R-im11-5 — MCP.** The `convert` and `recognize` tools take the same parameters. When `overlay` is true (default for
a picture source) the tool result carries the overlay as **image content** (the route `render` already uses in
`McpServer`), alongside the JSON. The tool descriptions say, in one line each, how an agent corrects a reading: edit the
parts CSV and pass it back (`parts`), flip a crossing (`disconnect`), or write the `.cnl` itself — the format is the
contract. The server instructions' *Artwork to circuit* walk gains a *Picture to circuit* walk.

**R-im11-6 — No second route.** `src/Cli` owns no image processing: a comment-stripped source scan holds that
`src/Cli` names nothing in `CircuitRF.Design.Imaging` or the two recognition namespaces but the entry points, their
options and results, `ImageOverlay` and `PartsTableCsv` (AS-7's rule, extended).

**R-im11-7 — `cli.md`.** §26 gains picture input; a new section documents the image reader in `convert`, the scale
rules and the overlay. Exit codes unchanged: 0, 1, 130; never 2.

## 3. Not in this phase
A new verb. Batch processing of many pictures (a shell loop does it).

## 4. Gates (minimal tests, run only these classes)
The CLI run as a **process** against the in-process entry point, byte for byte (AS-7's gate shape):
- `ImageConvertCliVerbTests` — a generated layout picture → `.clay` at `--scale 20um/px` is byte-identical to
  `ImageTrace.Run`'s; → `.gds` too; no scale exits 1 with the sentence; `--scale auto` on a picture with two pairs exits 1
  listing the candidates; a bare-number `--region` is refused.
- `ImageRecognizeCliVerbTests` — a schematic picture's `-o x.cnl` is byte-identical to
  `SchematicImageRecognition.Circuit`'s; a layout picture's to `ImageRecognition.Circuit`'s; a blank picture exits 1
  naming `--image-kind`; `--overlay` is written on a refused read.
- `McpImageToolTests` — the `recognize` tool's result for a picture holds one image content item and the JSON.
- The R-im11-6 source scan.
