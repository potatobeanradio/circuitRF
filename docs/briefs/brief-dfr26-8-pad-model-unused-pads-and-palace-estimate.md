# Brief DFR26-8 — The test-pad model misses a via's unused inner pads; Palace's size estimate is off both ways

**Series:** designer feedback round 26 (2026-10-24), follow-up to DFR26-7. · **Tag:** `R-dfr26-8-<n>`
**Area:** `src/Core/Devices/TestPadModel.cs` / `TestPadFormulas` / `TestPadGeometry`; `SubstrateResolver.ResolveTestPad`;
the impedance tool's pad report (`TraceImpedanceAnalysis*`, `PadShuntAt`); the Palace size estimate
(`Em3dSizeEstimate` or wherever `explain`'s "palace estimate" line and the pre-run memory check are computed).
Findings go in `src/Core/RESOLVED.md` and `src/Design/RESOLVED.md`.
**Tests:** `tests/Core.Tests/Devices/TestPadModelTests.cs`, `tests/Ui.Tests/Layout/TestPadRound26Tests.cs`, and a
counter-style gate for the estimate (no timing).

---

## 0. What the board study found

Solving the designer's TP149 problem on the real ten-layer board (Palace electrostatic, 2nd order, one adaptive pass,
the cropped Gerber import — the workspace is in the round-26 feedback folder), the test point's net measured
**4.80 pF**, and **1.44 pF** with the test point cut off: the stub is **3.36 pF**. The TESTPAD model, for the same pad,
gives **1.54 pF + 0.03 pF** for the via. It is low by a factor of two, and the reason is structural, not a formula
constant:

| Change to the as-built board | C of the TP net | ΔC |
|---|---|---|
| (as built) | 4.80 pF | — |
| the via's eight unused inner pads (0.6 mm in a 0.94 mm antipad) removed | 3.19 pF | **−1.61** |
| Inner 8 + Inner 7 cleared under the pad (2.0 mm) | 3.80 pF | −1.00 |
| both | 2.19 pF | −2.61 |

**The unused inner pads are the largest single term**, larger than the pad's own plane capacitance — about 0.2 pF a
pad. None of it is parallel plate: the planes above and below are cleared round the via too (the same 0.94 mm antipad),
so a pad overlaps no copper. Each pad couples through its RIM — across its 0.17 mm ring gap to its own plane, and
diagonally to the antipad edges of the neighbouring planes. The stackup sets how close those are: the conductors sit
in pairs 69 to 77 µm apart with 150 and 460 µm cores between the pairs, so a pad has a thin dielectric to a plane on
ONE side (both sides only for Inner 8).
`TestPadPCell` draws no inner pads, so the model is right about the PART it draws — and wrong about every real board
whose CAD kept non-functional pads, which is the default in most layout tools. Nothing in circuitRF says so.

The Palace estimate missed in both directions on the same model: **60 GB estimated, 4.6 GB used** for the electrostatic
solve (order 2, one pass), and **5.4 GB / 457 k unknowns estimated, > 11 GB / 1.09 M unknowns actual** for the 6-port
driven solve (order 2, no passes). The second one stopped at the memory limit after its mesh; the first would have been
refused at 150 % without `--force` on a smaller machine.

## 1. What to do

### R-dfr26-8-1 — `TESTPAD`: unused pads on the feed via

Add `InnerPadD` (empty: none, as drawn today) — the diameter of the pads the via carries on the conductors it passes —
and `AntipadD` (empty: the technology's via antipad). Each passed conductor contributes a rim term: the pad's
edge across the antipad gap to its own plane, plus the diagonal coupling to the antipad edges of the planes above and
below, which depends on the dielectric thickness to each (the planes there are cleared to the same antipad, so there
is no parallel-plate overlap unless `AntipadD` is smaller than `InnerPadD` on a neighbour). Subtract the bare barrel's
own rim term, which the pad replaces. A conductor in `ClearLayers` contributes nothing. Draw the pads in `TestPadPCell` when `InnerPadD` is set,
so the artwork and the model stay one statement. Messages names the inner-pad term separately so a designer sees it.

Gate: on the shipped ten-layer stackup, D = 1.5 mm, drill 0.3, `InnerPadD` 0.6, `AntipadD` 0.94, the model's stub
capacitance is within 25 % of 3.36 pF, and removing the pads moves it by 1.6 pF ± 25 %.

*Restated 2026-10-25 (owner's choice):* a converged axisymmetric field solve of that via gives 1.446 pF and 0.192 pF;
the Palace pass above was under-resolved (`src/Core/RESOLVED.md`). The gate is 25 % of those.

### R-dfr26-8-2 — The impedance tool's pad report counts the pads that are drawn

`PadShuntAt` reads the via's land on each passed conductor from the artwork. Report each unused pad and its share,
and say in one line that removing unused pads (a CAM option) removes it.

### R-dfr26-8-3 — Recalibrate the Palace estimate on this board

Use the round-26 crop as the calibration case (both setups are in the feedback workspace). The electrostatic estimate
must not assume the driven problem's vector unknowns; the driven estimate must count the thin-layer refinement a
ten-layer stackup forces. A counter test: for a fixed small multilayer fixture, the estimate's unknown count is within
a factor of 2 of what Gmsh + Palace report on a mesh-only dry run.

### R-dfr26-8-4 — A via the crop line cuts through breaks one 3D route or the other

Crop to Rectangle keeps a via whose CENTRE is inside the rectangle, so a GND via at (125.7, 119.2) mm on the round-26
crop straddles the new board edge. With it, the 3D view's route (a `.c3d` instancing the layout) fails in Gmsh ("PLC
Error: A segment and a facet intersect" at the board edge). Without it, the `.cem` route's driven solve is refused
because port 6's sheet, on the same edge 1.9 mm away, "selected nothing in the mesh". The delivered workspace keeps the
via, so its `.cem` S-parameter setups run and its 3D views' electrostatic setups do not re-mesh (their results, solved
without the via, are kept). Either drop or clip a via that leaves the board in `LayoutCrop`, or make both builders
tolerate one — and find why removing it loses port 6's sheet. Reproduce from the round-26 workspace.

## 2. Not in scope

The model's rim term, the 1.7× susceptance gap DFR26-7 measured near the plane, and the electrostatic accuracy floor
(Inner 8 cleared at 2.4 mm read 0.13 pF HIGHER than at 2.0 mm at one adaptive pass — numerical noise of ~3 %, which a
sizing study needs to know about but which is not a defect).
