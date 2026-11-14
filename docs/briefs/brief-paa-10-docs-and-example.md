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
