# EM-TFR reference campaign: a zig-zag resistive film

One structure solved by the planar kernel, Palace, EMerge and openEMS, to judge a resistive film on a metal level. The
routine gates in `tests/Ui.Tests/Em/ResistiveFilmTests.cs` read what is here. No solver runs in CI.

## The structure

100 µm GaAs (εᵣ 12.9, tanδ 0.0006), ground below, 3 µm gold (4.1e7 S/m), a 50 Ω/sq film. In µm, x along the line:

- Feeds: 300 long, 73 wide, at each end.
- Contacts: 20 long, 40 wide, so the film's ends lie at x = 320 and x = 480. In the `.clay` the film overlaps each by 5.
- Film: 40 wide, three 1-square legs joined by four 90° bends between two half-square leads. Up at x = 340..380, across
  at y = 60..100, down at x = 420..460. Four squares of straight film (the legs and the two leads) plus four 0.559-square
  corners is **311.8 Ω**.
- The 2×-thru is the two feeds and their contacts joined, 640 long, with no film.

Each film section is its DUT de-embedded by half of that solver's own thru, through the ABCD square root
(`scripts/deembed.py`). A half-thru with its step is not a symmetric two-port, so this is an approximation every run
shares. The results below compare film sections with each other, never with a closed form.

## The runs

Frequencies are DC, 1, 5, 10, 20 and 40 GHz. The 0 Hz row of every 3D run is circuitRF's conduction solve of the
conductors, the same code in each, which is why they agree exactly at DC.

| Run | Solver | Route | Metal | Film | Mesh | Wall clock, DUT + thru |
|---|---|---|---|---|---|---|
| P | Planar MoM | `zigzag/`, `thru/` | sheet | per-cell Z_s | 4,067 unknowns | 92 s + 24 s |
| HA | Palace | `hand/ha_*` | sheet | sheet | 80,858 unknowns | 47 s + 39 s |
| HA refined | Palace | `hand/ha_refined_*` | sheet | sheet | edge size 0.7×, 162,064 unknowns | 169 s + 82 s |
| HC | Palace | `hand/hc_*` | 3 µm box in 103 µm of GaAs | sheet on its top face, GaAs beneath | 81,804 unknowns | 58 s + 54 s |
| PA | Palace | `em/sheets.cem` | sheet | sheet | 101,994 unknowns | 137 s + 244 s |
| PB | Palace | `em/solids.cem` | 3 µm solid | sheet on its top face | 103,212 unknowns | 955 s + 452 s |
| E | EMerge | `em/sheets.cem` | sheet | 3 µm lossy volume, same σ·t | 21,684 unknowns | 3 s + 3 s |
| O | openEMS | `em/sheets.cem` | sheet | `ConductingSheet` at 1.38 µm, same σ·t | 231,420 cells | 46 s + 21 s |

Palace 0.18.1 (0dc74cd), EMerge 3.0.0b2, openEMS 0.37.0, Gmsh 4.15.2. CPU, 10 processes, on one machine, 2026-11-04. Other
work shared the machine during the PB and PA legs, so those clocks are not costs to compare.

**HF, a 50 nm box of film, was run and dropped.** Palace's solid conductor is a surface impedance in the thick limit, so it
read about a third of an ohm at 1 GHz, where the film is 300 Ω. It answers a different structure.

**The Palace setups state `AdaptiveMaxIterations: 0`**, as the shipped coupled-microstrip example does. With the Standard
preset's refinement on, one wave port read Z_PV near 1 Ω (`src/Design/RESOLVED.md`, EM-TFR).

## Series impedance of the film section, Ω

The B term of its ABCD. Its imaginary part is largely the film's capacitance to ground, ωCR²/6, not inductance.

| Run | DC | 1 GHz | 5 GHz | 10 GHz | 20 GHz | 40 GHz |
|---|---|---|---|---|---|---|
| HA refined | 308.27 | 307.67 + 3.09j | 308.49 + 15.58j | 310.74 + 31.63j | 318.43 + 66.59j | 339.59 + 152.78j |
| HA | 308.27 | 305.59 + 3.04j | 306.44 + 15.31j | 308.83 + 31.07j | 317.09 + 65.38j | 340.80 + 149.63j |
| P | 315.29 | 312.58 + 3.22j | 312.60 + 16.19j | 312.48 + 32.93j | 309.59 + 70.10j | 321.49 + 140.90j |
| PA | 308.27 | 305.58 + 3.03j | 306.43 + 15.30j | 308.79 + 31.05j | 316.94 + 65.31j | 340.21 + 149.25j |
| HC | 307.28 | 305.97 + 2.80j | 307.03 + 14.09j | 309.58 + 28.61j | 318.44 + 60.35j | 344.69 + 138.39j |
| PB | 307.28 | 306.01 + 2.70j | 307.31 + 13.60j | 310.19 + 27.64j | 319.92 + 58.25j | 349.08 + 132.78j |
| E | 308.27 | 306.20 + 2.87j | 307.11 + 14.76j | 309.87 + 29.91j | 320.29 + 62.20j | 356.31 + 135.72j |
| O | 308.27 | 302.42 + 2.73j | 303.00 + 13.49j | 304.79 + 27.12j | 311.80 + 55.60j | 338.46 + 120.18j |

## |ΔS| of the film sections, the worst of the four entries

| Pair | What it asks | DC | 1 | 5 | 10 | 20 | 40 GHz |
|---|---|---|---|---|---|---|---|
| HA vs HA refined | HA's own mesh spread | 0 | 0.0014 | 0.0024 | 0.0043 | 0.0080 | 0.0132 |
| **P vs HA refined** | the thing under test | 0.0041 | 0.0032 | 0.0044 | 0.0066 | 0.0081 | 0.0111 |
| PA vs HA | the generated sheets against the hand-built ones | 0 | 0.0000 | 0.0001 | 0.0002 | 0.0003 | 0.0007 |
| PB vs HC | the generated solids against the hand-built ones | 0 | 0.0002 | 0.0010 | 0.0018 | 0.0031 | 0.0063 |
| HC vs HA | what 3 µm of metal changes | 0.0006 | 0.0012 | 0.0013 | 0.0022 | 0.0041 | 0.0081 |
| E vs PA | a second FEM | 0 | 0.0033 | 0.0058 | 0.0101 | 0.0173 | 0.0251 |
| O vs PA | a time-domain solver | 0 | 0.0021 | 0.0032 | 0.0054 | 0.0112 | 0.0239 |
| P vs PA | the planar solve against the same Palace run | 0.0041 | 0.0043 | 0.0066 | 0.0101 | 0.0137 | 0.0125 |
| E vs O | the other two against each other | 0 | 0.0033 | 0.0039 | 0.0056 | 0.0090 | 0.0123 |

## What this does and does not establish

**P agrees with Palace to about HA's own mesh spread**, and the gate holds it to 0.02.

**DC: the two methods bracket the closed form rather than agreeing.** P-DC is 315.3 Ω and falls with refinement (313.6 Ω at
8 cells across, 312.9 Ω at 16). The 3D DC row is 308.3 Ω. 311.8 Ω lies between them. The difference is 2.3 %, not 1 %.

**A film sharing a 3 µm metal's top surface barely differs from one sheet at one height.** The film lies on GaAs, the band
beside the metal being filled by it. Against the sheet model that moves the film section by at most 0.008 up to 40 GHz.

**Three solvers agree once each is told what it can solve.** Up to 20 GHz P, E and O are all within about 0.017 of PA, and at
40 GHz within 0.025, which is the uncertainty P is judged within. What each needed is in `src/Design/RESOLVED.md`, EM-TFR:

- **EMerge takes the film as a volume**, because its thin-conductor boundary read this film 13 % low on the shipped mesh.
- **openEMS writes the film thicker at the same σ·t**, because at 50 nm its sheet model's stabilising capacitor bypasses the
  film, and **resolves it six cells across**, because two read the corners' resistance short by 9 %.

These rules were measured at 50 Ω/sq on this fixture and on a straight strip. They apply from 10 Ω/sq, and nothing between
10 and 50 Ω/sq was measured.

The reactance is where O and P differ most from Palace at the top of the band: 120 and 141 Ω at 40 GHz against 149 Ω.

## Files

- `hand/`: the hand-built `.c3d`s. No generator wrote them from the layout, and they read `tech/hand-materials.ctech`.
- `zigzag/`, `thru/`: the `.clay`s and `.cem`s, on `tech/mmic-GaAs_2LM_100um.ctech`.
- `refs/`: each run's raw `<run>_dut.s2p` and `<run>_thru.s2p`, and its de-embedded `film_<run>.s2p`.
- `results/`, written by any run here, is git-ignored.
