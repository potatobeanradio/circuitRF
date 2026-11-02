# Brief DFR29-3 — The impedance check finds a test pad on an imported board

**Series:** designer feedback round 29 (2026-10-27), after DFR29-2. · **Tag:** `R-dfr29-3-<n>`
**Area:** `src/Design/Layout/Em/TraceImpedanceAnalysis.Pads.cs` (`PadShuntAt`, `SidePadShunts`, `Isolated`,
`MeasuredLand`), `src/Design/Layout/ViaSpanResolver.cs` (read, not changed). Findings go in `src/Design/RESOLVED.md`.
**Tests:** `tests/Ui.Tests/Layout/TestPadShuntFindingTests.cs` (DFR29-2's class; add to it). The formulas are not
touched: `TestPadGeometry` (`src/Core/Devices/TestPadFormulas.cs`) stays the one estimate.

---

## 0. Why

DFR29-2 made a pad shunt a finding, but on the board that asked for it — the round-26 TP149 board, a Gerber import —
the check finds **no pad at all**, not even as a note (DFR29-2's R-dfr29-2-3 measurement, `src/Design/RESOLVED.md`).
The search only knows the shapes circuitRF draws itself: a `ViaShape`, and a `CircleShape` pad with no other copper
joined to it. TP149 is drawn the way an imported board draws everything:

- **The via is a bare circle on the drill layer.** Net N7120220's drill, Ø0.3 mm at (126.6, 117.5) mm, is a
  `CircleShape` on *Drill*, not a `ViaShape`: its land on Bottom is part of a larger polygon, so the import's
  drill-to-flash pairing (`DrillViaPairing`) left it an unpaired hole. Its 0.6 mm lands on all eight inner conductors
  are separate small polygons.
- **The pad is a polygon.** A flashed round aperture arrives as a many-sided `PolygonShape`.
- **The pad is not on the via.** On Bottom, ONE polygon (1.5 × 2.35 mm) is the via's land, a short trace and a 1.5 mm
  round pad centred ~1.36 mm from the via. So the pad is neither centred on the via nor `Isolated` — the stub is
  copper joined to it.

The Top side is found already: trace T2 on Top ends at the via's land (its end reads "pad").

Next door is the case that must NOT be flagged: net N7119846 has the same kind of drill (Ø0.2 mm at (125.6, 117.0) mm,
lands on every inner layer), but on Bottom it continues as the 47.5 Ω line the round-26 report names — a trace, not a
pad.

## 1. What to do

### R-dfr29-3-1 — A circle on a drill layer is a via

Wherever the pad search looks for a `ViaShape`, also take a `CircleShape` on a layer some `StackupKind.Via` entry
claims (`ViaSpanResolver.DrillLayerKeys` / `EntryFor`) — the same identity DRC connectivity and every export already
use for "a barrel" (R-via-5), so the check does not invent a second one. Drill = 2R; the span is the entry's.
`BandsSpanned` takes the layer and the point rather than a `ViaShape`.

A bare circle carries no `PadSize`. Its land is **measured**: the largest land `MeasuredLand` finds round it on the
conductors it passes (TP149: 0.6 mm), else 2 × the drill. That land is what `PadOverLand` and the search radius use.

### R-dfr29-3-2 — A round pad drawn as a polygon

A pad may be any copper shape whose outline holds a circle: the longest run of consecutive outline vertices that lie on
one circle (least squares, residual under 2 % of the radius) and sweep **at least 240°** — a pad joined by a stub is
still round everywhere but where the stub meets it. Its centre and radius are the fit's. Rectangles, rounded rectangles
and arbitrary polygons with no such run are not pads (DFR29-2's R-dfr29-2-3 rectangle case stays out of scope).
`ImageTrace.CircleOf` (`src/Design/Layout/Recognition/Image`) fits a whole region and so does not fit a pad with a
stub on it; it is the reference for the tolerance, not the routine.

### R-dfr29-3-3 — A pad joined to the via by a short stub on its own layer

Through a via, on each conductor the via reaches other than the trace's, take the **copper island** joined to the via's
land on that conductor. It is a test pad when all of these hold:

1. it holds a round pad (R-dfr29-3-2) wider than `PadOverLand` × the land;
2. the pad's edge is within `MinAspect` × the joining stub's width of the land's edge — a stub too short to be a trace
   (TP149: ~0.3 mm of exposed trace, ~1.36 mm centre to centre);
3. **nothing else is joined to the island**: no other drill or via in it, no trace this run reviews or would review on
   it (no reviewed chain's copper inside it), and no second round pad. This is `Isolated`'s rule for an island rather
   than for a disc, and it is what keeps N7119846's Bottom line, and a component's land, from reading as a test pad.

The shunt's numbers are still the TESTPAD's: the pad's diameter and its centre's stackup walk, the via's span, the
lands measured. The stub between them is **owner decision D1**.

**Owner decision D1 — the stub on the pad's layer.** The round-26 report found the TESTPAD estimate 0.44–0.53 pF low on
this board, most of it this 1.4 mm trace over Inner 8 (moving the pad onto the via took 0.37 pF off in Palace).
- **(a) recommended:** leave it out of the numbers, as the TESTPAD does, and name it in the sentence — *"…, joined by a
  0.31 mm trace on 'Bottom Copper' the estimate leaves out"*. The numbers stay the model's (gate 4), and the gap is
  stated rather than hidden.
- (b) add the stub's own capacitance from the cross-section the check already solves (C′ = √ε_eff / (c·Z0) × length) —
  no new formula, but the finding's C then differs from the TESTPAD a schematic would place.

### R-dfr29-3-4 — Report which way it was found

The sentence says the via is a drill hole and the pad an outline where they are (*"through a 0.3 mm drill hole"*, *"a
round pad 1.5 mm across, fitted to its outline"*), so a reviewer reading a number off an imported board knows it was
read rather than drawn.

## 2. Gates

1. **Fixtures, one per rule** (synthetic, in the test class; the ten-layer shipped technology): a drill-layer circle
   with a `CircleShape` pad on the far side (R1); a `ViaShape` with a 64-gon pad (R2); a drill circle with a polygon
   that is land + 0.3 mm stub + 1.5 mm round pad on Bottom (R1+R2+R3) — each found, failing at 2.44 GHz.
2. **The negatives:** the same drill circle whose Bottom copper continues as a reviewed trace is not a pad; a land
   island carrying a second drill is not a pad; a rectangle is not a pad.
3. **TP149 as built** (round-26 workspace, `TP149_board`, `--max-freq 2.44GHz`): **exactly one** pad-shunt finding,
   at the pad (~(127.0, 116.2) mm), a **fail**, with the round-26 report's TESTPAD numbers: pad 1.12 pF, unused pads
   0.19 pF, via 0.59 nH, stub 1.35 pF (± what the measured lands and clearances move them, stated). N7119846 is not
   flagged. Every other trace's verdict is unchanged.
4. **TP149 with the fix** (`TP149_board_fix`): the same pad loads the line less — the report's fix reads stub 0.29 pF —
   and is a warning or nothing per DFR29-2's D1.
5. Gates 3 and 4 are run by hand on the designer's workspace and reported; the workspace is not committed and the
   committed tests name none of it.

## 3. Not in this brief

- Rectangular and rounded-rectangle pads, and TESTPAD instances read from their own parameters (DFR29-2 R-dfr29-2-3's
  other cases).
- Re-pairing drills with lands in the Gerber import itself — the import's pairing is right to leave a land inside a
  polygon unpaired; the check reads what is there.
- A better stub estimate than D1 (b) — the TESTPAD's own gap (src/Core/RESOLVED.md, DFR26-8) is its own question.
