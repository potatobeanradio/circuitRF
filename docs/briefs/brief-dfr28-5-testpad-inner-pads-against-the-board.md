# Brief DFR28-5 — TESTPAD's unused inner pads read about ten times low on the TP149 board

**Series:** designer feedback round 28 (2026-10-27). Follows DFR26-8 and DFR27-1. · **Tag:** `R-dfr28-5-<n>`
**Area:** `src/Core/Devices/TestPadFormulas.cs` (`InnerPadCapacitance`, `InnerPadScreen`), `src/Core/Devices/TestPadModel.cs`
(`TestPadInnerPad`), `src/Design/Schematic/SubstrateResolver*` (the inner-pad slots the injection hands the model). Findings go
in `src/Core/RESOLVED.md`, under the DFR26-8 section, which already records the 2026-10-27 measurement below.
**Tests:** `tests/Core.Tests/Devices/TestPadModelTests.cs`, `tests/Ui.Tests/Layout/TestPadRound26Tests.cs` (its fixture is
the round-26 via), and a fixture ladder of your own (R-2).

---

## 0. What is known

DFR26-8 taught TESTPAD the unused pads a via keeps on the planes it passes. Its gate was the Palace figure from round 26 —
removing the TP149 via's eight inner pads (0.6 mm in 0.94 mm antipads) took 1.61 pF off the TP net — but that figure came
from one second-order electrostatic pass with no refinement, and the owner chose a converged **axisymmetric
finite-difference solve** as the reference instead: the via alone, every pad round and centred in a round clearance, the
1.5 mm test pad centred on the via. It gives the eight pads **0.192 pF**, and the model was fitted to it (0.191 pF).

On 2026-10-27 the real board was solved again in Palace, electrostatic, order 2, **three adaptive refinement passes**, as
built and with only the eight inner pads deleted (Robust meshing on; it moves no outline more than 0.5 µm):

| Pass | As built | Pads removed | Pads' worth |
|---|---|---|---|
| 1 | 4.65 pF | 2.93 pF | 1.72 pF |
| 2 | 4.57 pF | 2.97 pF | 1.60 pF |
| 3 | 4.81 pF | 3.01 pF | 1.80 pF |
| final | 5.02 pF | 3.03 pF | 1.99 pF |

So on the board the pads are worth **1.6 to 2.0 pF, not 0.19**, and the share does not shrink with refinement: "an
under-resolved conforming FEM overstates capacitance" does not explain it. Two things are still loose:

- **The as-built value is not converged.** It rises with refinement (4.57 → 5.02 pF), the opposite of an energy bound
  approaching from above, and Palace's error indicator was still 0.19 after pass 3. The pads-removed board converges
  (2.93 → 3.03). The unconverged part is round the pads.
- **The two references are different geometries.** On the board the 1.5 mm pad is 1.4 mm from the via at the end of a
  Bottom Copper trace, inside a 0.2 mm gap in the pour, beside C211; the planes' clearances are what the artwork draws, not
  necessarily round or 0.94 mm on every layer. The FD solve has none of that.

The reissued TP149 report (page 3) tells the designer the model reads the pads about ten times low on his board and to use
the Palace numbers until this is understood. This brief is understanding it.

## 1. What to do

### R-dfr28-5-1 — Read the artwork first

Before any solve, measure the TP149 via's surroundings on every inner layer from the round-26 board's artwork: each
plane's clearance round the via (diameter, roundness, whether it is a clearance at all or a thermal relief or a gap to
another net's copper), the pad's own diameter on each layer, and what else of the TP net or other nets lies within 1 mm.
`circuitrf check`'s impedance tool already measures a land and its clearance (`MeasuredLand`, `MeasuredClearance`, DFR26-8);
use it, and list per layer what it reads. A clearance smaller than 0.94 mm on some layer, or another conductor inside it, is
the cheapest explanation and must be ruled in or out first.

### R-dfr28-5-2 — A ladder from the FD geometry to the board

Build the same structure in steps, each a small `.cem` or `.c3d` electrostatic setup solved in Palace (order 2, refinement
passes until the pads' worth moves less than 5 %), measuring at every rung the pads' worth (as built minus pads removed):

1. the FD solve's own geometry — via, eight round pads in round 0.94 mm clearances, solid planes, test pad centred on the
   via. Palace here must give about 0.19 pF; if it gives 1.6, the disagreement is between the two solvers, not the
   geometry, and that is the finding (go to R-3 with it);
2. the test pad moved 1.4 mm off the via, on a trace, as on the board;
3. the board's own clearance shapes and sizes per layer (R-1);
4. the pour, C211 and the other nets within the crop;
5. the board crop itself (the 10-27 numbers).

The rung where the pads' worth jumps names the cause. Keep each solve short: a few minutes, under 10 GB. Record each
rung's numbers in `src/Core/RESOLVED.md`.

### R-dfr28-5-3 — Convergence of the reference

Whichever rung reproduces the board, make its as-built value converge: more refinement passes, or a finer starting mesh
round the pads (the rings are 64-gons; DFR26-8 found their short segments drive the mesh), until two successive passes
agree within 2 %. Report the converged pads' worth and how it was reached. If Palace cannot converge it on this machine,
say so with the numbers and stop; the owner decides.

### R-dfr28-5-4 — Fix the model, or bound it

With the cause known, change the inner-pad term so the model is within **25 %** of the converged board reference on the
board configuration, and still within 25 % of the FD solve on the FD geometry (if both are right, the model must tell
the two apart from what it is given; if R-2 shows one reference is wrong, restate the gate on the right one and say why).
If the cause is something TESTPAD is not told — a clearance that is not round, another net's copper — the model cannot
know it: then add what the injection CAN read from the stackup and the artwork, or, failing that, make the component's
readout say what it assumes (round clearances of AntipadD, nothing else near) so a user knows when not to trust it. The
owner decides between those if it comes to it.

## 2. Gates

1. R-1's per-layer table of the TP149 via's clearances and neighbours, in `RESOLVED.md`.
2. R-2's ladder: the pads' worth at every rung, and the rung that moves it.
3. R-3: a converged reference (two passes within 2 %), or the reason there is none.
4. R-4: the model within 25 % of the reference(s) it is gated on; `TestPadModelTests` and `TestPadRound26Tests` updated to
   the restated numbers, every other TESTPAD test unchanged.

Run only the tests the change reaches. Every Palace run under a memory watch, and short: no sweep, one electrostatic
problem at a time.

## 3. Not in scope

The TP149 report (already reissued; it is updated again only if R-4 changes what page 3 says), TESTPAD's pad term
(Kirchhoff's disc, DFR27-1), the driven S-parameter gap between Palace and the electrostatic value (DFR28-3, page 6 of the
report).
