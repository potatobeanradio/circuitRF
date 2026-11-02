# Brief DFR31-2 — Extracting capacitance: two shipped examples and a user-doc page

**Series:** designer feedback round 31 (2026-10-29). · **Tag:** `R-dfr31-2-<n>`
**Area:** `examples/Capacitance Extraction/` and `examples/Test Pad Capacitance/` (new), `examples/examples.json`,
`docs/user/src/app-notes/an03-extracting-capacitance.md` (new), its row in `app-notes/index.md` and `_nav.txt`, and a
link from `docs/user/src/reference/em-3d.md`.
**Depends on:** DFR31-1. The complex example must be built through the GUI path that brief adds (the picker, connected
bodies, floating metal), not by hand-writing `Net:` keys. An example that only works when written by hand teaches a path
the GUI does not offer.
**Tests:** `tests/Ui.Tests/Examples/` — a new class modelled on `Em3dWavePortsExampleTests` and the `3D EM` example's
`expected-numbers.json` contract.
Findings go in `examples/RESOLVED.md`.

---

## 0. Why

Electrostatic capacitance extraction answered the round-26 test-pad question faster and more directly than a
driven solve, yet nothing that ships shows the technique. The only static setup in the examples is one cell of
`3D EM` (the package's C and L matrices), and it is not presented as a method. A technique this useful for PCB and
package work should have examples and a page of its own.

**Why it is a good method, and when it is not** (the app note must say this plainly):

- A test pad, a via stub, a connector pin, a pad under a part, the coupling between two nets: each is a small piece of
  metal hanging on a line. When it is small against the wavelength (rule of thumb: under a tenth of a wavelength in the
  dielectric), its effect on the circuit IS its capacitance. A 1 mm pad on FR-4 is under λ/10 up to about 14 GHz.
- An electrostatic solve gets that capacitance directly: one Laplace solve per terminal, with no frequency sweep, no
  ports and no de-embedding. It is fast, it converges well under refinement, and the answer is a number you can put
  straight into a schematic as a shunt C.
- A driven S-parameter solve gets the same physics indirectly. You fit C from Y₁₁/jω, or from the dip in S21. That
  takes minutes to hours, and the de-embedding of the line around the pad is a source of error in its own right. In
  round 26 the Draft driven solve read 3.1 pF where the electrostatic one read 4.6–4.8 pF on the same geometry.
- **Limits:** it gives no inductance (use magnetostatic for that), no loss, and no frequency dependence. It is wrong
  once the structure is a sizeable fraction of a wavelength, where it is a resonator rather than a lumped C. What
  counts as "ground" and what floats changes the answer, so the example must show both. Cross-check once against a
  driven solve, then trust the cheap method for design iterations.
- **The Maxwell matrix is not the circuit matrix.** Palace reports the Maxwell matrix: the diagonal is each terminal's
  total capacitance, and the off-diagonals are negative. The schematic wants each terminal's capacitance to ground
  (the row sum) and the mutual capacitances (minus the off-diagonals). The app note shows the conversion once, with
  numbers. The example's Data Display shows both, labelled.

## 1. Requirements

**R-dfr31-2-1 — Example 1, "Capacitance Extraction" (simple, minutes on a laptop).** A two-layer board: a top-copper
square pad, and beside it a short microstrip line segment, over a solid bottom ground plane. Two terminals (pad,
line), with the plane as ground. It shows:
- the pad's C against the parallel-plate estimate εA/d. The README states how much of the gap is fringing;
- the line's C per unit length against the value the line's impedance and effective permittivity imply,
  C′ = √εeff / (c·Z₀), with Z₀ and εeff from circuitRF's own line calculator. That is an independent check against
  a number every RF designer already uses;
- the mutual capacitance between pad and line, as a function of their gap. Use a parametric sweep of the gap, three
  points;
- an E-field clip plane through the pad, with the plane's position visible (DFR31-3).
The setup is created through the 3D view's setup window with the picker (DFR31-1), and the README walks through those
clicks.

**R-dfr31-2-2 — Example 2, "Test Pad Capacitance" (the round-26 problem on synthetic geometry).** A synthetic
multilayer board, at least six copper layers, with:
- a 50 Ω top-side line;
- a test pad on the BOTTOM side, joined to the line through a through-via;
- unused landing pads on the inner layers;
- ground planes with antipads;
- stitching vias.
No net names on the copper. It is an imported-board-like case, and it relies on DFR31-1's connected bodies. Three
cells:
- **As built:** electrostatic, terminal = the test-pad body (picked by clicking the bottom pad), ground = every body
  not in a terminal.
- **Fixed:** the inner landing pads removed and the plane openings under the pad enlarged. Same setup.
- **Compare:** a schematic with the line, a shunt C from each electrostatic result, the TESTPAD model's estimate, and
  a driven `.cem` of the as-built board. One Data Display shows S21 from all four, so the reader can see that the
  cheap electrostatic number reproduces the driven dip.
The README states, per cell, the measured C, the time and peak memory on the reference machine, and what a
designer would do with the number. Keep the driven run under 15 minutes on the reference machine (owner rule: keep EM
runs short). Use Draft and a modest board if that is what it takes, and say so in the README (the setting traded away
is documented, owner rule).

**R-dfr31-2-3 — `expected-numbers.json`** for both, in the `3D EM` example's format. The new test class re-runs each
electrostatic setup and holds the numbers within a stated tolerance (start at ±2 % for C), and checks that each
number's README text appears in the README. Tag the class `Category=Benchmark` if the runs exceed the ~5 s threshold
(they will). The routine tier keeps a fast test that the examples open, elaborate and resolve their terminals without
solving.

**R-dfr31-2-4 — App note AN03, "Extracting capacitance".** Cover:
- when to use it (§0);
- the floating-versus-ground choice, with the example's numbers for both;
- Maxwell versus circuit capacitance, with the conversion worked once;
- how to name terminals on a board with no nets;
- how to move the cut plane after the solve;
- how to put the result into a schematic;
- the cross-check against a driven run.
No citations and no outside-tool references (owner rules). Regenerate only this page's HTML when it is done (owner
rule: regenerate the edited page at once; the full regeneration is the owner's).

**R-dfr31-2-5 — Index.** Two rows in `examples/examples.json`, next to each other and after the 3D EM examples, with
summaries that say what the user learns. The `3D EM` example's README gets one line pointing at them.

## 2. Gates

1. Both examples open from File ▸ New ▸ Examples, and `check` reports no errors on either.
2. Example 1: the line's C′ is within 3 % of √εeff/(c·Z₀) from the line calculator, and the pad's C exceeds εA/d by
   the fringing fraction the README states.
3. Example 2: the fixed cell's test-pad C is lower than the as-built cell's. The Compare display's S21 dip from the
   electrostatic shunt C and from the driven run agree within 0.5 dB at the dip frequency.
4. `ExampleWorkspacesTests` (index against disk) passes with the two new rows.
