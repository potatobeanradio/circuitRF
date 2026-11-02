# circuitRF — 3D thermal, F3 spike: references and findings

**Status:** done on macOS, except W5 (waits for the paper) · **Date:** 2026-09-28 ·
**Brief:** [`brief-em3d-72-thermal-references-and-spike.md`](../briefs/brief-em3d-72-thermal-references-and-spike.md) ·
**Series:** [`brief-em3d-71-thermal-overview.md`](../briefs/brief-em3d-71-thermal-overview.md) ·
**Data:** [`testdata/thermal/`](../../testdata/thermal/README.md) — every reference, every script, every
command and tool version; the spike's harnesses and raw results in
[`testdata/thermal/spike/`](../../testdata/thermal/spike/README.md).

Every number below comes from a file under `testdata/thermal/`, named beside the claim. No code under
`src/` or `tests/` changed; the one file touched there is `src/Design/RESOLVED.md`, which the brief's §6 asks for. "Gmsh" is Gmsh 4.15.2 — the one version circuitRF validates
(`SolverDiscovery.Gmsh` refuses a newer minor) — and "the harness" is the Release C# scratch program in
`testdata/thermal/spike/harness/`.

---

## 0. The answers in one screen

| # | Question | Answer |
|---|---|---|
| Q1 | Gmsh embedded curves | **Yes.** Both `Curve{…} In Volume{…}` and the lowering's `BooleanFragments` embed a wire centreline; every line element is an **edge of the tetrahedra** and every node (order-2 mid-nodes too) is **shared** with them; the meshed length equals the geometric length exactly. The consequence is the harder half: a 1D wire tied to a 3D mesh is a line source, whose temperature depends on the local mesh size (§Q1) |
| Q2 | Gmsh embedded surfaces | **Yes**, through the one fragment the Palace lowering uses: a strip inside a solid stays embedded (the volume is not split; die tetrahedra on both sides) and a foot patch on a shared face splits it (mould on one side, pad on the other); both physical groups survive, every triangle is a tetrahedron face |
| Q3 | Thin interfaces | **Yes, two ways**: the `.geo` can print, for every surface, the volumes whose `Boundary{}` contains it (two ⇒ a contact); and from the MSH 2.2 file alone, each interface triangle is a face of exactly one tetrahedron on each side, whose elementary tags name the volumes |
| Q4 | Element order | **P2, by 15–40×** in error at equal unknowns on S5: at 100k unknowns the peak is 0.015 % off with P2 and 0.64 % with P1 |
| Q5 | Direct/iterative crossover | **Far lower than the overview assumed.** On S5 (P2, graded), CSparse's Cholesky and CG + smoothed-aggregation AMG are level at **~2,000–3,000 unknowns**; above that AMG wins by a margin that keeps growing: **11×** at 90k, **19×** at 205k, **51×** at 643k (509 s and a 6.8 GB factor against 10 s and 1.8 GB). AMG's CG count stays at 14–20 from 1.8k to 1M. **Default: AMG from ~5,000 unknowns** — i.e. almost always; Cholesky for tiny meshes, as the fallback, and for many right-hand sides on small meshes (§Q5) |
| Q6 | Contrast 600:1 | **The contrast is not what separates them; refinement is.** At 600:1 (copper/mould, 1300:1 copper/FR-4) every preconditioner needs 20–40 % more iterations than on the same mesh with one k. IC(0)'s count grows like ~n^1/3 (94 → 205 from 23k to 230k), AMG's stays flat (16 → 18); at 230k their times are comparable (IC(0) 3.0 s, AMG 1.9–3.5 s). **AMG stands, on scalability** — the overview's contrast argument is not what this measured |
| Q7 | Mesh-size span | **Yes, with the existing fields.** A Distance/Threshold field on the strip plus a Box field over the die grades from **0.28 µm** (median strip edge) to **1.5 mm** — a 10⁴ span — in **2–4 s**, giving **175k–305k P2 nodes** (117k–208k tetrahedra) and no element below quality 0.2. Too steep a threshold (1 mm growth over 1 mm) leaves 52 slivers |
| Q8 | The wire foot | **The foot forces nothing by itself** — Gmsh meshes a 50 × 25 µm patch with 4 triangles; placed 5 µm from the pad's edge it leaves **slivers** (3 tetrahedra of mean-ratio quality 0.19). A size field of 4 elements across (6.25 µm) gives good elements at **~2,500 P2 nodes per pad** |

Beyond the questions, four findings change what briefs 74–80 must do (§3): gold and copper wires reach a
**fold** before their centre melts (W2); a linear-ρ fusing current is **15 % optimistic** for gold (W2);
a harmonic sum of a **distributed** Z_th converges like N^(−1/2) and cannot give a pulsed peak unaided
(Z2); and `generic-materials.cmat`'s gold, copper and aluminium values disagree with the pure-metal
tables by more than 1 % (§2).

---

## 1. What was delivered (R-em3d72-1, -2, -4)

| Case | Folder | Method | State |
|---|---|---|---|
| S1 slab | `slab/` | closed form | done |
| S2 composite + interface | `composite/` | closed form | done |
| S3 Robin | `robin/` | closed form | done |
| S4 k(T) slab | `kslab/` | Kirchhoff closed form; BVP (checked against Kirchhoff, 2.7e-10 K) | done |
| S5 spreading | `spreading/` | layered-channel series + 3D FEM ladder (P2, to 0.013 %) | done |
| S6 fingers, Rth matrix | `fingers/` | 3D FEM ladder (to 0.03 % between rungs) + series (0.039 %) | done |
| W1 wire, ρ(T) linear | `wire-rho/` | closed form + BVP (9.5e-6 K) | done, 4 metals |
| W1b unequal ends, α → 0 | `wire-ends/` | closed form + BVP (9.8e-6 K) | done, 4 metals |
| W2 ρ(T), k(T) tables | `wire-rho-k/` | shooting + BVP (6.1e-8 K) | done, 4 metals |
| W3 RF | `wire-rf/` | shooting + BVP (9.2e-9 K), SciPy Bessel | done, 4 metals |
| W4 mould | `wire-mould/` | closed form + BVP (8.9e-6 K) | done, 4 metals |
| **W5 the Shah paper** | `shah/` | — | **not done — the PDF has not arrived**; nothing was reconstructed from memory |
| Z1 Z_th(jω) | `zth-slab/` | closed form + finite differences (7e-6) | done |
| Z2 pulse train | `pulse/` | closed form + Fourier sums | done |
| Metals ρ(T), k(T) | `metals/` | cited tables | done |

**Independence.** Closed forms in NumPy; BVP/IVP solutions from SciPy (`solve_bvp`, `solve_ivp` DOP853);
3D FEM from **scikit-fem 12.0.2 (BSD-3-Clause)** with pyamg 5.3.0 (MIT) and meshio 5.3.5 (MIT). Gmsh (GPL)
is **called as a program** on `.geo` text and never imported or committed. Every table-driven reference has
a second, independent numerical solution beside it, and the agreement is recorded in its JSON.

---

## Q1. Gmsh embedded curves

`testdata/thermal/spike/q1-embedded-curve/`: `a.geo` embeds a three-segment polyline strictly inside a box
with `Curve{1001, 1002, 1003} In Volume{1};`; `b.geo` puts the wire's feet ON the box's bottom face and uses
the lowering's own route, `BooleanFragments{ Volume{1}; Delete; }{ Curve{…}; Delete; }`; `c.geo` does the same
with a curved (spline) wire. `check_embedding.py` reads each `.msh`:

| Mesh | Tetrahedra | Wire elements | Nodes shared | Every segment a tet edge | Meshed / geometric length |
|---|---|---|---|---|---|
| a, order 1 | 25,830 | 106 `line` | yes | yes | 2094.427 / 2094.427 µm |
| a, order 2 | 25,830 | 106 `line3` (213 nodes) | yes | yes | equal |
| b, order 1 | 25,856 | 114 `line` | yes | yes | 2277.033 / 2277.033 µm |
| b, order 2 | 25,856 | 114 `line3` (229 nodes) | yes | yes | equal |
| c, order 2, spline | 26,277 | 116 `line3` | yes | yes | — |

The element count along the curve is set by the Distance/Threshold field on the curve (20 µm here).
Three details the lowering must respect:

- **A curve that the fragment does not split keeps its tag** (the default `OCCBooleanPreserveNumbering`),
  so a wire can be followed through the fragment by tag; a bounding-box query also works.
- **A spline THROUGH points is not the wire.** `c.geo`'s five-point spline bulges to x = 159.5 µm, 40 µm
  outside its own foot at x = 200 — so a tight bounding-box query built from the points misses it. The
  resolved centreline (`C3dWires.Resolve`) is a polyline and should be written as `Line` segments.
- **Explicit tags collide.** `Line(1)` after a `Box` fails ("OpenCASCADE curve with tag 1 already exists"):
  the box took curve tags 1–12. The circuitRF writer already uses `newp`/`newl`/`newv`, which avoids it.

**The consequence — a line source (`line_source.py`).** In 3D the temperature of a line source is logarithmically singular, so
a wire tied to the tetrahedra at shared nodes reads a temperature that **depends on the mesh size at the
wire and does not converge** — it keeps rising as the mesh is refined. `line_source.py` puts a 1 mil gold
wire (1 mm, ρ constant, 1.25 A) on the axis of a mould cylinder (k = 0.8, r_o = 500 µm, outer surface
25 °C) — W4's coax, whose closed form gives a centre temperature of **52.86 °C** — and couples a 1D wire
element to the mesh at the embedded curve's nodes (`line-source.csv`):

| h at the wire | P1 centre | P2 centre | r_eff/h, P1 | r_eff/h, P2 |
|---|---|---|---|---|
| 80 µm | 49.34 °C | 54.08 °C | 0.369 | 0.112 |
| 40 µm | 52.15 °C | 56.05 °C | 0.384 | 0.117 |
| 20 µm | 54.58 °C | 57.87 °C | 0.383 | 0.115 |
| 10 µm | 56.48 °C | 59.30 °C | 0.401 | 0.119 |
| 5 µm | 58.16 °C | **60.60 °C** | 0.406 | 0.120 |

r_eff is the radius at which the coax model reproduces the discrete centre temperature: it is **a
constant fraction of the local mesh size** — ≈ 0.39 h for P1, ≈ 0.12 h for P2. That is the finite-element
analogue of the reservoir-simulation well-index problem (Peaceman), and it has the same cure: tie the wire
to the mesh through a resistance per unit length **ln(r_eff/r_w) / (2π k_m)** with r_eff = c·h, which is
positive (a real element) only while **h ≥ r_w / c** — for a 1 mil wire and P2, h ≥ ~106 µm at the wire.
Refining the mould AROUND a wire past that makes the uncorrected answer worse, not better: at 5 µm the P2
wire reads 7.7 K (28 %) above the coax model. **Brief 77 must decide this** — the constant c for the
element order it uses, the minimum mesh size at a wire, and the coupling element. (The coax model neglects
the mould's axial conduction; the constancy of r_eff/h is what shows the effect is the line source, not that.)

---

## Q2. Gmsh embedded surfaces

`q2-embedded-surface/q2.geo`: a 2 µm × 60 µm strip 5 µm **below** a die's top face (touching nothing), and
a 50 µm × 25 µm foot patch on the face a pad shares with a mould block above it, all through ONE
`BooleanFragments{ Volume{1, 2, 3}; Delete; }{ Surface{1010, 1011}; Delete; }`. The entity table the
`.geo` printed (`q2-entities.txt`): `strip 1 surfaces (tag 1010), foot 1 surfaces (tag 1011), volumes die 1
pad 1 mould 1, all volumes 3`. `check_q2.py` on the order-2 MSH 2.2 mesh:

| Group | Triangles | Tet faces | Sides |
|---|---|---|---|
| strip | 246 | 246 | die / die (the volume is NOT split: the surface is embedded) |
| foot | 2,920 | 2,920 | mould / pad |

Both surfaces keep their construction tags through the fragment, and the physical groups survive.

---

## Q3. Thin interfaces: the shared surface and its two sides

`q3-interfaces/q3.geo`: a die on a larger flange (so the contact is PART of the flange's top face) and a
lid on the die, fragmented. Two independent answers:

1. **From the script.** Looping over `Volume{:}` and `Abs(Boundary{ Volume{v}; })` and inverting prints
   every surface bounded by two volumes (`q3-entities.txt`): `surface 17 bounds 2 volumes: 1 3`,
   `surface 23 bounds 2 volumes: 1 2` — exactly the die/lid and die/flange contacts. This is the entity
   file the lowering already prints, with one more loop.
2. **From the mesh alone.** MSH 2.2 carries each element's physical and elementary tags but no adjacency;
   matching each interface triangle to the tetrahedra that have it as a face recovers it: all 118
   die_flange triangles have one `die` and one `flange` tetrahedron, all 44 die_lid triangles one `die`
   and one `lid`.

The two sides' nodes are shared in the mesh (conformal), which is what brief 76 duplicates.

---

## Q4. Element order: P1 against P2 at equal unknowns

`q4-element-order/q4.csv`, on S5 against its series (peak 21.7076 K/W, mean 18.0079 K/W), both orders on
the same graded-mesh family (P1 simply run further down it):

| Unknowns | P1 peak error | P2 peak error | P1 mean error | P2 mean error |
|---|---|---|---|---|
| 10,000 | 0.89 % | 0.026 % | 1.15 % | 0.072 % |
| 30,000 | 0.74 % | 0.024 % | 0.89 % | 0.036 % |
| 100,000 | 0.64 % | 0.015 % | 0.71 % | 0.020 % |

(log-log interpolation along each ladder; the raw rungs are in the CSV). **P2 is the default for brief 74**,
as expected: 3,111 P2 unknowns already beat 170,000 P1 unknowns. P1 converges slowly here partly because
this family keeps the far field coarse; a P1-optimal grading would narrow the gap but not close it.

---

## Q5. The direct/iterative crossover

`q5-crossover/crossover.csv`: S5's condensed P2 stiffness matrix at ten refinements, each solved in its
own Release process by the harness (CG to a relative residual of 1e-8; peak memory from `/usr/bin/time -l`).
Single-threaded, this Mac.

| Unknowns | AMG setup + solve | CG its | AMG peak | Cholesky factor + solve | L factor | Cholesky peak | Cholesky / AMG |
|---|---|---|---|---|---|---|---|
| 1,803 | 0.008 s | 14 | 54 MB | 0.007 s | 1.3 MB | 54 MB | **0.87** |
| 3,111 | 0.014 s | 14 | 58 MB | 0.017 s | 2.8 MB | 62 MB | 1.24 |
| 5,863 | 0.031 s | 14 | 72 MB | 0.035 s | 7.0 MB | 77 MB | 1.13 |
| 11,598 | 0.071 s | 15 | 85 MB | 0.132 s | 20 MB | 148 MB | 1.9 |
| 28,161 | 0.196 s | 17 | 153 MB | 0.889 s | 78 MB | 371 MB | 4.5 |
| 89,761 | 0.734 s | 16 | 463 MB | 8.28 s | 401 MB | 1.7 GB | 11 |
| 204,503 | 2.19 s | 17 | 525 MB | 41.6 s | 1.26 GB | 1.8 GB | 19 |
| 372,891 | 4.73 s | 18 | 987 MB | 152 s | 3.05 GB | 3.7 GB | 32 |
| 642,664 | 9.98 s | 19 | 1.8 GB | 509 s | 6.82 GB | 7.7 GB | 51 |
| 996,810 | 17.8 s | 20 | 2.6 GB | not run — projected ~22 min, ~13 GB | | | |

AMG grows like n^1.3 in time (setup and solve both) and its iteration count barely moves; CSparse's Cholesky grows like n^2.2 in
time and n^1.5 in memory. The two lines cross at **~2,500 unknowns** for one right-hand side.

- **Why not "a few hundred thousand".** The overview's figure assumed a factorization far faster than
  CSparse's, which is a compact, up-looking, single-threaded Cholesky (no supernodes, no BLAS). A
  supernodal, multithreaded factorization would move the crossover up a great deal; CSparse is the one
  circuitRF has, and the recommendation is for it.
- **Several right-hand sides** (the 8 fingers of S6, a Rth matrix): a solve with the factor is 3× cheaper
  than an AMG solve, so Cholesky pays back its factorization after ~40 right-hand sides at 90k unknowns and
  ~70 at 205k. **Newton (k(T), σ(T)) and a Z_th(jω) sweep change the matrix every step**, so they get no
  such amortization.
- **Recommendation for brief 74:** CG + smoothed-aggregation AMG by default above **~5,000 unknowns** (every
  realistic 3D mesh — S5's coarsest rung within 0.1 % already has 28k); Cholesky below, as the robust
  fallback when CG does not converge, and for ≥ ~40 right-hand sides on meshes under ~100k. The AMG is
  ~400 lines of managed code (`harness/Program.cs`), and its CG counts agree with pyamg's (§Q6).

---

## Q6. Conductivity contrast: IC(0) against AMG

`q6-contrast/`: a 3 × 3 × 1 mm copper block (k = 390) on a 10 × 10 × 1.6 mm FR-4 board (k = 0.3,
through-plane) under a 5 × 5 × 2 mm mould cap (k = 0.65), 1 W on a 1 mm² patch on the block's top face, the
board's bottom held at 25 °C, P2; three refinements, and each mesh also with **one** conductivity everywhere
to isolate the contrast (`contrast.csv`). CG to 1e-8:

| Unknowns | k | Jacobi | IC(0) | AMG θ = 0.08 | AMG θ = 0 |
|---|---|---|---|---|---|
| 22,891 | contrast | 290 | 94 | 16 | 27 |
| 22,891 | uniform | 240 | 78 | 12 | 22 |
| 67,094 | contrast | 434 | 135 | 17 | 35 |
| 67,094 | uniform | 343 | 107 | 13 | 26 |
| 230,097 | contrast | 596 (3.7 s) | 205 (3.0 s) | 18 (3.5 s) | 41 (1.9 s) |
| 230,097 | uniform | 479 | 163 | 14 | 29 |

- **The contrast costs every preconditioner about the same, 20–40 % more iterations.** IC(0) did not need
  a diagonal shift anywhere and did not "crawl". What separates the methods is **refinement**: IC(0) and
  Jacobi grow like ~n^0.3 (the condition number's h⁻² through a preconditioner that does not fix it); AMG
  is flat. At 230k their times are comparable; at the ~1M unknowns of scenario 1, IC(0) would be at ~350
  iterations against AMG's ~20.
- **θ (the strength threshold)**: θ = 0 (every connection strong, pyamg's default) coarsens aggressively
  (operator complexity 1.02 against 1.6–1.9), needs twice the iterations and is **the fastest overall**
  (1.9 s against 3.5 s at 230k). Brief 74 should start at θ = 0 and keep θ as a setting.
- **The harness's AMG is a faithful smoothed aggregation**: pyamg 5.3.0 on the same matrices gives
  29/34/38 iterations at θ = 0 (harness 27/35/41) and 15/17/17 at θ = 0.08 (harness 16/17/18) — `pyamg.json`.

So the case for AMG over IC(0) is **scalability with mesh size**, not robustness to contrast. The overview's
§1f wording ("simpler preconditioners crawl" under contrast) should be corrected when em-3d.md gets its F3
revision.

---

## Q7. Mesh-size span: 0.5 µm on 10 mm

`q7-mesh-span/q7.csv`: a 0.5 µm × 200 µm source strip on the top face of a 1 mm × 0.5 mm × 100 µm die on
a 10 mm × 10 mm × 1 mm flange, order 2, one thread. A Threshold on the Distance to the strip from 0.25 µm to
1 mm (the lowering's existing fields), optionally with a Box field (20 µm inside a box around the die,
grading over 200 µm), `Min` of the two:

| Variant | P2 nodes | Tetrahedra | Mesh time | Strip edge (median) | Tet edges | Min quality | < 0.2 |
|---|---|---|---|---|---|---|---|
| Threshold only (DistMax 3.3 mm) | 197,425 | 130,894 | 2.4 s | 0.28 µm | 0.17 µm – 1.55 mm | 0.38 | 0 |
| + Box field | 305,248 | 208,188 | 3.5 s | 0.28 µm | 0.17 µm – 1.54 mm | 0.22 | 0 |
| + Box field, HXT (algorithm 10) | 244,313 | 161,561 | 2.4 s | 0.28 µm | same | 0.22 | 0 |
| steep Threshold (DistMax 1 mm) + Box | 175,316 | 117,318 | 2.1 s | 0.27 µm | 0.17 µm – 1.67 mm | **0.011** | **52** |

- **A 10⁴ span is cheap** because the refinement is local: the strip is 3,206 triangles, and the element size reaches
  1.5 mm across the flange. Scenario 2 is feasible with the existing fields; a mesh region is a Box field
  added to the same `Min`.
- **Grading is the constraint, not the span**: growing 1 mm in 1 mm of distance produced 52 slivers.
  Keep the Threshold's (SizeMax − SizeMin)/(DistMax − DistMin) at or below ~0.3, which is what the lowering's
  `Grading` setting already controls.
- The strip here is on a face; one embedded 5 µm under the surface (a channel under a field plate) goes
  through the same fragment (Q2).

---

## Q8. The wire foot

`q8-wire-foot/q8.csv`: a 50 × 25 µm foot on a 100 × 100 × 10 µm pad on a 400 × 400 × 100 µm substrate,
order 2, far size 100 µm.

| Variant | Foot size field | P2 nodes | Foot triangles | Min tet edge | Min quality | Tets < 0.2 |
|---|---|---|---|---|---|---|
| centred | none | 1,255 | 4 | 10 µm | 0.31 | 0 |
| 5 µm from the pad edge | none | 1,276 | 4 | 10 µm | **0.19** | **3** |
| centred | 6.25 µm (4 across) | 2,439 | 84 | 4.4 µm | 0.40 | 0 |
| 5 µm from the pad edge | 6.25 µm | 2,572 | 86 | 3.8 µm | 0.39 | 0 |
| centred | 3.125 µm (8 across) | 5,011 | 320 | 2.0 µm | 0.40 | 0 |

(Quality is the mean ratio, 1 for a regular tetrahedron.) Gmsh does not refine for a feature by itself —
a foot with no size field is four triangles, and near an edge it leaves slivers. **A foot needs its own
size field**; four elements across costs ~2,500 P2 nodes per pad-with-foot, so **a 100-wire array
(200 feet) spends ~0.5 M unknowns on feet alone** at that resolution — worth knowing when brief 77 sets the
default, and a reason to keep the foot at two or three elements across unless a gate shows it matters.

---

## 2. The metals (R-em3d72-4)

`testdata/thermal/metals/` — ρ(T) from Matula (1979) for Cu, Au, Ag and Desai, James & Ho (1984) for Al;
k(T) from Ho, Powell & Liley (1974); melting point, density, specific heat from the CRC Handbook. Pure bulk
metal, 0 °C to the melting point.

- **Gold's k(T) against the owner's figures:** 311.1 W/(m·K) at 125 °C (owner 312, −0.3 %) and 255.0 at
  927 °C (owner 262, **−2.7 %**). Within the few percent the brief allows; nothing to raise.
- **`generic-materials.cmat` against the tables (over 1 % is reported, not resolved):**

  | Metal | ρ₂₀ cmat vs table | α₂₀ cmat vs table | dρ/dT cmat vs table |
  |---|---|---|---|
  | gold | **+10.1 %** | **−8.1 %** | +1.2 % |
  | copper | **+2.7 %** | **−3.2 %** | −0.6 % |
  | aluminium | +0.03 % | **−12.3 %** | **−12.2 %** |
  | silver | −0.04 % | +0.2 % | +0.1 % |

  Gold and copper obey Matthiessen's rule: the `.cmat` describes a less pure metal (its gold record says
  "deposited gold"; copper's is the 100 % IACS standard), whose higher ρ₂₀ comes with a proportionally lower
  α₂₀ and the **same absolute slope** within 1.2 %. Aluminium is different in kind — its slope is 12 % low
  (0.0039 /K is a conductor-grade alloy's coefficient, not the pure metal's). **The owner decides** which
  numbers brief 73 carries; under the table-wins rule the table's ρ₂₀ and slope would replace the
  `.cmat`'s for any metal given a table.
- **Linearity for W1:** the largest deviation of ρ(T) from its 20 °C tangent, anywhere to the melting
  point: gold 19.5 %, silver 13.7 %, copper 12.7 %, aluminium 3.5 % — all at the melting point, all upward.
  Below ~300 °C every metal is within ~1–2 %.
- **No wire-specific source was consulted**, so no drawn-wire difference is stated; the tables are the
  pure metal's (bond wire's ρ₂₀ is higher, its slope about the same — the gold and copper rows above are
  that effect).

---

## 3. Findings that are not questions

**3.1 The steady-state limit of a wire with tables is a fold, not a melt (W2).** With ρ(T) curving up and
k(T) falling, the current I(T_c) that sustains centre temperature T_c **peaks below melting** for gold at
both end temperatures and copper at 25 °C ends (gold, 25 °C ends: T_c ≈ 876 °C). Above the peak there is
no steady state; between the centre-melt current and the peak there are two, and the hotter is unstable.
The fold is only 0.03–0.6 % above the centre-melt current, but it is what brief 77's bisection
(overview §1g) will find, and it means a gold wire with 25 °C ends **never** sits in steady state with its
centre between ~876 °C and melting. The reference profiles take the stable (cooler) branch.

**3.2 A linear-ρ fusing current is optimistic (W2).** For 1 mil, 1 mm gold with 25 °C ends: W1's linear
model 2.716 A, the ρ table with constant k 2.494 A, both tables **2.383 A** — the linear model is 15 %
high (copper 10 %, silver 9 %, aluminium 2 %). About 60 % of gold's gap is ρ's curvature, 40 % is k(T):
the case for k(T) in the loop, and for tables over α₂₀.

**3.3 RF heat is several times the DC-equivalent (W3).** 1 mil gold at 0.25 I* RMS: 27 W/m as DC, 80 W/m
at 1 GHz, 237 W/m at 10 GHz (8.7×) — the centre at 269 °C instead of 48 °C. Confirms overview §1h's
"unsafe direction" with numbers.

**3.4 Harmonic sums for a pulsed peak (Z2).** A rectangular pulse train's peak converges like **1/N** in
harmonics for a lumped Foster network (Z2a: 0.19 % at 10⁴) and like **N^(−1/2)** for a distributed one
(Z2c, the slab: 0.59 % at 10⁴, 0.06 % at 10⁶). Removing the lumped network's 1/(jωC) asymptote and adding
its exact time response back (a sawtooth) makes it 1/N³ (1e-7 at 10⁴). **Brief 80's FEM Z_th is
distributed**, so it must either remove the semi-infinite √ω asymptote analytically (its pulse response is
closed form) or fit a Foster network to Z_th(jω) and use the closed-form peak. A plain truncated sum is not
an option for the peak.

**3.5 Spreading dominates the example stack (S5).** 17.2 of S5's 18.0 K/W is spreading, 0.81 K/W is 1D —
the kind of number a user estimating Rth by hand gets wrong by 20×, and the reason S5 is the solver's main
3D gate.

---

## 4. What briefs 74–80 take from here

| Brief | Takes |
|---|---|
| 74 | P2 default (Q4); AMG from ~5k unknowns, Cholesky below and as the fallback (Q5); AMG θ = 0 as the starting setting, and AMG over IC(0) on scalability (Q6); S1–S5 as gates |
| 76 | the interface identification route (Q3); S2 as the jump gate; S5/S6 layered cases |
| 77 | embedded curves work (Q1) but a wire tied to the mesh needs the line-source coupling and a minimum mesh size at the wire (Q1); the foot's size field and its cost (Q8); W1–W4 in four metals; the fold (§3.1) as the runaway answer; W5 when the paper arrives |
| 78 | W3's R′_ac tables and harmonic cases |
| 80 | S6's Rth matrix; Z1; Z2 and §3.4 — no plain harmonic sum for a peak |

## 5. Not done

- **W5** — the Shah paper's cases: waits for the PDF (`testdata/thermal/shah/README.md`).
- **Windows and Linux** — every script is plain Python + Gmsh + .NET and should run there unchanged, but
  none was run anywhere but this Mac. The Q5 timings are this machine's.
