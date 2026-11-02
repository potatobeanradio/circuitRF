# Brief — conductor loss in the full-wave kernel: the series

**Status:** CL1-CL5 ALL SHIPPED 2026-09-14 (`RESOLVED.md` §CL1, §CL2, §CL3, §CL4, §CL5);
**CL6-CL7 briefed 2026-09-14, not started** ·
**Date:** 2026-09-14 · **Area:** `src/Engine/Mom`,
`docs/user/src/reference`
**Requirement tag for the series:** `R-cl-n`, scoped per brief (`R-cl1-3` is brief 1's third)

---

## 0. The short answer

Kernel B's metal is a perfect conductor. `PlanarConductorLayer.SigmaSm` and `ThicknessM` are carried
from the stackup editor, through `PlanarExtractor`, into `PlanarProblem`, and into the provenance
hash — and **never read by the fill**. `PlanarProblem.cs:97` says so in as many words: *"carried for
L8c's loss model"*. That model was scoped out at L8d (`brief-L8d-ports-and-de-embedding.md:250`,
R-prt-12) and never came back.

This series reads them.

### The measurement that decided the series

Run with kernel A, which **does** model conductor loss, on 50 Ω lines with the width re-bisected per
case. α_c as a share of conducted loss, i.e. the share kernel B omits:

| Substrate | 1 GHz | 2 GHz | 10 GHz | 20 GHz | 40 GHz |
|---|---|---|---|---|---|
| FR-4 1.6 mm, 35 µm Cu, tanδ 0.02 | 8.8% | 6.4% | 3.0% | 2.1% | 1.5% |
| **MMIC starter — 100 µm GaAs, 3 µm Au, tanδ 0.0006** | **99.0%** | **98.5%** | **96.2%** | **94.6%** | **92.5%** |
| Low-loss laminate 0.508 mm, 35 µm Cu, tanδ 0.0015 | 78.6% | 72.2% | 53.7% | 45.1% | 36.7% |

The FR-4 row reproduces the figure already recorded in `src/Engine/Mom/CLAUDE.md` §5 and in
`docs/design/mom-engine.md:1197` (6.5 / 3.0 / 2.1% at 2 / 10 / 20 GHz) to within a tenth of a point,
which is what says the measurement is of the same quantity and not of something else.

**On the MMIC technology this repository ships, the full-wave kernel omits 92-99% of the loss.**
Absolutely: 0.19 / 0.37 / 0.52 dB/cm of conductor loss at 2 / 10 / 20 GHz against a dielectric term
of 0.003 / 0.015 / 0.029. Those absolutes sit on published thin-film microstrip loss, so the kernel-A
model is not misbehaving. The five hero circuits are power amplifiers on exactly this kind of
substrate, and insertion loss in an output match is the number that decides the efficiency the tool
is for.

> **The recorded 6.5 / 3.0 / 2.1% is correct and was measured on the one substrate class where
> conductor loss matters least.** The generalisation built on it — *"dielectric loss dominates on
> ordinary substrates"*, `docs/user/src/reference/mom-engine.md:132` — is false for the shipped MMIC
> technology, by a factor of about thirty. **Fixing that sentence is independent of this series and
> should not wait for it**; see §4.

### What each brief does

| | Brief | Risk |
|---|---|---|
| **CL1** | The surface-impedance term in the fill. Strip metal only, behind an internal flag, default OFF | Low physics, **broad plumbing**. Carries the gate that decides whether thick metal is ever needed |
| **CL2** | `P_conductor` stops being an identical zero: the power budget, the metrics, the radiation efficiency | Low |
| **CL3** | Flip the default, re-bless the goldens, the docs, the A-vs-B gate, the cost measurement | Low, **wide** |
| **CL4** | The lossy ground plane — an impedance termination in the layered Green's function | **High.** May return a measured refusal |
| **CL5** ✅ | The lossless-stack refusal belongs to the integrator, not to the fit | Low. Also closes a live defect. **SHIPPED** — `RESOLVED.md` §CL5 |
| **CL6** | The one-slab kernel gets the same floor, so no run is re-based onto a looser tier | **High.** One re-derivation, with a named landing |
| **CL7** | The extractor writes the floor: the QSC ground term, the budget's label, the re-bless | Low, **wide** |

CL1 → CL2 → CL3 in that order. **CL4 is conditional on CL3 having landed** and is worth 11-25% of
the conductor term (§2). **Thick metal is not a brief in this series** and §3 says why.

### And the three that follow, because CL4 did not reach a user

**CL4 landed as a success and a partial one**: all four of its ordered measurements pass, the
termination is built and measured — and `PlanarExtractor` still writes a PEC floor, so **no run
anyone can make has a conducting ground plane** (§CL4 §9). CL5 → CL6 → CL7 is what closes that, in
that order, and **CL6 is the one that decides whether the series lands**.

CL4 §9 weighed three obstructions to simply turning the floor on. **One of them is gone: CL5 shipped
and removed it** — the general path's lossless-dielectric refusal was the DIRECT integrator's
precondition, borrowed by a fit that integrates nothing, and the fit's own spectral residual is flat
across twelve decades of tanδ down to and including zero (`RESOLVED.md` §CL5 §3). A two-level design
on a tanδ = 0 substrate solves through `PlanarSolve` now, and a CL4 conducting floor is admitted
rather than called lossless. The second is real and is why CL6 exists rather than a one-line
extractor change: the general kernel's validated range is **2.6× looser** (1.6e-2 against 6e-3), and
buying an 11-25% term by re-basing every ordinary microstrip onto a looser tier is not a trade worth
making. The third is the re-bless, and it is CL7's.

**A conditional CL8 is named and not briefed:** a two-sided, TRANSMITTING plane — finite-thickness
metal into a stated medium below — which is what would make `FrontToBackDb` computable and change the
far field's θ range. CL4 §6 refuses it correctly as things stand: a Leontovich condition replaces
everything below z = 0 and computes no transmitted field, so the leakage is not small in this model,
it is ABSENT from it. That is different physics, not a tolerance, and it gets a brief only if
backside radiation is actually wanted.

### One thing landed between CL1 and CL2, and it moves where CL3's flip lands

**`brief-quasistatic-port-calibration.md` shipped on 2026-09-14, after CL1** (`RESOLVED.md` §QSC).
Below a per-stack crossover a port is no longer calibrated from two full-wave line standards: γ and
Z_c are supplied from the standard's own electrostatics, **where the metal is a perfect conductor by
construction**. The standards are still solved full-wave, so after CL3's flip their `S` carries α_c
while the γ the error box is solved against does not.

The crossover is **3.048 GHz** on the FR-4 starter and **26.07 GHz** on the MMIC starter — so on the
substrate class this series exists for, **every frequency anyone runs is on the quasi-static side of
it**. CL1 is unaffected (its own gates read γ from the two-line extraction directly); CL2 is
unaffected (the power budget is a quadratic form in the solved current and no calibration touches
it). **CL3 §0 is where it is answered, and it gates the flip rather than following it.** CL4
inherits the same gap for the ground term.

---

## 1. The one rule the whole series is built on

> **A surface impedance is a term in the EFIE, not a post-processing correction.**

`Z_s·J` joins `jωA + ∇Φ` on the left-hand side and changes the solved current. The alternative —
solve PEC, then integrate `R_s|J|²` over the answer — is cheaper, is what a first attempt reaches
for, and is wrong in the way that matters: the PEC current has an unregularised `1/√d` edge
singularity, so `∫R_s|J|²` over it is logarithmically divergent in the edge mesh and **the number
you get depends on `EdgeCells`**. Loading the operator penalises that singular edge current
self-consistently. It does not remove the convergence question — R-cl1-9 is how that gets measured —
but it is the difference between a quantity that converges slowly and one that does not converge at
all.

The second rule, which follows from the first:

> **Every surface-impedance number the series produces is checked against kernel A's Wheeler term on
> a uniform line.** Kernel A is an independent formulation — incremental inductance over a receded
> boundary mesh, not a second copy of an EFIE — so an agreement between them is evidence. Its
> `RlgcModel.RMatrix` is the oracle, and `RlgcExtractor.cs:116-117` is the whole of it.

---

## 2. Where the loss actually is, and what a strip-only model reaches

Kernel A's Wheeler term is a sum over surfaces, so setting the ground's σ to infinity isolates the
strip's own contribution exactly. Ground-plane share of the **conductor** term:

| | FR-4 | GaAs MMIC | Low-loss laminate |
|---|---|---|---|
| ground plane | 21.1% | ~11%¹ | 25.0% |
| **strip only — what CL1 reaches** | **78.9%** | **~89%** | **75.0%** |

¹ 8.1 / 10.6 / 11.0% at 2 / 10 / 20 GHz. The 2 GHz figure is depressed because 3 µm gold puts
`RlgcModel.WheelerValidAboveHz` at 2.75 GHz, so that row is on the DC blend. 11% is the clean number.

**CL1 without CL4 is a real result, not a half-measure** — and the case where it reaches least
(75%, the low-loss laminate) is not the case where the absolute error is largest.

**It is also not always partial.** A ground drawn as artwork on a meshed level — coplanar waveguide,
a drawn backside path, anything `PlanarGroundPath` builds — is an ordinary conductor level and CL1
covers its loss completely. CL4 is only about the laterally infinite PEC the Green's function
terminates on, i.e. the microstrip case.

---

## 3. Why thick metal is not in this series

The question was asked directly, so the answer is measured rather than argued. At 10 GHz, every row
re-bisected to a true 50 Ω, ground held PEC, against the two-sided sheet impedance
`Z_s = (η/2)·coth(γt/2)` that a zero-thickness model would use:

**FR-4, copper, δ = 0.661 µm**

| t (µm) | t/δ | kernel-A R (Ω/m) | Re(Z_s) (Ω/sq) | R, normalised | Z_s, normalised |
|---|---|---|---|---|---|
| 5 | 7.6 | 10.29 | 0.01306 | 1.256 | 1.001 |
| 18 | 27 | 8.72 | 0.01304 | 1.065 | 1.000 |
| 35 | 53 | 8.19 | 0.01304 | 1.000 | 1.000 |
| 105 | 159 | 7.31 | 0.01304 | 0.892 | 1.000 |

**GaAs, gold, δ = 0.786 µm**

| t (µm) | t/δ | kernel-A R (Ω/m) | Re(Z_s) (Ω/sq) | R, normalised | Z_s, normalised |
|---|---|---|---|---|---|
| 1 | 1.3 | 535.7 | 0.02474 | 1.400 | 1.698 |
| 2 | 2.5 | 420.6 | 0.01478 | 1.099 | 1.014 |
| 3 | 3.8 | 382.6 | 0.01457 | 1.000 | 1.000 |
| 12 | 15.3 | 293.8 | 0.01552 | 0.768 | 1.065 |

**The coth saturates at t/δ ≳ 3 and every shipped metal at RF is past it.** Over the thickness range
real stackups use, `Z_s` is flat to 0.1% (FR-4) / 5% (GaAs) while the true loss moves by **1.41× and
1.43×**. A zero-thickness sheet is thickness-blind in exactly the regime that matters, and the real
dependence is geometric — perimeter and sidewall current — not through-thickness skin effect. That
is a genuine structural limit of the single-sheet model and it is stated here rather than discovered
at CL3.

**Four reasons it is still not the first move, in order of weight:**

1. **Multi-sheet is not the sheet model plus one more sheet.** Two sheets of one conductor are joined
   at every polygon boundary by sidewall current, so it needs a z-directed basis at every edge cell
   of every shape. `PlanarBasisFunctions`' vertical basis exists (L9c) but was built for vias — a
   handful of bases — where this is the entire perimeter of every drawn polygon. Mesher change,
   basis change, port change, on top of the fill change.
2. **R17's 5,000-unknown ceiling is already binding** and `PlanarSystem.GuardCeiling` refuses on it.
   Doubling the unknowns on every metal level halves the drawable geometry. Worse, two sheets ~3 µm
   apart with ~50 µm cells is the thin-layer regime `InteriorStaticGreens`, the MIM thin-layer gate
   and `Dcim.ValidatedRhoOverLambdaAtHeights = 0.1` had to be built for at 0.2 µm plate separation —
   on capacitors. This would put every trace there.
3. **CL1 is a strict prerequisite either way.** `Z_s(ω,σ,t)`, the Gram matrix, both fill seams, the
   AIM near block, the `P_conductor` integral, the DC-continuity gate — every one is load-bearing in
   a thick-metal model too. Only the coth's argument and the number of sheets change.
4. **Nobody yet knows it is needed.** A loaded EFIE computes its own edge-crowding factor from the
   solved current; backing that factor out of the table above gives **1.897** (FR-4, 35 µm) and
   **1.857** (GaAs, 3 µm) — two very different geometries landing in the same place, which is weak
   evidence the single sheet calibrates well at a fixed thickness. Within one technology thickness
   *is* fixed.

**So the decision is deferred to a measurement, not to a preference** — R-cl1-9, CL1's own edge
convergence gate, on the pattern L8a used for its branch-point order and P4 used for its cache
layout. Converges against kernel A within the band R-cl1-9 states → thick metal is a refinement
nobody has asked for. Comes back mesh-dependent → there is a measured structural argument for it,
and *then* it gets a brief.

---

## 4. Independent of the series, and it should not wait for it

`docs/user/src/reference/mom-engine.md:130-143` tells the user that dielectric loss dominates and
that the omission is a few per cent. On the technology the New Workspace dialog offers, it is
92-99%. **That paragraph should be corrected now**, whether or not CL1 is ever written: the fix is
to state the FR-4 number as the FR-4 number, add the MMIC row, and say which substrate class each
applies to. A user reading it today on a GaAs design is being told the opposite of the truth.

`src/Engine/Mom/CLAUDE.md` §5's own bullet and `docs/design/mom-engine.md:1197`'s blockquote carry
the same generalisation. **Per the standing instruction, none of these briefs writes to a
`CLAUDE.md`** — the findings go to `src/Engine/Mom/RESOLVED.md`, and each brief's completion section
instead **reports the stale lines to the owner by file and line** so the correction is theirs to
make rather than silently dropped.

---

## 5. Out of scope for the whole series

- **Surface roughness.** A multiplier on `R_s` once CL1 exists, but it needs a roughness field on
  `StackupLayer`, which is the tech model, the `.ctech` round trip, `TechValidation` and the Stackup
  tab. That is a format change, not a kernel change, and it gets its own brief if it is wanted.
- **Kernel A.** It already models conductor loss and nothing here touches `RlgcExtractor`,
  `BoundaryMesher` or `QuasiStaticKernel` except to READ them as the oracle.
- **Kernel W.** `wBond`'s Bessel internal impedance is exact and unrelated.
- **A user-facing on/off control.** §D3's rule for `PlanarMeshSettings` governs: a control earns its
  place by being a decision the user should make. Whether Maxwell's equations include Ohm's law is
  not one. The flag CL1 adds is an internal `PlanarFillSettings` member kept as the PEC oracle, on
  the pattern of `UseSymmetricFactorization = false` and `UseRadialTable = false`. **It never
  reaches `EmSetupPersistence` and there is no `.cem` key for it.**
