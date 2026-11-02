# Brief 70 — the showcase: a connector launch

**Series:** [3D EM, fourth series](brief-em3d-60-overview.md) · **Tag:** `R-em3d70-n` ·
**Precedent:** [brief 52](brief-em3d-52-showcase.md) (the `3D Package` workspace: drawn with the editor's own
operations, one numbers file quoted by the README and the user page, a test that holds all three together) and
[brief 30](brief-em3d-30-showcase.md)
**Area:** `examples/3D Connector/` (new), `examples/examples.json`, `docs/user/src/reference/drawing-in-3d.md`,
`docs/user/src/reference/em-solvers.md`, `docs/user/src/reference/file-formats.md`,
`docs/user/src/reference/cli.md`, `docs/user/src/new-user-guide/`, the figure catalogue,
`tests/Ui.Tests/Examples/`
**Depends on:** all of 61–69

---

## 0. What this brief delivers

The series' four capabilities in one example a newcomer can open, and the user pages that teach them through it:

- a **connector launch**: a coaxial connector body meeting a 50 Ω microstrip at a board edge — the most common
  3D problem an RF board designer has, and one that needs every feature this series adds:
  - a **metal housing with a bore subtracted** from it (Boolean *Subtract*), the bore **kept** as the
    dielectric fill (*Keep tools*, overview §1i — the case em-3d.md §6.3a could only state with an air-role
    trick until now);
  - a **centre pin whose tip is filleted** — a fillet on a **curved** edge;
  - a **flange body imported from STEP** and united with the housing — a STEP part as a boolean operand;
- **two setups**, Palace and openEMS, on a tiny sweep, and the **fidelity notes** (brief 65) visible on both;
- **one A/B experiment**: the pin's fillet toggled with **Enabled**, and what each solver makes of it — the
  owner's question (*do the solvers respect fillets?*) answered with measured numbers, not asserted;
- the **user pages**: booleans, Edge mode, fillets and chamfers, STEP import and export, and what each solver
  sees of curved geometry.

**No third-party file is in it.** The STEP body is made by circuitRF itself (§1c), from a cell shipped beside it.

---

## 1. `R-em3d70-1` — the workspace

**`R-em3d70-1a`** `examples/3D Connector/`, registered in `examples/examples.json` and offered by *Tools ▸
Examples*. One technology in `tech/`, **named generically**: a PTFE-glass board laminate, copper, a brass-like
connector alloy (σ stated), PTFE (εr 2.1). No material brand, laminate product, connector maker, part number or
process name appears anywhere — CLAUDE.md, *Commercial Vendor References*. In particular the shipped
`src/Design/resources/technologies/` board technology is **not** reused, because its name carries a laminate
product name.

**`R-em3d70-1b` Cells:**

| Cell | View | What it is |
|---|---|---|
| **Board** | layout, mil | a short 50 Ω microstrip on the laminate, ground below, ending at the board edge |
| **Flange** | 3D, mm | the connector's flange body, drawn with the editor — the **source** of `flange.step` |
| **Launch** | 3D, mm | the board placed as a layout instance; the housing with its bore subtracted and kept as fill; the pin, filleted; `flange.step` imported and united with the housing; two ports; two setups |

**`R-em3d70-1c` The STEP body is circuitRF's own, and provably so.** `Flange/3d/` is exported by brief 69's
exporter to `Launch/3d/flange.step` (the copy brief 68 makes on import). A test re-exports `Flange` and compares
the shipped file **byte for byte except `FILE_NAME`'s time stamp** (brief 69 gate 1's rule). So the example can
never ship a STEP file of unknown origin, and a format change that alters the exporter's output fails loudly
rather than leaving a stale file. The README says where the file came from and why a real connector's STEP from
its maker would be imported the same way.

**`R-em3d70-1d` Drawn with the editor's operations**, as brief 52's package was: an authoring test
(`tests/Ui.Tests/Examples/Em3dConnectorAuthoring.cs`, skipped unless `CRF_AUTHOR_3D_CONNECTOR=1`, the
`Em3dPackageAuthoring` pattern) replays the gestures — Box, Cylinder, *Boolean ▸ Subtract…* with the dialog's
Tool/Blank and *Keep tools*, Edge mode and *Fillet…*, *Import STEP…* with its material table, *Boolean ▸
Unite…*, the Port tool — and writes the cells. The README lists those gestures in order. A hand edit later is not
forbidden; it is only not the origin.

**`R-em3d70-1e` Dimensions, stated once:**
- the coax section is dimensioned to **50 Ω by the closed form** Z₀ = (60/√εr)·ln(D/d) with the PTFE's εr; the
  pin and bore diameters are VARs (`pin_d`, `bore_d`), so the README shows the formula with the file's numbers;
- the pin's tip fillet radius is a VAR (`tip_r`), chosen so it spans **several** Palace elements and **about
  one** openEMS cell at the default grid — the point of the A/B (§3);
- the board's line width is the laminate's 50 Ω width, stated with the closed-form microstrip value the
  `Klopfenstein Taper` example's page already uses.

**`R-em3d70-1f` Ports.** The coax end is an **annular lumped port** (`Em3dAnnulus`, brief 22), which both
backends lower; the microstrip end is a lumped port from the line to ground, as the `3D EM` example's are.

## 2. `R-em3d70-2` — the numbers, measured once, quoted everywhere

**`R-em3d70-2a`** `expected-numbers.json` is the **one** source, in the `3D Package` file's shape: per setup and
per fillet variant, |S11| and |S21| at the band centre and at the top, the run time and peak memory on the
reference machine, the preset used, and the versions of Palace, Gmsh, openEMS **and OCCT** it was measured with.
The README and the user page **quote each value's text**; a test fails when they disagree.

**`R-em3d70-2b` Presets are chosen by measurement, and the setting traded away is documented.** The example is
opened by newcomers, so its first Simulate must finish in minutes on a laptop: a tiny sweep (a handful of
points over the connector's band) and the coarsest preset that still separates the two fillet variants. **For
every setting chosen for speed the README states the alternative and its expected numbers** — the Palace preset
one step finer and its S11, the openEMS grid one step finer and its S11 and cell count, the full sweep and how
long it takes. A user who asks "is this converged?" reads the answer beside the number.

**`R-em3d70-2c` External checks, labelled as external.** Two numbers on the page are not circuitRF's own output:
the coax section's closed-form Z₀ against the impedance Palace reports at the annular port's reference plane,
and the microstrip's closed-form width. The page says which numbers are references and which are solver output,
as brief 52 §2a does.

## 3. `R-em3d70-3` — the fidelity A/B: do the solvers respect a fillet?

**`R-em3d70-3a`** Both setups are run twice: the pin's `Fillet` **Enabled**, then disabled (overview §1f —
disabled is "as if absent"). `expected-numbers.json` records S11 at the top of the band for all four runs.

**`R-em3d70-3b` What the page must show, whatever the numbers turn out to be:**
- **Palace:** the fillet is meshed as a curved surface (second-order elements, curvature sizing from brief 65);
  the S11 difference between the variants is stated;
- **openEMS:** the run's note (brief 65) says how many cells span the fillet radius; the S11 difference is stated
  — and if it is **smaller than the grid's own refinement spread** (§2b's one-step-finer run), the page says the
  staircase cannot resolve this fillet at this grid, which is the honest answer to the owner's question;
- the **Setups panel's pre-run warning** for openEMS, as a figure.

This section is measured, not predicted. If Palace also cannot tell the variants apart at the connector's band,
the page says the fillet is electrically negligible here and the example still demonstrates where each solver's
resolution sits — the figure is not rigged by exaggerating `tip_r`.

**`R-em3d70-3c` Agreement.** Palace and openEMS S21 are compared as the `3D EM` example compares them (em-3d.md
§4.4), with the difference stated and the cause named where brief 65's notes name one.

## 4. `R-em3d70-4` — the user pages (doc sources only)

**DocGen is not run by this brief** — the owner regenerates `docs/user` at the end of a series. The
figure-existence gate is red until then, recorded as briefs 30 and 52 recorded it.

**`R-em3d70-4a` `drawing-in-3d.md` (the 3D Editor chapter)** gains, in its existing structure:
- **Booleans** `{#booleans}` — select, right-click, the dialog; **the first-selected object is the Tool**, one
  row is the Blank, Swap, *Keep tools*; the result takes the Blank's name and material; the tree node, its
  operands, **Enabled**; editing an operand; why a boolean result's faces cannot be pushed directly (D13) and
  what to edit instead;
- **Edges, fillets and chamfers** `{#fillets}` — Edge mode (**E**), tangent chains, *Fillet…* and *Chamfer…*,
  radius as an expression, the tree rows and **Enabled**;
- **STEP** `{#step}` — import (units, the material table, *Map all of this colour*, what is not imported and
  why, the copy into the cell, *Reload from Source*) and export (flattened or as an assembly, precedence applied,
  units, the air box option);
- **The example** section updated to point at both examples;
- **Keys at a glance:** **E** Edge mode; *Extrude face* now **Shift+E** (D6);
- **Headless:** `convert x.step -o y.c3d`, `convert x.c3d -o x.step`, and the `Boolean`/`Fillet`/`Chamfer`/
  `Step` objects being written by hand from the reference page.

**`R-em3d70-4b` `em-solvers.md`** gains **What each solver sees of curved geometry**: Palace meshes the exact
surface with curved elements, limited by the initial mesh; openEMS staircases to its grid, and a feature under
about one cell is not represented; the notes and warnings that say so, where they appear, and what to change
(finer grid near the feature, or Palace). The A/B's numbers are quoted here.

**`R-em3d70-4c` `file-formats.md`** — the four new `.c3d` object kinds, the face and edge naming rules
(overview §1g), and the `Hash` rule; **`cli.md`** — `convert`'s two STEP directions.

**`R-em3d70-4d` The newcomer walk-through** (`new-user-guide/`): one short section — *open the 3D Connector
example, Simulate, then untick the pin's fillet and Simulate again* — so a first-time user meets booleans,
fillets and the fidelity note in ten minutes without drawing anything.

**`R-em3d70-4e` About box and third-party notices** are brief 62's; this brief only links to them from the
STEP section (*the geometry kernel is Open CASCADE Technology*), the prominent notice the exception asks for
being already in place.

**Figures** come from `render` sections of the `.c3d` and from the Metal offscreen path where a perspective
picture is needed (brief 52's sources), registered in the figure catalogue.

## 5. Gates

`tests/Ui.Tests/Examples/Em3dConnectorExampleTests.cs`. Tests that need the worker are `[KernelFact]` and skip
with the reason when it is absent.

1. **Opens and checks clean.** `check` on the workspace: 0 errors; warnings listed and justified in the README.
2. **Elaborates.** The Launch problem holds the expected kernel solids (the housing-minus-bore united with the
   flange, the kept fill, the filleted pin) with the Blank's names and materials; the bore fill is a dielectric;
   `face<n>`, `fillet(...)` and `<tool>:<face>` names the ports use all resolve.
3. **The STEP file is circuitRF's own** (§1c): re-exporting `Flange` matches the shipped file except the time
   stamp; its `Hash` in the `.c3d` matches its bytes.
4. **Numbers agree.** README, user pages and `expected-numbers.json` quote identical text.
5. **Disabled means absent.** With the pin's fillet disabled the elaborated pin is the plain cylinder, and the
   lowered Palace and openEMS inputs equal those of the same document with the `Fillet` object removed.
6. **The solve** (`Category=Benchmark`): each setup and variant reproduces `expected-numbers.json` within its
   stated tolerance, and the openEMS run carries the fidelity note naming the pin.
7. **Registered.** `ExampleWorkspacesTests` lists the folder; the name gate passes over it by the existing
   mechanism, without listing the names it forbids.

## 6. Owner check — the series walk-through (overview R-em3d60-4)

In the **Debug** build, with no terminal at any step:
1. open the 3D Package example; draw a cylinder through the lid;
2. select the cylinder then the lid; right-click ▸ *Boolean ▸ Subtract…*; confirm the cylinder is the Tool; OK;
   see the hole;
3. in the tree, select the boolean; untick **Enabled**; see the cylinder back; tick it again;
4. press **E**, pick the lid's top edge around the hole, *Fillet…* 50 µm; see it round;
5. *Import STEP…* a connector body; map its parts to materials;
6. Simulate in Palace, then in openEMS; read the fidelity note on the openEMS run;
7. *Export STEP…*; open the file in any STEP viewer;
8. then open *Tools ▸ Examples ▸ 3D Connector*; run the A/B from the page's instructions alone.

Record the elapsed time of each step, and anything that did not feel immediate. Pixels were not seen by the
agent that built this brief; the completion note says so.

## 7. Scope

- **One new example workspace.** `3D EM` and `3D Package` are unchanged, and their numbers do not move
  (overview gate `R-em3d60-3`).
- **No third-party geometry, no vendor names** — in cells, technologies, materials, STEP product names, figures
  or pages.
- **Keep EM runs short**: the example's own presets are the smallest that separate the variants; anything longer
  is the README's documented alternative, not the default.
- Findings go in `examples/RESOLVED.md` and the relevant `src/*/RESOLVED.md`, never `CLAUDE.md`.
