# Brief SAR-5 — circuitRF's own SAR engine, and `sar_calc` as a cross-check

**Series:** `brief-sar-0-overview.md` (L1, L3, L5, O3) · **Tag:** `R-sar5-<m>`
**Depends on:** SAR-4
**Area:** `src/Engine/Em3d/Sar/` (new; numeric layer, no domain types), `src/Design/Em3d/OpenEmsRun.cs` (the
`sar_calc` step), `src/Design/Em3d/SolverDiscovery.cs` (finding `sar_calc`), `docs/design/sar.md` §7

---

## 1. Goal

Given a Cartesian grid with complex E, conductivity, density and cell volume per cell, compute:
- local SAR;
- the 1 g and 10 g peak spatial-average SAR, by cube averaging;
- whole-volume SAR and absorbed power.

All in circuitRF's own MIT code, written from public descriptions of the method. Agreement with openEMS's `sar_calc`
on the same input is the gate. The engine knows nothing about openEMS (L5).

## 2. Requirements

**R-sar5-1 — Input.** `SarGrid`: mesh lines per axis (metres); per cell, complex Ex, Ey, Ez (peak phasors), σ (S/m),
ρ (kg/m³) and volume; a cell with ρ = 0 is background (air). It is read from SAR-4's raw `DumpType 29` file, which
holds exactly these quantities as openEMS assigned them. That avoids any disagreement about which material a cell holds.

**R-sar5-2 — Local SAR.** σ|E|²/(2ρ) per cell; zero where ρ = 0. The total absorbed power is Σ σ|E|²V/2.

**R-sar5-3 — Cube averaging.** For each tissue cell, the smallest cube centred on it containing the target mass, with
the partial-cell treatment and the background-fraction rule. Where a cube cannot be centred within the allowed
background fraction, it is grown from a face, and a cell with no valid cube takes the value of a valid cube containing
it. The method follows the procedure as published in the open literature and as openEMS documents its `IEEE_62704`
method. The design note writes it out step by step with its sources, including **every point where the public
descriptions leave a choice**, and which choice was made. Masses: any, defaults 1 g and 10 g. Methods offered:
`Cube` (the above) and `Simple` (no background rule), matching `sar_calc`'s `IEEE_62704` and `SIMPLE`.
openEMS's C++ is read to understand behaviour only; nothing is copied (L3).

**R-sar5-4 — Results.** Local and averaged SAR volumes (float32); the peak value and the cell (and cube) where it
occurs; the averaged-cube statistics; total absorbed power; tissue mass; whole-volume SAR = P_abs / mass. All of it
can be restricted to a **region** (a box, or a named phantom or tissue set), so head-only or trunk-only SAR is a
region, not a separate code path.

**R-sar5-5 — Performance and memory.** Multithreaded over cells, with a prefix-sum (summed-volume) table for mass and
power, so each cube is O(1) once its size is known. Streaming input from SAR-4. A whole head at 1 mm (about 10 M
cells) must run on the owner's 16 GB machine. That is shown by a counter-based test of the algorithm's operations, not
a timing test, plus one measured run reported in the design note.

**R-sar5-6 — `sar_calc` alongside (O3).** `SolverDiscovery` finds `sar_calc` beside `openEMS`. A setup key
`Sar.CrossCheck` (default off) runs `sar_calc --method IEEE_62704 --mass <g>` on the same raw dump after the run. The
result carries both peaks and their relative difference; a difference above 0.5 % is a warning on the run. `sar_calc`
absent with the key on is a note, not an error.

## 3. Not in this phase

Wiring into the `.c3d` run and its plots (SAR-6); FEM or tetrahedral input.

## 4. Gates (minimal)

- `SarLocalTests`: a uniform 4 × 4 × 4 block, local SAR exact (1e-6) to σ|E|²/(2ρ). The same case openEMS's analytic
  test uses, built in C#, not copied.
- `SarAveragingTests`: a uniform block large enough for a 10 g cube, where the averaged value equals the local value
  in its interior; a two-layer block where the 1 g value is hand-checkable; a cell at a tissue–air corner taking a
  grown cube.
- `SarAgainstSarCalcTests`: SAR-1's raw dipole dump through circuitRF's engine against SAR-1's `sar_calc` output:
  peak within 0.5 %, ≥ 99 % of cells within 1 % (§4). Fixed data; no openEMS process.
- `SarOperationCountTests`: cube evaluations scale as O(cells × growth steps), not O(cells × cube volume).
