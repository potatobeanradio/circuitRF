# Phased-array fixtures (PAA-1)

Planar EM results of a patch array, used by the phased-array series (`docs/briefs/brief-paa-*.md`) to test the array
engine against arrays that were solved whole. How they were made and what they showed is in
`docs/briefs/brief-paa-1-em-measurement.md` (completion note) and `src/Design/RESOLVED.md` (§ brief-paa-1).

## The element and the arrays

The *Patch Antenna* example's inset-fed patch (16.94 × 13.3 mm) on its 30 mil, εr 3.66 laminate over an infinite
ground, fed by its 5 mm line ending in a de-embedded edge port. Arrays at 25.86 mm in both directions (0.508 λ₀ at
5.9 GHz). Element n is port n, numbered row-major from the top left seen from +z; its reference point is the patch
centre, at `((c − (C−1)/2)·25.86 mm, ((R−1)/2 − r)·25.86 mm)` for row r and column c from 0. Columns run along x
(the H-plane of the element), rows along y (its E-plane).

| Cell | Array | Ports |
|---|---|---|
| `e1` | one element | 1 |
| `e3x` | 1×4 along x | 4 |
| `e33` | 3×3 | 9 |
| `e5` | 5×5 | 25 |
| `e66` | 6×6, the reference the 5×5 is gated against (5.9 GHz only) | 36 |

Mesh: `PlanarMesh` `Auto` off, 12 cells per wavelength, edge mesh off (coarser than the example's, so the arrays fit;
the patch resonates at about 5.88 GHz on it). Solved at 5.8, 5.9 and 6.0 GHz (the 6×6 at 5.9 GHz only) with the dense solve.

## The reduction

`results/` holds each run **reduced to 5.9 GHz and a 5° grid** (θ 0…90°, φ 0…355°) by `harness/reduce.py`, which
SAMPLES the stored arrays: nothing was solved again, and every value present is the full run's value at that point (a
pattern sampled this way equals the full one at the same directions to 4e-16). Scalars that the run integrated over its
1° grid (a realized gain, a radiated power) are the full run's numbers and are not recomputed for the coarser grid.
Stamps are unchanged, so each record opens through `EmFieldDataRecord.Open`. Sizes: 110 KB, 313 KB, 690 KB, 1.9 MB and 2.7 MB.

The technology file and its laminate layer carry neutral names; the stored results do not depend on either.

## The harness

`harness/` is the scratch comparison PAA-1 ran (not a test, not in the solution): `gen_arrays.py` writes the cells,
`wd_run.sh` runs an EM setup under a memory and time watchdog, `extract.py` and `paa1.py` take each port's terminated
embedded pattern from circuitRF's own `fields` analysis (one drive per port), and `compare.py`, `classes.py` and
`summary.py` form L0, L1 and L2 and the tables. Set `PAA1_WS` to a scratch workspace holding full results to rerun it;
`CRF_CLI` overrides the CLI it calls (default: the Release build in this repository).

`paa2_reference.json` is that comparison run on the fixtures themselves (6×6 from the 5×5 and the element, ten scans), made by
`harness/paa2_reference.py`; the array engine's tests (`PhasedArrayRecordTests`) reproduce it to 1e-9.
