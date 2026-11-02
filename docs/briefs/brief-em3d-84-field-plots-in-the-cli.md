# Brief 84 — Field plots in the CLI: `circuitrf render <c3d> --field <plot>`

**Tag:** `R-em3d84-n` · **Design notes:** [`cli.md`](../design/cli.md) §13 (`render`), §13.8 (a 3D setup's sections);
[`em-3d.md`](../design/em-3d.md) §8.4
**Area:** `src/Cli/Render.cs`, `src/Cli/RenderEm3d.cs`, `src/Render/Scene3D/Fields/` (a new headless field picture),
`src/Render/Renderers/Em3dSection*.cs`, the plot-resolution code moved out of `src/Ui/Viewer3D/Viewer3DViewModel.Plot.cs`,
`src/Cli/CliDiagnostics.cs`, `docs/design/cli.md`, `docs/user/src/reference/cli.md`, `tests/Ui.Tests/Render/`
**Depends on:** 83 (field plots in the document), 5 (a 3D setup's sections and iso outline), 29 (fields), 82 (fields on faces)
· **Blocks:** —

---

## 0. Why, and what is already there

Brief 83 made a field plot a record of the `.c3d` (`C3dDocument.FieldPlots`): its setup (pinned), its solution **by
value**, its quantity, where it is drawn, its colour scale. Its §8 left one thing out on purpose: *"Field plots in the CLI
(`render --field`) — the document now carries enough to do it; brief of its own."* This is that brief.

Today `circuitrf render x.c3d -o out.svg --section z=35um` (or `--iso`) draws the elaborated problem as a section or an
isometric outline (`RenderEm3d` → `Em3dSectionScene` → `Em3dSectionRenderer`, CPU Skia, SVG/PDF/PNG). It draws no field. The
GUI's field picture is the GPU's (`Viewer3DViewModel.CapturePicture`), so it can't be made headlessly. That is also why the
documentation's four 3D figures are still placeholders ("cannot be made by the documentation's headless generator",
`em-3d.md`).

**What does NOT need writing.** Everything that turns a run into coloured triangles already lives below the firewall in
`src/Render/Scene3D/Fields/`: `FieldRun`/`FieldStep` (reading), `FieldSlicer` (a plane cut), `FieldSurfaces`
(region boundary, conductors), `FieldFacePainter` (brief 82's faces), `FieldColorScale.Auto` (the range, dB, percentile),
`ColorMap3D` (the maps), `FieldQuantity.Offered`/`Evaluate`, and `FieldPicture` (the legend painter used by Export
picture). **The rule `render` was built on applies unchanged: it owns no rendering and no field arithmetic** (`cli.md`
§13.1). A second slicer or colour scale in `src/Cli` would drift from the GUI without anyone noticing.

## 1. `R-em3d84-1` — the plot's resolution moves below the firewall, and the GUI calls it from there

Brief 83 put the plot's resolution in `src/Ui/Viewer3D/Viewer3DViewModel.Plot.cs`: `RunDirectories`, `Discover`,
`SolutionKey`, `PickSolution`, `PlotProblem` (the R-em3d83-5 sentences). Some of it sits in the editor too:
`PlotRequest`'s setup lookup (`C3dSetups.Read` → `C3dSetups.ForRun`, a missing setup's sentence) and `SceneFaces`'
`object/face` resolution. **Move all of it** to one framework-free class, e.g. `CircuitRF.Render.Scene3D.Fields.FieldPlotResolver`
(it needs `FieldRun`, so `src/Render`, not `src/Design`). The viewer and the editor then call it. `FieldPlotRequest`,
`FieldDiscovery` and `FieldSolutionItem` move with it (`FieldSolutionItem` is only a label and a run).

Gate: a comment-stripped source scan shows `src/Ui` defines none of `SolutionKey`, `PickSolution`, `PlotProblem`,
`RunDirectories` (the `AuthoringCliVerbTests` pattern). Brief 83's `FieldPlotTests` still pass **unchanged**. Those tests
are the proof that moving the code changed nothing.

## 2. `R-em3d84-2` — the verb

```
circuitrf render cavity.c3d -o cut.png --field Field1
circuitrf render cavity.c3d -o cut.svg --field Field1 --section xz@y=5mm
circuitrf render cavity.c3d -o wave.png --field Field1 --phase 90
circuitrf render cavity.c3d --list-fields
```

- `--field <name>` names a plot of the document (by name, exact; an unknown name is a refusal **listing** the plots, as
  `--view` lists views). It is a `.c3d` option only: on any other kind it is `RenderEm3dNotApplicable`'s refusal.
- **The view follows the plot unless the caller says otherwise.** A `ClipPlane` plot **is** a section: with no
  `--section`/`--iso` it is drawn as the section on its own axis at its own offset (the plot's DBU offset in metres,
  reported in `--json` as today's section is). A `--section` that disagrees with a ClipPlane plot's plane is a
  **refusal** naming both, never a silent re-cut. A `Surfaces` or `Faces` plot is **not drawn headlessly yet** (owner
  decision Q1, §3): it is a refusal saying so, naming the GUI's Export picture as the way to get it today. `--list-fields`
  still lists it.
- `--phase <deg>` (owner decision Q3), optional, default 0: the phase φ of an ANIMATED quantity (Re{v·e^jφ}), read as
  `FieldQuantity.Evaluate(ch, φ)` reads it. On a quantity that is not animated it is a refusal naming the plot's quantity,
  not a silent no-op. It is a render option, never written to the document: the phase stays view state there.
- `--list-fields` prints each plot: name, setup, solution (`Describe()`), quantity, target, and whether its data is there
  (the R-em3d83-5 sentence when not). Exit 0. `--json` gives the same as an array. This is how an agent finds out what it
  can draw without opening the file.
- **Hidden does not matter headlessly.** `Hidden` is the tree tick, i.e. which plot the GUI draws. `--field` names a plot, so
  a hidden plot renders the same as a shown one. Say so in `--help`.
- The **results root** is resolved exactly as `em` resolves it (the nearest `.cws`'s `results/`, else beside the
  document). There is one function for this; find it and call it. The run directory is
  `Em3dRunService.RunDirectory(root, C3dSetups.ForRun(setup, path), solver)`, the same as the GUI's.
- **Missing data is a refusal, exit 1, with the R-em3d83-5 sentence verbatim** (no run of the setup; a run that did not
  save the plot's solution, naming what it did save; a quantity the step no longer offers). Never a fallback to the
  nearest frequency, and never an outline presented as if the field had been drawn. **A stale run still draws** (as in the GUI),
  with a `note:` on stderr and in `--json` that the model has changed since that run. The comparison is
  `C3dPersistence.SerializeForRun` against the run's `document.c3d`, i.e. `RefreshFieldsStale`'s comparison. Move that
  comparison below the firewall too, so the GUI and the CLI make it in one place.
- `--detail`, `--layers`, `--hide-layers` keep their current meaning or refusal for a 3D document. `--theme` and
  `--variant` apply to the outline under the field. The field's colour map is the quantity's (`Viridis`, `CoolWarm` for
  a signed quantity, `Inferno` for temperature, which is `Viewer3DViewModel.FieldMap`'s rule; move it next to
  `ColorMap3D`).

## 3. `R-em3d84-3` — the picture

**Section (a ClipPlane plot, or an explicit `--section` matching it).** Today's `Em3dSectionScene` outline, with
`FieldSlicer.Slice`'s triangles on the plane under it, projected to the section's (u, v) and filled with Gouraud colours
from `FieldColorScale.Auto(q, [slice], plot.Db, plot.Percentile)` and the map. Draw order: fill first, then the region
outlines, then ports and box edges, so the geometry stays readable over the field. **Vector output stays vector.** In SVG
and PDF each triangle is a filled path. Skia's SVG device has no mesh gradient, so a triangle takes one colour: its value at
the centroid. Write that down as the one difference from PNG, which gets `SKCanvas.DrawVertices` with per-vertex colours.
Measure the SVG size on the cavity fixture and on the bond-wire example's slice, and report both. The thinning below is
chosen from those two measurements.

**Thinning (owner decision Q2).** When vector output would carry more slice triangles than a limit, the slice is
thinned for SVG/PDF only: merge neighbouring triangles whose centroid colours fall in the same map step (the eye cannot
tell them apart), never dropping area, never moving the outline. PNG is never thinned. Choose the limit from the
measurement above, name it as a constant with the measurement in its comment, and report `Triangles` AND `TrianglesDrawn`
in `--json` and on stdout (`1,904 triangles, drawn as 612`), so a thinned picture is never mistaken for the full one.
`--no-thin` draws every triangle whatever the size.

**Iso is deferred (owner decision Q1: sections ship first).** Surfaces and Faces plots need a depth-ordered iso picture,
which is its own brief. What goes there, so it is not lost: painter's algorithm on triangle centroid depth along
`Em3dSectionScene.Viewer`, under the visible outline; faces via `FieldFacePainter` (sides included); surfaces via
`FieldSurfaces.RegionBoundary`/`Boundary`; and a `--select <object>` flag for a Surfaces plot of a volume quantity (the
GUI's "solid selected in the tree", the one thing the document does not carry). This brief only refuses those plots
(§2).

**Legend.** `FieldPicture`'s legend (the plot name, the quantity and unit, the range, the solution's label), painted
beside the picture as Export picture paints it. `--no-legend` leaves it off. The legend's lines come from the same function
the GUI's legend uses (`FieldLegendLines`, moved with §1). An animated quantity's legend states the phase drawn
(`φ = 90°`); there is no "cycle on screen" line headlessly.

## 4. `R-em3d84-4` — the report

`--json`'s `Render.Em3d` gains `Field`: `{ Plot, Setup, Solver, Solution: <C3dFieldSolution as the file spells it>,
Label, Quantity, Mode, On, Triangles, Range: { Lo, Hi, Unit, Db, Percentile }, Stale: bool, Run: <run directory> }`.
Stdout adds one line: `Field1: |E| at mode 1 (5.73 GHz, Q 1.2e4), 1,904 triangles, 0 … 812 V/m`. The run directory is
part of the report because the walk is what a caller can't otherwise see (`explain`'s rule, `cli.md` §15).

## 5. `R-em3d84-5` — the MCP server and the documentation figures

- The MCP server's `render` tool already passes the verb's flags through. Check that `field` and `list_fields` reach it,
  and add them to the tool's schema description if it lists flags explicitly.
- The four `FIGURE PLACEHOLDER em3d-view-…` comments in `docs/user/src/reference/em-3d.md` become producible headlessly
  **if** each example's `.c3d`/`.cem` carries the plot the caption describes. Do **not** add example runs to the repo for
  this (they are large; brief 83 §"Fields are large"). Write the exact `circuitrf render … --field …` line each figure
  would take into its placeholder comment, and leave generating them to the owner.

## 6. Non-goals

- GPU parity: the headless picture is a section or an iso projection, not the view's perspective camera, lighting or
  transparency. The GUI's Export picture remains the way to get that picture.
- Iso pictures of Surfaces and Faces plots (owner decision Q1: a later brief; §3 records what it needs).
- More than one plot in one picture, and a plot on a plane that is not axis-aligned (brief 83's own non-goals).
- Vector arrows, streamlines, the far field.
- Rendering a `.cem`'s fields: a `.cem` has no plots. `render x.cem --field` is a refusal saying the plots live in a `.c3d`.

## 7. Gates

1. **Same slice as the GUI.** On the committed cavity fixture (`testdata/em3d/fields/cavity`), a ClipPlane plot rendered
   headlessly draws exactly the triangle count and colour range (`Lo`, `Hi`) that the GUI's viewer builds for the same plot
   (`FieldPlotTests`' harness, `Viewer.FieldGeometry` / `Viewer.FieldScale`). Read the counts from `--json`.
2. **Value, not colour.** At the slice's centre the PNG pixel, inverted through the map and range, reads TE101's |E|
   within the map's quantisation. At a wall E is tangential to, the pixel is the map's bottom colour.
3. **By value.** A run re-listed with one more frequency below the plot's (brief 83 gate 5's re-listing) renders the same
   bytes as before for a PNG (same step, same data).
4. **Missing data refuses.** A plot at a frequency the run did not save exits 1, and stderr carries brief 83's sentence
   verbatim, with the frequencies that were saved. No file is written.
5. **Stale draws, and says so.** Edit the model after the run: exit 0, `Stale: true`, a `note:` line.
6. **Refusals.** Unknown plot (lists the plots), `--section` disagreeing with a ClipPlane plot, a Surfaces or Faces plot
   (not yet headless), `--phase` on a quantity that is not animated, `--field` on a `.clay`, and `--field` on a `.cem`:
   each refuses with its own diagnostic, exit 1, and writes nothing.
7. **One copy.** The source scan from §1, plus a scan that `src/Cli` calls `FieldSlicer`/`FieldColorScale` only through the
   Render entry point (no slicing or range arithmetic of its own).
8. `--list-fields` on a document with two plots, one missing its data: both are listed, the missing one with its sentence;
   exit 0.
9. **Phase.** On an animated quantity, `--phase 0` and `--phase 90` give different PNGs, and the slice centre's pixel at
   90° reads `Evaluate(ch, π/2)` within the map's quantisation. The legend says `φ = 90°`.
10. **Thinning.** Forcing the limit low on the cavity slice: the SVG has fewer paths than triangles, `TrianglesDrawn <
    Triangles` in `--json`, the thinned picture covers the same area (the summed triangle areas agree to 1e-9), and
    `--no-thin` or a PNG draws every triangle.

Tests go in `tests/Ui.Tests/Render/`, launching the built CLI as `RenderCliVerbTests` does. Use committed fixtures only,
no timing test, and one test per claim. Run only the classes these gates add, plus brief 83's `FieldPlotTests` and
`RenderCliVerbTests`, never the whole suite.

## 8. Owner decisions (2026-09-28)

- **Q1.** Sections ship first. Surfaces and Faces plots are refused headlessly in this brief; the iso picture is a later
  brief (§3 records what it needs).
- **Q2.** A very large slice may be thinned for vector output, reported as triangles before and after (§3).
- **Q3.** An optional `--phase <deg>` exists for an animated quantity, default 0, never written to the document (§2).

## On completion

Findings go in `src/Cli/RESOLVED.md` (a "brief-em3d-84" heading), and in `src/Render/RESOLVED.md` for the moved resolver
and the headless field picture. Never in any `CLAUDE.md`. Documentation: `docs/design/cli.md` §13.8 gains the field
subsection. `docs/user/src/reference/cli.md`'s render section and `em-3d.md`'s field-plot bullet each get the
headless spelling. Edit the doc sources only; the owner regenerates `docs/user`.
