# Brief 72 — thermal references and the solver spike

**Series:** [3D thermal, fifth series](brief-em3d-71-thermal-overview.md) · **Tag:** `R-em3d72-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §9, §10 *Validation*; overview §1a, §1d, §1f–§1h
**Area:** `testdata/thermal/` (new), `docs/design/em-3d-f3-spike-findings.md` (new). **No product code.**
**Depends on:** — · **Blocks:** 74 (the crossover, the element order), 76 (interface cases), 77 (the wire
cases, Gmsh embedding), 80 (Z_th and pulse cases)

---

## 0. What this brief delivers

The F0 of the thermal series, on [brief 1](brief-em3d-1-f0-spike.md)'s and [brief 61](brief-em3d-61-kernel-spike.md)'s
pattern:

1. **Externally generated reference data** for every claim the solver briefs will gate on (§2) — each a
   committed file of numbers plus the script that produced it, **none of it produced by circuitRF**
   (CLAUDE.md §Validation).
2. **Answers to named questions** (§3) about Gmsh and about the linear solvers, each a sentence with evidence.
3. **A findings note**, `docs/design/em-3d-f3-spike-findings.md`, that briefs 74–80 cite.

**Nothing under `src/` changes.** Throwaway harnesses live under `testdata/thermal/spike/`, marked as spike
material. A spike that leaves product code behind has made a design decision nobody reviewed.

---

## 1. `R-em3d72-1` — the reference tooling

**`R-em3d72-1a`** References are computed by **independent** means: closed forms evaluated in a short Python
script, ordinary-differential-equation boundary-value solutions (a general-purpose BVP solver from a
scientific Python stack), and, for the 3D cases, **an independent open-source FEM library with a permissive
licence** (a BSD-licensed Python FEM package is the expected choice; the findings note names the one used and
its version). No GPL code enters the repository — a script that *calls* a GPL program is acceptable only if
the program is not committed and the script says so; prefer permissive tools.

**`R-em3d72-1b`** Every case folder holds: `README.md` (the physics, the formula or method, the source
citation, the exact command that regenerates it and the tool versions), the script, and the data
(`*.csv`, `*.json`). The regeneration is **never** run by the test suite; the data is fixed. This is the rule
`testdata/em3d/f0/` follows.

**`R-em3d72-1c`** Units: SI in files, temperatures in °C, each column headed with its unit.

---

## 2. `R-em3d72-2` — the cases

Each is small on purpose: one claim per case, fast to solve.

| Case | Folder | What it pins | Used by |
|---|---|---|---|
| **S1** 1D slab, fixed T one side, uniform flux the other | `slab/` | assembly, Dirichlet, Neumann source, P2 exactness (a linear field is exact on any mesh) | 74 |
| **S2** composite slab, three layers, one interface resistance | `composite/` | interface elements; the jump ΔT = q″R″ | 76 |
| **S3** slab with convection (Robin) face | `robin/` | Robin term; T_surface = T_amb + q″/h | 74 |
| **S4** k(T) slab: k = k₀/(1+β(T−T₀)) and a table-defined k(T) | `kslab/` | Newton on k(T); the Kirchhoff transform gives the closed form for the first, a BVP solve for the second | 74 |
| **S5** rectangular heat source on a two-layer substrate, bottom fixed | `spreading/` | spreading resistance; a published series solution for a rectangular source on a layered, finite substrate — the README cites it by authors and title | 74, 76 |
| **S6** multi-finger source (8 strips) on a layered substrate | `fingers/` | mutual heating, the Rth matrix; an independent 3D FEM reference | 80 |
| **W1** wire, fixed-T ends, DC current, ρ(T) linear, k constant | `wire-rho/` | the closed form θ = θ_b cos(βx)/cos(βL/2); the runaway current I* = (π/L)·A·√(k/(ρ₀α)); the steady-state **fusing** (centre-melt) current I_fuse = (2A/L)·√(k/(ρ₀α))·arccos(r) (§2b) | 77 |
| **W1b** as W1 with **unequal end temperatures** (die pad hotter than the lead), and the constant-ρ limit α → 0 | `wire-ends/` | T(x) at every point, the hot spot's position shifted toward the hotter end; the parabola-plus-line limit (§2b) | 77 |
| **W2** as W1 with the metal's ρ(T) and k(T) **tables** (§4) | `wire-rho-k/` | conductive balance with both dependences — BVP solve; the fusing current with both | 77 |
| **W3** as W1 at a harmonic current, R′_ac from the exact Bessel internal impedance of a round wire | `wire-rf/` | RF heat per unit length at f ≫ the skin corner | 78 |
| **W4** wire in a coaxial cylinder of mould compound, outer surface fixed | `wire-mould/` | lateral loss into the mould; the cos/cosh closed form with a conductance per unit length g′, and the runaway current it raises: I*² = (A/(ρ₀α))·(g′ + kAπ²/L²) | 77 |
| **W5** the **Shah paper's** cases | `shah/` | the published temperature-rise and fusing-current results for bond wires | 77 |
| **Z1** slab, periodic flux: Z_th(jω) = tanh(γL)/(kγ), γ = √(jωρc/k) | `zth-slab/` | the frequency-domain solve | 80 |
| **Z2** Foster network of known terms driven by a periodic pulse train | `pulse/` | the closed form ΔT_peak = P·Σ Rᵢ(1−e^{−t_on/τᵢ})/(1−e^{−T/τᵢ}) against a Fourier-series sum | 80 |

**`R-em3d72-2a` — W5 needs the paper.** The owner named *"Temperature rise and fusing current in wire bonds
for high power RF applications"* (M. Shah) as the validator. **Do not reconstruct its equations or data
from memory.** Ask the owner for the PDF; the case README quotes nothing, paraphrases the model, cites it by
author and title, and tabulates the published results the gate compares against. Until the PDF arrives, W5
is listed as **not done** in the findings note and W1–W4 stand in.

**`R-em3d72-2b` — the wire closed forms are derived in the README**, not cited (they are standard physics
and are **not** attributed to the Shah paper):
- the 1D steady equation k·A·T″ + I²ρ₀[1+α(T−T₀)]/A − g′(T − T_amb) = 0, ends at T_end;
- with g′ = 0: substitution θ = T − T₀ + 1/α, β² = I²ρ₀α/(kA²), T(x) = T₀ − 1/α + (T_end − T₀ + 1/α)·cos(βx)/cos(βL/2),
  x from the centre; the existence limit βL/2 = π/2 gives I*;
- the fusing current: the centre reaches the melting point T_m when cos(βL/2) = r,
  r = (T_end − T₀ + 1/α)/(T_m − T₀ + 1/α), so I_fuse = (2A/L)·√(k/(ρ₀α))·arccos(r) < I*;
- **unequal ends** T_a at x = 0 and T_b at x = L (x now from one end): θ(x) = [θ_a·sin(β(L−x)) + θ_b·sin(βx)]/sin(βL),
  the same runaway limit βL = π, and the hot spot **not** at the centre — its position is part of the reference;
- the **constant-ρ limit** (α → 0): T(x) = T_a + (T_b − T_a)·x/L + (I²ρ/(2kA²))·x(L − x) — the parabola the
  cos/sin forms tend to, which also gates that the solver has no trouble as α → 0;
- with g′ > 0 (W4): m² = (g′ − I²ρ₀α/A)/(kA); T(x) = T_p + (T_end − T_p)·cosh(mx)/cosh(mL/2), with T_p the
  particular constant, and cos in place of cosh when m² < 0; unequal ends take the two-end form above with sinh(m·) in place of sin(β·);
- every W gate compares **T(x) along the whole wire** (at least 51 points), not only its maximum — the owner's
  display is the temperature along the wire, so that is what is referenced.
A worked check the README reproduces: 1 mil gold, L = 1 mm, ρ₀ = 2.44e-8 Ω·m and α = 0.0034 /K at 20 °C,
k = 312 W/(m·K), ends at 25 °C → I* ≈ 3.09 A, I_fuse ≈ 2.64 A.

**`R-em3d72-2c` — every W case is run for all four wire metals**: **gold, copper, aluminium and silver**,
each with its own ρ₀, α, k and melting point, and for W2 its own tables (§4). Diameters 25.4 µm (1 mil) and
50.8 µm (2 mil) — plus 38.1 µm (1.5 mil) for aluminium and copper, which are common there — lengths 1 and
3 mm, ends at 25 °C and 100 °C, currents at 0.25, 0.5, 0.9 and 0.99 of I*. One data file per metal, so a gate
can name the metal that failed.

**`R-em3d72-2d` — S5 and S6 are 3D** and come from the independent FEM library on meshes refined until the
reported temperature moves by less than 0.1 %, with that refinement ladder in the README.

---

## 3. `R-em3d72-3` — the questions

Each gets a sentence and evidence in the findings note.

- **Q1 — Gmsh embedded curves.** Can Gmsh embed a polyline (a wire's centreline) **inside** a volume (the
  mould compound) and conform the tetrahedra to it, from a `.geo` script using the OpenCASCADE kernel, at the
  Gmsh versions circuitRF validates? Give the script lines, the resulting element count along the curve,
  and whether the curve's nodes are shared with the tetrahedra.
- **Q2 — Gmsh embedded surfaces.** A heat-source rectangle inside a solid (a strip under a field plate) and a
  foot contact patch on a pad's top face: embedded and conformal, with the fragment the Palace lowering
  already uses? The physical group of the embedded surface survives?
- **Q3 — Thin interfaces.** Two touching solids after the fragment share one surface; can the thermal
  reader identify that shared surface **and** which volume lies on each side, from the `.msh` and the
  entities file the lowering already prints? (The solver duplicates those nodes, brief 76.)
- **Q4 — Element order.** On S5, compare P1 and P2 tetrahedra at equal unknown counts: the error in the peak
  temperature. The expectation is that P2 wins near the source; the answer fixes brief 74's default.
- **Q5 — The direct/iterative crossover.** In a **scratch harness** (not a test — CLAUDE.md, no timing tests),
  in **Release**, on S5 at growing refinement: CSparse Cholesky time and memory against CG with a
  smoothed-aggregation AMG preconditioner (a minimal implementation in the harness, or a permissive
  library used only for the comparison and never committed). Report both curves and where they cross, on
  the owner's Mac. Brief 74's default crossover comes from this.
- **Q6 — Contrast.** On a copper block on FR-4 with a mould-compound cap (contrast ≈ 600:1), CG iteration
  counts with IC(0) against AMG. This is the evidence for AMG over simpler preconditioners (overview §1f).
- **Q7 — Mesh-size span.** A 0.5 µm source strip on a 100 µm die on a 10 mm flange: can Gmsh produce a
  graded mesh with the **existing** Distance/Threshold fields plus a Box field for a mesh region, and what are
  the element and node counts? This is the scenario-2 feasibility answer.
- **Q8 — The wire foot.** The wire's contact patch on a pad of 100 µm × 100 µm: minimum element size forced by
  a foot 50 µm long and 25 µm wide, and its node count.

---

## 4. `R-em3d72-4` — the four wire metals over temperature

Gold, copper, aluminium and silver are the common bond-wire metals, and conductive balance needs **both**
of their temperature dependences. For **each** of the four, collect from standard reference handbooks, each
value cited:

| Quantity | Range | Spacing |
|---|---|---|
| **Electrical resistivity ρ(T)** (Ω·m) | 20 °C to just below the metal's melting point | at least every 100 °C, and at 20, 85 and 125 °C |
| **Thermal conductivity k(T)** (W/(m·K)) | the same | the same |
| Melting point (°C), density, specific heat at 25 °C | — | — |

The melting points bound each table and set W1's fusing current: aluminium's (about 660 °C) is far below
the others' (silver about 962 °C, gold about 1064 °C, copper about 1085 °C), so aluminium's tables end much
lower. The values are for the **pure bulk metal**; bond wire is drawn and often lightly doped, and the
findings note says so and states any difference a cited wire-specific source gives.

**Checks the findings note records:**
- gold's k(T) against the owner's figures — about **312 W/(m·K) at 125 °C** and **262 W/(m·K) at 927 °C** (a
  few percent between references is normal; more is a question for the owner);
- each metal's ρ(20 °C) and the slope of ρ(T) near 20 °C against the `Sigma20` and `Alpha20` already in
  `generic-materials.cmat` — a disagreement over 1 % is reported, not silently resolved (brief 73's table-wins
  rule would otherwise change which number is in force);
- whether ρ(T) is linear enough over each metal's range for W1's closed form, stated as the largest deviation
  from the 20 °C linear fit — the reason W2 uses the tables.

One file per metal, `testdata/thermal/metals/<metal>.csv`, with the sources in its README. Brief 73 puts these
tables in `generic-materials.cmat`.

---

## 5. Gates

1. Every case folder of §2 exists with its README, script and data, or is listed **not done** with the
   reason (W5 until the PDF arrives).
2. Every question of §3 is answered with evidence, or listed **not done**.
3. `rg -i` over `testdata/thermal/` and the findings note for the repository's vendor-name list finds
   nothing (CLAUDE.md, *Commercial Vendor References*).
4. No file under `src/` or `tests/` is changed (`git status`).

## 6. Scope

- **No product code**, no tests, no timing assertions.
- **Scripts never run in CI**; the data is the reference.
- Findings go in the findings note; anything learned about Gmsh that the Palace path would also want goes in
  `src/Design/RESOLVED.md` as well. Never `CLAUDE.md`.
