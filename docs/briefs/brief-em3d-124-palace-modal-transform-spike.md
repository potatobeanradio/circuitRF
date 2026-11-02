# Brief 124 — Spike: terminal S from Palace's modal S on a shared face, any cross-section

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d124-n`
**Precedent:** brief 113's corrected transform (`src/Design/RESOLVED.md` § "Terminal wave ports — brief-em3d-113",
R-em3d113-1d/e/g), brief 113-a, brief 119 (`RESOLVED.md` § "Palace terminal ports by symmetry — brief-em3d-119")
**Area:** the harness in `tools/palace-symmetry-spike/` (extend it; scratch output under its git-ignored `runs/`);
findings in `src/Design/RESOLVED.md`; fixtures in `testdata/em3d/terminal/palace-modal/`
**Depends on:** 119 · **Holds:** 115 (not to be built until this spike's go/no-go names what 115 builds)

---

## 0. Why this brief exists

Brief 115 is written around brief 119's route B, the mirror-symmetry half-model. Route B works (0.05 dB / 0.5°, max
|ΔS| 0.0012), but only for a pair that is its own mirror image. **An asymmetric pair, such as two microstrips of
different widths against one port face, is refused**, and so is any face with more than two conductors. openEMS
handles both today.

Brief 113 measured a route that is not limited that way. Every terminal's mode goes on ONE shared port face (`Mode`
1…N, mode 1 `Active`, the rest `"Active": false`), and the modal S is converted to terminal S by a transform with two
corrections derived from what Palace writes:
- **The Gram correction (degenerate modes).** On a homogeneous (stripline) face, Palace's modes are not
  power-orthogonal, so P = 1 + S_m = Gᵀ·C with G[j,i] = ⟨e_j, h_i⟩. G is fitted from Palace's own Z_PV, over REAL g.
- **The Robin correction (non-degenerate modes).** Palace's scalar Robin term uses the active mode's k₁, so an
  inactive mode m is launched at 2k_m/(k₁+k_m) and reflected by (k_m−k₁)/(k_m+k₁). The correction is
  A − B = 2s − (k₁/k_m)·C in place of 2s − C.

**What 113 measured, and what it did not.** As written, without the corrections, the transform was off by 9–30 dB on
A. With them:
- A (the symmetric air stripline pair) was within **0.053 dB / 0.75°** of the 2D reference on its finest mesh, and
  converging. That is just over the bar.
- B (the symmetric microstrip pair) agreed with an ideal line built from Palace's OWN modes (thru 0.003 dB).

It was **never checked against an independent solver, never on an asymmetric pair, never on a lossy substrate**, and
its fixtures and harness are gone. 113 was set aside because its report read "no-go" and the corrections were
unvalidated, not because they were derived. **A derived correction is acceptable if it is shown to work.** This spike
decides whether it does.

**What changes from 113:** 113-a's Gmsh meshes (GMRES in 22–34 iterations, where 113's extruded meshes needed
170–350), 119's strip-edge refinement (the lever that made route B's bar), and independent references for every case.

## 1. Read first

- `RESOLVED.md` § brief-em3d-113: R-em3d113-1a to 1i, all of it. **It is the only record of the transform.** Rebuild it
  from that text and say in the findings where the text was ambiguous and what you chose.
- `RESOLVED.md` § brief-em3d-113-a (the shared-face run, R-em3d113a-1d: raw modal S, σ_max 1.27) and § brief-em3d-119.
- Palace v0.18.1: `docs/src/guide/boundaries.md` (*Lumped and wave port excitation*), `scripts/schema/config-schema.json`
  (`WavePort`: `Mode`, `Active`, `MaxSize`, `VoltagePath`), `palace/models/waveportoperator.cpp` (normalisation,
  `Initialize`, the Robin term, ≈ line 1548), `palace/models/postoperator.cpp` (`V_wp`, ≈ 1747).
- Issues (read only, never post): #328 (degenerate TEM modes), #960 (lossy-port normalisation and projection, which
  bears directly on §3d), #171 / PR #197 (`Active`), and the ½/√2 convention fix #960 cites (#937). Search the tracker
  again for anything since 2026-10-06 on several modes per face or terminal ports, and record it.

## 2. Method rules

- 113-a's and 119's rules apply: start from a committed fixture and reproduce it before changing anything; change one
  thing at a time; runs under 5 minutes each, about an hour of solver time in total. **Size every mesh before
  launching it**, because 119 overran on two runs it had not sized. Record every number in `RESOLVED.md` with its
  geometry, mesh, wall clock and memory. Judge entries below −30 dB by |ΔS|.
- **Derived corrections are allowed and must be stated**: each as a formula, with its derivation in a few lines,
  computed only from what Palace writes (`port-S.csv`, `port-V.csv`, `port-Z.csv`, the log's kₙ). **Never fit anything
  to a reference.** A correction that needs the answer to compute the answer is not a correction.
- When a result does not make sense, search the issue tracker and the web for a documented cause before theorising.

## 3. `R-em3d124-1` — what to measure

Setup on every case: one port rectangle per end covering every strip (113-a's `pair-a-shared-face` is the template).
On it, N `WavePort` entries, `Mode` k on entry k, mode 1 `Active`, the rest `"Active": false`, **equal `MaxSize`** on all
entries of a face (113's rule), `Offset` 0, and every entry excited. Use the mesh density that made 119's bar (r3 with
the 0.02 mm strip-edge field) unless sizing says otherwise. Use 5 GHz on A and 2 / 6 GHz on B, as 119 did.

**a. Reproduce, then rebuild the transform.** First, A symmetric on the shared face: Palace's raw modal S should show
113-a's mixture (σ_max well above 1). Then, through the rebuilt transform: against the 2D reference (101.95 / 70.885 Ω)
and against 119's route B result on the same mesh density (`testdata/em3d/terminal/palace-symmetry/a-half*-r3e0.02`).
Report the fitted g per face, its residual, and the Gram that the 2D reference predicts (113: −0.624 against −0.622).
**Also report whether the complex-g fit still lands on a wrong phase.** If it does, the real-g restriction is
load-bearing, and §3d decides whether it can survive loss.

**b. The asymmetric air pair (the decisive case).** Line 2 10 % wider (1.32 mm); also 3 % if (b) at 10 % passes. The
exact reference is `tools/palace-symmetry-spike/asym.py`: the multiconductor line from the 2D capacitance matrix, exact
in homogeneous air. Note that the modes are still degenerate here (both at k₀), so this exercises the Gram correction
on modes that are no longer even and odd.

**c. An asymmetric microstrip pair (non-degenerate modes).** Geometry B (εr 3.5, h 0.508, S 0.3, ℓ 15 mm), line 1
1.1 mm, line 2 1.32 mm, **lossless, in 119's shielded box** (PEC sides ±4.25 mm, PEC lid 3.508 mm). The shielding is
not optional: 119 measured 113-a's absorbing walls loading a guided mode. The reference is **openEMS through circuitRF's
own terminal ports**: write a `.c3d` of the same geometry and run `circuitrf em <file>.c3d` (brief 116's path). Use
the same strip thickness on both solvers; t = 0 sheets are exact on openEMS (116, 117), and then Palace needs PEC
sheets too. Say which you chose. **First establish openEMS's own error on microstrip**: run the SYMMETRIC B pair on
openEMS and compare it with 119's shielded route-B result (`b-half*-r3e-shield-full`), which matches Palace's own port
modes to 0.0006. The bar for (c) is then **0.1 dB / 1° above −30 dB and |ΔS| ≤ 0.006 below, against openEMS, or
openEMS's own measured error on the symmetric pair, whichever is larger**. Say which it was.

**d. The same asymmetric microstrip pair, lossy** (substrate loss tangent 0.02, on both solvers). The modes are complex
now. Report:
- whether the real-g fit still applies (it should not be needed: B's modes are orthogonal to −100 dB in 113, so G ≈ 1);
- the Robin correction with complex kₙ;
- the agreement with openEMS.

Read #960 first: Palace's conjugate projection is inexact for a lossy port cross-section (an O(φ) phase error,
φ ≈ ½·arctan tan δ, about 0.6° at 0.02). Measure whether that error is visible here and whether it can be corrected
from what Palace writes. Do not hide it inside the tolerance.

**e. Robustness.**
- Two mesh densities on (b): does the error converge?
- Three frequencies on (c): does the Robin correction hold across the band?
- Every case: σ_max, σ_min, and the per-port power balance |S11|² + Σ|S_i1|². On a lossless case these must be within
  0.002 of 1; σ_max alone is blind to a missing mode (113-a), and σ is blind to a wrong one (119).

**f. Cost.** Wall clock and peak memory per case, against route B's two half-model runs (99 + 103 s on A). The shared
face excites every entry, so N excitations per face.

## 4. Go/no-go: what brief 115 builds

The bar on every case is the one stated in its subsection, plus σ within 0.002 of 1 on the lossless ones. The finding
names exactly one of:
1. **The derived transform alone**: it meets the bar on (a), (b) and (c), and (d) agrees with openEMS to 0.1 dB / 1°.
   Brief 115 is rewritten around it, covering symmetric and asymmetric pairs and N > 2 if (b) generalises (say whether
   it does). Route B is not built.
2. **Route B for symmetric pairs, plus the derived transform for asymmetric ones**: (a)–(c) pass but (d) does not, so the
   transform is lossless-only. Say how 115 would tell the cases apart, and whether a lossy asymmetric pair is refused.
3. **115 as written (route B only)**: the transform fails (b) or (c). Asymmetric pairs stay on openEMS.

Whichever it is, record the cost comparison and every place the transform depends on Palace internals that #960 or
#328 could change. A Palace version bump is when those are re-checked (`SolverDiscovery`'s validated list).

## 5. Deliverables

- `src/Design/RESOLVED.md`: a section "Palace modal transform on a shared face — brief-em3d-124". It is headed by the
  documentation and issues read, then the transform as rebuilt (formulas, every choice the 113 text left open), every
  number as §2 asks, and the go/no-go.
- `testdata/em3d/terminal/palace-modal/`: configs, `port-S.csv`, `port-V.csv`, `port-Z.csv` and logs of the runs the
  findings rest on, with a README naming each departure. Also the openEMS `.c3d` files and their `.sNp` results. No
  meshes, no personal paths; shorten launch lines as the 119 fixtures do.
- `tools/palace-symmetry-spike/`: the shared-face mesh and config options, the transform, and the openEMS comparison,
  added to the existing scripts, with the README's table and the `run.sh` lines updated.
- The overview: the 124 row and D14's note say what was found. Brief 115's header line says which route it is to
  build. **Do not rewrite 115 in this brief.** That is the owner's next step once the go/no-go is in.

## 6. Scope

- No product code, no UI, no file-format change.
- Two-conductor faces, plus one three-conductor case only if (b) passes and there is solver time left (report it, do
  not solve its generalisation).
- Nothing is posted upstream.
