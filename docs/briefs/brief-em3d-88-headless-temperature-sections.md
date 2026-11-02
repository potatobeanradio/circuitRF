# Brief 88 — `render --field` draws a temperature section

**Tag:** `R-em3d88-n` · **Series:** found by brief 85, whose documentation figures (and brief 81's) could only be made by a
one-off patch to the renderer. It follows brief 84 (`render --field`), which refused temperature on purpose.
**Area:** `src/Cli/RenderEm3dField.cs`, `src/Cli/RenderEm3d.cs`, `src/Render/Renderers/Em3dSectionRenderer.cs`,
`src/Render/Renderers/Em3dSectionField.cs`, `src/Render/Renderers/Em3dSectionScene.cs`, `src/Render/Scene3D/Fields/`
(`FieldPlotResolver`, `FieldColorScale`), `src/Design/Thermal/` (the wires' chains), `src/Cli/CliDiagnostics.cs`,
`docs/design/cli.md` §13.8.1, `docs/user/src/reference/cli.md`, `docs/user/assets/fixed/README.md`, `tests/Ui.Tests/Render/`
**Depends on:** 84 (`render --field`), 75 (a temperature's range, D9), 77 (wires as 1D elements), 83 (field plots in the
document) · **Blocks:** —

---

## 0. The short answer

`circuitrf render view.c3d -o out.png --field <plot>` draws an EM field on a clip plane, and **refuses a temperature**: *"a
temperature is not drawn headlessly yet: its range spans the wires and the sweep, which only the 3D view draws"*. So every
thermal picture in the user documentation is a *fixed* PNG, made by a patch that is then thrown away
(`docs/user/assets/fixed/README.md`: *"reproducing them takes that same one-off setup, not a command"*). Brief 85 did this for
four more pictures, and it had to work around six things. This brief makes those six the renderer's own, so a thermal section
is one command and the fixed pictures can be re-made by anyone.

Surface and face plots (a 3D projection, not a section) stay out of scope, as brief 84 left them.

## 1. What stands in the way, precisely

Each of these was hit, in this order, by brief 85's harness on *Thermal Output Wires* (`Output.c3d`, setups `DcSweep` and
`RfHarmonics`, clip planes along a wire, across the six wires and in plan through the loops).

| # | Where | What happens | What a thermal picture needs |
|---|---|---|---|
| 1 | `RenderEm3dField.NotHeadless` | `plot.IsTemperature` is refused outright | drawn |
| 2 | `RenderEm3dField.Draw` → `Em3dSetupSource.ForThreeDView(path, setup)` | a thermal setup has no EM problem: *"Setup 'Output DcSweep' is a thermal setup … no EM solver takes it"*, so there is no section scene to draw under the field | the scene from the view's own elaborated geometry, whatever the setup's kind (the harness borrowed the view's EM setup, which only works when the view has one) |
| 3 | `FieldSection.Cut` → `FieldColorScale.Auto` | the range is **0** to the 99th percentile: an EM magnitude's rule. A temperature section of a flange at 85 °C read "0 … 342.6 °C" and spent a third of the map on nothing | `FieldColorScale.MinMax` — the true minimum and maximum, as the 3D view ranges a temperature (brief 75 D9), extended to the wires in the plane (item 5) |
| 4 | `Em3dSectionRenderer.Draw` | a conductor region is FILLED over the field (`field is null \|\| r.Role == Conductor`), right for an EM field (none inside a perfect conductor) | metals carry a temperature: outline them, do not fill them, when the plot is a temperature |
| 5 | wires | a bond wire is a 1D element: its temperature is the result's `wires.Twire:<wire>(s)` table, not the volume field. The section draws the wire's EM solid in a flat conductor colour, so the hottest thing in the picture looked like the coolest — the owner's first question on seeing it | each wire painted from its own T(s) on the plot's range: a wire lying in the plane as a strip along its chain, coloured per chain node; a wire crossing it as its section at the crossing, coloured by T interpolated there. This is the 3D view's rule already (the viewer colours a wire from the same table) |
| 6 | the page | ports, face labels (`zmax: PEC`), and a caption about the EM air box: all EM, all meaningless on a thermal picture — and the frame fills a third of the page (the legend column, the label bands, the caption) | for a temperature: no ports, the thermal boundaries instead of the air box's (a fixed-temperature face and its °C, a convection face, or nothing), no EM caption |

How the harness did item 5, for reference (it is in no repository): each wire's chain from `ThermalWireLowering.Chain(report,
sweep)` over the elaboration's `Wires` and their `Em3dSweep` solids; the chain's node points (`Points`, metres) against the
cube's values at the plot's solution point (`Solution.Point` is 1-based; the cube is `[sweep…, s]`, one value per chain node).
A wire is "in the plane" when most of its nodes lie within its radius of it. The extra triangles were appended to the field
layer's raster vertices before `Em3dSectionField` drew them.

## 2. What to build

1. **`R-em3d88-1` — the refusal goes, for a clip-plane temperature.** `render --field` draws a ClipPlane plot whose quantity
   is a temperature. Surfaces and Faces stay refused with today's sentence.
2. **`R-em3d88-2` — the scene without an EM setup.** The section scene is built from the view's elaborated geometry for any
   setup kind. Find how the 3D view builds its outline for a thermal setup and share that path; do not borrow another setup.
3. **`R-em3d88-3` — the range.** A temperature's range is `FieldColorScale.MinMax` over the slice and the wires drawn in it.
   The legend says "minimum … maximum", as the 3D view's does. Whether a picture should instead use the union across the
   SWEEP (so pictures of different points share one legend) is an owner decision (§4 Q1).
4. **`R-em3d88-4` — metals are not filled over a temperature.** Outline only. An EM field keeps today's fill.
5. **`R-em3d88-5` — wires from T(s)**, as in §1 item 5. Put the chain-and-table walk below the firewall, next to the wire table
   it reads; `src/Cli` only asks for it. If the 3D view's own wire colouring can be shared, share it: one rule, two callers,
   as brief 84 did for plot resolution.
6. **`R-em3d88-6` — a thermal page.** No ports; the thermal boundaries labelled on the frame; a caption naming the setup, the
   point and the plane.
7. **`R-em3d88-7` — the fixed pictures become reproducible.** `docs/user/assets/fixed/README.md`'s "Made from" column states
   the command for each of the six thermal pictures (Eight Fingers × 2, Output Wires × 4). Re-render them. They are expected
   to differ slightly from the committed ones, which were made by the patch: compare them by eye and report, do not
   gate on bytes.

## 3. Gates

1. `render --field` on a ClipPlane temperature plot of a committed thermal example, as a PROCESS, writes a PNG; the report's
   range is the slice's true min and max (checked against the field file's own values on that slice).
2. A wire crossing the plane: its section's pixels carry the colour of T(s) interpolated at the crossing (a counter on the
   drawn wire pieces, and one pixel read-back on a wire whose T differs from the mould around it by ≥ 50 K).
3. A conductor in a temperature section is not filled with its material colour (a pixel inside the flange carries the field's
   colour); an EM section's conductors are unchanged (the existing `render --field` gates pass unmodified).
4. A thermal page draws no port and no `PEC`/absorbing label.
5. Surfaces and Faces plots are still refused, with today's sentences.

## 4. Owner decisions

- **Q1.** The range: this point's min/max (what brief 85's pictures used), or the union across the setup's sweep as the 3D view's
  sweep slider holds it?
- **Q2.** Brief 85's pictures also carried **material labels** on each solid ("Mould compound (overmold)", "SiC die", …) and
  were **cropped** to the frame with the legend inset in its corner, by a script. The owner asked for both. Make either a
  `render` option (`--labels`, `--tight`), or leave them to the documentation's own post-processing?
- **Q3.** Surfaces/Faces headlessly (a 3D projection of the exposed faces) — its own brief, or not wanted?

## 5. Also found (not this brief's work unless the owner says so)

`circuitrf plot` cannot reach a wire's temperature along its length: the cube is named `wires.Twire:<wire>(s)`, and the trace
parser reads `(s)` as a call (*"No cube 's' in the result"*), with or without quotes. The per-wire T(s) curve — the natural
companion to these pictures — therefore has no headless spelling.

## 6. Scope

- No change to an EM field's picture.
- No new field arithmetic in `src/Cli` (brief 84's rule: `render` owns no rendering).
- Findings in `src/Render/RESOLVED.md` and `src/Cli/RESOLVED.md`; never `CLAUDE.md`. Doc sources only; DocGen is not run.
