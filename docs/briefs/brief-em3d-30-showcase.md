# Brief 30 — the showcase: an example workspace and a user page

**Series:** [3D EM, second series](brief-em3d-20-overview.md) · **Tag:** `R-em3d30-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §1 (where 3D is the primary solver), §4.3, §4.6;
F0 findings Q1
**Area:** `examples/3D EM/` (new), `examples/examples.json`, `docs/user/src/` (a new page), the
generated reference pages for the new `.cem` fields, `tests/Ui.Tests/Examples/`
**Depends on:** every other brief in the series (each part below names its own) · **Blocks:** —

---

## 0. What this brief delivers

The thing the owner shows people. It is a shipped **example workspace** that anyone opens from
Tools ▸ Examples and runs, and a **user page** that says what 3D EM in circuitRF is for, how to get
Palace, and what the example demonstrates. It is honest about cost and accuracy: every number in it is
measured, and the settings that trade accuracy for speed say what they trade.

**Nothing here is posted anywhere** (overview §1a, R-em3d20-3). The page lives in the repository's own
docs and ships with the application.

---

## 1. `R-em3d30-1` — the example workspace

**`R-em3d30-1a`** `examples/3D EM/` is an ordinary workspace, authored with the ordinary tools and
documents (examples/README.md: nothing a user could not have drawn). It holds a technology with named
materials (series 1 brief 2) and these cells:

| Cell | What it shows | Why only 3D does it | Needs |
|---|---|---|---|
| **Bond-wire transition** | F0 case A's shape: a 1 mil gold wire, hexagonal section, wedge feet, from a `.wBond` | the reference kernel W is validated against; the viewer shows the surface current crowding at the feet | 21, 28, 29 |
| **Via through a plane** | F0 case B: microstrip → via → inverted microstrip | **circuitRF's planar solver refuses this geometry** (F0 Q1). The example's README shows the refusal and then the Palace answer | 21, 28, 29 |
| **Package RLC** | a small leaded package: pads, wires, leads, and a lid over a cavity | C and L matrices in seconds (22). The **lid's first cavity mode** from eigenmode (23), and whether it falls in band | 22, 23 |

A cell whose brief was not built is left out, and the README says nothing about it.

**`R-em3d30-1b` Each setup runs in minutes on a 16 GB laptop.** Use the `Draft` or `Standard` preset
(brief 21), whichever keeps the run under ~3 minutes on the F0 Mac, and use the smallest air box the
answer allows. **The README states, per cell (the standing practice: document the setting you traded
away):**
- the preset used, the wall time and peak memory on the reference machine (named);
- the expected key numbers (|S21| at a named frequency, the C diagonal, the first mode's f and Q);
- **what `Accurate` gives instead, and how long it takes**, from brief 21's measured table or a run of
  this cell.

**`R-em3d30-1c` Comparison where circuitRF has one.**
- The bond wire also has its **kernel W** setup in the same workspace, and the README gives both
  answers and the one-sentence reason they differ (F0 Q1: the terminal definition, 197 pH).
- The via has the **planar setup that refuses**, so a user sees the refusal themselves.
- Any openEMS setup (if built) is labelled as the cross-check (§4.4), not a reference.

**`R-em3d30-1d` Without Palace, it still teaches.** Opening the workspace needs no solver. Simulate
without Palace produces the refusal with *Install Palace…* (brief 24), and the README's first section
says that is the expected first step. *Show 3D* (brief 28) works before anything is installed, so the
model can be explored first.

**`R-em3d30-1e`** No results are committed (examples/README.md). A row goes in `examples.json` after
the existing EM examples.

---

## 2. `R-em3d30-2` — the user page

**`R-em3d30-2a`** A new page under `docs/user/src/` (reference guide or app notes; follow where the
existing EM setup page lives, `docs/user/*/em-setup.html`'s source):
1. **When to use 3D, and when not.** §1's table: planar and kernel W stay the default wherever they fit,
   and 3D is for what they cannot represent. §4.3's guidance on FEM versus FDTD, in the page's own
   words.
2. **Getting the solvers.** *Install…*, what it costs (measured times per platform from briefs 24 and
   26, stated with their machines), the ParMETIS sentence, Windows through the Linux subsystem, and
   uninstall. Also installing by hand, for anyone who prefers it: the manual section series 1 brief 6
   wrote, updated.
3. **Setting one up.** Solver, problem type, ports (lumped or wave), terminals, air box, presets, with
   the `.cem` keys and a link to the generated reference.
4. **Watching it run, and reading the answer.** The stages (brief 21), the summary, and where results
   land (§4.5).
5. **The 3D view.** Model, mesh, grid, fields and animation, and what the mesh or grid tells you about
   whether to trust the answer.
6. **Walking through the example**, cell by cell, with its numbers.

**`R-em3d30-2b` Figures.**
- Everything that can be drawn headlessly is: `render`'s section views of each cell (series 1 brief
  5) and Data Display plots, through DocGen's ordinary figure pipeline.
- **Viewer pictures cannot be generated headlessly** (overview §1e). The page has placeholders for
  them, named and captioned, and the owner fills them with *Export picture…* (brief 29 §5).
- Committing those PNGs is the owner's call. List the exact views and sizes in the completion note.

**`R-em3d30-2c` Doc sources only.** DocGen is not run in this brief (standing practice). The owner
regenerates at the end of the series.

**`R-em3d30-2d` No commercial vendor or product names** anywhere in the page or the example (CLAUDE.md).
Palace, openEMS and Gmsh are named because the design decided so (§7.1). Grep before finishing, and
report what was removed.

---

## 3. Gate

`tests/Ui.Tests/Examples/Em3dExampleTests.cs`, plus the existing `ExampleWorkspacesTests` (which picks
up the new folder).

1. **Index and disk agree** (existing test), and no path names a home directory (existing test).
2. **`check` is clean** on the workspace: 0 errors, and warnings only where the README explains them
   (the planar via's refusal is a *run* refusal, not a `check` error; confirm that and say which).
3. **Sizes fit.** `explain`'s size estimate for every 3D setup is under 75 % of 16 GB (brief 21's
   warning threshold), so no user on a 16 GB machine meets the warning in the example.
4. **Writers without a solver.** Every setup lowers to Palace config and `.geo` with no refusal, and
   needs no solver installed.
5. **The README's numbers.** *(Palace; Benchmark.)* Each cell's run reproduces the README's stated key
   numbers within the tolerance the README states. The README and the test read the numbers from one
   place: a small JSON beside the README that the test reads and the README cites. One source, so the
   two cannot drift.
6. **The planar via refuses**, with the text the README quotes.

## 4. Owner check: the series gate

The overview's **R-em3d20-2 walk-through**, on macOS and on Windows, recording each step's elapsed
time. Then fill the viewer figure placeholders.

## 5. Scope

- No new engine capability. If the example needs something no brief built, cut it from the example
  and do not add the capability here.
