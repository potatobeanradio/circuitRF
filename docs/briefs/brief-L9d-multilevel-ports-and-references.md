# Sonnet Brief — Phase L9d: ports on more than one level, references, and the multi-level extractor

**Design:** `docs/design/layout-view.md` **§10.6** (ports, references, de-embedding), §10.3.4 (the
registry and `EmCapabilities`), §10.5's D8 grid decision, §11's phase table row **L9**.

**Read, in this order, before planning anything:** `src/Engine/Mom/CLAUDE.md` **§L9c** end to end —
its M4 and M5 sections are the specification of what you are handed and its "What is NOT built"
section is the specification of what you owe — then **§L8d** (ports, excitation, de-embedding: the
whole of it is what this slice generalises), then **§L8e** (the registry, the diagnostics group, the
current-density reduction), then **§L8b**'s R-msh-2 and **§L9c**'s R-via-5, which are the two ordering
contracts everything here indexes by. Then read `PlanarPort.cs`'s header, `PlanarDeembed.cs`'s D5/D7,
and `src/Ui/Layout/Em/PlanarExtractor.cs` — they are the code you are extending, not summaries of it.

**Fourth of L9's five slices.** L9a built the spectral kernel, L9b made DCIM work for it, **L9c
delivered the z-directed components, the interior inverse transform, the interior fit, the via basis,
the multi-level problem type and the multi-level FILL. There is a two-level `Z` matrix and nothing
excites it.** This brief specifies the excitation, the ports, the references, the de-embedding and the
Ui-side extractor. No adaptive frequency sampling, no ACA, no N-budget enforcement, no refusal audit,
no phase gate (L9e).

---

## Gate command — and it is NOT the full solution

```
dotnet test tests/Engine.Tests --no-build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

**This slice touches `src/Ui` and therefore breaks L8a–L9c's precedent that a slice's blast radius is
`src/Engine/Mom/`.** `PlanarExtractor` is behind the firewall and produces the `PlanarProblem`; L9c
deliberately made its new members optional so this slice would get a design rather than a compile
error. **You own that design.** Run `Ui.Tests` every time.

**The routine tier's headroom is small and the constraint has MOVED again.** L9c left `Engine.Tests`
at **965 routine tests in 51 s** against the ~60 s ceiling. It is no longer the oracle that costs — it
is the **FILL**: a two-level 514-unknown fill is seconds, and L8d's own de-embedding costs 4.4× a bare
fill because the standards are 2.58× the DUT's unknowns. **Budget the routine tier at a handful of
multi-level fills in total and put every sweep and every de-embedded point behind
`Category=Benchmark`.** L8's opt-in tier was ~8.5 min, L9a added 8 s, L9b ~11 min, L9c ~4 min; say what
you add and say how much of it is a solve rather than an oracle.

---

## 0. Read this before planning anything

### 0.1 What L9c hands you, in numbers

These are measured, they are in `src/Engine/Mom/CLAUDE.md` §L9c with the tests that produce them, and
this slice is scheduled against them.

- **`PlanarFill.FillMultiLevel` produces a symmetric `Z` for a mesh with vertical bases**, and
  reproduces L8c's own fill on a one-level mesh to **6.8e-7** through two independent fits and two
  independent extractions. `Z` is symmetric **bit-identically**, with the mixed (ẑx̂) block non-zero,
  so `Y = BᵀZ⁻¹B`'s structural reciprocity (L8d's D1) survives unchanged.
- **The unknown vector's shape is a contract: every horizontal basis of every level comes first, then
  every vertical one** (R-via-5). `PlanarMeshReport.ViaUnknownCount` says how many of the tail are
  vertical. **Ports, the current-density map and de-embedding all index by this**, and the horizontal
  block being a PREFIX is what lets a port resolver stay ignorant of whether a mesh has vias.
- **N for §10.7's FR-4 hero: 552 one level, 1,104 two levels, 1,140 with a via.** 2.07×, well inside
  R17's 5,000. A library PCell at L8b's worst (2,055) projects to ~4,200 — inside the ceiling and
  inside the warning band.
- **D7's fit counter measured 9 fits per frequency** for a two-level structure with a via, against a
  ~12 projection. `PlanarKernelSet` is keyed on the height PAIRING, is mesh-independent, and
  `FitCount` is asserted — so L8d's "fit once per frequency, share across the DUT and every standard"
  decision carries over intact and is the thing you must not break.
- **`G_A^zz` is validated only to ρ/λ ≤ 0.1** (`Dcim.ValidatedRhoOverLambdaAtHeights`), against
  ρ/λ ≤ 1.0 for the horizontal components. §10.7's hero is ~0.67 λ across at 10 GHz, so **a
  via-coupled entry between widely separated cells is outside the validated range and the fill knows
  it** — `PlanarKernelSet.WithinValidatedRange` asks once per mesh. §0.2 item 4 is about what you do
  with that answer.
- **A via is treated as electrically SHORT** and its kernel evaluated at the midpoint of its two feet;
  `PlanarLevels.CanUseMidpointRule` refuses kℓ > 0.05 by name.

### 0.2 Six things that are true before you start

**1. `PlanarPort.LayerIndex` exists, is resolved, and has never been given a non-zero value.** That is
L8d's own words and it is still true. The port machinery is written over a basis-index incidence
matrix (L8d's D1: "a port is an INCIDENCE MATRIX, and that is what makes the whole slice small"), and
a rooftop is normalised to unit current across its shared edge so a delta-gap of *v* volts reacts with
it as exactly *v*. **Nothing about that argument mentions a level**, which is why the port resolver may
turn out to need less work than it looks. Check that before designing around it.

**2. A port ON A VIA is a genuinely different object and it is the one that needs a decision.** A
horizontal port cuts a line and drives the rooftops across the cut. A vertical basis's "cut" is the
via itself, its unit current already crosses the footprint, and there is no second side to reference
against. Whether a via port is (a) meaningless and refused, (b) a port between the two LEVELS at the
via's location, or (c) an internal port for co-simulation, is a design decision — **and (a) is a
legitimate answer if it is the measured one.** Do not build (c); §10.6's co-simulation ports are not
L9's.

**3. De-embedding assumes a UNIFORM LINE and L8d's own finding is the warning.** "What limits
de-embedding accuracy is RADIATION, not the algebra" — the de-embedded S of a uniform section is exact
at the two lengths the calibration was solved from and drifts away from them, scaling as f², because
direct radiative and surface-wave coupling between the ports has no term in a "box + matched line +
box" model. **A two-level structure radiates more, not less**, and a via is a discontinuity in the
middle of the very thing the calibration assumes is uniform. **The calibration standards must stay
SINGLE-LEVEL uniform lines** — a standard with a via in it is not a standard. Say so, and gate it.

**4. The `G_A^zz` range is the one number that could make this slice produce plausible wrong
s-parameters, and it must be a REFUSAL rather than a note.** R-mom-17's shape: name the feature, name
where it is handled instead. The fill already asks; what is missing is a caller that acts on the
answer. **A refusal is narrowed when the capability arrives and never in advance of it** — and the
converse holds here: do not widen `ValidatedRhoOverLambdaAtHeights` because a hero geometry is
inconvenient. L9c measured it and recorded exactly what closing it would cost (a depth search that is
not Prony-on-two-fixed-paths, i.e. D8's declined eigensolver).

**5. `EmCapabilities.LayeredWithVias` has been declared since L6 and read by nothing, and L9c
deliberately still did not wire it** — with no solve there was nothing true to declare. **After this
slice there is. Declaring what it means, in one place, and wiring `EmKernelRegistry` to it, is
yours** — and D2's rule from L8e stands: auto-selection takes extractor VERDICTS, not geometry.

**6. The Ui-side extractor is the half of this slice that has no precedent to copy.** `PlanarExtractor`
currently produces exactly one `PlanarConductorLayer` from one signal layer, with the slab from the
technology. It must learn to produce N levels with their z coordinates, a `LayerStack`, and the vias
between them — from a `.ctech` stackup that already describes the layers. **`EmSetupModel` and the
`.cem` document are where the user says which levels to include**, and that is a UI design question as
much as an extraction one. Budget for it; it is not a footnote on the engine work.

---

## 1. Decisions taken

**D1 — The port is still an INCIDENCE MATRIX, and the burden of proof is on anything that says
otherwise.** L8d's D1 is what made ports cheap, and nothing in L9c's basis changes the argument: a
vertical basis is normalised to unit current across its shared footprint exactly as a rooftop is
across its shared edge, so a delta-gap still reacts with it as exactly *v*. **Start by trying the
existing resolver on a two-level mesh and reporting what actually breaks.** If the answer is
"nothing", that is the finding and this decision costs one test.

**D2 — A port's LEVEL is part of its identity, and the resolver must refuse an ambiguous one BY
NAME.** `PlanarPort.LayerIndex` exists; with two levels a port cut at a given (x, y) can intersect
metal on both, and picking one silently is exactly the shape of failure R-mom-17 exists to prevent.
L8d's own port-ambiguity threshold (§L8e: "must scale to the line END not the bounding box") is the
precedent for how to word it.

**D3 — Calibration standards stay SINGLE-LEVEL, and that is a refusal rather than a convention.**
Per §0.2 item 3. A two-level DUT is de-embedded against single-level uniform-line standards on the
level the port sits on. **Whether that is even legitimate is a measurement**: L8d's Tier 3 (A and B
agree on a uniform line) has no two-level analogue, and the honest gate is L8d's own — the de-embedded
S of a uniform section, at and away from the lengths the calibration was solved from. Report the drift
and compare it to L8d's measured 3.9e-4 at 2 GHz / 6.0e-3 at 10 GHz on 1.6 mm FR-4.

**D4 — The current-density reduction gains a VERTICAL map, and its two surprising consequences are
L8e's, not new.** L8e's D5 records that an outermost cell carries HALF what its neighbour does (a
rooftop spans two cells) and that the exact identity is against the two adjacent EDGE currents' mean.
A via's current is a single number per vertical basis, not a per-cell density, so the map is a
different object — decide whether it is a third quantity in the same `DataSet` group or an annotation
on the two levels it joins, and say why.

**D5 — The extractor produces N levels or it refuses, and there is no half-way.** Per §0.2 item 6. The
`.ctech` stackup already carries layer heights; the question is which conductor layers the user wants
in the analysis and how vias between them are identified from the artwork. **`PlanarExtractor`'s
ungrounded-stack refusal is narrowed HERE** — L9b measured *whether* it can be (yes for an
equal-density open bottom, never for a denser one) and L9c did not touch it. That is this slice's, and
it is a narrowing, never a deletion.

**D6 — The `DataSet` shape does not change.** L8e's D1/D4 stand: same `S`, same per-port `Z0`, same
`"planar"` diagnostics group. A two-level result is not a new result type. If something genuinely does
not fit, that is a finding worth reporting before inventing one.

**D7 — Report the COST as a measurement, not a projection.** L9c measured N (2.07× for two levels
with a via) and the fit count (9) but **not the seconds**, because there was no solve. You have one.
L8d measured **7.66 s per de-embedded point** at N = 552 and 78% of it in the standards; L8c measured
the fill at 114× the LU at N = 552 and still 1.8× at N = 4,933, so the fill is what grows and 2.07× N
is ~4.3× the fill. **Check that projection rather than quoting it** — L9a's was wrong by 15–35×. If a
101-point de-embedded sweep of a two-level structure lands in hours, that is the finding and L9e's
adaptive frequency sampling stops being optional.

**D8 — No dependency, and no eigensolver.** Unchanged since L8a, declined by L7b-b, L8a, L9b and L9c.
Nothing in ports or de-embedding needs one.

---

## 2. What already exists, and what genuinely does not

**Exists and is load-bearing — read it before writing anything:**

- `PlanarFill.FillMultiLevel`, `PlanarKernelSet`, `PlanarLevels` — the matrix, the per-pairing kernel
  set with its fit counter, and the midpoint rule with its refusal.
- `PlanarProblem` with `MediumStack`, per-level `ZM`, `ViaList` and `CanSolve`'s three earned
  refusals; `SurfaceMesher` emitting vertical bases with R-via-5's ordering; `PlanarMeshReport.ViaUnknownCount`.
- `PlanarPort`, `PlanarExcitation`, `PlanarSolve`, `PlanarDeembed`, `PlanarCalibration` — **all of
  L8d, unchanged**, and all of it written over a one-level mesh.
- `EmKernelRegistry`, `EmCapabilities`, the `"planar"` diagnostics group — all of L8e, unchanged.
- `PlanarExtractor`, `EmSetupModel`, `EmSetupDocument` in `src/Ui/Layout/Em/`.

**Does not exist:**

- **Any excitation, solve or s-parameter for a multi-level mesh.** `PlanarSystem.Build` takes two
  `PlanarKernelTerms`; `PlanarSolveContext.SolveAt` takes a `PlanarKernelPair`. Neither knows about
  `PlanarKernelSet`.
- **Any port that has been given a non-zero `LayerIndex`.**
- **Any multi-level calibration or de-embedding.**
- **Any extractor that produces more than one level, a `LayerStack`, or a via.**
- **Any consumer of `EmCapabilities.LayeredWithVias`.**

---

## 3. The formulation, stated as requirements

**R-mlp-1 — The one-level path stays bit-identical.** Every number in L8d's and L8e's tables must be
unchanged, and L9c's own `M5_1` (the multi-level fill vs L8c's, 6.8e-7) must not move. If you refactor
`PlanarSolveContext` or `PlanarSystem` to take a set instead of a pair — and they are going to want it
— reconstruct the pre-change s-parameters and compare at **full precision**, the way L9b pinned twelve
dumped fit configurations and L9c pinned 600 `Voltage` values. The Tier oracles carry tolerances and
structurally cannot catch a one-ulp move.

**R-mlp-2 — Reciprocity and passivity are GATES; losslessness is NOT.** L8d's R-prt-10/11/12,
unchanged and for the same reason: a two-level open structure with vias radiates more, not less.

**R-mlp-3 — Every refusal names the specific feature and where the capability arrives, and every
refusal is EARNED.** The test that measures a refused case must assert the answer out there is
**actually bad**, so a mis-scoped refusal fails loudly instead of standing. L9b added that assertion
and it immediately caught a wrong refusal; L9c's own three problem-type refusals each accept their
legitimate neighbour in the same test.

**R-mlp-4 — The accuracy claim is a MEASURED range, per structure class.** Two measures as always: the
scaled error and the strict relative one. And **say explicitly which of them the `G_A^zz` ρ/λ ≤ 0.1
limit binds**, because it is the only place a two-level answer can be worse than a one-level one for a
reason that is not radiation.

**R-mlp-5 — Determinism, bit for bit.** R-msh-2's cell order, R-via-5's basis order and R-fil-11's
single-write parallel fill are all contracts already. A port map and a de-embedding index into them;
neither may introduce a dictionary iteration or a floating-point tie.

**R-mlp-6 — Nothing is cached across frequencies** that is not already provably frequency-independent.
`PlanarKernelSet.FitCount` and L8c's `CoreFillCount` are the two counters; a new cache must join one of
them or neither.

---

## 4. The oracle ladder

**Tier 0 — structural, free.** `Z` symmetric on a multi-level mesh (already gated); `Y = BᵀZ⁻¹B`
symmetric; the port incidence matrix's rows summing to zero; every refusal.

**Tier 1 — THE ONE-LEVEL REDUCTION, and it is the strongest single check in the slice.** A two-level
`PlanarProblem` whose second level is EMPTY must reproduce the one-level answer exactly — same N, same
`Z`, same S. Then a two-level problem whose two levels are at the SAME z with no via is a degenerate
case that must be refused rather than answered. Do these before anything empirical.

**Tier 2 — the εᵣ = 1 reduction, one level up.** With no dielectric anywhere, a via over a ground
plane is a wire whose inductance has a closed form to within a stated approximation. That is a real
external-data-free check on the ẑẑ block reaching a terminal quantity, and it is the analogue of
L8c's "against the εᵣ = 1 reduction, where the kernel is exact and only the quadrature can be wrong".

**Tier 3 — de-embedding on a uniform SINGLE-LEVEL line, which is L8d's Tier 3 re-run.** The
de-embedded S must reproduce L8d's numbers on the same geometry through the new code path. **This is
what catches a port-indexing error**, because a mis-indexed port on a one-level mesh is still a wrong
answer.

**Tier 4 — the via itself: a through-via between two lines, against its own convergence.** No external
data. Refine the mesh and the quadrature separately, exactly as L8c's Tier 6 separates them, and
report both sequences.

**Tier 5 — the cost (D7).** THE REPORTED MEASUREMENT alongside R-mlp-4's accuracy.

**A warning that has now cost this area six milestones: check the oracle before concluding the method
is wrong.** L8a records two occasions, L7b-b a third, L8e's phase gate a fourth, L9a a fifth, and L9c a
sixth — where an "obvious" structural argument (the branch point) turned out not to be the binding
constraint, and where asserting the ABSENCE of a theorem was wrong. When a rung disagrees, **the first
hypothesis is the rung.**

---

## 5. What must NOT be built here

- **Adaptive frequency sampling, ACA/MLFMM, N-budget enforcement, the refusal audit, L9's phase
  gate** — **L9e**. Report the N and the cost; do not act on them.
- **A gate on published multilayer reference data.** §11's L9 gate sentence is still unresolved and is
  still not this slice's to settle. L9a proposed a replacement and the owner has not ruled.
- **Any widening of `Dcim.ValidatedRhoOverLambda`, `ValidatedRhoOverLambdaLayered`,
  `ValidatedRhoOverLambdaAtHeights`, `Dcim.CanFit`, or any existing refusal string** on the grounds
  that a hero geometry is inconvenient. New cases get new, separately measured refusals.
- **A depth search that is not Prony-on-two-fixed-paths**, i.e. GPOF with an SVD truncation. L9c
  measured what it would buy and D8 declines the eigensolver it needs. That is an owner decision.
- **A z-integral along a via.** L9c's midpoint rule is refused by name above kℓ = 0.05; leave it that
  way.
- **Co-simulation ports, finite ground pours, a conformal or diagonal boundary cell, a losslessness
  check, a new starter technology.** None of these are L9's.

---

## 6. Milestones, each with its own gate

| | content | gate |
|---|---|---|
| **M1** | `PlanarSystem`/`PlanarSolveContext` take a `PlanarKernelSet`; the one-level path bit-identical (D1, R-mlp-1) | **Tier 1** and the full-precision reconstruction |
| **M2** | Ports on a level, the ambiguity refusal, the via-port decision (D1, D2) | **Tier 0** and **Tier 3** |
| **M3** | Multi-level de-embedding against single-level standards (D3) | **Tier 3**, with L8d's drift numbers quoted beside the new ones |
| **M4** | The Ui-side extractor: N levels, a `LayerStack`, vias, and the narrowed ungrounded refusal (D5) | `Ui.Tests`, and the ungrounded narrowing measured rather than asserted |
| **M5** | The registry, `EmCapabilities.LayeredWithVias`, the current-density map (D4, D6), the cost (D7) | **Tier 4**, **Tier 5** |

**M1 is the one with a wrong obvious answer** — that `PlanarKernelPair` can simply be widened in
place. It is L8d's cache and its "fit once per frequency, share across the DUT and every standard"
decision is load-bearing; widening it carelessly turns 9 fits per frequency into 9 per mesh.
**M4 is the one with no precedent to copy** and the one most likely to consume the slice. **If M1–M3
consume it, stop and report** — that is the natural fault line, the milestone order is unchanged
either way, and whether L9 becomes six slices is the owner's call and not this brief's.

---

## 7. File map (indicative)

```
src/Engine/Mom/
  PlanarSystem.cs        + assembly from a PlanarKernelSet (M1)
  PlanarSolve.cs         + PlanarSolveContext/PlanarKernelPair generalised; the CACHE decision (M1)
  PlanarPort.cs          + the level, the ambiguity refusal, the via-port decision (M2)
  PlanarCalibration.cs   + D3's single-level-standard rule, as a refusal
  PlanarDeembed.cs         probably unchanged — check before assuming (M3)
  PlanarCurrentDensity.cs+ the vertical map (D4)
  EmKernelRegistry.cs    + EmCapabilities.LayeredWithVias, finally read (M5)

src/Ui/Layout/Em/
  PlanarExtractor.cs     + N levels, the LayerStack, vias; the ungrounded refusal NARROWED (M4)
  EmSetupModel.cs        + which levels are in the analysis
  EmSetupEditorViewModel.cs / EmSetupEditorView.axaml  + the UI for it

tests/Engine.Tests/Mom/
  MultiLevelPortTests.cs  + Tiers 0-5
  PlanarDeembedTests.cs     L8d's ladder — EXTEND, loosen nothing
  ViaBasisTests.cs          L9c's ladder — EXTEND, loosen nothing
tests/Ui.Tests/Em/
  PlanarExtractorTests.cs   the extractor half
```

Nothing outside `src/Engine/Mom/`, `src/Ui/Layout/Em/` and their tests should change. If something
does, that is a finding worth reporting, not a step to take quietly.

---

## 8. Five things to report back on, whatever else happens

1. **What actually broke when L8d's port resolver met a two-level mesh** — D1 says the burden of proof
   is on "it needs rewriting", and the honest answer might be "one index". Say which.

2. **What a via PORT is**, and if the answer is "refused", the measurement or the argument that earns
   the refusal. This is the one design decision in the slice with no precedent in L8d.

3. **Whether de-embedding a two-level DUT against single-level standards is legitimate**, measured the
   way L8d measured its own: the de-embedded S of a uniform section at and away from the calibration
   lengths, against L8d's 3.9e-4 at 2 GHz and 6.0e-3 at 10 GHz. **A two-level structure radiates
   more**, and L8d's finding was that radiation is what limits this.

4. **Where the `G_A^zz` ρ/λ ≤ 0.1 limit binds a real answer**, and what the refusal says. If it turns
   out not to bind — because via-coupled entries between distant cells are negligible in the assembled
   `Y` — that is a better answer and it needs the measurement, not the argument.

5. **The cost of a de-embedded two-level point and a 101-point sweep**, against L8d's measured 7.66 s
   and ~780 s at N = 552. If it lands in hours, that is the finding, and it is what decides whether
   L9e's adaptive frequency sampling and its N-budget enforcement are optional.
