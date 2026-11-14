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

## Completion note (2026-11-14)

**Decisions handed on (R-paa1-5):**

1. **Default level for a sub-array record: `embedded` (L1). L1 beats L0 in every case**, so the stop rule does not apply.
   **A 5×5 sub-array is the useful minimum:** predicting the 6×6 it gives 0.75 dB worst peak gain and 0.11 worst
   active reflection (RMS ≤ 0.04), where a 3×3 gives 1.99 dB and 0.19 and L0 3.61 dB and 0.50. The docs (PAA-10) should
   recommend a 5×5 sub-array and state these numbers.
   Worst active-reflection error: L0 0.15–0.50, L1-classes 0.04–0.16. Worst peak realized-gain error: L0 up to 3.6 dB,
   L1-classes 0.26–1.58 dB.
2. **Build `Patterns=classes`, and make it the default.** It beat the centre pattern alone in every case: worst RMS pattern
   error 0.80 vs 2.48 dB (1×9 along x), 1.80 vs 2.65 (1×9 along y), 2.56 vs 4.23 (5×5 from 3×3).
3. **Tolerances.** The L2 identity is exact arithmetic: 9.4e-16 measured, so gate it at 1e-12, not 1e-9. L1 against a
   reference array is a physics error, not something the engine can be gated to within; PAA-2's gate 3 should instead
   require the engine to **reproduce this harness's L1 numbers on the fixtures to 1e-9**, and the measured physics errors go
   in the user docs. **Power balance:** on a planar record L2's own radiated/accepted is 0.41–0.88 (it is the efficiency,
   surface-wave loss included), so no absolute band can judge an approximation. Say nothing within 0.1 of the centre
   element's own embedded efficiency; warn beyond it; **always warn above 1** (L0 reached 1.29).
4. **Pitch warning at 0.02 λ₀** from the record's pitch (a 0.042 λ₀ move changed nearest-neighbour |S21| by 0.035–0.046).

**What changed from the brief, and why.**

- **Frequencies 5.8/5.9/6.0 GHz, comparisons at 5.9.** At the coarser mesh the patch resonates at about 5.88 GHz (S11
  −15 dB at 5.9, −5.7 dB at 5.8), so 5.9 is the operating point; the pitch is 0.508 λ₀ there.
- **7×7 does not fit** (25,704 unknowns, est 28 GB). The fallbacks were taken together: a 3×3 as a second sub-array,
  **1×9 and 1×16 lines along x and y predicted from 1×4s and 1×9s**, the 5×5 predicted from the 3×3, and **E6 = 6×6**,
  solved dense at 5.9 GHz only with the owner's go-ahead (dry run 13 GB, past half the machine; actual 251 s and 5.7 GB,
  after the far-field fix below). The first attempt, with the accelerated solve, peaked at 6.4 GB and was stopped at 30
  minutes.
- **E3 is the 1×4 along x** (|S21| −17.06 dB against −17.71 along y).
- **E7 (thick substrate) and E8 (finite ground on EMerge) were not run.** E8 is not feasible as written: EMerge gives no pattern over a PEC floor (it needs a drawn finite ground and every face absorbing), and the estimate for even the 2×2 is ~153,000 elements (about 38 GB at EMerge's 0.25 MB an element; the 6×6 ~1.4 million). openEMS images a conducting floor and is the route if the edge effect is wanted.
- **Run cost, fixed after the runs** (`src/Engine/Mom/RESOLVED.md` § brief-paa-1): the far field re-evaluated every rooftop spectrum per port and was 26 of 40 s a point on the 3×3; it is now one pass for all ports, bit-identical, 4.2 s. The accelerated dry run now prices the factorisation's peak (3×3: 1.3 GB, measured 1.1-1.3; 6×6: at least 4.7 GB and no factor time, where it said 0.9 GB).
- **Fixtures are 5° and one frequency, as stated, but the 5×5 is 1.9 MB, not ~1 MB** (25 ports × 1,368 directions × two
  components). The 3×3 is included as well, since it is the sub-array the 5×5 was predicted from. They load through
  `EmFieldDataRecord.Open` (gate 4).

**Gates:** 1 — every run's dry-run and actual numbers are in `src/Design/RESOLVED.md` § brief-paa-1. 2 — 9.4e-16 on E4.
3 — tables for E2–E6 and the lines, below and in `src/Design/RESOLVED.md`. 4 — met (sizes above; the 6×6 is 2.7 MB).

**Summary (5.9 GHz, scans 0°, and 30/45/60° in each principal plane and the diagonal):**

| target ← sub-array | level | max |ΔGr peak| dB | max RMS pattern err dB | max |ΔΓact| | RMS |ΔΓact| (worst scan) | max |Prad/Pacc − L2's| |
|---|---|---|---|---|---|---|
| 1×2 x ← isolated | L0 | 1.24 | 4.15 | 0.158 | 0.158 | 0.198 |
| 1×2 y ← isolated | L0 | 0.53 | 3.18 | 0.148 | 0.140 | 0.069 |
| 1×4 x ← isolated | L0 | 2.15 | 3.35 | 0.365 | 0.304 | 0.276 |
| 1×4 y ← isolated | L0 | 1.18 | 2.78 | 0.259 | 0.230 | 0.091 |
| 2×2 ← isolated | L0 | 2.03 | 4.72 | 0.276 | 0.219 | 0.298 |
| 3×3 ← isolated | L0 | 2.79 | 5.08 | 0.483 | 0.315 | 0.457 |
| 5×5 ← isolated | L0 | 3.56 | 4.80 | 0.498 | 0.380 | 0.624 |
| 1×9 x ← 1×4 x | L1 centre | 0.43 | 2.48 | 0.052 | 0.038 | 0.053 |
| 1×9 x ← 1×4 x | L1 classes | 0.26 | 0.80 | 0.042 | 0.035 | 0.041 |
| 1×9 y ← 1×4 y | L1 centre | 0.34 | 2.65 | 0.095 | 0.070 | 0.078 |
| 1×9 y ← 1×4 y | L1 classes | 0.29 | 1.80 | 0.091 | 0.069 | 0.063 |
| 5×5 ← 3×3 | L1 centre | 3.11 | 4.23 | 0.160 | 0.081 | 0.257 |
| 5×5 ← 3×3 | L1 classes | 1.58 | 2.56 | 0.163 | 0.077 | 0.205 |
| 6×6 ← isolated | L0 | 3.61 | 4.39 | 0.502 | 0.389 | 0.659 |
| 6×6 ← 3×3 | L1 classes | 1.99 | 3.32 | 0.190 | 0.096 | 0.255 |
| 6×6 ← 5×5 | L1 centre | 1.11 | 3.51 | 0.095 | 0.046 | 0.105 |
| 6×6 ← 5×5 | L1 classes | 0.75 | 2.65 | 0.114 | 0.039 | 0.073 |
| 1×16 x ← 1×4 x | L1 classes | 0.33 | 0.54 | 0.040 | 0.025 | 0.053 |
| 1×16 x ← 1×9 x | L1 classes | 0.15 | 0.54 | 0.027 | 0.013 | 0.013 |
| 1×16 y ← 1×4 y | L1 classes | 0.30 | 0.87 | 0.098 | 0.083 | 0.052 |
| 1×16 y ← 1×9 y | L1 classes | 0.67 | 1.02 | 0.069 | 0.040 | 0.083 |

L0 rows for the 1×9 lines (from the isolated element): 2.83 / 2.46 / 0.446 and 1.34 / 2.52 / 0.327. Per-scan tables are
produced by `testdata/phased-array/harness/compare.py <target> <sub-array>`.
