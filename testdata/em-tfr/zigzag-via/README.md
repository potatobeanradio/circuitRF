# TFR-V reference runs: the zig-zag film on a level of its own

The structure in `../zigzag/README.md`, with its film moved off Metal1 onto a level with no metal, contacted by a solid via
on each contact's overlap. The resistor's length is unchanged, so its result compares directly with the overlap model. The
routine gates in `tests/Ui.Tests/Em/FilmLevelTests.cs` read what is here. No solver runs in CI.

## The stack

Metal1 sits on 200 nm of dielectric (εᵣ 6.8), over a 50 nm film level with no metal (`tech/zigzag-via-GaAs.ctech`), over
100 µm of GaAs. The band holds a film at 50 Ω/sq on drawing layer 4, and the via `TFR Contact` spans Metal1 to `TFR Level`.

## The runs

Planar kernel, CPU, DC, 1, 5, 10, 20 and 40 GHz. Both `.cem`s name only Metal1, and a drawn film's level joins on its own.

| File | Structure | Unknowns |
|---|---|---|
| `refs/p_dut.s2p` | the zig-zag, `zigzag/` | 4,495 (72 vertical) |
| `refs/p_thru.s2p` | the 2×-thru with no film drawn, `thru/` | 988 |
| `refs/p_thru_noband.s2p` | that thru with no band, `noband/`: 200 nm over 100.05 µm of GaAs, the same total height | 988 |

## What they show

Worst |ΔS| of the four entries:

| Pair | 1 GHz | 5 GHz | 10 GHz | 20 GHz | 40 GHz |
|---|---|---|---|---|---|
| `p_dut` against the overlap run `../zigzag/refs/p_dut.s2p` | 0.0005 | 0.0014 | 0.0026 | 0.0048 | 0.0073 |
| `p_thru` against `p_thru_noband` | 0.00003 | 0.0002 | 0.0003 | 0.0006 | 0.0013 |

DC is 316.6 Ω here and 315.4 Ω in the overlap run. `p_dut` is reciprocal and passive at every point.

## The no-band thru keeps the height

The calibration's feed is three substrate heights. Taking the band out and leaving the rest would shorten it from 300.75 to
300.6 µm against a 300 µm feed, and that decides by round-off whether a uniform lead is grown. The same line then differs by
0.011 at 40 GHz, on plain GaAs with no film (`src/Engine/Mom/RESOLVED.md`, TFR-V), which would hide what this pair asks.

## In 3D

`zigzag/em/sheets.cem`, `solids.cem` and their `thru/` counterparts. Each film section is its DUT de-embedded by half of the same solver's thru,
through `../zigzag/scripts/deembed.py`, which pairs rows by position, so compare a run of other frequencies by frequency. The DC row of every 3D
run is circuitRF's own conduction solve.

| Run | Solver | Metal | Mesh, DUT | Wall clock, DUT + thru | Peak |
|---|---|---|---|---|---|
| PA | Palace | sheet | 113,970 unknowns | 13 min + 7 min | 1.9 GB |
| PB | Palace | 3 µm solid | 114,976 unknowns | 19 min + 15 min | 2.3 GB |
| E | EMerge, Standard | sheet | 27,134 unknowns | 7 s + 6 s | 1.7 GB |
| E accurate | EMerge, Accurate; 1, 20, 40 GHz | sheet | 90,102 unknowns | 19 s + 18 s | 3.5 GB |
| O | openEMS | sheet | 1,125,873 cells | estimated 4.2 h; thru 21 s | |

Palace 0.18.1, EMerge 3.0.0b2, openEMS 0.37.0. CPU, on one machine shared with builds and other EM runs, so the clocks are not costs to compare.
**The 200 nm gap does not blow up a tetrahedral mesh:** the DUT is 12 % larger than the overlap fixture's PA. **It does blow up openEMS**, where it sets a
250 nm cell in z and 1.35 million time steps. That run was stopped at 15 minutes. The thru has no gap to resolve.

Series impedance of the film section, the B term of its ABCD, Ω:

| Run | DC | 1 GHz | 5 GHz | 10 GHz | 20 GHz | 40 GHz |
|---|---|---|---|---|---|---|
| P | 316.5 | 313.3 + 3.3j | 313.3 + 16.4j | 313.2 + 33.3j | 310.4 + 70.8j | 322.3 + 142.6j |
| PA | 309.4 | 304.5 + 2.7j | 305.7 + 13.7j | 308.7 + 27.7j | 319.2 + 58.3j | 351.0 + 131.1j |
| PB | 307.8 | 304.1 + 2.7j | 305.5 + 13.4j | 308.6 + 27.2j | 319.1 + 57.2j | 350.8 + 129.1j |
| E | 309.4 | 295.6 + 1.3j | 296.2 + 7.0j | 298.0 + 14.1j | 304.2 + 28.8j | 321.0 + 60.8j |
| E accurate | 309.4 | 301.4 + 3.1j | | | 310.6 + 66.5j | 332.3 + 145.9j |

Worst |ΔS| of the film sections:

| Pair | DC | 1 GHz | 5 GHz | 10 GHz | 20 GHz | 40 GHz |
|---|---|---|---|---|---|---|
| **P vs PA** | 0.0042 | 0.0056 | 0.0098 | 0.0163 | 0.0239 | **0.0287** |
| **P vs PB** | 0.0051 | 0.0058 | 0.0098 | 0.0161 | 0.0237 | 0.0277 |
| PA vs PB | 0.0009 | 0.0002 | 0.0003 | 0.0004 | 0.0007 | 0.0016 |
| E vs PA | 0 | 0.0272 | 0.0301 | 0.0373 | 0.0547 | 0.0805 |
| E accurate vs PA | 0 | 0.0126 | | | 0.0137 | 0.0117 |
| E accurate vs P | 0.0042 | 0.0096 | | | 0.0207 | 0.0248 |

**P against Palace passes 0.02 through 10 GHz and reaches 0.0287 at 40 GHz.** The owner accepted that, so the gate here is 0.03 above 10 GHz, where EM-TFR's
is 0.02. The overlap fixture's P against its PB is 0.0140 at the same point, and why this stack is twice that is not established. No refined run was made.

**EMerge needs the Accurate preset here.** Its film goes below the level, and at Standard the reactance reads half what Accurate reads. A run's note says so.

`refs/` holds each run's raw `.s2p`.

`results/`, written by any run here, is git-ignored.
