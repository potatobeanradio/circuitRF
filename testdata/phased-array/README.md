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

## `hb-1x4/` — brute-force harmonic balance (PAA-6)

Four amplifiers on the four ports of `results/e3x.cemr`, in one harmonic-balance run, with the pattern formed from that run's
waves (`analysis F1 type=fields block=EM1 waves=HB1`). No EM run: the record is the one above.

| File | What |
|---|---|
| `Array Bench.cnl` | Four P1Tones, each through a `PhaseShifter` (`Phase=-steer(theta0,0,f0,x_n,0)`: the block delays by `Phase`, and `steer()` is the weight's phase) into a copy of the *Harmonic Balance* example's 48 V FET amplifier, retuned for 5.9 GHz, on the record's four ports. HB1 swept over `theta0` ∈ {0, 15, 30, 45}° and `Pin` ∈ {−20, 16, 22} dBm; each PA's Pout and PAE as measures on its own instance. |
| `PA Loadpull.cnl`, `load_grid.gam` | The *Loadpull* example's bench retargeted to the same amplifier at 5.9 GHz (a circuit loadpull), its grid polar around 50 Ω out to \|Γ\| = 0.55, where the active reflections fall. The drain supply is the load tuner's, so the loadpull counts its DC power. |
| `PA Loadpull.spl` | That loadpull's result: the Pout and PAE contours. |
| `Array Bench F1.npy`, `Array Bench HB1.npy` | `circuitrf fields` and `circuitrf hb` of the bench. |
| `Array Bench.cdd` | Each element's active reflection against `theta0` on a Smith chart, the same loci over the PA's Pout and PAE contours, the φ = 0 cut at each `theta0`, the peak realized gain, and each PA's Pout and PAE against `theta0`. |

**5.9 GHz, not the 5.8 GHz the brief names**: the record holds 5.9 GHz only, and `waves=` refuses a harmonic the record does
not hold. The amplifier's matching: a shunt 1.08 pF and series 1.27 nH take 50 Ω to the gate's 10 Ω + 1 pF; a 2.43 nH drain
feed resonates its 0.3 pF drain capacitance. It gives 12 dB small-signal gain, 40 dBm at 3 dB compression and about 69 % PAE
at boresight (its active load there, Γ ≈ 0.34∠2°, sits near the loadpull's 70 % point). Biased near pinch-off it is
potentially unstable (μ just below 1 across the band, as the 2 GHz original is); every point of the bench converges.

Regenerate the results from the repository root with
`circuitrf fields "testdata/phased-array/hb-1x4/Array Bench.cnl" -o ".../Array Bench F1.npy"`, the same with `hb` for
`Array Bench HB1.npy`, and `circuitrf lp ".../PA Loadpull.cnl" -o ".../PA Loadpull.spl"`.
