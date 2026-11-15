# Brief PAA-10 — user pages, the format reference, and a shipped example

**Series:** PAA-0 … PAA-10 · **Tag:** `R-paa10-<n>` · **Depends on:** all · **Design note:** all
**Area:** `docs/user/src/reference/` (a new `phased-array.md`, and `cosimulation.md`, `system-components.md`,
`simulations.md`, `cli.md`, `tuning.md`), the MCP `reference` topics, `examples/` and `examples/examples.json`,
`docs/design/phased-array.md` (status line and anything the series changed).

## The gap

Nothing tells a user that an array can be formed from one element's EM result, what each fidelity level costs and
claims, how to write a steered and tapered array in a netlist, or when to reach for the brute-force path; and no example
shows any of it running.

## Requirements

**R-paa10-1 — `phased-array.md`.** The user page: what the component computes and the three levels (with PAA-1's
measured errors, in a table, and what an infinite planar ground leaves out from E8 if it was run); Feed and Elements mode
and when to use each; every parameter with units, defaults and whether it can be swept; element scope and the weight
functions with a worked steer-and-taper example; the channel cell; receive, noise and G/T; brute-force HB with the Γ_act
loci on the loadpull contours; the speed paths and what makes a bench leave the fast path; the tool window. Figures from
UiSnapshot and from the example's results.

**R-paa10-2 — The other pages.** `cosimulation.md` links the array page from its pattern section; `system-components.md`
gains the PhaseShifter and the PhasedArray rows; `simulations.md` the `fields` keys `waves=` and `harmonic=`; `cli.md`
what `check` and `explain` say about an array; `tuning.md` the "estimated" drag rule.

**R-paa10-3 — The format reference.** The MCP `reference` format topic and `docs/design/project-file-formats.md` list
`PhasedArray` and `PhaseShifter` with every parameter.

**R-paa10-4 — The example: *Phased Array*.** An `examples/Phased Array/` workspace: the patch element of *Patch Antenna*
fed as in PAA-1, its 5×5 sub-array `.cem`, and three benches —

1. **Transmit**: a Feed-mode 16×16 array with a PA channel, steered by a `theta0` VAR, a parametric sweep of it, the
   realized-gain cuts and the Γ_act heat map;
2. **Receive**: PAA-7's bench, G/T against scan and an interferer swept in angle;
3. **Brute force**: PAA-6's 1×4 bench with four PAs, the Γ_act loci on the PA's contours.

The README states what was traded for speed (mesh density, frequencies) per the existing example convention. Ship the
5×5 result if its reduced record is under ~2 MB (one frequency, a 2° grid, sampled, never re-solved); otherwise the README
says to simulate the `.cem` first and how long it takes. Registered in `examples.json` with its abstract.

**R-paa10-5 — The design note.** Status line updated; every place where the series decided differently from the note
corrected in the note, not left beside it.

## Gates

1. The example's three benches pass `check` and simulate headlessly from the CLI.
2. Every figure in `phased-array.md` is verified with Svg.Skia (not qlmanage) at its real aspect.
3. The edited user pages regenerate cleanly with DocGen, figures opt-in for the changed pages only.
4. A grep of every new and changed file for commercial vendor and product names finds none.

## Not here

A tutorial video; any change to the shipped *Patch Antenna* example itself.

## Completion note (2026-11-15)

**Built.**

- **`examples/Phased Array/`**, registered in `examples.json` after *Antenna Co-Simulation* with its abstract on the Example
  Workspaces page. Cells: `Patch Element`, `Patch 1x4`, `Patch 5x5` (layouts and `.cem`s from PAA-1's fixtures), the sub-cells
  `FET` (the Harmonic Balance example's), `PA`, `PACore`, `TxChannel`, `RxChannel`, and six benches, each with its display:
  `1 Transmit` (16 × 16 Feed, −25 dB Taylor, 6-bit phase, an identical driver-and-PA channel, `theta0` swept 0–60° around `F2` with
  `F1` unswept for the measures), `2 Receive` (PAA-7's 8 × 8 receiver, now steered by `theta0`), `3 Receive Interferer`,
  `4 PA Loadpull`, `5 Brute Force` (PAA-6's bench on an Elements-mode array), and `6 Envelope` (PAA-6b's, added at the owner's
  request: one NR 20 MHz signal through a Combiner into the four PAs, the radiated envelope in four directions over the loadpull
  contours). The two reduced records ship in `results/`.
- **`docs/user/src/reference/phased-array.md`**, in the nav after *Patch antennas*: the three levels with PAA-1's table, Feed and
  Elements mode, every parameter (the component table) and what sweeps, element scope and the weight functions with the example's
  steer-and-taper line, the channel cell, receive/noise/G/T, brute force in HB and envelope with the loci on the contours, speed and
  what leaves the fast path, the tool window, limits. Seven figures, verified with Svg.Skia at their real sizes (1320 × 900 tool,
  1180 × 700 and 1500 × 880 displays), light and dark.
- **The other pages**: `cosimulation.md` links the array from its pattern section; `system-components.md` gains Phase Shifter and
  Phased Antenna Array entries; `simulations.md` gains a **Fields** section with `waves=`, `harmonic=` and `dirs=`; `cli.md` what
  `explain --analysis` and `check` say about an array (outputs captured from the example); `tuning.md` the *estimated* drag rule;
  `netlist.md` (the MCP `netlist` reference topic) and `docs/design/project-file-formats.md` both lines with every key, indexed
  ones included. The tool's Help button opens `reference/phased-array.html#tool`.
- **The design note**: status line; PAA-1's results in §3 and §3.1 (5 × 5, classes default); the power-balance band (§2.4); the
  0.02 λ pitch warning; `Level=auto`; the angle rules; refused measures under a sweep; the taper's dissipated power; the channel
  cell's extraction; the fast path's no-channel case, Woodbury and refusals; measured frame times; envelope; the tool's (u,v)
  directivity and no 3D tab; what PAA-1 actually ran (6 × 6, no E7/E8); `src/Core/Devices/Arrays`.

**Gates.**

1. All six benches pass `check` (0 errors) and simulate headlessly: `fields` for Transmit (0.5 s) and Receive's linear measures,
   `hb` for Receive (3.6 s), Interferer (10.7 s) and Brute Force (0.4 s), `lp` for the loadpull (0.25 s), `fields` for Envelope
   (19 s), Release, from the drawn `.csch`. The numbers match PAA-6, 6b and 7 exactly where the bench is theirs.
2. Every figure rasterised with Svg.Skia (docs fonts registered) and looked at.
3. DocGen wrote the edited pages and the seven figures (`--figure` by name; see "What changed").
4. No vendor or product name in any added line.

**What changed from the brief, and why.**

- **The 5 × 5 record ships at 5°, not 2°.** The full runs were not kept, re-solving takes about 15 minutes and 3.2 GB, and the 5°
  record is 1.9 MB, under the ~2 MB the brief allows. The array forms its cuts and readouts on a fine grid with the element pattern
  interpolated, so only the overview polar plots show the 5° grid. The README says how to re-solve.
- **The Γ_act "heat map" is the tool's aperture coloured by |Γ_act|** (a figure on the page); the Data Display has no heat-map plot,
  so `1 Transmit.cdd` plots every element's |Γ_act| against element number at 0, 30 and 60°.
- **G/T against scan is a table, not a swept plot**: a sweep wraps one analysis and G/T needs the noise and fields analyses at the
  same steering. Read per steering on a Tuning slider or with `--set`; a follow-up could let a sweep wrap several analyses.
- **`--figures` was not used**: it reaches `workspace-overview`, whose fixture fails on its own ("no cell 'Mitred Bend'"), and stops
  the run. The seven figures were captured by name; the stamps are left for the series' full run.

**Found and fixed on the way** (`src/Design/RESOLVED.md`, `src/Cli/RESOLVED.md`, `src/Ui/Diagnostics/RESOLVED.md` § brief-paa-10):
a drawn bench's `Channel=` cell was never extracted ("names no cell"); `--set` dropped a variable's unit (`theta0=30` became 30 rad);
the `fields` verb dropped the bench's measures and refused to run the noise analysis receive measures read; the tool's status line
showed the frame time in a figure; `NetlistToSchematicTests` did not pin PAA-6/7/9's cell-defining benches, and `Uniform 64 Off.cnl`
round-tripped its `Off` list quoted (now written quoted); `ExampleWorkspacesTests`' passivity gate elaborated with no base directory.
Tests: `PhasedArrayExampleTests` (2).
