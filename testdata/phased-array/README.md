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

### `Array Envelope.cnl` — brute-force envelope (PAA-6b)

The same four amplifiers on the same record, driven by **one** NR 20 MHz 64QAM carrier at 5.9 GHz (`MOD_Source`) through an ideal
4-way `Combiner` into the PhaseShifters. ENV1 runs 40 µs, swept over `Pavg` ∈ {−8, 22} dBm and `theta0` ∈ {0, 30}°; F1
(`waves=ENV1 dirs="peak;0,0;-30,0;60,0"`) gives the radiated envelope in those directions with its PSD, ACLR, EVM, PAPR and CCDF,
each element's active reflection over the record, and the time-averaged pattern. `Array Envelope.cdd` draws the clouds over the
contours, the PSD per direction, ACPR1 and EVM against drive per direction, and the averaged cut.

**Its result is not committed**: 19,662 steps at four sweep points make `Array Envelope F1.npy` 34 MB (it is in this folder's
`.gitignore`). Regenerate it in about 30 s with
`circuitrf fields "testdata/phased-array/hb-1x4/Array Envelope.cnl" -o "testdata/phased-array/hb-1x4/Array Envelope F1.npy"`.
40 µs is where in-beam ACLR is within 0.2 dB of a 200 µs record; EVM is still 0.4 pp low there and creeps up with more symbols.


## `receive-8x8/` — the array as a receiver (PAA-7)

An 8 × 8 Feed-mode array on `results/e5.cemr` (embedded level, position classes), Taylor −25 dB, each element through a channel of an
LNA (20 dB, NF 1.5 dB, OIP3 +10 dBm) and a 3 dB attenuator, under a sky of 5 K overhead rising to 285 K at the horizon
(`Tsky=5+280*exp(-el/0.0873)`; the planar record's ground plane hides `Tground`). No EM run.

| File | What |
|---|---|
| `Receive Bench.cnl` | The whole chain: the array, a mixer (LO 5.5 GHz, +7 dBm) and a 400 MHz IF filter after the feed, a signal of −90 dBm/m² from boresight. NF1 (S-parameter noise at the feed), F1 (the pattern from the same port) and the receive measures; HB1 two tones, the signal and the LO. |
| `Interferer Bench.cnl` | The same front end, the feed loaded by 50 Ω as the mixer's RF port loads it in harmonic balance, and two equal interferers (5.92 and 5.94 GHz, −40 dBm/m²) from `thetaI` in φ = 0, swept 0…30°. Their third-order product falls at 5.90 GHz, on the signal. |

Measured (the linear measures are `PhasedArrayReceiveTests.TheReceiveBench_ReadsItsReceiveMetrics`; the HB numbers are `circuitrf hb` of each
bench, about one and two minutes in a Debug build):

| | |
|---|---|
| Antenna temperature `AntennaTempK(F1)` | 98.6 K (the sky's 9.5 K pattern average over the element's radiated share, the rest — dielectric loss and the surface wave — at 300 K) |
| System temperature `SystemTempK(F1, NF1)` | 242.2 K |
| G/T `GoverTDbK(F1, NF1)` | −2.70 dB/K |
| SNR of the signal in 1 MHz `SnrDb(F1, NF1, 1, 1e6)` | 9.02 dB |
| Signal at the feed / at the IF output (HB1) | −97.58 dBm / −105.08 dBm |

| `thetaI` | interferer at one LNA input | at the feed | IM3 at the feed (5.90 GHz) | SFDR |
|---|---|---|---|---|
| 0° | −73.33 dBm | −44.09 dBm | −170.5 dBm | 80.3 dB |
| 10° | −73.54 dBm | −50.28 dBm | −177.6 dBm | 82.7 dB |
| 20° | −73.80 dBm | −77.40 dBm | −199.2 dBm | 89.9 dB |
| 30° | −73.69 dBm | −81.71 dBm | −176.8 dBm | 82.4 dB |

**What reaches the channels is the element's pattern, not the array's**: at every angle the interferers arrive at each LNA within 0.5 dB of
each other, and the channels compress and intermodulate by that. The beamformer then suppresses both the interferers and their in-band product
by the array pattern at `thetaI` (20° sits near a null of the pattern at 5.90 GHz, 30° on a sidelobe). The SFDR is referred to the incident
density at the array: the IM3 at the feed is turned into the boresight density that would deliver the same power there (the signal's gain from
density to feed, −90 → −97.58 dBm, so −7.58 dB), giving the interferers' input third-order intercept density IIP3 = Pd + (Pd − Pd_IM3)/2; the
noise floor density in 1 MHz is the signal's density less its SNR, −99.02 dBm/m²; and SFDR = ⅔·(IIP3 − floor). The interferers are 63 dB below the
LNA's input intercept, so the products follow their 3:1 slope.

Regenerate with `circuitrf hb "testdata/phased-array/receive-8x8/Receive Bench.cnl"` and the same for `Interferer Bench.cnl`.
