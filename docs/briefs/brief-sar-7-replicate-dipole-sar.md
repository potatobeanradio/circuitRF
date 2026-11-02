# Brief SAR-7 — `Dipole_SAR`, rebuilt in circuitRF (the phone-SAR gate)

**Series:** `brief-sar-0-overview.md` (§4, L4) · **Tag:** `R-sar7-<m>`
**Depends on:** SAR-6
**Area:** `testdata/sar/circuitrf/dipole/` (new), `tests/Ui.Tests/Sar/` (new), `docs/design/sar.md` §9

---

## 1. Goal

Prove the whole chain with openEMS's own SAR example. The chain is: `.c3d` objects, the CSXCAD writer, the openEMS
run, the raw dump, circuitRF's SAR engine, then the results. Rebuild `Dipole_SAR` from `.c3d` objects only, run it
through circuitRF, and match SAR-1's reference to overview §4. **This is a gate: if it fails, the series stops here
until the cause is found and recorded.**

## 2. Requirements

**R-sar7-1 — The `.c3d`.** Built from the tutorial's stated parameters (SAR-1's README restates them):
- **dipole:** a PEC box of length 0.48·λ0 at 1 GHz, with a 50 Ω lumped port at its centre spanning ±1.25 mm;
- **phantom:** an `Ellipsoid3` centred 100 mm along x, with skin, bone and brain semi-axes and materials at the
  script's values (SAR-2's openEMS-phantom records);
- **air box and boundaries:** the script's air box, `PML_8` on all faces;
- **excitation:** a Gaussian to 1.5 GHz, `EndCriteria 1e-4`, `CellConstantMaterial` on, and one SAR frequency of
  1 GHz at 10 g.

It is written with the format reference's keys, so `check` passes it with no warning.

**R-sar7-2 — Two runs.**
- **(a)** `OpenEms.GridLines` taken from SAR-1's `Dipole_SAR` XML (SAR-3). This run tests the pipeline.
- **(b)** circuitRF's own grid at the same 2.5 mm target in the phantom. This run tests the grid generator.

Both are run once on the developer machine. Their outputs are committed under `testdata/sar/circuitrf/dipole/`
(scalars, port results, the 10 g volume), labelled as **circuitRF output**, kept apart from the reference.

**R-sar7-3 — Comparison, fixed data.** `DipoleSarReplicationTests` reads both directories and asserts overview §4:
- run (a): S11, Zin, Pacc, psSAR10g per W, whole-phantom SAR and the power budget, at the tight tolerances;
- run (b): psSAR10g per W within 5 %.

It also checks that the peak 10 g cube is in the same tissue and within one cell of the reference's position. **No
openEMS process in the routine gate.** A `Benchmark`-tagged test reruns both through the CLI for whoever changes the
writer.

**R-sar7-4 — Diagnose before tuning.** If run (a) misses, compare in order:
1. the two XMLs, structurally (primitives, priorities, materials, grid);
2. the raw dumps cell by cell;
3. the averaged volumes.

Record the first divergence and its cause in the design note before changing anything. **A tolerance is never widened
to make this pass**; widening needs the owner (O4).

**R-sar7-5 — Run (b)'s difference, explained.** Report run (b)'s grid-line count against the script's, the cell size
at the skin, and the change in psSAR10g. If run (b) misses by more than 5 %, find the grid property responsible (for
example, a line on the skin surface) before proposing a generator change.

## 3. Not in this phase

Phone placement and limits (SAR-12).

## 4. Gates

`DipoleSarReplicationTests` (fixed data, routine). The `Benchmark` rerun is opt-in.
