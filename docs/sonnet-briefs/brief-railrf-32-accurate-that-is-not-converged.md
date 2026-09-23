# Brief 32 — "Accurate" that is not converged

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail32-n` · **Phase:** defect, measure first, changes numbers
**Area:** `src/Design/Layout/Pdn/PdnMeshExtractor.cs` (`MinimumFeatureWidthDbu`, `PdnGrid.Build`,
`RefinementBands`, `MeshBuilder.AddCopper`, `StampMesh`, the cell-size choice in `Extract`,
`PdnMeshSettings`)
**Depends on:** brief 31 (its gate is the reference number this brief converges) · **Blocks:** 33
(Fast is gated against Accurate, so Accurate has to be right first)
**Evidence (outside the repo, never copy into it):** the fourth field report's workspace and brief 30's
`h30` harness, held by the owner (README beside them). `CELLS=<n>` sets `CellsAcrossMinimumFeature`
with `MaxCells` raised; `REFNET=GND` stands in for brief 31 until it lands.
**Rule:** the board, its customer and the reporter must not be named anywhere in the repo.

---

## 0. What was measured

The designer's rail, correctly seeded (the Bottom GND pour kept out of the rail by hand, the reference
net GND — i.e. what brief 31 makes the default), Accurate, Release, the only thing varied being how many
cells span the rail's narrowest copper (0.251 mm):

| Cells across | Cell pitch | Cells | Drop at the load (30 mA) | Time |
|---|---|---|---|---|
| 2 | 126 µm | 161,235 | 12.31 mV | 10.5 s |
| **3 (default)** | 84 µm | 142,973 | **14.55 mV** | 9.6 s |
| 4 | 63 µm | 588,930 | 4.13 mV | 44 s |
| 6 | 42 µm | 1,215,351 | 3.93 mV | 136 s |
| 8 | 31 µm | 929,950 | 4.03 mV | 80 s |

**The default is 3.6× too high and does not say so.** From 4 cells across the answer sits at
4.0 mV ± 3 %. Two things in the table are not understood and are part of this brief: the answer jumps
by 3.5× between 3 and 4 cells rather than converging towards it, and the cell count is not monotonic
in the pitch (2 across has more cells than 3; 8 has fewer than 6). A mesh that is fine everywhere cannot
do either, so the grid (`PdnGrid.Build` with its port refinement, or the `MaxCells` ceiling doubling the
pitch) is doing something the provenance does not show.

Pessimistic here — but the same mechanism can as easily cut a path that the real copper has a
parallel route around, and then Accurate reads LOW and looks entirely ordinary. §2.9 rule 4 (fast against
accurate on the user's own board) only works if the accurate side is right.

## 1. `R-rail32-1` — find the cause (measure; no fix yet)

- **Where the extra 10 mV is dropped.** Diff the 3-across and 6-across solutions: the drop along the
  rail, section by section (the breakdown rows already locate copper). The expectation is one or a few
  places — a diagonal 0.25 mm trace that rasterises at 84 µm into cells touching only at corners, a neck,
  a via land — carrying the difference. Name them, with coordinates and the rasterised picture.
- **Why 2 across has more cells than 3, and 8 fewer than 6.** Log `PdnGrid.Build`'s lines per axis, the
  refinement bands, and whether the `MaxCells` ceiling coarsened anything (the note it writes). The
  provenance must be able to explain a cell count; today it cannot.
- **Is it the rail or the return?** Repeat the ladder with the reference replaced by a solid plate
  (`Infinite` extent). If the 3-across jump survives, it is the rail's copper; if not, it is the plane.

## 2. `R-rail32-2` — the fix, chosen from what §1 finds

**The requirement: Accurate never returns a number it has not shown to be converged.** In order of preference:

1. **The rasterisation keeps copper connected at the default pitch.** If §1 shows corner-touching cells
   on diagonals, the cure is in how a cell's copper and its links are computed (a link's conductance from
   the copper actually shared across the cell edge, and diagonal continuity where the copper runs
   diagonally), not a finer grid. Gate: a 45° trace fixture at 3 cells across agrees with the closed
   form to the same 1 % the axis-aligned fixture already meets (`PdnMeshExtractorTests`), and the field
   board at 3 across is within 2 % of its 8-across value.
2. **Otherwise, a convergence check the user can see.** Solve at n and at a finer n (refined only where
   the current density is high, not everywhere); if the answers differ by more than the stated
   tolerance, refine again, and report the last change in the provenance. Refuse, naming the region,
   when the ceiling is reached without convergence. The cost on this board must be stated — a blanket
   6 across is 136 s where the default is 10 s.

Whichever it is, the provenance line for the cell size says what the answer's discretisation error is
believed to be, measured, not assumed.

## 3. Gates

1. The field board (the designer's document, brief 31 landed): the default Accurate is within 2 % of the
   8-across answer, and the provenance explains its cell count.
2. The existing mesh gates (`PdnMeshExtractorTests`' closed-form 1 %, `PdnDcSolveTests`,
   `PdnDistributedTests`) still pass, and where a number moves, say by how much and why it is closer to
   the closed form.
3. The fast-vs-accurate gate (`PdnFastExtractorTests.FastAgreesWithAccurateOnTracesAndRefusesOnAPour`)
   still holds.

## 4. Tests (minimal)

1. A 45° trace of the minimum width at the default pitch: closed form to 1 %. (Fails at HEAD if §1 finds
   what the table suggests.)
2. Whatever §1 names as the mechanism, reduced to the smallest fixture that shows it.
No timing tests; the cost of the chosen fix is measured in the harness and written in RESOLVED.

## 5. On completion

Findings and both ladders (before and after) in `src/Design/RESOLVED.md`, never in any `CLAUDE.md`.
Record the converged drop on the designer's board — it is the number brief 33 gates Fast against and
the one the designer is waiting for.
