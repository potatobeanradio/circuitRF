# circuitRF — 3D EM, F0 spike: findings

**Status:** F0 complete on macOS; Windows and Linux installs are the owner's (§1) · **Date:** 2026-09-24 ·
**Brief:** [`brief-em3d-1-f0-spike.md`](../sonnet-briefs/brief-em3d-1-f0-spike.md) ·
**Design note:** [`em-3d.md`](em-3d.md) (rev 5 carries the corrections this spike forced) ·
**Data:** [`testdata/em3d/f0/`](../../testdata/em3d/f0/README.md) — every input, every port file, every
command and timing.

Every number below comes from a file under `testdata/em3d/f0/`; the file is named beside the claim.
"Palace" means Palace 0.18.1 (git `0dc74cd`), "openEMS" openEMS 0.37.0-rc3 (git `67d3784`), "Gmsh"
Gmsh 4.15.2 — the validated list proposed in §6.

---

## 0. The answers in one screen

| # | Question | Answer |
|---|---|---|
| Q1 | Agreement | **Case A:** the three Palace wire models agree with each other to 0.06 dB; openEMS converges onto Palace (0.05 dB at 10 GHz once the grid resolves the wire, §Q2); kernel W agrees on \|S21\| to 0.08 dB below 10 GHz but reads **197 pH less inductance** — its terminal is the wire end, Palace's includes the pads and the 100 µm port drop. **Case B:** Palace and openEMS agree on \|S21\| to **0.035 dB** over 0.1–20 GHz and on phase to **0.6 %** of electrical length; circuitRF's planar MoM **refuses case B** |
| Q2 | FDTD on the bond wire | A grid step of **half the wire radius** (6.35 µm) in the wire's box gets within 0.1 dB of Palace at 10 GHz; the thin-wire model does not get there at any usual grid (1109 pH against Palace's 880, 0.47 dB off at 25 µm). Cost figures in §Q2 |
| Q3 | Hexagon vs round | The hexagon dissipates **+1.7 % at 1 GHz, +5.0 % at 10 GHz, +2.5 % at 40 GHz** more than the round wire of equal perimeter — measured, stable to 0.02 % under mesh refinement. Palace's boundary model itself is 3 % low on a round wire at 10 GHz (§Q6), so read the 10 GHz figure as 5 ± 3 % |
| Q4 | The foot | A 2d wedge foot changes \|S21\| by **0.003 dB** and L by **−1.4 pH (−0.16 %)** at 10 GHz — the same size as the mesh-refinement spread on \|S21\|. **D2 is close to a formality** for S-parameters |
| Q5 | `.geo` provenance | **Works, with caveats** (four, listed in §Q5). The fragment map is not available in `.geo`; the §1e recipe replaces it and recovered every named object in all six geometries |
| Q6 | Palace conductor model | **Not the same at 1 GHz.** A void with Palace's conductivity boundary is the flat-surface impedance: 9.5 % below the exact round-wire resistance at 1 GHz, 3.1 % at 10, 1.5 % at 40. A **zero-thickness sheet with `Thickness`** matches loss (0.02 dB) but not phase (1 % of electrical length) or match (\|S11\| up to 28 dB different) |
| Q7 | openEMS probe timing | Confirmed: current probes start at **t = Δt/2** (1.74247895953e-14 s in a 3.48496e-14 s step), voltage probes at t = 0 |
| Q8 | Triangle import | **STL and PLY both read** (`PolyhedronReader`). A missing file is **silently skipped**: a warning, exit code 0 |
| Q9 | Palace capability probe | `palace --version` prints a **git hash** and a schema version, **nothing about the build** — no solver list. The cheapest probe is a `--dry-run` of a config naming the solver, which exits within 0.03 s |
| Q10 | Palace schema | Installed as a file (`<prefix>/bin/schema/config-schema.json`, draft-07, `$id` `urn:palace:schema:1-7-0`); the bundled validator needs Julia; `--dry-run` validates against the embedded copy |
| Q11 | openEMS output | Probe files: `%` header lines then `t/s value` columns. **Exit code 0 whether or not the end criterion was met**; 1 on an unreadable XML; SIGABRT (134) on an unknown option |
| Q12 | Install cost | macOS: Palace **52 min, three failures** before it built (35 min of it building); openEMS 8 min, first time; Gmsh 1 min. Windows and Linux: **not done** (owner) |
| D4 | F1 or F1b first | **F1 (Palace) first** — §5 |

---

## 1. What was done, and by whom (R-em3d1-1a)

| Part | Who | State |
|---|---|---|
| Install Gmsh, openEMS, Palace on macOS arm64 | agent | **done** — log in `testdata/em3d/f0/README.md` §Install |
| Case A through Palace ×3 (round, hexagon, hexagon + foot) | agent | **done**, plus a PEC-wire twin, two mesh-refinement runs and the Q6 pair |
| Case A through openEMS | agent | **done** — thin-wire run plus a four-step grid ladder (Q2) |
| Case A through kernel W | agent | **done** — distributed (kernel W proper, from a scratch harness calling the GUI's own export, because no CLI verb reaches it) and lumped (the netlist route) |
| Case B through Palace | agent | **done** — lossy, a lossless twin, and the Q6 sheet variant |
| Case B through openEMS | agent | **done** — lossless and lossy |
| Case B through circuitRF's planar MoM | agent | **not done: the solver refuses the geometry**, three ways, verbatim in `B-via/planar/REFUSED.md` (§Q1) |
| Three clean installs of each solver on Windows (openEMS, Gmsh native; Palace in the Linux subsystem) | owner | **not done** |
| Three clean installs of each solver on Linux x64 | owner | **not done** |
| Three clean installs on macOS | agent | **not done** — one install each; repeating a 35-minute Spack build twice more was not a good use of the machine for this spike, and the three failures met are recorded |
| A default foot length and ball size from assembly data (§6.6, D2) | owner | **not done** — Q4 says how much it matters |

## 2. The machine

Apple M4 (10 cores, no SMT), 16 GB, macOS 27.0 (26A428), Homebrew 6.0.21, Xcode command-line tools,
Homebrew GCC 16.2.0. Every Palace run used `palace -np 8`; openEMS used its default thread count. No
two solver runs overlapped except where a run's row says so.

---

## Q1. Agreement

**Extraction.** S-parameters are compared directly (\|S21\| in dB, arg S21, \|S11\|). For case A the
series inductance and resistance are those of the π-model: **Z_series = −1/Y21**, L = Im(Z)/ω,
R = Re(Z), with Y from S at 50 Ω (`tools/summarize.py`). The π-model is a fair reading of a bond
wire up to about 20 GHz; above that it absorbs the shunt and radiation effects and its R stops being
physical (it goes negative at 40 GHz on a mesh-refined run), which is why Q3 uses dissipated power
instead. Spreads are the **largest** difference inside each band (`tools/spread.py`).

### Case A — one 1 mil gold wire over ground (1–40 GHz)

| | \|S21\| 1 GHz | \|S21\| 10 GHz | \|S21\| 40 GHz | L 1 GHz | R 1 GHz |
|---|---|---|---|---|---|
| Palace, round (gold boundary) | −0.0201 dB | −0.7560 dB | −5.259 dB | 901.1 pH | 135.4 mΩ |
| Palace, round, PEC wire | −0.0078 | −0.7118 | −5.216 | 879.5 | — |
| Palace, hexagon | −0.0204 | −0.7704 | −5.314 | 907.7 | 137.8 |
| Palace, hexagon + 2d feet | −0.0204 | −0.7672 | −5.306 | 906.3 | 138.0 |
| openEMS, PEC wire, true radius, step 6.35 µm | −0.0092 | −0.7647 | −5.651 | 922.2 | — |
| openEMS, PEC wire, true radius, step 3.175 µm | — | −0.7044 | −5.418 | 889.0 | — |
| openEMS, thin PEC curve, step 25 µm | −0.0139 | −1.1862 | −7.286 | 1108.9 | — |
| kernel W, distributed (24 or 96 segments: same to 0.001 dB) | −0.0200 | −0.6796 | −4.892 | 704.1 | 145.9 |
| wBond lumped model (the netlist route) | −0.0201 | −0.6798 | −4.809 | 704.1 | 145.9 |

Spread per decade (max over the band):

| Pair | 1–10 GHz | 10–40 GHz |
|---|---|---|
| Palace round ↔ Palace hexagon | 0.014 dB, 0.15°, 6.6 pH | 0.055 dB, 0.18° |
| Palace hexagon ↔ hexagon + foot | 0.003 dB, 0.03°, 1.5 pH | 0.010 dB, 0.04° |
| Palace round ↔ same after 2 AMR iterations (the mesh's own spread) | 0.003 dB, 0.05°, 0.2 pH | 0.006 dB, 0.32° |
| Palace round PEC ↔ openEMS 6.35 µm | 0.053 dB, 2.1°, 43 pH | 0.44 dB, 6.0° |
| Palace round PEC ↔ openEMS 3.175 µm | 0.008 dB, 1.2°, 11 pH | 0.20 dB, 5.1° |
| Palace round PEC ↔ openEMS thin curve 25 µm | 0.47 dB, 7.1°, 229 pH | 2.07 dB, 9.8° |
| Palace round ↔ kernel W | 0.076 dB, 12.6°, **197 pH** | 0.37 dB, 47° |

**Kernel W's 197 pH is a reference plane, not a disagreement between solvers** (em-3d.md §4.4).
Kernel W's two terminals are the wire's own end points referenced to the ground plane; Palace's ports
sit at the pads' outer edges and include 62 µm of pad plus a 100 µm vertical drop to ground at each
end. Kernel W also has no substrate (εr 9.8 under the pads) and no pads, which is where the phase
difference comes from. Its resistance agrees to 8 % (145.9 vs 135.4 mΩ at 1 GHz; the exact
round-wire value is 150.8, §Q6). A like-for-like kernel W reference needs brief 10's reference-plane
machinery or a hand de-embedding of the pad-and-drop fixture; neither is in F0's scope, and a fixture
run in Palace is the cheapest next step (it would cost one 2.5-minute run).

### Case B — microstrip → via → inverted microstrip (0.1–20 GHz)

| | \|S21\| 1 GHz | \|S21\| 10 GHz | \|S21\| 20 GHz | arg S21 10 GHz | arg S21 20 GHz |
|---|---|---|---|---|---|
| Palace, copper σ 5.8e7, tanδ 0.004 | −0.0247 dB | −0.1846 | −0.3375 | 146.95° | −66.36° |
| Palace, lossless twin | −0.0006 | −0.0664 | −0.1345 | 147.39 | −65.73 |
| openEMS, lossless, step 25 µm | −0.0002 | −0.0319 | −0.1375 | 149.45 | −62.46 |
| openEMS, κ fitted at 10 GHz, PEC copper, stopped at 2.0 ns | −0.0537 | −0.0856 | −0.1921 | 149.45 | −62.46 |
| circuitRF planar MoM | **refused** | | | | |

| Pair | 0.1–1 GHz | 1–10 GHz | 10–20 GHz |
|---|---|---|---|
| Palace lossless ↔ openEMS lossless | 0.0004 dB, 0.24° | 0.034 dB, 2.1° | 0.034 dB, 3.3° |
| Palace lossy ↔ Palace lossless (what loss is worth) | 0.024 dB | 0.118 dB | 0.203 dB |
| Palace lossy ↔ openEMS lossy | 0.048 dB, 0.37° | 0.099 dB, 2.5° | 0.145 dB, 3.9° |

The lossy pair differs for two stated reasons, both expected: openEMS's copper is PEC (no conductor
loss — that alone is most of Palace's lossy-minus-lossless column), and its dielectric loss is a
conductivity fitted at 10 GHz, which **over-states the loss below band centre and under-states it
above** exactly as overview §1k predicts: at 1 GHz openEMS's dielectric-only loss (−0.054 dB)
exceeds Palace's dielectric-plus-copper loss (−0.025 dB), because a conductivity fitted at 10 GHz is
a tanδ of 0.04 at 1 GHz.

The phase difference between Palace and openEMS grows **linearly** with frequency (−0.24° at 1 GHz,
−2.1° at 10, −3.3° at 20): openEMS's line is 0.6 % shorter electrically, the signature of a 25 µm
grid on 35 µm-thick copper and a staircased round barrel. \|S11\| sits between −25 and −70 dB for both
solvers, where each resolves a different residual; the two are **not** compared on \|S11\| below −30 dB,
and brief 10 should not be either.

**Planar MoM refuses case B, three ways** (verbatim in `B-via/planar/REFUSED.md`):
1. as drawn, naming the middle plane as the return: *"1 via(s) join 'Top Copper' to 'Bottom Copper'
   without touching 'Ground Plane'. These are signal vias, not stitches: they are NOT modelled"*, and
   port 2 is refused as not on any conductor of the signal layer;
2. with both copper levels analysed: *"… 'Ground Plane' … is NOT below the lowest analysis level
   'Bottom Copper' … A return plane must lie BENEATH the conductor it feeds"*;
3. with the plane as ordinary meshed copper and open top and bottom: *"has no ground plane below the
   lowest analysis level"*.

The planar solver's ground reference is always the laterally infinite boundary its Green's function
terminates on, so a signal on both sides of it cannot be stated. **The brief's premise that case B
"is also solvable by circuitRF's planar MoM with vias (L9)" is wrong**; the three-way comparison is
two-way. em-3d.md §1's table now says a signal via through a reference plane is 3D's to solve.

## Q2. FDTD on the bond wire

The target is Palace's **PEC-wire** twin (\|S21\| = −0.7118 dB at 10 GHz), because openEMS's wire is
PEC too: the gold wire's own loss is 0.044 dB of \|S21\| at 10 GHz (Palace gold vs PEC), a sizeable
fraction of the 0.1 dB criterion.

| Model, grid step in the wire's box | Cells | Time step | Steps to −50 dB | Wall | Peak RSS | \|S21\| 10 GHz | vs Palace PEC | L 1 GHz |
|---|---|---|---|---|---|---|---|---|
| thin PEC curve on grid edges, 25 µm | 55,025 | 3.48e-14 s | 28,035 | 4.1 s | 31 MB | −1.1862 dB | −0.474 dB | 1108.9 pH |
| PEC wire of true radius, 12.7 µm (= r) | 130,900 | 2.01e-14 s | 12,090 | 4.2 s | 40 MB | −1.0540 | −0.342 | 1061.7 |
| same, **6.35 µm (= r/2)** | **400,816** | 8.57e-15 s | 22,568 | **12.7 s** | **67 MB** | **−0.7647** | **−0.053** | 922.2 |
| same, 4.23 µm (= r/3) | 853,332 | 4.88e-15 s | 31,360 | 35.0 s | 113 MB | −0.7249 | −0.013 | 901.0 |
| same, 3.175 µm (= r/4) | 1,558,730 | 3.50e-15 s | 42,018 | 65.8 s | 182 MB | −0.7044 | +0.007 | 889.0 |
| Palace, round PEC wire (the target) | 954 k unknowns | — | — | 108.5 s, 8 ranks | 7.7 GB | −0.7118 | — | 879.5 |

(`A-bondwire/openems/*/run.log`; openEMS on all cores.) **Answer: about 400,000 cells, a grid step of
half the wire's radius in a box around it, 13 seconds and 67 MB** gets within 0.1 dB of Palace at
10 GHz; r/4 gets within 0.01 dB in a minute. The thin-wire model is the wrong tool at any grid: a PEC
curve's effective radius is a fraction of the *cell*, so it reads 26 % too much inductance at 25 µm
and gets worse, not better, as the grid is refined. At 40 GHz even r/4 is still 0.20 dB off — the
staircased 45° legs cost more as the wavelength shrinks. **Two readings of this for §4.3's guidance
text:** FDTD *can* do a bond wire on a laptop, cheaply, if the grid resolves the wire's radius; and
the cells it needs are set by a 25 µm feature in a 3 mm box, which is what makes a 200-wire package
the case FDTD struggles with (every wire's box is refined, and the time step falls with the finest
cell).

**Two findings for brief 8 (the grid).** (1) A grid step that resolves the wire is set by the *wire*,
not by the wavelength: the step that meets 0.1 dB is λ/1000 at 40 GHz in the substrate. (2) The first
ladder attempt ran ten times slower than it needed to: fixed lines (pad edges, the crest height) sat
within 0.6 µm of fill lines, and one sub-micron cell set the time step for the whole grid (1.2e-15 s on
a 12.7 µm grid). Merging lines closer than half a step before smoothing (`make_case.py` `merge`)
fixed it. **The grid generator must merge near-coincident lines, and report the smallest cell and the
feature that made it** — em-3d.md §6.5 already asks for the second; F0 shows the first is not optional.

## Q3. Hexagon vs round — the corner crowding

Measured as **dissipated power**, 1 − \|S11\|² − \|S21\|², which in case A is the wire's ohmic loss
alone (PEC box, PEC pads, lossless substrate) and needs no circuit model:

| f | round | hexagon | hexagon / round | same, after 2 AMR iterations |
|---|---|---|---|---|
| 1 GHz | 2.7009e-3 | 2.7458e-3 | **1.0166** | 1.0166 |
| 10 GHz | 7.5447e-3 | 7.9208e-3 | **1.0498** | 1.0499 |
| 40 GHz | 1.4049e-2 | 1.4397e-2 | **1.0248** | 1.0265 |

(`A-bondwire/palace-round`, `palace-hex`, `palace-*-amr`.) At 1 GHz the current still fills much of
the section (δ = 2.49 µm against a 12.7 µm radius) and the hexagon's 9.3 % smaller area is visible;
by 10 GHz the corners crowd the current and the excess is 5 %. It falls again at 40 GHz, where the
standing-wave current distribution along the wire changes. **Uncertainty:** both are Palace's
flat-surface impedance model, which on the round wire is 3.1 % below the exact Bessel-function
resistance at 10 GHz (Q6) and whose error on a hexagon's corners is not known. The honest statement
is: *the perimeter-matched hexagon loses 2–8 % more than the round wire at 10 GHz, most likely ~5 %*.
It is not "small" in the sense of negligible, and it is not large against the 9.3 % DC excess the note
already states. Brief 4 should publish it beside the hexagon default rather than hide it.

## Q4. The foot

| at 10 GHz | hexagon | hexagon + 2d feet | difference |
|---|---|---|---|
| \|S21\| | −0.77036 dB | −0.76723 dB | **0.0031 dB** |
| L (π-model) | 874.53 pH | 873.11 pH | **−1.42 pH (−0.16 %)** |
| dissipated power | 7.9208e-3 | 7.9358e-3 | +0.19 % |

The mesh-refinement spread on the same quantities is 0.003 dB and 0.2–1.5 pH. **The foot is at the
level of the solver's own convergence on \|S21\|**; on L it is a real 1.4 pH, about the inductance of
7 µm of wire. The foot's default barely matters for S-parameters; it matters for **meshability**, which
was its purpose (§6.6), and all three geometries meshed without trouble. **D2 is close to a
formality**, and the owner's assembly data can set it whenever it arrives.

## Q5. Gmsh provenance from `.geo`

**Works, with caveats.** The `.geo` language returns `BooleanFragments`' output list and no
input→output map. Overview §1e's recipe replaces it, and in all six geometries of this spike
(`A-bondwire/palace-*/case.geo`, `B-via/palace*/case.geo`) every named object came back with exactly
the count expected, every face of the model was classified exactly once, and every volume kept its
tag through the fragment (the `tag_* before N after N` lines of each `entities.txt`). The caveats, each
of which the writer in brief 7 has to carry:

1. **OCCT's bounding boxes are loose on curved faces.** Without `Geometry.OCCBoundsUseStl = 1`, the
   air volume matched *nothing* and the wire got 1 face of 3: a cylinder that meets the pad plane has a
   box that dips below it. With the option set, every count was right. Set it always.
2. **Faces with the same x-y extent are told apart only by z.** The antipad's cylindrical wall and the
   fill's two annular faces share one footprint; the writer must query by *plane* (a zero-thickness
   box) for flat faces and subtract. The selection is set algebra (`a[] -= b[]`), and the writer should
   generate it, not rely on one box per object.
3. **Close the books.** The recipe is only safe because the script counts *everything*: for case B,
   6 + 15 + 7 + 2 + 2 named faces + 12 dielectric interfaces = 44 = all surfaces. A face that belongs
   to no group becomes Palace's natural boundary — a **PMC**, silently. The writer's check is "every
   face classified once, and the unclassified ones are all interfaces between two volumes".
4. **OCCT's own fragility shows up at the writer, not at run time.** A round wire built from cylinders
   and joint spheres of the wire's radius fails the union (`BOPAlgo_AlertIntersectionOfPairOfShapesFailed`:
   the sphere is tangent to both cylinders). Mitred cylinders — each segment cut on the bisector plane —
   union cleanly and reproduce πr²L to 0.2 %. Generate mitres; never tangent primitives.

Also measured, for brief 7's error handling: a `.geo` syntax error still leaves Gmsh **writing a mesh**
and exiting 1 — the exit code is the only signal, never the presence of the `.msh`; after certain
syntax errors Gmsh 4.15.2 **segfaults** (exit 139) instead. `-setnumber` does not override a
`DefineConstant` when given after the file name; the writer should write the value into the script.

## Q6. Palace conductor model

**Case A — a void with the conductivity boundary vs a meshed gold volume.** Not the same at 1 GHz:

| at 1 GHz | void + boundary, order 2 | void + boundary, order 1 | meshed gold, order 1 | exact (Bessel) |
|---|---|---|---|---|
| R (π-model) | 135.4 mΩ | 135.4 | 164.3 | **150.8** |
| L | 901.1 pH | 889.9 | 890.4 | — |
| unknowns | 954 k | 178 k | 856 k | — |
| Palace time, memory | 147 s (40 pts), 7.4 GB | 9.6 s, 6.6 GB | 108 s, **10.5 GB** | — |

The void model is exactly the **flat-surface** impedance (136.5 mΩ by hand): it lacks the curvature
term, R/R_dc ≈ a/2δ **+ 1/4**, which on a 1 mil wire is 9.5 % at 1 GHz, 3.1 % at 10 GHz and 1.5 % at
40 GHz. The meshed volume, at the element order this machine can afford (order 2 would be ~4.5 M
unknowns), over-reads by 9 %. Inductance agrees at equal order to 0.07 %. **Conclusion for brief 7:**
the void model is the only practical one and is good to ~3 % above 10 GHz on a bond wire; below a few
GHz its wire resistance is low by up to 10 %, which the result's notes should say for any conductor
whose radius is under ~10 skin depths.

**Case B — a zero-thickness sheet with `Thickness: 35` vs the 35 µm-thick line.** Loss matches
(\|S21\| within 0.021 dB everywhere), **geometry does not**: the sheet line is ~1 % longer electrically
(+2.4° at 10 GHz, +4.6° at 20 GHz) and its match differs by up to 28 dB in \|S11\| (−57 vs −29 dB at
5 GHz, where the thick line happens to sit on a minimum). `Thickness` corrects the metal's loss, not
the capacitance its height adds. For a 35 µm line on 200 µm of dielectric that is a 1 % effect; the
note's "thin metal is a sheet … wherever its thickness is small against … the neighbouring
dimensions" (§6.1) stands, and brief 7 needs a numeric threshold, which this pair suggests is well
below t/h = 0.17.

## Q7. openEMS probe timing

From the pinned version's own files (`A-bondwire/openems/curve-dw25/port_ut_1`, `port_it_1`):
the voltage probe's first sample is at **t = 0**, the current probe's at **t = 1.74247895953e-14 s** —
exactly half of that run's time step (3.48496e-14 s; the same half-step in every run of the spike).
Both then sample every 89 steps (3.1016e-12 s), so the offset persists in every sample. openEMS's own `CalcPort` uses each file's time column; the spike's
own arithmetic (`openems/source/postproc.py`) does the same and **agrees with `CalcPort` to 0.0**
(1e-16) in every run. Brief 9's closed-form gate is the right one.

## Q8. CSXCAD triangle import

**STL and PLY are both read.** A PEC block read from each file changed a port's peak voltage from
4.722e-4 to 2.636e-4, identically for both formats (`probes/q8-polyhedron/q8.py`; the XML element is
`<PolyhedronReader FileName="…" FileType="STL"/>`). **A file that does not exist is not an error**:
openEMS prints `Warning: No primitives found in property: block!`, solves without the solid and exits
0 — the result was identical to having no block at all. The backend must check the file itself.

## Q9. Palace capability probe

```
$ palace --version
Palace version: 0dc74cd
Schema version: 1-7-0
```

That is the whole of it: **a git hash, not "0.18.1"** (the build came from the `v0.18.1` tag; the log
header says `Git changeset ID: 0dc74cd` and `postpro/palace.json` carries `"GitTag": "0dc74cd"`), and
**nothing naming the eigensolver**, the direct solver or any other build option. The binary's only
flags are `--help`, `--version`, `--dry-run`. Consequences for brief 6:
- **Version check:** discovery must map hashes to releases (`0dc74cd` ↔ v0.18.1), or accept the schema
  version as the compatibility key. The schema-compatibility table shipped with this release
  (`schema-compatibility.json`) lists 1-6-0 for 0.18.0 and **has no row for 1-7-0**, the schema this
  build reports — the table lags the release.
- **Capability probe:** `--dry-run` validates the configuration against the embedded schema and exits
  in 0.03 s, but it also **opens the mesh** — it is a probe only with a real mesh file beside the
  config. A missing mesh and a schema violation both end as an uncaught exception, exit **134**, with the
  message in the output (`At ["Solver"]: validation failed for additional property 'Bogus'`). A wave-port
  or eigen capability probe therefore needs a tiny mesh shipped with circuitRF's probe config; the
  `--dry-run` of a SLEPc eigen config on a build without SLEPc is the probe to build and was **not
  tested** here (this build has SLEPc).
- The wrapper `palace` is a bash script that needs `mpirun` on `PATH`; outside the Spack environment
  it fails with `Error: Could not locate MPI launcher, try specifying a value for --launcher`, exit
  code 1. Discovery must run it inside the environment it was installed with, or call
  `palace-<arch>.bin` through the Spack view's `mpirun`.

## Q10. Palace config schema

The schema ships **as a file**: `<prefix>/bin/schema/config-schema.json` (JSON Schema draft-07,
`"$id": "urn:palace:schema:1-7-0"`, 127 kB), with `schema-compatibility.json` beside it. It is also
compiled into the binary (`cmake/EmbedSchema.cmake`), which is what `--dry-run` uses. The bundled
`validate-config` script **needs Julia** (`Error: Could not locate 'julia' executable`), which the Spack
build does not install. A writer can be tested against the schema file with any draft-07 validator.
Two more outputs a writer's tests want: every run writes **`postpro/config_resolved.json`** — the
config with every default filled in — and **`postpro/palace.json`** (git tag, per-phase timings,
peak memory, iteration counts).

## Q11. openEMS output formats and behaviour

- **Probe file:** header lines begin with `%` — `% time-domain voltage integration by openEMS 67d3784
  @<date>`, `% start-coordinates: (x,y,z) m -> [i,j,k]`, `% stop-coordinates: …`, `% t/s	voltage`
  (or `current`) — then two tab-separated columns, time in seconds and the integral in volts or amps.
  The header carries the build hash, not a version.
- **Success:** exit 0; the log's last lines are `RunFDTD: end-criteria of -50.00dB reached after N
  timesteps (…dB)` and `Time for N iterations with C cells : T sec`.
- **Unmet end criterion:** **exit 0**; the log says `RunFDTD: Warning: Max. number of timesteps was
  reached before the end-criteria of -50dB was reached...`. The exit code does not distinguish it.
- **XML error or missing file:** exit 1, `openEMS: Error File-Loading failed!!! File: <name>` then
  `openEMS - ParseFDTDSetup failed.`
- **No arguments:** usage text, exit 255.
- **Unknown option** (including `--version`, which does not exist): the banner, then `libc++abi:
  terminating due to uncaught exception of type boost::…unknown_option`, SIGABRT (exit 134). The
  banner's `version 67d3784` is the only version string the executable prints.
- **The energy end criterion can be unreachable.** Case B's conductors float in an open (Mur-bounded)
  domain: the field energy stuck at **−0.4 dB** in the lossless run (1.34 M steps, 38 ns) and at
  **−5.9 dB** in the first lossy run (0.98 M steps, 27 ns), while the port signals were down **129 dB
  after 1 ns**. Cutting that record at 2 ns changes S by
  under 5e-7. Reducing the excitation's DC content left −25.5 dB; PEC walls left a lossless cavity
  ringing at −21 dB. Brief 9 must stop on the **ports' own decay** and treat openEMS's end criterion as
  a ceiling, not a verdict.

## Q12. Install cost

The full log, one row per attempt, is `testdata/em3d/f0/README.md` §Install. On this Mac:

| Tool | Route | Wall clock | Disk | Failures |
|---|---|---|---|---|
| Palace 0.18.1 | Spack v1.2 + Palace's own macOS environment and recipe | **52 min** (35 min of builds) | 1.9 GB (+0.44 GB left by the failed build, since removed) | **3** |
| openEMS 0.37.0-rc3 | `update_openEMS.sh --disable-GUI --python` over Homebrew deps | **8 min** | 2.3 GB (Qt via VTK) | 0 |
| Gmsh 4.15.2 | `brew install gmsh` | **1 min** | 0.4 GB | 0 |

The three Palace failures — the seed of em-3d.md §7.2's known-failures table:

| Failure, verbatim | Cause | Remedy that worked |
|---|---|---|
| `[SSL: CERTIFICATE_VERIFY_FAILED] certificate verify failed: unable to get local issuer certificate` | Spack ran on a python.org Python whose certificate store was never installed | `SPACK_PYTHON=` a Python with certificates (Homebrew's) |
| `Error: No such variant 'gkrand' in package metis` | Spack v1.2's package repository (2026-06-20) is older than Palace's recipe needs | `spack repo update -b develop builtin` — Palace's own FAQ |
| `The PETSc test program compiled, but CMake could not execute it.` / `Illegal instruction` / `PETSc could not be found, be sure to set PETSC_DIR` | Spack targets an M4 as `m4` = `-march=armv9.2-a+sme2`; GCC then emits SVE, which the M4 does not have outside streaming mode (SIGILL on `ptrue`, 0x2518e107) | `packages: all: require: target=m3`, rebuild everything |

A failed build also leaves its whole dependency tree behind, and `spack gc` does not see it from a
directory environment; removing it by hash showed that **one package in the dead tree was still live**
(`gmake`, whose own `require:` overrides the target), so §7.2's uninstall must go by what the install
record says is unused, never by directory. The third failure is the one an assistant must know before it starts: it fails **17 minutes in**, after every
dependency has built, with a message that names the wrong library. Also met, not a failure: the
concretizer **silently dropped three of Palace's default variants** (SLEPc, GSLIB, SUNDIALS) until the
spec stated them. And upstream Spack still packages **Palace 0.16.0**; Palace's own recipe at the
v0.18.1 tag lists 0.18.0 as its newest version, so 0.18.1 was installed as
`local.palace@git.v0.18.1=0.18.0`.

**Windows and Linux: not done** — the owner's, in the log format of `README.md` §Install.

---

## 4. Run costs (R-em3d1-2b)

Palace (all `-np 8`; "peak memory" is Palace's own high-water mark over all ranks):

| Run | Tets | ND unknowns | AMR iterations | Sweep samples (tolerance 1e-4) | Palace time | Peak memory |
|---|---|---|---|---|---|---|
| A palace-round | 146,769 | 953,946 | 0 | 12 | 147.0 s | 7.4 GB |
| A palace-hex | 148,469 | 967,768 | 0 | 12 | 139.3 s | 8.0 GB |
| A palace-hex-foot | 155,883 | 1,016,400 | 0 | 12 | 135.4 s | 7.4 GB |
| A palace-round-pec-wire | 146,769 | 953,946 | 0 | 10 | 108.5 s | 7.7 GB |
| A palace-round-amr | 153,510 | 987,180 | 2 | 12 | 415.4 s | 11.9 GB |
| A palace-hex-amr | 155,602 | 1,002,684 | 2 | 12 | 424.5 s | 11.7 GB |
| A palace-round-order1 (Q6) | 146,769 | 178,142 | 0 | 1 point | 9.6 s | 6.6 GB |
| A palace-round-meshed-wire (Q6) | 730,392 | 855,719 | 0 | 1 point | 108.1 s | 10.5 GB |
| B palace | 87,467 | 593,548 | 0 | 20 | 270.8 s ¹ | 7.0 GB |
| B palace-lossless | 87,467 | 593,548 | 0 | 16 | 166.4 s | 6.8 GB |
| B palace-sheet-lines (Q6) | 81,203 | 549,784 | 0 | 20 | 210.1 s | 7.1 GB |

¹ overlapped the 5-minute Gmsh run of the Q6 meshed-wire mesh; the lossless twin, on the same mesh
and alone, took 166 s. Palace's own peak memory counts all eight ranks; the largest single process
was 1.1 GB. **A 16 GB machine is the practical ceiling at about 1.2 M second-order unknowns**; the
second AMR iteration was capped (`MaxSize` 1.5 M) for that reason.

openEMS:

| Run | Cells | Time step | Steps | Stopped by | Wall | Peak RSS |
|---|---|---|---|---|---|---|
| A thin curve, 25 µm | 55,025 | 3.48e-14 s | 28,035 | end criterion (−97 dB at first check) | 4.1 s | 31 MB |
| A wire ladder (4 runs) | 0.13–1.56 M | 2.0e-14 – 3.5e-15 s | 12–42 k | end criterion | 4–66 s | 40–182 MB |
| B lossless, 25 µm | 199,200 | 2.75e-14 s | 1,341,536 | **killed by hand at 38.3 ns**: energy stuck at −0.4 dB | ~10 min | — |
| B lossy, 25 µm, first attempt | 199,200 | 2.75e-14 s | 975,868 | **killed by hand**: energy stuck at −5.9 dB | ~14 min | — |
| B lossy, 25 µm, bounded | 199,200 | 2.75e-14 s | 73,000 (2.0 ns) | step limit (the end criterion was never going to fire) | 36.8 s | 47 MB |

Gmsh: case A meshes in 38–40 s at 2.6–2.7 GB peak (second-order, `HighOrderOptimize = 2`; with
`= 1` it is 3 s but leaves 3 elements with negative Jacobian), case B in 16 s at 1.1 GB, the Q6 meshed
wire in 321 s at 6.8 GB.

## 5. D4 — F1 (Palace) or F1b (openEMS) first

**Recommendation: F1, Palace first.** The evidence, both ways:

For openEMS first:
- It installs in 8 minutes, first time, and publishes Windows binaries; Palace took 52 minutes and three
  diagnoses on a Mac and has no Windows story until the deferred series.
- On a Manhattan board structure (case B) it agrees with Palace to 0.035 dB with a hand-placed grid.

For Palace first, which decide it:
- **The geometry 3D exists for is the geometry FDTD pays most for.** em-3d.md §1 makes 3D the primary
  solver for curved and non-Manhattan metal — wires, leadframes, packages — and the reference for kernel
  W. On the one such case here, FDTD needed a grid step of half the wire radius (Q2) to reach what
  Palace's adaptive mesh reached with no tuning, and the hexagon, the foot and the loop-height work of
  §6.6 (brief 4) exist only for the FEM model: in FDTD the wire is a staircase regardless.
- **The FEM pipeline's unknowns are now known and answered.** Q5 shows the `.geo` route works with four
  caveats a writer can carry; Q9/Q10 give brief 6 its version and schema story. The openEMS pipeline's
  largest unknown is the part F0 *cannot* retire: **the grid generator (brief 8) is the real work**, and
  the spike's two grid findings (near-coincident lines, a wire-set step) show it has sharp edges.
- **FDTD's run control needs new machinery** F1 does not: an end criterion on port decay (Q11), because
  openEMS's own criterion never fires on the commonest board geometry and its exit code is 0 either way.
- Case B also showed that the structures planar MoM cannot solve (a signal via through a plane) are
  ones both solvers handle; neither is uniquely needed there.

The cost of this choice is the one the overview already states: **until F1b ships, a Windows user has no
3D solver.** If that is the constraint that matters most, F1b first is defensible on this evidence —
the case B agreement is good and the install is trivial — provided brief 8 is treated as the long pole.

## 6. The validated versions (R-em3d1-4)

Re-surveyed on 2026-09-24 before installing: Palace's newest release is still **0.18.1**
(2026-09-21); openEMS's newest stable is still **0.0.36** (2023-10-22) with **0.37.0-rc3** released
2026-09-22; Gmsh's Homebrew formula is **4.15.2**.

| Tool | Validated version | Identified by | Note |
|---|---|---|---|
| Palace | **0.18.1** | `palace --version` → `Palace version: 0dc74cd`, `Schema version: 1-7-0` | built `+superlu-dist+sundials+slepc+libxsmm+gslib`, `target=m3` on arm64 |
| Gmsh | **4.15.2** | `gmsh --version` → `4.15.2-git`; OCC 7.9.3 | Homebrew bottle |
| openEMS | **0.37.0-rc3** | banner `version 67d3784`, CSXCAD `dcdb62b` | **a release candidate**: 0.0.36 is three years old and was not tried. When 0.37.0 final ships, re-run `testdata/em3d/f0` and replace this entry; an rc should not stay on a validated list |

## 7. Things F0 found that are not questions

- **circuitRF's `.clay` reader ignored an unknown field silently.** A polygon written with `"Points"`
  instead of `"Xy"` loaded as a polygon with **no vertices**, `check` reported 0 errors, and the
  ground plane would have vanished from the solve. Found while authoring case B. **Fixed after F0
  (2026-09-24):** the read now reports an ignored key as a warning and a shape with too few vertices
  as an error, in `check` and in the GUI alike (`src/Design/RESOLVED.md`).
- **Kernel W is reachable only from the GUI.** The netlist route stamps the lumped model; the
  distributed MoM is reached only through `WBondTouchstoneExport`, so the spike called it from a
  scratch harness. A headless reference run of kernel W needs a verb.
- **wBond's Touchstone export writes non-ASCII as `?`**: its own header reads `! circuitRF wBond ? one
  port per TERMINAL`, where the source has a dash (`A-bondwire/kernelw/reference-distributed.s2p`).
- libxsmm's runtime JIT reports `LIBXSMM_TARGET: appl_m4` even in the `m3` build — it worked, but it is
  the one component still choosing M4 code paths.
