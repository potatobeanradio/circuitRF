# Sonnet Brief — Phase L9e: adaptive frequency sampling, the N budget, the refusal audit, and L9's PHASE GATE

**Design:** `docs/design/layout-view.md` **§10.7** (the cost model, R17's ceiling, and the adaptive-
frequency-sampling paragraph that has now been overtaken twice by measurement), §10.5's mesh report,
§10.9 (what may become a gate), §11's phase table row **L9**.

**Read, in this order, before planning anything:** `src/Engine/Mom/CLAUDE.md` **§L9d** end to end — its
COST section is the reason this slice exists and its "What is NOT built" section is the specification of
what you owe — then **§L8d**'s Tier 7 (the calibrator's ordering invariant, which is the collision this
slice has to resolve), then **§L8c**'s Tier 8 (the cost table and the two named savings), then
**§L9b**'s R-dcm-4 (the low-frequency `PathExtent` finding, recorded and deliberately not acted on), then
**§L8e**'s "near-DC hole". Then read `PlanarSolve.cs`'s `Run` and `PlanarPortCalibrator`, `SurfaceMesher`'s
`UnknownCeiling` and its three enforcement sites, and `src/Ui/Layout/Em/EmRunService.cs`'s frequency
expansion — they are the code you are changing, not summaries of it.

**Last of L9's five slices, and the only one that is mostly ENGINEERING rather than physics.** L9a built
the spectral kernel, L9b made DCIM work for it, L9c delivered the z-directed components and the via
basis, **L9d turned a two-level `Z` into a de-embedded s-parameter and measured what it costs: 71.9 s
per point, ~73 minutes for a 101-point sweep.** This brief closes the phase: make the sweep affordable,
say honestly what the budget is, fix the refusals that now point at capabilities that exist, and **build
L9's phase gate — including the part of it nobody has built yet, which is any check that a via's
terminal answer is physically right.**

---

## Gate command — and it is NOT the full solution

```
dotnet test tests/Engine.Tests --no-build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

**This slice touches `src/Ui` and it touches shipped defaults.** Adaptive sampling changes what a Simulate
actually solves; the refusal audit changes user-facing strings; the phase gate runs through the product
path the way L8's did (`src/Ui/CLAUDE.md`'s L8e entry is the model for how to report it). Run all three.

**The routine tier's headroom is small and the constraint has not moved since L9d.** `Engine.Tests` is at
**973 routine tests in 51 s** against the ~60 s ceiling, and what costs is the FILL, not the oracle. The
opt-in tier is now roughly **30 minutes** in total (L8 ~8.5 min, L9a 8 s, L9b ~11 min, L9c ~4 min, L9d
~6 min). **Every sweep, every de-embedded point and the whole phase gate go behind `Category=Benchmark`**,
and L8e's own precedent applies: the phase gate is opt-in, and what stays routine is the cheap
product-path wiring case. Say what you add and say how much of it is a solve.

**One measurement discipline, because this slice is nothing but measurements.** L8d's own note: a
benchmark sharing a run with nine others *reads more than twice as slow*. **Take every timing measurement
alone or not at all**, and say in the report that you did.

---

## 0. Read this before planning anything

### 0.1 What L9d hands you, in numbers

- **A de-embedded two-level point costs 71.9 s** at N = 514 with one via and four single-level standards:
  DUT 15.7 s, **standards 27.5 s**, frequency-independent cores 28.6 s once for all five meshes. A
  101-point sweep is **~73 minutes**. Against L8d's 7.66 s at N = 552 that is **9.4× at essentially the
  same N** — the mover is the per-entry cost of the general kernel, not the unknown count.
- **The standards are 64% of the per-frequency cost** (27.5 of 43.2 s), which is the same shape L8d found
  (78%) and for the same reason: they are 2.58× the DUT's own unknowns and there are two per port.
- **N for §10.7's FR-4 hero: 552 one level, 1,104 two levels, 1,140 with a via** — 2.07×, inside R17's
  5,000. A library PCell at L8b's worst (2,055 one level) projects to ~4,200: inside the ceiling and
  inside the warning band.
- **`PlanarKernelSet` fits once per (component, height pairing, frequency) and shares across the DUT and
  every standard** — 9 fits per frequency for a two-level structure with a via, asserted by `FitCount`.
  L9d's own headline bug was a cache that would have made that 9 per MESH; **do not break it.**
- **`G_A^zz` is validated only to ρ/λ ≤ 0.1** and it is now a REFUSAL, scoped to meshes carrying vertical
  current. §10.7's FR-4 hero is ~0.67 λ across at 10 GHz, so **a via-bearing DUT that size is refused
  outright.** This constrains the phase-gate fixture — see §0.2 item 5.

### 0.2 Seven things that are true before you start

**1. THE VIA HAS NEVER BEEN CHECKED AGAINST A PHYSICAL QUANTITY, and that is the largest single gap in
L9.** L9d's brief specified a Tier 2 (a via over a ground plane at εᵣ = 1, against the closed-form
wire inductance) and a Tier 4 (a through-via against its own mesh and quadrature convergence). **Neither
was built.** Every via test in L9c and L9d is structural — `Z` symmetric, the divergence signs summing to
zero, the via carrying non-zero current, the map being its own quantity, the refusals firing. Those are
all worth having and none of them would catch a via whose terminal inductance is 3× too large.
**Verified by grep: there is no test anywhere in `tests/Engine.Tests/Mom/` that compares a via to a
closed form or refines a mesh around one.** This is a gate item, not a nice-to-have.

**2. The calibrator is STATEFUL and must be stepped in increasing frequency order — and adaptive
refinement inserts points mid-band.** `PlanarPortCalibrator` resolves γ's 2π and `a₂₂`'s sign by
**continuation from the previous frequency** (L8d's D6, three branch decisions, gated by `T7_3`). Every
adaptive scheme picks its next point in the middle of the interval that disagreed most. **These two
facts collide, and resolving that collision is the design work in M1** — not the interpolation, which is
the easy half. Do not discover this in week three.

**3. Adaptive sampling must be INVISIBLE in the output shape and LOUD in the notes.** The user asked for
their own frequency grid (`EmSetup.Frequency.Expand()`) and must get exactly that grid back in the `S`
cube and the `.snp`. What must be reported, per run, is **how many points were actually solved, and the
worst disagreement the refinement stopped at** — because a user who cannot tell whether a value was
solved or interpolated cannot tell whether it is credible. That is the same argument L8e made for
naming the kernel in every outcome's `Reason`.

**4. The fit residual does NOT predict the error, and this codebase has now found that twice.** L7b-b's
`ModeCouplingResidual` is *anti-correlated* with the terminal error in frequency and at 20 GHz exceeds
it, so it is not even a bound. L8a's `FitResidual` picks a configuration whose far-field error is one of
the worst. **A refinement criterion built on how well an interpolant fits the points it was built from
is the same mistake a third time.** The criterion must be on the physical quantity — the disagreement
between two independently-supported estimates of **S** at a frequency neither of them was given.

**5. The phase-gate fixture has to be electrically small, or the gate cannot run.** §11's L9 row reads
*"Multi-layer structure with backside vias"*. With `G_A^zz` refused above ρ/λ = 0.1, an FR-4-scale
via-bearing structure at any interesting frequency is refused by construction; an MMIC at a few hundred
µm is ~0.01 λ and passes comfortably. **Design the gate on the MMIC starter and say why**, or get the
owner to rule on the eigensolver that would widen the limit (§5). Do not widen
`ValidatedRhoOverLambdaAtHeights` because the gate is inconvenient — that is the exact failure R-mom-17
exists to prevent, and L9c measured the 14× error that justifies the number.

**6. The buried-level de-embedding refusal does NOT block the gate, and knowing that saves a slice.** L9d
refuses a port on a level that is not the slab's top, because C_pul comes from an electrostatic image
series over a grounded slab (L9c's un-run Tier 4 is what would fix it). **A backside via runs from the
top metal down to the ground plane, so both ports are on the top level** and the gate is reachable
without touching it. Say so explicitly in the report so the next reader does not scope Tier 4 into L9.

**7. §11's L9 gate sentence is UNRESOLVED, the owner has not ruled, and L9a's proposed replacement is not
written down anywhere.** The row's second half reads *"agreement with published reference structures"*.
L9a found that cannot be a gate under this project's own rules — a published multilayer S-parameter
almost always arrives without a verifiable stackup, so the gate would measure the transcription — and
"proposed a replacement". **`src/Engine/CLAUDE.md` points at the L9a section for that proposal and the
section does not contain it** (checked: no occurrence anywhere in the repository). So the proposal was
made conversationally and lost. **Reconstructing it and putting it in front of the owner is yours**, and
§10.9's own rule stands: golden data becomes a gate only when the owner has approved it.

---

## 1. Decisions taken

**D1 — Adaptive sampling is a SETTING, and with it off the path is bit-identical.** L9a's D5 precedent
and R-mlp-1's: the general capability is built alongside the shipped one and gated against it, not on top
of it. Every measured number in §L8c, §L8d and §L9d must be reproducible by turning it off, at full
precision. Whether it defaults on is a decision to take **after** the measurement, and it is worth
stating either way in the report.

**D2 — The refinement criterion is on S, never on a fit residual.** Per §0.2 item 4. The honest form is
the standard one: solve a coarse set, build the interpolant, then for each interval solve the midpoint
and compare **the solved S against what the interpolant predicted there**. That number is an error, not
a residual, and it is what the stopping tolerance is expressed in. Report the tolerance and report the
worst final disagreement per run.

**D3 — Which interpolant is a MEASUREMENT, and there is a free one already in the repository.**
`RFNetwork.Interpolate` (cubic spline in the complex plane over an `SNP`, with per-component handling)
exists, is tested, and costs nothing to try. Rational interpolation is what §10.7 names and is genuinely
better on resonant structures — **and a Thiele continued-fraction or barycentric rational interpolant
needs no pole extraction to EVALUATE**, so it does not require the eigensolver D9 declines; only
*interpreting* it as poles would. **Vector fitting does require one and is out.** Try the spline first,
measure both on a structure with a real resonance (L8e's own λ_g/4 open stub is the obvious one), and let
the measurement decide — the L7b-b Route A/Route B precedent exactly.

**D4 — The DUT and its standards are sampled TOGETHER unless measurement says otherwise.** A de-embedded
S at a frequency needs both at that frequency. But the standards are uniform lines and are far smoother
than the DUT, so they may converge on a much sparser set — and they are 64% of the per-point cost, which
makes this the single largest available saving after the point count itself. **Measure it; do not assume
it either way.** If a sparser standard set is safe, the calibration is a first-class object and can be
built once on its own grid (L8d already says a UI could cache one per feed cross-section).

**D5 — The N budget is already enforced; what is owed is re-examining the CEILING against reality.**
`SurfaceMesher.UnknownCeiling = 5000` is checked in three places (the mesh report, `PlanarFill`'s cores,
`PlanarSystem`) with a warning band below it. What is *not* policed is a RUN: a de-embedded multi-level
run holds five meshes' cached cores plus a matrix, and L8c already measured the cached cores at **+51% on
top of the matrix** (559 MB resident at N = 4,933 for a single one-level mesh). **Report actual peak
resident memory for a de-embedded two-level run near the ceiling**, and say whether R17's number and its
message still mean what they say. Changing the constant is an owner decision; measuring it is not.

**D6 — ACA is a MEASUREMENT before it is a feature, and the honest answer may be "not yet".** At these N
the fill dominates the LU by 114× at the hero and still 1.8× at the ceiling, so ACA's value here is in
**not computing most of the matrix**, not in the solve. But a compressed matrix needs a solver that
consumes it — an iterative one, whose convergence on a MoM system is not guaranteed and is its own
research item. **Measure the achievable compression on a real two-level mesh first** (sample a few
far-field blocks, report the rank and the error at a stated tolerance). If it is poor at N ≈ 1,000–5,000,
say so and defer with the number — that is a legitimate answer with two precedents (L7b-b's Route B,
L9c's amplitude cap).

**D7 — The refusal audit edits wording, never scope, and each edit is a NARROWING.** Five strings are
known stale or misleading and are listed in §3. A refusal is narrowed when the capability arrives and
never in advance of it; the converse also holds — **do not delete a refusal because its phase number
looks old.** Every edit needs its test updated rather than loosened, and R-mlp-3 stands: the test that
measures a refused case must assert the answer out there is actually bad.

**D8 — The near-DC hole is closed here, because adaptive sampling is what will find it.** L8e recorded a
6 Hz frequency point producing `Array dimensions exceeded supported range` after 50 s — a raw framework
exception with no refusal, because the per-frequency radial table is sized for a wavelength of 50,000 km.
It is unreachable from the EM panel today. **An adaptive scheme chooses frequencies, and it must never
choose one there**, so the refusal belongs in this slice. L9b's own low-frequency finding is the same
region seen from the fit's side: `PathExtent` is stated in units of k₀ while the stack's image structure
lives at k_ρ ~ 1/H, so `PathExtent·k₀H` falls through 1 between 300 and 100 MHz on a 1.4 mm stack and the
error **grows as the frequency falls** (3.8e-3 → 2.9e-2). **Decide whether the fix is a frequency-aware
path extent or a refusal, measure it, and do not leave both open.**

**D9 — No dependency, and no eigensolver.** Unchanged since L8a; declined by L7b-b, L8a, L9b, L9c and
L9d. Nothing in this slice needs one — see D3 for the one place it looks like it might.

---

## 2. What already exists, and what genuinely does not

**Exists and is load-bearing:**

- `PlanarSolve.Run` (both overloads), `PlanarSolveContext`, `PlanarFrequencyKernel`, `PlanarKernelSet`
  with its `FitCount`, `PlanarFill`'s `CoreFillCount` — the two counters R-mlp-6 says a new cache must
  join or avoid.
- `PlanarPortCalibrator` — stateful, increasing-frequency-order, `T7_3`.
- `SurfaceMesher.UnknownCeiling` and its three enforcement sites; `PlanarMeshReport`'s three-way verdict
  and warning band.
- `RFNetwork.Interpolate` — complex cubic spline over an `SNP`, already tested (D3).
- `EmRunService`'s frequency expansion; `EmSnpProvenance`'s three hashes; `EmKernelRegistry`.

**Does not exist:**

- **Any adaptive frequency sampling anywhere.** `PlanarSolve.Run` loops the list it is given.
- **Any interpolation of a planar result onto a denser grid.**
- **Any check that a via's terminal answer is physically right** (§0.2 item 1).
- **Any run-level memory accounting**, as against per-mesh N accounting.
- **Any ACA, block tree, clustering or iterative solver.**
- **Any low-frequency guard** on the planar path.
- **L9's phase gate.**

---

## 3. The refusal audit — the concrete list

Each of these is a wording change plus its test. None is a scope change.

| file:line | current | why it is wrong now |
|---|---|---|
| `Dcim.cs:1526` | *"buried metal arrives with L9c"* | L9c built it — `Dcim.FitAtHeights`. Point at the API, and say what still cannot be done (de-embedded, per L9d's M3) rather than naming a phase. |
| `LayeredMedium.cs:53` | *"arrive with the general layered stack in L9"* | The general stack exists (`LayerStack`, `LayeredSpectralGreens`). This refusal is on `GroundedSlab`, the one-slab type; it should point at the general PATH, not at a phase. |
| `LayeredMedium.cs:59` | *"Buried and multi-level metal arrive with L9"* | Same. L9c/L9d built exactly that. |
| `SpectralGreens.cs:358` | *"Sources inside or below the slab arrive with L9"* | On the one-layer kernel. The general kernel does it now (`LayeredSpectralGreens.KernelAtHeights`). |
| `QuasiStaticKernel.cs:130-135` | *"[kernel B] is built on ONE grounded slab with ONE conductor layer on its top surface… A general dielectric stack arrives at L9"* — on a **sloped boundary** | **Both halves are now false.** Kernel B is no longer one slab with one layer, and the general stack has arrived — but neither helps here, because *"the general layered stack"* means N **horizontal** layers and a sloped or vertical dielectric boundary is outside the 2.5D premise entirely: **it is not L9's, in any slice.** L9a flagged this and `LayeredMedium.cs:164` carries the note. This one is actively misleading and is the reason the audit is a milestone rather than a chore. |

**`Dcim.CanFit`'s two refusals are correct as written and must be left alone.** The denser-bottom
half-space one (`Dcim.cs:214`) is a permanent structural refusal carrying its own measured numbers —
59× on G_q and 2.3e+4× on G_A — and reads *"this is a MISSING TERM, not a tolerance"*, which is exactly
the right shape. The closed-guide one (`Dcim.cs:207`) ends *"Nothing in L9 provides one"*, which is a
scope statement rather than a promise and is defensible; **if you touch it, make it name the
decomposition that would be needed, not the phase that would not provide it.** `PlanarPort.cs`'s
coplanar / differential / via-port refusals are likewise correct — L9d re-pointed that set at §10.6.

**Then sweep for the general case rather than only fixing this list**: any user-facing string naming a
phase letter is a liability, because a phase number is a promise about a schedule and a §-reference is a
statement about a design. L8d's own coplanar refusals pointed at *"L9"*, L9 arrived, and neither was
built — which is the argument for **naming where a capability arrives, not when.**

---

## 4. The formulation, stated as requirements

**R-adf-1 — With adaptive sampling off, every path is bit-identical.** Per D1. Reconstruct and compare at
full precision, the way L9b pinned twelve dumped fit configurations, L9c pinned 600 `Voltage` values and
L9d reconstructed L8d's own call sequence. The Tier oracles carry tolerances and structurally cannot
catch a one-ulp move.

**R-adf-2 — The published grid is the user's grid.** The `S` cube, the per-port `Z0` cube, the `"planar"`
diagnostics group and the `.snp` all carry exactly the requested frequencies. **The diagnostics group is
where the solved count and the final worst disagreement go**, and D6's rule from L8e stands: no new
result type.

**R-adf-3 — Determinism, bit for bit, and the refinement order is part of it.** An adaptive scheme has
state; a set iteration or a floating-point tie in "which interval disagreed most" makes two runs of the
same problem produce different point sets and therefore different interpolated values. Break ties on the
interval's own index, and gate that two runs are identical.

**R-adf-4 — Reciprocity and passivity are gates; losslessness is NOT.** Unchanged since L8a wrote the
warning and L8d/L9d honoured it: an open planar structure with vias radiates more, not less. **Do not add
a losslessness check anywhere**, and do not "fix" the kernel toward one.

**R-adf-5 — Nothing is cached across frequencies** that is not already provably frequency-independent.
`PlanarKernelSet.FitCount` and `CoreFillCount` are the two counters. An interpolant is not a cache of a
solve and must not be allowed to look like one — it is a **model of the answer**, and R-adf-2 is what
keeps the distinction visible to the user.

**R-adf-6 — The accuracy claim is a measured curve, per structure class.** How many solved points a
stated tolerance actually costs, on a smooth structure (a uniform line), a resonant one (the λ_g/4 stub)
and a two-level one with a via. **The resonant case is the one that decides D3**, and a scheme that is
excellent on a uniform line proves nothing.

---

## 5. The oracle ladder

**Tier 0 — structural, free.** Adaptive off is bit-identical; the published grid is the requested grid;
two runs are identical; the solved count is reported; every refusal fires and its neighbour is accepted.

**Tier 1 — the DENSE reduction, and it is the strongest single check in the slice.** Ask for a grid so
dense that refinement terminates immediately, and the answer must equal the non-adaptive one **exactly**.
Then ask for a grid the scheme genuinely has to refine, and compare against the fully-solved answer on
the same grid: that difference is the scheme's own error and it is what R-adf-6 reports.

**Tier 2 — THE VIA, against a closed form.** §0.2 item 1. At εᵣ = 1 over a ground plane a via is a wire
whose partial inductance has a closed form to within a stated approximation — the analogue of L8c's
*"against the εᵣ = 1 reduction, where the kernel is exact and only the quadrature can be wrong"* and of
kernel A's own `T1_2` image gate. **This is external-data-free and it is the first check that the ẑẑ
block reaches a terminal quantity correctly.** It is a gate item and it should be built first, because
everything in the phase gate stands on it.

**Tier 3 — THE VIA, against its own convergence.** Refine the mesh and the quadrature **separately**,
exactly as L8c's Tier 6 separates them, and report both sequences. A closed form bounded to a few percent
plus a clean convergence order is a much stronger statement than either alone.

**Tier 4 — the cost and the budget (D5).** The measured curve of solved points vs tolerance; the
end-to-end sweep time against L9d's 73 minutes; peak resident memory for a de-embedded two-level run.

**Tier 5 — ACA's measurement (D6)**, whatever it says.

**Tier 6 — L9's PHASE GATE**, through the product path: one drawn layout, a `.cem`, Simulate. L8e's own
gate is the model — three sentences, both starters where that is meaningful, at the shipping mesh, with
what it costs stated and the whole thing `Category=Benchmark`.

**A warning that has now cost this area seven milestones: check the oracle before concluding the method
is wrong.** L8a records two occasions, L7b-b a third, L8e's phase gate a fourth, L9a a fifth, L9c a
sixth, L9d a seventh (a drift number that reads alarming until you notice the two fixtures are not
electrically comparable). **When a rung disagrees, the first hypothesis is the rung.**

---

## 6. The phase gate — proposing it is part of the work

§11's L9 row reads: *"Multi-layer structure with backside vias; agreement with published reference
structures."* The first clause is buildable. The second is the one L9a says does not survive this
project's own rules and the owner has not ruled on (§0.2 item 7).

**What is on the record that a gate may rest on** (§10.9: prefer self-consistency checks that need no
external tool; golden data is owner-approved before it becomes a gate): an exact reduction; an
independently-computed geometry; a closed form whose source was read directly and whose inputs are
verifiable; a convergence sequence; a physically-predictable signature.

**A candidate set, offered for the owner to rule on rather than adopted:**

1. **A backside via's inductance against a closed form and against its own convergence** (Tiers 2 and 3),
   on the MMIC starter where the ρ/λ limit is comfortable. This is the *"backside vias"* half of the row,
   and it is the check that does not currently exist.
2. **A two-level coupled structure against the one-level reduction it degenerates to** — increase the
   inter-level spacing and the answer must converge onto two independent single-level results computed by
   the shipped path. An exact-limit check with no external data, the analogue of L7b's own far-apart gate.
3. **A physically-predictable signature that a wrong sign or a wrong image would destroy** — L8e used a
   λ_g/4 stub notch and a bend's rising |S₁₁|; the multi-level analogue is a via's series inductance
   showing as a rising |S₁₁| with frequency and a broadside-coupled pair's coupling falling with spacing.

**Say plainly whether you think the row's second clause should be struck or kept**, and if kept, what
verifiable published structure could satisfy it. That is an owner decision and this is the moment it is
due.

---

## 7. What must NOT be built here

- **A general complex eigensolver, GPOF with SVD truncation, or vector fitting.** D9. L9c measured what
  the first would buy for `G_A^zz` and the owner has not ruled.
- **L9c's un-run Tier 4** (a static Green's function at interior heights). It unblocks buried-level
  de-embedding and it is the most valuable thing anyone could build next for this area — **and it is not
  needed for L9's gate** (§0.2 item 6). Name it; do not scope it in.
- **Any widening of `Dcim.ValidatedRhoOverLambda`, `ValidatedRhoOverLambdaLayered`,
  `ValidatedRhoOverLambdaAtHeights` or `Dcim.CanFit`** on the grounds that a gate fixture is inconvenient.
- **A losslessness check** (R-adf-4).
- **Co-simulation ports, coplanar or differential references, finite ground pours, meshed ground,
  conformal or diagonal boundary cells, a new starter technology, wirebonds.** None of these are L9's.
- **A z-integral along a via.** L9c's midpoint rule is refused by name above kℓ = 0.05; leave it.
- **Changing `SurfaceMesher.UnknownCeiling`.** Measure it (D5); the constant is the owner's.

---

## 8. Milestones, each with its own gate

| | content | gate |
|---|---|---|
| **M1** | Adaptive frequency sampling: the criterion (D2), the interpolant measurement (D3), and **the calibrator-ordering collision** (§0.2 item 2) | **Tier 0** and **Tier 1** |
| **M2** | The via's physical correctness — the check that does not exist (§0.2 item 1) | **Tier 2** and **Tier 3** |
| **M3** | The N budget re-examined against reality; the low-frequency guard (D5, D8) | **Tier 4** |
| **M4** | The refusal audit (§3) and the phase-number sweep | every touched test updated, not loosened |
| **M5** | ACA's measurement (D6), and **L9's PHASE GATE** through the product path (§6) | **Tier 5**, **Tier 6** |

**M1 is the one with a wrong obvious answer** — that adaptive sampling is an interpolation problem. The
interpolation is the easy half; **the calibrator's increasing-frequency-order invariant is the hard one**,
and the two obvious resolutions (cache the standards' raw T-matrices per frequency and re-run the cheap
branch continuation over the sorted union each round, versus making the branch resolution non-incremental
by predicting βΔℓ from the physics) have different costs and different failure modes. Measure, decide,
and say which.

**M2 is the one that should be built FIRST regardless of the milestone order**, because the phase gate
stands on it and because a via that is structurally perfect and physically wrong is exactly the class of
defect this area's oracle ladders exist to catch. **If it turns out the via is wrong, that is the finding
and it outranks everything else in this brief.**

**M5's gate is the phase's**, and if M1–M4 consume the slice, stop and report — that is the natural fault
line, and whether L9 becomes six slices is the owner's call and not this brief's.

---

## 9. File map (indicative)

```
src/Engine/Mom/
  PlanarSolve.cs          + the adaptive driver, the setting, the solved-count report (M1)
  PlanarAdaptiveSweep.cs  NEW — the criterion, the interpolant, the refinement loop (M1)
  PlanarSolve.cs          + the calibrator-ordering resolution (M1) — check PlanarPortCalibrator first
  PlanarSystem.cs         + run-level memory accounting, if D5 says the per-mesh check is not enough
  SurfaceMesher.cs        + the low-frequency guard (D8) — or Dcim, if the fit is where it belongs
  Dcim.cs                 + the frequency-aware PathExtent decision (D8), or its refusal
  LayeredMedium.cs / SpectralGreens.cs / QuasiStaticKernel.cs  the refusal audit (M4)

src/Ui/Layout/Em/
  EmRunService.cs         + surfacing the solved count and the worst disagreement in the notes
  EmSetupModel.cs         + the adaptive setting, if it is user-visible — omitted at its default

tests/Engine.Tests/Mom/
  AdaptiveSweepTests.cs   NEW — Tiers 0, 1, 4
  ViaPhysicsTests.cs      NEW — Tiers 2, 3.  THE ONE THAT MATTERS MOST
  L9PhaseGateTests.cs     NEW — Tier 6, all Category=Benchmark
  RefusalWordingTests.cs  extend; loosen nothing
```

Nothing outside `src/Engine/Mom/`, `src/Ui/Layout/Em/` and their tests should change. If something does,
that is a finding worth reporting, not a step to take quietly.

---

## 10. Six things to report back on, whatever else happens

1. **Whether the via is physically right** (Tier 2 and Tier 3). This has never been checked, it is the
   foundation of L9's gate, and if the answer is no, it outranks the rest of the brief.

2. **How the calibrator-ordering collision was resolved**, and what the alternative cost. This is the
   slice's real design decision and it is invisible from the outside once it works.

3. **The measured curve of solved points vs tolerance**, on a smooth structure, a resonant one and a
   two-level one with a via — and **the end-to-end sweep time against L9d's 73 minutes.** §10.7 predicts
   5–10× fewer solves; say what it actually is, and on which structure class it is worst.

4. **Which interpolant won and by how much** (D3), on the resonant case. If the free cubic spline is
   good enough, that is a better answer than a rational one and it needs the measurement, not the
   argument.

5. **What R17's ceiling actually means now** — peak resident memory for a de-embedded two-level run near
   the ceiling, against §10.7's 400 MB line and L8c's already-corrected 559 MB. Say whether the constant
   and its message still tell the truth.

6. **Your proposal for §11's L9 gate sentence**, with the reasoning, for the owner to rule on. L9a's was
   lost; do not let this one be. And say plainly whether ACA earned its keep at these N or whether the
   honest answer is a measured deferral.
