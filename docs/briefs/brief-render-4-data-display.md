# Sonnet Brief — RND-4: rendering a `.cdd`

**Read `brief-render-0-overview.md` first.** Depends on **RND-1** (the project and the fonts) and
**RND-2** (the verb, its flags and its `--json` shape). This brief extends `render` to one more
document kind. It adds no verb.

**This is the hard half of the series, and R-rnd0-3 says so.** Read §2 before writing any code; the
first decision it asks for may be to stop.

---

## 1. What a `.cdd` render actually is

A `.cdd` is `DataDisplayConfig` — tabs, plot containers, traces, axes, markers and marker info boxes —
and **it holds no data**. Its traces name a source: a path, or the sentinel `run.npy`
(`DataSourceRef.Selected`) meaning "whatever dataset this document has selected". Rendering one is
therefore three jobs, not one:

1. **Resolve** the sources to `DataSet`s — `.npy` through `DataSetImporter`, Touchstone through
   `TouchstoneIO` + `DataSetBuilder.FromSnp`. This pair already exists headlessly: it is exactly what
   `circuitrf read` does (`cli.md` §11.4), and it is the same pair the GUI's own source library uses.
2. **Resolve each trace** against its cube — expression, slice, versus-axis, derived metric,
   network-parameter conversion, stability circle, loadpull contour.
3. **Compose and draw** — `PlotRenderer` + `AxesRenderer` + the marker and table renderers, laid out
   by the bounding-box fit `PlotExporter` already performs.

Job 1 is free. Job 3 is a file move. **Job 2 is the whole risk of this brief**, and §2 is about
finding out how big it is before committing to it.

---

## 2. The measurement this brief opens with, and the decision it feeds

**R-rnd4-1. Measure the closure of trace resolution before writing anything, and report it.**

What is known now, measured by `grep` and not assumed:

- `src/Ui/DataDisplay/Models` — 15 files. **Six name an Avalonia type**: `Axes`, `Marker`, `Plot`,
  `Misc`, `Trace`, `TraceLabeler`.
- `src/Ui/DataDisplay/Renderers` — 8 files, ~11,500 lines. **Three name one**: `AxesRenderer` (in a
  comment only), `PlotRenderer` (`Avalonia.Rect` and `Point`, in a viewport struct and its helpers),
  `RenderTheme` (`Avalonia.Media`, `Styling`, `Threading`).
- Across all of Models, ViewModels and Renderers the Avalonia surface is **17 `Rect`, 3 `Point`,
  1 `Media.Color`** plus theming, `Controls`, `Threading`, `Input`, `Platform.Storage` and
  `Data.Converters` in the outer view models. `Rect` and `Point` are value types with framework-free
  equivalents; the rest belong to the window, not to the drawing.
- `src/Ui/DataDisplay/ViewModels` — ~14,750 lines, **13 files naming Avalonia**. `TraceRowViewModel`
  is 3,738 lines of it and names exactly one Avalonia namespace (`Controls`).

So the shape of the answer is probably: **the models and renderers can come down; the view models
must not.** Trace resolution living inside `TraceRowViewModel` is the thing to check, because if it
does, the choice is between dragging a 3,700-line view model below the wall and extracting the
resolution from it.

**R-rnd4-2. Extract, do not drag.** If the resolution logic is entangled with a view model, the move
is to lift the *resolution* into `src/Render` as plain functions and have `TraceRowViewModel` call
them — the rule `brief-automation-3-authoring-verbs.md` established for authoring
(`WorkspaceCreate`, `CellCreate`, `ComponentImport`): **the extraction is the feature.** A CLI verb
that re-implements what a view model does diverges from it silently, and a plot that is subtly
different from the one on screen is the worst possible output of this series, because nobody can see
that it is wrong.

**R-rnd4-3. If the extraction is not bounded, stop and report.** R-aut4-3's clause, and it is meant
literally here. A `render` that handles `.csch`, `.csym` and `.clay` and refuses a `.cdd` with a
sentence saying why is a good outcome; a `src/Render` that pulled `DataDisplayViewModel` and
`DisplayWindowViewModel` below the firewall is not. **The measurement in R-rnd4-1 is the deliverable
even if no code follows it.**

---

## 3. The shape, once it is possible

```
circuitrf render <path.cdd> -o <out.svg|.pdf|.png> [--data <file>...] [--tab <name|n>] [--plot <n>]
```

**R-rnd4-4. A source that the document cannot resolve is a refusal naming `--data`.** R-rnd0-6. The
sentinel `run.npy` means "the selected dataset", and headlessly there is no selection and no window
to make one in — so a `.cdd` whose traces bind to it, with no `--data`, is refused *naming the
sentinel and the flag*, never rendered as an empty plot. **An empty plot is the single most dangerous
output in this brief**: it is a valid picture, it exports cleanly, and it looks exactly like a
measurement that came back empty.

`--data` may be repeated. A document referencing several sources by path resolves them relative to
the `.cdd`, as it does when opened; `--data` binds the sentinel and overrides a path that does not
resolve. A `--data` that binds nothing is a refusal, not a shrug.

**R-rnd4-5. Selection: `--tab` and `--plot`, and multi-page is PDF's alone.** Default is the whole
canvas of the first tab, which is what the document opens on. `--all-tabs` writes one page per tab
and is valid **for PDF only** — `SKDocument` is a multi-page format and SVG and PNG are not, so on
those it is a refusal naming `--tab`. Writing `out-1.png`, `out-2.png` from one `-o` is a filename
invented by the tool, which R-rnd0-6 forbids.

**R-rnd4-6. `--size` replaces the fixed page.** `PlotExporter` targets 792×612 pt landscape with 36 pt
margins and scales the whole composition uniformly to fit — good for a printed page, arbitrary for a
caller who wants a 2000-pixel-wide plot for a report. Keep the composition arithmetic exactly (it is
what makes a dragged marker info box land in the export where it sits on screen); make the page a
parameter. **The default stays 792×612 pt** so an unadorned `render` of a `.cdd` produces the byte-
identical file the GUI's own export does — which is §5.1's gate.

**R-rnd4-7. What of the GUI's chrome comes out, and what does not**, stated rather than discovered:
markers, marker info boxes, axis label strips and the file prefix are content and come out; selection
handles, hover states, the inspector and the tab strip do not. `PlotExporter`'s existing signature
already carries `showFilePrefix` and the container — mirror its decisions rather than making new ones.

The `--theme`/`--variant`/`--background` flags are RND-2's and apply here through `RenderTheme`,
which is the one file in the renderer set that reads the *application's* theme and must instead be
handed one (R-rnd4-1's list).

---

## 4. What this brief must not do

- **No new plot types, no new derived metrics, no new colormaps.** Whatever `.cdd` the GUI can draw,
  this draws; anything else is out of scope and refused by name.
- **No `.cdd` authoring.** `render` reads. Creating or editing a data display headlessly is the
  format's job (`automation-architecture.md` §4 — the formats ARE the interface) and there is
  deliberately no per-primitive edit verb in this CLI.
- **No re-running an analysis to fill a source.** If the `.npy` is not there, that is a refusal. The
  run verbs exist and a caller composes them.

---

## 5. Gates

1. **Byte identity against `PlotExporter`.** `render out.pdf` and `out.svg` on a fixture `.cdd` with
   a fixture `.npy`, at the default page size, match what the GUI's own **Export** writes — byte for
   byte, with the PDF metadata date excluded by name if the format writes one (RND-1 §5.2's
   precedent). This is the gate that makes R-rnd4-2's extraction provable rather than plausible.
2. **A trace of every kind renders identically.** One `.cdd` fixture per trace kind the resolution
   path supports — plain cube, expression, versus, derived metric, S→Z/Y conversion, stability
   circle, loadpull contour, summary table — each compared against the GUI's export. **A kind with no
   test is a kind that will drift**, and this is the list §2 measured the closure of.
3. **The unresolvable source is refused, not drawn.** A `.cdd` bound to the sentinel with no `--data`
   exits 1 with a diagnostic naming `--data`, and **writes no file** (R-rnd4-4).
4. **`--all-tabs` is a refusal on PNG and SVG**, and produces the right page count on PDF.
5. **The firewall row still passes.** `CircuitRF.Render.dll` references no Avalonia after this
   brief's moves — the assertion is RND-1's and this brief is the one most likely to break it.
6. **Whatever did NOT move is named in the test file's own comment**, so the next person reads the
   boundary rather than re-measuring it.

---

## 6. On completion

`src/Render/RESOLVED.md` and `src/Ui/DataDisplay/RESOLVED.md`, **never a `CLAUDE.md`**. Update
`docs/design/cli.md`'s `render` section and `docs/design/data-display.md` with where the resolution
logic now lives and why.

**Report, do not silently absorb:**
- **R-rnd4-1's measurement in full** — the closure of trace resolution, file by file, whether or not
  the rest of the brief was built. This is the finding this brief exists to produce.
- Every trace kind that could NOT be rendered headlessly, and what blocked each. A partial `.cdd`
  render that names its gaps is a fine shipped state; one that draws a blank where a stability circle
  should be is not.
- Whether the composition arithmetic survived being parameterised (R-rnd4-6) — a dragged marker info
  box landing in the wrong place at a non-default page size is the regression to look for, and it is
  invisible at the default.
