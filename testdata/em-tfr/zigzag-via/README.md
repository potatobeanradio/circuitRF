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

`results/`, written by any run here, is git-ignored.
