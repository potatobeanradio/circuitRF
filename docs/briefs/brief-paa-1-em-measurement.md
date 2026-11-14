# Brief PAA-1 — the EM evidence: how far an element's pattern carries an array

**Series:** PAA-0 … PAA-10 · **Tag:** `R-paa1-<n>` · **Depends on:** — · **Design note:** §2, §3, §9
**Area:** a scratch workspace outside the repo for the runs; `testdata/phased-array/` for the committed fixtures and the
comparison's reference numbers; `src/Design/RESOLVED.md` for the findings.

## The gap

The series rests on equation (2) of the note: an element's embedded pattern times an array factor stands for the array.
That is an approximation whose error depends on coupling, pitch, scan angle and position in the array, and nobody has
measured it in circuitRF. Superposition (1) itself is exact and already verified by the co-simulation series; the open
question is only how much L0 (an isolated element) and L1 (a sub-array's embedded pattern and extended coupling) lose
against L2 (the array solved whole). This brief answers it with EM runs and **writes no product code**: every later
tolerance, and D3's default level, comes from its numbers.

## The fixture

The shipped *Patch Antenna* example's element — a 5.8 GHz inset-fed patch on the example's own 30 mil laminate technology (16.94 × 13.3 mm) — copied
into a scratch workspace and arrayed at **half a free-space wavelength at 5.8 GHz, 25.86 mm**, in both directions.

- **Feed.** The example's edge port sits at the end of a 5 mm feed line, which in a column runs toward the next patch. Cut
  the line to about 2 mm and put an **internal delta-gap port** on it (a port label inside metal defaults to an internal
  via, which stores no pattern; set it to Internal delta gap). Every element keeps the same feed, so element n's port is
  port n, numbered row-major from the top left as seen from +z (D6).
- **Mesh.** One `PlanarMesh` for every run, coarser than the example's (start at 12 cells per wavelength, edge mesh off)
  so the largest array fits. The same settings on every run: the comparison measures the approximation, not the mesh.
- **Frequencies.** 5.7, 5.8 and 5.9 GHz, resonance search off, radiation pattern on.

## Requirements

**R-paa1-1 — Dry-run everything first.** `run analysis=em dryRun=true` on each setup below, recording unknowns, memory
and estimated time in the completion note before any solve. A run estimated beyond ~15 minutes or beyond half the
machine's memory is not started: shrink the mesh for **all** runs together, or take the fallback in R-paa1-2. Every solve
has a memory watchdog on the solver's own process.

**R-paa1-2 — The runs.**

| Run | Ports | What it is for |
|---|---|---|
| E1 — one element | 1 | L0's pattern and S₁₁ |
| E2 — 1×2 along x, 1×2 along y | 2 + 2 | The strongest and weakest coupling at this pitch |
| E3 — 1×4 along the more strongly coupled axis | 4 | PAA-6's brute-force HB bench |
| E4 — 2×2 | 4 | Corners only |
| E5 — 5×5 | 25 | The sub-array L1 reads: centre pattern, the nine position classes, offsets to ±4 |
| E6 — the reference: 7×7, else the largest square array that fits, else a 1×8 line | ≤ 49 | What L1 predicted from E5 is compared against |
| E7 — by hand, optional: 1×7 on a thick high-permittivity substrate (εr ≈ 10, h ≈ 0.08 λ) | 7 | Does L1 predict a scan blindness? |
| E8 — by hand, EMerge Draft, time-capped at 10 min: E4 on a finite 3D ground | 4 | The size of the edge effect a planar ground leaves out |

E7 and E8 are worth doing only if E1–E6 come in under budget; skipping them is reported, not hidden.

**R-paa1-3 — The comparison, as a scratch harness.** A small program (not a test, not in the solution) that reads the
records through `PortFarFieldSet` and forms, for each array E2–E6 and each scan:

- **L2:** equation (1) with the steering weights, through `FieldCosimulation.PointOf`'s path (terminated embedded
  patterns formed from the planar record's shorted ones by solving "port k driven, others in Z0", note §2.1);
- **L0:** E1's pattern, re-referenced to each element's position, times the array factor, with each element reflecting
  E1's S₁₁ and no coupling;
- **L1-centre:** E5's centre embedded pattern times the array factor, with E5's coupling extended by offset (note §3.2);
- **L1-classes:** the same with E5's nine position classes;

at θ₀ ∈ {0°, 30°, 45°, 60°} in both principal planes and one diagonal (φ₀ = 45°), at 5.8 GHz, and at 5.7/5.9 GHz for
θ₀ = 30° only (squint). Phase-shifter steering at 5.8 GHz.

**R-paa1-4 — What is reported, per array and scan.** Realized gain at the peak (dB), pointing error (°), first sidelobe
level (dB), RMS pattern error over directions within 20 dB of the peak (dB), |ΔΓ_act| per element (max and RMS), and the
power balance (note §2.4) of each level. For E6 additionally the error of L1 by position class (corner, edge, interior),
which decides whether `Patterns=classes` is worth having. Tables in `src/Design/RESOLVED.md`; plots (gain cuts of the
three levels over L2, Γ_act per element as a heat map) as PNG in the completion note.

**R-paa1-5 — The decisions it hands on.** Written at the top of the completion note:

1. The default level for a sub-array record (D3), and whether L1 beats L0 at all. **If it does not, stop and report.**
2. Whether `Patterns=classes` is built in PAA-2 or dropped.
3. The tolerances PAA-2 and PAA-3 gate on: the L2 identity (expected ~1e-9, it is the same sum), L1 against E6 (the
   measured error plus a margin), and the power-balance band within which a run says nothing.
4. The pitch-difference threshold at which D5's warning fires, from how fast E2's coupling changes — optional, by a
   second E2 at 0.6 λ if the budget allows.

**R-paa1-6 — Fixtures for the later tests.** Reduce E1, E3, E5 and E6 to one frequency (5.8 GHz) and a 5° grid, write
them as ordinary result records (`.cemr` + `.npy` + `.sNp`) under `testdata/phased-array/`, each under ~1 MB, with a
README naming the fixture, the mesh and the reduction. Full-resolution results stay out of git. Reducing the grid is a
sampling of the stored pattern, never a re-solve, and the README says so.

## Gates

1. Every run's `dryRun` numbers and actual time and peak memory are in the completion note.
2. On E4 and E5, L2 formed by the harness equals the existing `type=fields … drive=… weights=…` analysis on the same
   record and weights, to 1e-9 in the pattern — the harness is measuring what the product computes.
3. The R-paa1-4 tables exist for E2–E6, and R-paa1-5's four decisions are written.
4. The fixtures load through `EmFieldDataRecord.Open` and are under the size stated.

## Not here

Any product code; the engine is PAA-2. Palace runs. Optimising the element.
