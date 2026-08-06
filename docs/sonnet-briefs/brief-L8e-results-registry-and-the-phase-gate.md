# Sonnet Brief — Phase L8e: results, the kernel registry, the narrowed refusals, and the phase gate

**Design:** `docs/design/layout-view.md` **§10.8 (results and EM/circuit co-simulation — R17a and the
staleness paragraph, which is the one failure mode that design introduces)**, §10.3.4 (the kernel
interface, and the "there is deliberately no kernel registry *yet*" paragraph — this is the moment it
named), §10.6 (the Port tool), §10.5 (the current-density heat map as a system layer), §10.10 (R18's
30-second acceptance test, **which the design note says gates the MoM phase**), §11's phase table row
**L8**, whose gate is three sentences and which this slice has to satisfy.

**Read, in this order, before planning anything:** `src/Engine/Mom/CLAUDE.md` §L8a → §L8b → §L8c →
§L8d, then `src/Ui/Layout/Em/CLAUDE.md` end to end. The engine is finished; **this slice adds no
physics at all** — the same rule R-em-21 already puts on the Ui half, now applying to the whole
slice. If you find yourself computing a capacitance, a γ or an s-parameter in this phase, stop: it
exists already and you are re-implementing it.

**Fifth and last of L8's five slices.** L8a built the Green's function, L8b the mesher and the
overlay, L8c the fill, L8d the ports and the calibration. What is left is everything that makes those
reachable by a user, plus the gate that says the phase is done.

---

## Gate command — and THIS is the slice where the full solution runs

**Every L8 slice so far has deliberately run only `tests/Engine.Tests`, `tests/Ui.Tests` and
`tests/Firewall.Tests`. That ends here.** L8a's own note says the full-solution run is *"required
once, at the end of L8, as part of L8e's gate"*, and the reason is exactly this slice: **L8e is where
the interface actually changes.** A registry, a widened `EmCapabilities`, a new `.cem` field and a new
DataSet group all reach outside `src/Engine/Mom/` and `src/Ui/Layout/Em/`, so this is the first slice
since L8a whose blast radius genuinely covers the repository.

So: **plain `dotnet test` at the repo root**, plus the per-project runs while iterating.

**`Hero1BTests` will be marginal and you must NOT widen its budget.** It gates on a 10 s
import-plus-solve wall clock. L8a measured it under full-solution load **with L8's tests excluded** at
4.2 / 9.6 / 8.6 s, and it failed once in that condition — i.e. **it is marginal on this machine
independently of this phase.** If it fails, say so with the measured spread and move on; changing its
budget to make an unrelated phase green is the one thing that would make that measurement useless.

**Tagging — and this slice has a COST PROBLEM that has to be solved on paper before any of it is
written.** L8d added 34 routine tests (Engine.Tests went 24 s → 40 s) and 10 `Category=Benchmark`.

**A de-embedded frequency point costs 7.66 s on §10.7's hero (N = 552), plus ~10 s of cores per
mesh.** L8d's own stub sweep — 14 points, one starter, shipping mesh — took **2 m 23 s**. Scale that
to the phase gate written naively:

| gate | points | starters | variants | ≈ |
|---|---|---|---|---|
| stub resonance | 14 | 2 | 1 | ~5 min |
| bend, mitred AND unmitred | 10 | 2 | 2 | ~5–6 min |
| A vs B on a uniform line | 5 | 2 | 1 | ~1.5 min |

**~12 minutes, which is not a routine gate and is barely a tolerable opt-in one.** Two levers, and the
difference between them matters:

- **Cut the frequency counts to what the CLAIMS need.** Locating a notch is a coarse scan plus one
  refine — 6 points, not 14. The bend's sanity checks are 3 points. A-vs-B is 2. That is ~3× on its
  own and costs nothing, because none of the three statements is about a dense sweep.
- **Calibration sharing is the big lever (the standards are 78% of a point) and it is only PARTLY
  SAFE.** Within one structure across frequency, `PlanarPortCalibrator` already does it and the
  measured cost above includes that. **Across structures it is the trap L8d's D4 exists for**: L8b
  derives grid spacing from the whole problem's narrowness per axis, so a stub and a bend do not get
  the same port cells even at the same feed width, and reusing one calibration across them moved a
  supposedly invariant answer by **1.8e-1** during L8d. **Do not share a calibration between two
  different DUT geometries without asserting `PlanarPortCalibrator.SameCrossSection` first**, and if
  it returns false, that is the answer rather than an obstacle.

**Therefore, and this supersedes any reading of R-res-11 that would put the whole gate in the routine
tier:** the routine tier keeps **one representative phase-gate case per starter** — the A-vs-B uniform
line at two frequencies, which is the cheapest of the three and the one that exercises the whole
product path — and **the stub and the bend are `Category=Benchmark`**, opt-in via
`--settings circuitrf.benchmark.runsettings`. Budget: **the routine repo-root run stays under ~90 s**;
the opt-in tier for this slice should come in under **~8 min**, and if it does not, say by how much.
A gate nobody runs is not a gate — but neither is one that makes `dotnet test` unusable, and the
honest split is to keep the cheapest true case routine and say plainly in the report what was moved
out and what it costs to run.

---

## 0. Read this before planning anything

**There are five separable pieces and only two of them have a wrong obvious answer.**

1. **Results plumbing** — `DataSet`, the `.snp`, back-annotation, staleness. Almost all of it exists
   and works for kernel A; the job is to feed it from kernel B without inventing anything (D4).
2. **Ports on the layout** — where a `PlanarPort` comes from when the user is holding a mouse. **The
   obvious answer (a new shape type) is wrong and the code already says why** (D3).
3. **The kernel registry** — §10.3.4 has been waiting for this moment since L6. **The obvious answer
   (widen `IEmKernel` so both kernels implement it) is wrong, and L8b's D1 already forbade it in
   advance** (D1). Read that before designing anything.
4. **The current-density heat map** — L8b left exactly one provision for it and nothing else (D5).
5. **The refusal audit** — every message in the repository that says *"arrives with the full-wave
   kernel at L8"* is now either TRUE, or MISLEADING, or FLATLY WRONG, and each one has to be
   classified by hand (D6). This is the least glamorous item and the one most likely to be skipped.

**The second thing to internalise: L8d handed over three findings that must NOT be "fixed" here.**

- **The de-embedded answer is limited by radiation, not by the calibration** — a matched section
  de-embeds to |S₁₁| = 3.9e-4 at 2 GHz and 6.0e-3 at 10 GHz on 1.6 mm FR-4, scaling as f², and a
  longer feed does not help. **Do not write a gate tighter than that and then tune something until it
  passes.** Any gate on a kernel-B s-parameter has to sit above this floor, and the number is
  frequency-dependent, so a single tolerance across a band is the wrong shape.
- **Losslessness does not hold and there must be no losslessness test.** L8a wrote the warning, L8d
  honoured it, and the temptation arrives here — this is the slice where a user-facing "is my answer
  sane?" check would naturally be written. Reciprocity and passivity carry over; losslessness does
  not, because radiating is one of the two things kernel B exists to model.
- **`Z_c` rises with frequency because C is held quasi-static.** That is the γ-and-C route's honest
  cost (0.4% / 2.3% / 6.3% at 1 / 5 / 20 GHz against kernel A's static value), it is on the record,
  and it must be surfaced to the user as a note rather than silently published as if it were
  dispersive.

**The third thing: the phase gate is the deliverable.** §11's L8 row is three sentences —
*"A quarter-wave open stub resonates at the right frequency; a bend's s-parameters are physically
sane; A and B agree on a uniform line."* Two of the three are already measured (L8d: the stub notches
at 5.700 GHz against 5.476 GHz predicted with a 0.4 h open-end extension; B reproduces A's ε_eff to
0.01% at 1 GHz). **The bend is the one nobody has run**, and it is the one the whole mesher argument
about staircasing was about. Write it early, not last.

---

## 1. Decisions taken

**D1. THE REGISTRY IS KEYED ON THE ANALYSIS KIND, AND IT UNIFIES THE OUTPUT CONTRACT, NOT THE INPUT
TYPE.** §10.3.4's signature has already been corrected once — R-mom-1 replaced
`Solve(LayoutFragment, Stackup, Port[], …)` with `Solve(EmProblem, …)` because the original was not
simultaneously satisfiable with the UI firewall. **This is the second correction and it is recorded
the same way.**

The obvious design is one interface both kernels implement. It cannot be built, because **L8b's D1
already decided that `PlanarProblem` is a SIBLING of `EmProblem`** — *"no shared base, no interface
implemented by both: two things that are genuinely different, described by two types, is the cheapest
arrangement to be correct in"* — and nothing since has weakened that. Forcing a common input type now
would either resurrect the base class L8b rejected or push a nullable-fields union through every
call site.

So:

- **`IEmKernel` is left exactly as it is** and stays kernel A's. Kernel B gets its own entry point
  with its own honest signature (`PlanarProblem`, `PlanarMeshSettings`, ports, frequencies).
- **The registry's contract is what comes OUT**: a `DataSet`, a note list, and an `EmSuitability`.
  That is the only thing the two kernels genuinely share, and it is enough for every caller.
- **`EmCapabilities` finally earns its `Planar` flag** — it has existed since L6 and nothing has read
  it. The registry is what reads it.

**D2. AUTO-SELECTION EXISTS, IS CONSERVATIVE, AND ALWAYS SAYS WHICH KERNEL AND WHY.** L8b's D7 said
*"choosing the kernel automatically from the geometry is a registry decision and it arrives with the
registry, in L8e"*. It arrives, with this rule:

```
Auto → kernel A  if CrossSectionExtractor ACCEPTS   (validated to ≤1.3% on ε_eff, and ~1000× cheaper)
     → kernel B  if it refuses and PlanarExtractor accepts
     → refuse, quoting BOTH refusals, if neither
```

**Explicit stays explicit.** A `.cem` that names `CrossSection` or `Planar` is honoured even when the
other would work — auto never overrides a user's choice, in either direction. And the selection is
reported in the notes with the reason, in R-msh-8a's own shape: *name the thing, name the
alternative*. A user who gets the slow kernel must be able to see in one line why.

**D3. PORTS COME FROM THE LAYOUT'S OWN `IsPort` LABELS. There is no new shape type, and the code
already says why.** `LabelShape.IsPort` exists (`src/Ui/Layout/LayoutModel.cs`), persists, survives
copy/paste and flatten, is excluded from boolean operations, and the Label tool sets it to `false`
with the comment *"port placement belongs with the EM work, not here"*. `LayoutModel.cs` also carries
a paragraph explaining why a port is a `LabelShape` with a flag rather than its own type. **That
provision is what this slice spends.**

- **The Port tool sets the flag and nothing else** — §10.6's *"click an edge, get P1"*. Auto-number
  from the existing labels; the reference impedance lives in the `.cem` (`EmSetup.PortZ0s` is already
  per-port and already additive, R-cpl-6), never on the shape.
- **The SIDE is inferred from geometry and the inference is REPORTED.** A `PlanarPort` needs to know
  which end of the conductor it is; a label carries a position. Infer it from the nearest conductor
  boundary, **surface the inference in the notes**, and **refuse by name** when it is ambiguous —
  never pick one silently. L8d's `PlanarPorts.TryResolve` already refuses a port that misses the
  metal, with wording to match.
- **Nothing about the reference PLANE is user-positionable**, because L8d's D2 fixed it one cell in
  from the drawn metal end and offering an offset would offer a way to get a different answer for the
  same structure. §10.6's *"show the de-embedding reference plane in the layout"* is therefore a
  drawing job over a location the engine already reports (`PlanarPortResolution.ReferencePlaneM`).

**D4. NO NEW RESULT TYPE — for the fifth phase running.** A planar run produces the same `DataSet`
shape kernel A's does: an `S` cube plus per-port `Z0`, through `DataSetBuilder.FromSnp`, written by
the existing `EmRunService` path to the existing predictable `.snp` location. What is *added* is one
group of diagnostics — γ, Z_c, ε_eff per port, and the de-embedding residuals L8d already computes —
under its own name so that nothing collides with the `"tline"` group's meaning. **The eight `tline`
scalars are what make a wrong kernel-A answer diagnosable (R-em-18's own words); the planar group
plays the same role and must not be filtered out on the way to Data Display either.**

**D5. THE HEAT MAP IS A PER-CELL SCALAR, ONE EXCITATION AT A TIME, WITH ITS SCALE SHOWN.** L8b left
exactly one provision — *"one per-cell scalar on `LayoutRenderer.DrawPlanarMeshOverlay`"* — and it is
still wired to nothing. L8d returns per-BASIS currents (`PlanarPortSolution.Currents`), so the
reduction to a per-cell |J| is this slice's, and it belongs in the **engine** rather than the
renderer: sum each cell's covering rooftops into an (Jx, Jy) at the cell centre, take the magnitude.

- **One port driven at a time**, selectable, defaulting to port 1. A map that superposes all ports is
  a map of nothing.
- **One frequency**, selectable. A heat map over a sweep is a different feature.
- **The colour scale is SHOWN with its units and its normalisation**, not implied. An unlabelled
  rainbow over an unstated normalisation is decoration; §10.5 asks for a diagnostic.
- **R-em-17's staleness contract applies unchanged**, and it matters more here than for the mesh
  overlay: a current map drawn over edited artwork looks like it still matches.

**D6. THE REFUSAL AUDIT IS A DELIVERABLE, NOT A TIDY-UP.** Every message that says *"arrives with the
full-wave kernel at L8"* is now in one of three states, and each has to be classified individually:

- **TRUE** — the capability now exists. Update the message to say so, or delete the refusal.
- **MISLEADING** — L8 exists but only for ONE grounded slab with ONE conductor layer, so the feature
  still is not supported. **Re-point at L9 by name.** `QuasiStaticKernel` has at least two of these
  (the multiple-dielectric-boundary refusal and the iterative-solver one).
- **WRONG** — the message describes a capability boundary that has moved. Fix it.

R-mom-17 is the standard every replacement is held to: *name the specific feature and where the
capability arrives instead*. **A test per message**, because a refusal nobody asserts is a string
that drifts.

**D7. THE PHASE GATE IS MEASURED ON THE SHIPPING MESH AND ON BOTH STARTERS.** No coarse fixtures for
the gate itself — L8d's coarse fixtures exist because the *algebra* is exact on them, and none of the
three phase-gate statements is about algebra.

**D8. R18's 30-SECOND ACCEPTANCE TEST IS RE-RUN AND RE-COSTED WITH KERNEL B SELECTED**, because
§10.10 says in as many words that it *gates the MoM phase*. The L6/L7 EM-UI brief's D5 removed the
5-second "Ports" row from the budget table on the grounds that kernel A needs no port placement —
**kernel B does**, so that row comes back, and whether the budget still holds is a MEASUREMENT. Note
that D3 makes it cheaper than it looks: schematic-to-layout already stamps labels, so a user who
arrived via §9 may have ports already.

**D9. THE PROVENANCE STAMP MUST COVER THE PLANAR PROBLEM, OR STALENESS DETECTION SILENTLY STOPS
WORKING.** §10.8 calls the stale-`.snp`-beside-an-edited-layout case *"the one failure mode this
design introduces"*, and `EmSnpProvenance` currently hashes an `EmProblem`, an `EmMeshSettings` and
kernel A's ports. A planar run through the same path with no planar hashes would write a stamp that
cannot go stale — **worse than no stamp**, because the warning would stay silent. Extend it, and test
that editing the layout, the mesh settings and a port each independently trip it.

**D10. NOTHING FROM L9 CREEPS IN.** One conductor layer, one grounded slab, no vias, no z-directed
current, no N-dielectric stack, no ACA/MLFMM, no adaptive frequency sampling. If the phase gate is
uncomfortable without one of them, that is a finding for L9's brief, not a licence.

---

## 2. What already exists, and what genuinely does not

**Exists and is reused unchanged — none of this should be re-implemented:**

- **The whole engine.** `PlanarSolve.Run(mesh, ports, slab, freqs, settings)` returns de-embedded,
  renormalised S per frequency plus γ/Z_c/diagnostics per port and a note list. `PlanarPorts.Resolve`
  turns a location + side into a port row, with its refusals already worded. `PlanarPortCalibrator`
  is a first-class object that can be built once and reused across DUTs sharing a feed cross-section.
- **`PlanarExtractor`** — real layout geometry → `PlanarProblem`, with its refusals pointing at L9 and
  `AnalyticAlternativeFor` naming the closed-form model where one exists (R-msh-8a).
- **`SurfaceMesher`** and `PlanarMeshReport` — N before solving, R17's verdict, the notes.
- **`EmRunService`** — the five-step Simulate path, `RunResultsWriter.WriteRun`,
  `RefreshOpenDataDisplaysAsync`, `AutoOpenOrCreateDataDisplayAsync`, and `ResolveSnpPath`'s
  predictable naming. **The `.snp` path convention is already right; do not mint a new one.**
- **`EmBackAnnotation`** — places-or-updates an ordinary `SnP` component with R-cpl-12's two-step key,
  which L7b-b already proved survives a changing port count. It needs no change for kernel B either;
  prove that rather than assuming it, as Tier G5 did.
- **`EmSnpProvenance`** — the stamp, the reader, and `DescribeStaleness`.
- **`LayoutRenderer.PlanarMesh.cs`** — the plan-view overlay, in world coordinates, with the per-cell
  scalar provision D5 spends.
- **`EmSetup.AnalysisKind`** (`CrossSection` | `Planar`) and `EmSetup.PlanarMesh`, both already
  omitted from the file at their defaults so old `.cem`s round-trip byte-identically.
- **`EmSetup.PortZ0s`** — per-port, complex, additive, already tested through the whole path.
- **`EmCapabilities.Planar`** — declared at L6, read by nothing. D1 is what reads it.

**Does NOT exist:**

- **Any registry, any kernel selection, any `EmCapabilities` consumer.** §10.3.4's own paragraph
  explains that this was deliberate: *"a registry earns its place when kernel W or B exists; adding
  it now is speculative plumbing with no second implementation to constrain it."* B exists now.
- **Any way to get a `PlanarPort` out of a layout.** The engine takes a location and a side; nothing
  produces either from a `.clay`.
- **Any Port tool.** `LabelShape.IsPort` is never set to true anywhere in the codebase.
- **Any per-cell current, any heat map, any colour scale.** The provision is a parameter nobody
  passes.
- **Any planar path through `EmRunService`.** It calls `QuasiStaticKernel` directly.
- **Any planar contribution to the provenance stamp.**
- **Any bend s-parameter, from either kernel.** The phase gate's middle sentence has never been run.

---

## 3. Requirements

**R-res-1. The registry is the ONLY place a kernel is chosen**, and the choice plus its reason appear
in the notes on every run. No caller constructs a kernel directly once the registry exists.

**R-res-2. `IEmKernel` does not grow a planar overload, and `PlanarProblem` gains no base type.**
D1/L8b-D1. If the two paths need to share something, they share the *result*, not the argument.

**R-res-3. Auto-selection never overrides an explicit `.cem` choice**, and never silently picks the
slower kernel when the cheaper one would have been valid. Both directions are tested.

**R-res-4. A port is a `LabelShape` with `IsPort`; no new shape type, no change to `.clay`'s schema
beyond what already round-trips.** An existing `.clay` must load unchanged, and a `.clay` with port
labels must round-trip byte-identically.

**R-res-5. Port side inference is reported, and an ambiguous port is refused by name.** Never guessed.
The refusal names the port and what to do about it (R-mom-17's shape).

**R-res-6. No new result type.** `DataSet` + `S` + per-port `Z0`, plus one diagnostics group. The
`.snp` goes to the path `EmRunService` already computes.

**R-res-7. The per-cell current density is computed in the ENGINE and is a documented reduction**, not
a renderer-side approximation. Its definition is stated once, in code, next to the basis it reduces.

**R-res-8. The heat map's scale is shown with units and normalisation**, and the map carries R-em-17's
staleness invalidation.

**R-res-9. The provenance stamp covers geometry, planar mesh settings AND ports for a planar run**,
and each independently trips `DescribeStaleness`. D9.

**R-res-10. Every "arrives at L8" refusal is classified and updated, with a test per message.** D6.

**R-res-11. THE PHASE GATE, all three sentences, on both starter technologies, on the shipping mesh.**
See §4 for the content and the gate-command section for the tiering: **the A-vs-B uniform line stays
routine, the stub and the bend are `Category=Benchmark`**, and the frequency counts are chosen from
what each claim needs rather than from habit. The measured 7.66 s per de-embedded point is what makes
that a design constraint rather than a preference.

**R-res-12. No gate is written tighter than L8d's measured radiative floor**, and any tolerance on a
kernel-B s-parameter carries a comment saying which measured number set it. A gate that passes today
because a tolerance was chosen to fit is not a gate.

**R-res-13. R18's acceptance test is re-run with kernel B and its click/keystroke budget re-measured**,
with the Ports row restored. Report the number whether or not it fits. D8.

**R-res-14. The full-solution `dotnet test` is run and its result reported**, including
`Hero1BTests`' timing under load — **reported, not fixed**. This is the gate command's whole point.

**R-res-15. Nothing here adds a physics computation.** R-em-21, extended to the slice. Every number
displayed came from `src/Engine/Mom/`.

---

## 4. The phase gate, and the tiers under it

**§11's L8 row is the gate.** Each sentence gets a test, and each is measured rather than asserted.

**Gate 1 — a quarter-wave open stub resonates at the right frequency.** L8d already measured this on
FR-4 (notch at 5.700 GHz; λ_g/4 alone predicts 6.000, with a 0.4 h open-end extension 5.476 — the
measurement sits between, nearer the corrected one). **What this slice adds is the second starter and
the run through the PRODUCT path** — extractor → registry → kernel → `DataSet` → `.snp` — rather than
through a hand-built mesh. The open-end extension must stay named in the prediction; a bare quarter
wavelength is not the reference and would invite tuning toward a formula that was never the truth.

**Gate 2 — a bend's s-parameters are physically sane. THIS IS THE ONE NOBODY HAS RUN, and "sane" has
to be given a meaning before it is measured.** Proposed, all of them falsifiable:

- reciprocal and passive (both carry over; losslessness does not — §0);
- |S₁₁| small at low frequency and rising with it, because a bend is a shunt capacitance to first
  order;
- **the MITRED and UNMITRED bends differ, and in the right direction** — the mitre exists to reduce
  the discontinuity, so its |S₁₁| must be *lower*. L8b measured that the staircased mitre survives
  meshing (2.8% cut-area error at the auto cell size, N 550 against the unmitred 586) and R-pc-18
  records that the two are distinct discontinuities. **This is the electrical half of that claim and
  it has never been checked.** If they come out identical, the mesh is not resolving the mitre and
  that is a finding about L8b, not a tolerance to widen;
- the equivalent shunt capacitance extracted from S₁₁ is within a stated band of a published
  microstrip-bend estimate — **and if no estimate whose inputs can be verified is obtainable, say so
  and substitute a physical limiting case**, exactly as Tier C3 did at L7b rather than building a
  gate on numbers of unknown provenance.

**Gate 3 — A and B agree on a uniform line.** L8d measured ε_eff to −0.01% at 1 GHz and Z_c to +0.40%.
**What this slice adds is that both come out of the two kernels through the SAME product path**, from
one layout, selected by the registry — which is a different claim from "the two engines agree" and is
the one a user experiences. Include the divergence above ~5 GHz **as a reported result**, not as an
error: it is microstrip dispersion and it is one of the two things kernel B exists to compute.

**Under the gate, in the usual ladder, each passing before the next is written:**

| Tier | What |
|---|---|
| 0 | the registry: selection, both explicit directions, the refusal when neither kernel fits, the reason string |
| 1 | ports from labels: numbering, side inference, the ambiguous refusal, a `.clay` round-trip with ports |
| 2 | the planar run through `EmRunService`: `DataSet` shape, `.snp` at the predictable path, back-annotation placing/updating one `SnP` |
| 3 | staleness: geometry, mesh settings and each port independently trip the stamp |
| 4 | the heat map reduction: total current across a cut equals the port current; a uniform line's map is uniform in the middle and peaks at the edges (the 1/√d edge current the edge mesh exists for) |
| 5 | the refusal audit, one test per message |
| 6 | **the phase gate**, all three sentences, both starters |
| 7 | R18's acceptance budget, re-measured |
| 8 | the full-solution run |

**Tier 4's second half is worth stating because it is the only physics check the heat map admits and
it is free:** a uniform line's current density must be flat along the line and peak at the two edges.
That is the 1/√d edge singularity R-msh-5's whole edge-mesh argument is about, and it is the first
time anything has *looked* at it. If the map is flat across the width, the edge mesh is not doing
what L8b measured it doing.

---

## 5. What must NOT be built here

- **Anything from L9**: N dielectrics, buried or multi-level metal, vias, z-directed current,
  adaptive frequency sampling, ACA/MLFMM. D10.
- **A surface-impedance / conductor-loss term for kernel B.** L8d named it and measured its size
  (6.5 / 3.0 / 2.1% of the conducted loss at 2 / 10 / 20 GHz); it is scheduled by whoever wants it,
  not smuggled in here.
- **A true edge port (a half-rooftop at the boundary).** L8d recorded that it would remove the
  low-frequency conditioning floor and that it needs the basis L8c's D2 forbids. A finding, not an
  edit.
- **Any change to `SurfaceMesher`, `PlanarMesh`, the cell/basis ordering, the fill's quadrature, or
  L8d's calibration settings.** All were measured. If the phase gate wants a different mesh, that is
  a finding to report.
- **A losslessness check anywhere**, user-facing or otherwise. §0.
- **A second `.snp` naming convention, a second renormalisation, a second T-matrix cascade, a second
  DataSet assembly.** R-mom-14's rule, now applying to four different things.
- **A heat map over a frequency sweep, or over superposed excitations.** D5.
- **A new dependency of any kind.**

---

## 6. Milestones, each with its own gate

| | Content | Gate |
|---|---|---|
| **M1** | The registry, `EmCapabilities` consumption, auto-selection, the refusal audit. | **Tier 0 and 5 green.** |
| **M2** | Ports from `IsPort` labels + the Port tool; the planar path through `EmRunService`; `.snp`, back-annotation, staleness. | **Tier 1, 2 and 3 green.** |
| **M3** | The per-cell current reduction and the heat map. | **Tier 4 green**, including the edge-current check. |
| **M4** | **The phase gate**, R18's re-measured budget, and the full-solution run. | **Tier 6, 7 and 8 green**, with all three phase-gate sentences reported as numbers. |

Stop and report at any gate that does not go green rather than proceeding with a tolerance loosened to
make it pass. **In particular: if the mitred and unmitred bends come out electrically identical, that
is a mesh finding and it goes in the report** — L8b's own staircasing measurement is what it would be
contradicting, and quietly widening a tolerance would bury the most interesting result in the slice.

---

## 7. File map (indicative)

```
src/Engine/Mom/
  EmKernelRegistry.cs      selection, capabilities, the unified OUTPUT contract          (new)
  PlanarKernel.cs          kernel B's own entry point: PlanarProblem → DataSet           (new)
  PlanarCurrentDensity.cs  the per-cell reduction of L8d's basis currents (R-res-7)      (new)
  IEmKernel.cs             UNCHANGED — D1                                              (no edit)

src/Ui/Layout/Em/
  EmRunService.cs          + the planar branch, behind the registry                     (edit)
  EmPortExtraction.cs      IsPort labels → PlanarPort, side inference, the refusals      (new)
  EmSnpProvenance.cs       + the planar hashes (R-res-9)                                 (edit)
  EmSetupEditorViewModel.cs  + kernel selection surfacing, the heat-map controls         (edit)

src/Ui/
  Layout/LayoutEditorViewModel.*   the Port tool: sets IsPort, auto-numbers              (edit)
  Renderers/LayoutRenderer.PlanarMesh.cs  the per-cell scalar the provision was for      (edit)

tests/
  Engine.Tests/Mom/EmKernelRegistryTests.cs        Tier 0
  Engine.Tests/Mom/PlanarCurrentDensityTests.cs    Tier 4
  Ui.Tests/Em/EmPortExtractionTests.cs             Tier 1
  Ui.Tests/Em/PlanarRunTests.cs                    Tiers 2, 3
  Ui.Tests/Em/EmRefusalWordingTests.cs             Tier 5
  Engine.Tests/Mom/L8PhaseGateTests.cs             Tier 6   ← the deliverable
  Ui.Tests/Em/EmAcceptanceBudgetTests.cs           Tier 7
```

---

## 8. Four things to report back on, whatever else happens

1. **THE PHASE GATE, all three sentences, as numbers, on both starters.** The stub's resonance against
   its open-end-corrected prediction; the bend's s-parameters with "sane" spelled out and **the mitred
   vs unmitred comparison**, which is the electrical half of L8b's staircasing measurement and has
   never been made; and A-vs-B on a uniform line through the product path. **If any of the three does
   not pass, that is the report — L8 is not done, and saying so is more useful than a green tick.**
2. **Whether §10.10's 30-second budget survives kernel B**, measured with the real click/keystroke
   count and the Ports row restored — and, separately, **what a user actually waits for after
   pressing Simulate**, since L8d measured 7.66 s per frequency on the hero. R18 is an *interaction*
   budget and the solve is not in it, but a user who waits three minutes for a 20-point sweep deserves
   to have been told, and whether they are told is a UI decision this slice makes.
3. **The refusal audit, as a table**: every message that said "arrives at L8", what it says now, and
   **which ones were WRONG rather than merely stale**. That third column is the interesting one — it
   is where a capability boundary moved without anyone noticing.
4. **The full-solution `dotnet test` result** — the first since L8a — with the total, the wall clock,
   and **`Hero1BTests`' timing under load reported rather than repaired**. If anything outside
   `src/Engine/Mom/` and `src/Ui/` broke, that is the single most valuable sentence in this report,
   because five slices have been run without that check.
