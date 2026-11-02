# Sonnet Brief — the ground via, the via's current profile, and interior electrostatics

**Design:** `docs/design/layout-view.md` §10.7, §11's phase-table row **L9** (whose gate sentence asks
for *"multi-layer structure with backside vias"* — the half of it that was found to be unbuildable),
and §11's **LW3** note, which is downstream of this. **This is a FOLLOW-UP to L9, like
`brief-via-z-integral.md` before it** — L9's five slices are complete, its phase gate is built and
green, and this closes the two gaps those notes themselves nominate as the most valuable remaining
work in this area.

**Read, in this order, before planning anything:**

1. `src/Engine/Mom/CLAUDE.md` **§L9's PHASE GATE, FINDING 1** — the whole finding. That is gap A and
   it is the reason §11's own gate sentence had to be re-worded rather than merely failed.
2. **§L9d's "De-embedding refuses a port on a BURIED level"** and `PlanarSolve.cs`'s refusal string
   (search `sits on conductor level`). That is gap B. Note that it points at a missing OBJECT — a
   static Green's function at interior heights — rather than at a phase.
3. **§L9c's M4** (the via basis, and D5's two exact equalities), **M2/D3** (the THREE closed-form
   extractions and why a source ON an interior interface has two), and **§L9b's D6** (the four
   families, and `Σ_b = z + z′ − 2z_b`).
4. **The via z-integral follow-up section at the END of `src/Engine/Mom/CLAUDE.md`** — the split, and
   why a plain Gauss rule in z does not work. **Its machinery is what gap A builds on.**
5. Then the code, not summaries of it: `PlanarBasisFunctions` (`Halves`, `VerticalWeight`,
   `Divergence`), `SurfaceMesher`'s via handling, `ViaZIntegral` end to end,
   `PlanarFill.FillMultiLevel`'s three branches, `PlanarDeembed.CapacitancePerMetre`,
   **`LayeredStaticGreens` end to end** (its cascade is already general; only `Evaluate` is not), and
   `tests/Engine.Tests/Mom/ViaPhysicsTests.cs` — the closed-form partial-inductance oracle gap A needs
   is already in there.

---

## Gate command

```
dotnet test tests/Engine.Tests --no-build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

**The routine tier has NO headroom.** `Engine.Tests` is at **993 tests in 68 s** against the ~60 s
ceiling — already over, and L9e's note says so plainly rather than smoothing it. **Do not add a
routine test that fills a matrix**, with the one exception the z-integral brief established: a
bit-identity gate on a deliberately tiny mesh (N ≈ 48) costs under a second and belongs in the default
tier, because it is the check most worth having there.

Everything that solves goes behind `Category=Benchmark`; that tier is currently ~42 minutes and **you
must say what you add to it.**

**One measurement discipline.** L8d's own finding: a benchmark sharing a run with nine others reads
more than twice as slow, and L9d's 71.9 s was first mis-measured at 16.79 s that way. **Take every
timing measurement alone, and say in the report that you did.**

---

## 0. Read this before planning anything

### 0.1 The two gaps, exactly

**Gap A — a via to the ground plane is not representable at all.** L9c's via basis is a rooftop
spanning two adjacent MESHED levels, with ±1/Area divergence pulses at its two feet. A backside via
joins a signal level to the **ground plane**, which is the laterally infinite PEC the Green's function
handles analytically and is never a meshed level. `PlanarExtractor.BuildVias` drops such a via with a
note (`"not among this EM setup's analysis levels"`), and `L9PhaseGateTests` asserts that it does. On
a GaAs MMIC this is how a source terminal reaches ground, so the kernel can model an airbridge and
cannot model the commonest via there is.

**Gap B — a port on a buried level is refused**, because `Z_c = γ/(jωC_pul)` and `C_pul` comes from
`PlanarDeembed.CapacitancePerMetre`, which differences two standards' static capacitances using
`PlanarKernelTerms.StaticScalar(slab)` — an electrostatic image series over a **grounded slab**. That
is the right electrostatic problem for a line on the slab's top surface and the wrong one for a line
buried inside the stack. L9d refused it rather than feeding it anyway, on the grounds that the
de-embedded S is *referenced* to the Z_c it produces, so a wrong `C_pul` renormalises every published
s-parameter rather than merely blurring it. That reasoning stands; what is missing is the object.

### 0.2 Six things that are true before you start

1. **The via's z-integral is BUILT, and its machinery already covers the ground plane.**
   `ViaZIntegral.PrismCore`'s `sumFamily` arm integrates the down-reflected asymptote over the two
   prisms with `floorZ` at the source region's own floor. For a via to ground on a grounded stack that
   floor **is** z = 0 and `Σ_b = z + z′` runs over [0, 2ℓ]. Nothing in that path assumes two meshed
   feet. **Check this before designing around it, but expect it to be free.**
2. **The image sign is +1 for the CURRENT and −1 for the CHARGE, and both are measured.** A PEC is a
   short: the voltage reflection is −1 and the current reflection is +1, which is why `G_A^zz` at
   εᵣ = 1 over a PEC is free space **plus** a positive image (≤ 3.0e-4, against **21** for a negative
   one — L9e's `T2_1`). A vertical current terminating on the plane therefore continues into its image
   as a single filament of length 2ℓ **with no charge at z = 0**, while the charge at the top foot has
   a −1 image. Both facts are load-bearing for the basis below and neither needs re-deriving.
3. **A BACKSIDE VIA ON THE SHIPPING MMIC STARTER IS ELECTRICALLY LONG, and this is what reshapes gap
   A.** `MaxElectricalLength` is k·ℓ ≤ 0.05, and it is now a limit on the BASIS rather than on any
   quadrature: one z-rooftop per gap means the via's current is UNIFORM along it. A 100 µm GaAs
   substrate at 30 GHz gives `k = 2π√12.9/λ₀ = 2255 m⁻¹` and **k·ℓ = 0.23** — 4.5× over. At 10 GHz it
   is 0.075, still over. **So a half basis alone does not deliver a usable backside via**, and the
   remedy L9's own refusal names ("split the via across intermediate levels") is unavailable, because
   there are no intermediate conductor levels inside a substrate. **The via needs more than one degree
   of freedom in z, and that is part of this brief rather than a later one.**
4. **Two structural invariants DO NOT hold for an attachment basis, and neither is a regression.**
   (a) L9c's D5 asserts `∫∇·f dS = 0` as an EQUALITY on every basis; an attachment basis has a pulse
   at its top foot only, so its net charge is **+1**, balanced by the image rather than by a second
   pulse. (b) L8c's file header records that `s_A + s_B = 0`, so *"any part of G_q that does not depend
   on ρ contributes exactly ZERO to the scalar block"* — that cancellation is what makes the extracted
   constant harmless today, and **it fails for the attachment row.** Both must be re-stated and
   re-gated rather than allowed to look like breakage; (b) in particular is a real trap, because the
   constant term becomes load-bearing in exactly one row of the matrix and nothing currently notices.
5. **`LayeredStaticGreens` already has the whole static cascade, and gap B is smaller than the notes
   imply.** `Reflection`, `InterfaceCoefficient` and `TerminationCoefficient` are general in the stack
   and in k_ρ, and carry no height dependence at all. What is top-half-space-specific is only
   `Evaluate`: its two extracted closed forms (`1/√(ρ²+Δ²)` and `gInf/√(ρ²+Σ²)` with `Σ = z + z′ − 2H`)
   and its tail bound. **Part B is an extension of one method, not a second electrostatic solver.**
6. **Gap A and gap B are INDEPENDENT, and L9e already established it.** A backside via runs from the
   top metal down to the ground plane, so **both ports stay on the top level** and L9d's buried-level
   refusal is never reached. Do not scope B into A or make A wait for it.

---

## 1. Decisions taken

**D1 — PART A FIRST, and the fault line between the two parts is real.** A is the phase table's own
unbuilt gate sentence and it is the one a user hits. B is the one the notes call most valuable *next*.
They share no code. **If A consumes the slice, stop after A and report** — that is a complete outcome,
and B is then a clean second brief with nothing half-built in front of it.

**D2 — a via becomes a CHAIN of z-segments, and the grounded end is a HALF basis.** Not two separate
mechanisms: an attachment basis is the bottom member of a chain whose other members are ordinary
z-rooftops. `n = 1` with two meshed feet must reproduce today's answer **bit for bit** — the R-viz-1
precedent, and the reason to build it this way round rather than bolting a half basis onto the side.

**D3 — the z-segment count is a SETTING and a MEASUREMENT, not a fixed number.** `PlanarFillSettings`
already carries `ViaZNodes`/`ViaZStaticNodes` for the *quadrature*; this is a different quantity — a
count of UNKNOWNS — and it belongs on the mesh settings beside `CellsPerWavelength`, derived from
k·ℓ by the same rule R-msh-3 uses. **Report the rule and the table that chose it.**

**D4 — the attachment basis's net charge is +1 and the ground plane is the return. Assert it, do not
cancel it.** The temptation is to add a compensating pulse somewhere so D5's equality survives. That
is wrong: the compensating charge is physically on the ground plane, the Green's function already
carries it as an image, and adding it again double-counts. **§0.2 item 4 becomes two new gates, not
two exemptions.**

**D5 — Part B extends `LayeredStaticGreens.Evaluate` to interior heights** (§0.2 item 5), by exact
analogy with `SommerfeldIntegral.EvaluateInterior` and `AsymptoticAtHeights`: a direct term at Δ, a
down-reflection at Σ_b, and — because an interior region is bounded ABOVE as well — an up-reflection
and a double round trip. **It is far easier than its full-wave sibling and the brief should say why:**
in the static limit k_z → −j k_ρ everywhere, so there is no branch cut, no proper-sheet rule, no
surface-wave pole and no k₀ sinθ substitution. Everything decays monotonically and the tail bound is
the distance the term travels.

**D6 — no eigensolver, no GPOF-with-SVD, no ACA, no new package.** Declined at L7b-b, L8a, L9c and
L9e on measured grounds, and nothing here disturbs those measurements.

**D7 — bit-identity is the gate for everything this does not touch.** A structure with no ground via
and no buried-level port must not move by one ulp under any new setting. Pin it by RECONSTRUCTION at
full precision on a tiny mesh, as `ViaBasisTests.M5_6` does — not by a tolerance.

**D8 — the gates are external-data-free, and A's is a SHORTED stub.** L8's own phase gate used a
shunt λ_g/4 **open** stub, whose |S₂₁| notches at resonance. Its dual — a shunt λ_g/4 **shorted**
stub — is a short at DC and an open at λ_g/4, so |S₂₁| **peaks** there, and it cannot exist at all
without a via to ground. That gives three independent claims from one fixture (§5, Tier 5).

---

## 2. What already exists, and what genuinely does not

**Exists, and must be reused rather than rewritten:**

- `ViaZIntegral` in full — `PrismCore`, `AveragedTerms`, `AveragedMixedDerivative`, and
  `RectangleIntegrals.InverseAtOffset`. **Gap A adds spans; it should add no new integral.**
- `PlanarKernelSet.Model` / `Asymptote` / `GetMinusStaticAsymptotes`, and the shared fit cache.
- `PlanarBasisFunctions.Halves` / `VerticalWeight` / `Divergence`, and R-via-5's ordering rule (every
  horizontal basis before every vertical one).
- **`ViaPhysicsTests`' whole oracle ladder**: `Stack`, `AirOverPec`, `SeriesInductance` (with its exact
  algebraic ω-separation — read its doc comment before re-deriving it), `MeanOverSquare`,
  `ExactInductance`, and `T2_0`'s oracle self-check. **A via to ground at εᵣ = 1 over a PEC is a bar
  of length ℓ plus its equal-direction image, i.e. exactly the half of a 2ℓ bar this file already
  integrates in closed form.** The oracle gap A needs is already built.
- `LayeredStaticGreens.Reflection` and its two coefficient helpers (§0.2 item 5).
- `PlanarDeembed.CapacitancePerMetre` / `StaticCapacitance`, and `PlanarCalibration.BuildLine`'s
  `standardLevelZ`, which L9d already threads through `PlanarPortCalibrator`.

**Does NOT exist:**

- Any basis with one meshed foot. Any via with more than one unknown along its length.
- Any representation of "the lower terminal is the ground plane" — `PlanarVia` names two conductor
  LAYER INDICES and `PlanarLevels` is indexed by conductor level, so neither can say it.
- Any static Green's function at an interior height, for any stack (§0.2 item 5 is the cascade, not
  the transform).
- **L9c's Tier 4** — the static-limit rung for the interior kernel — has still never been run. Part B
  is what makes it runnable, and it should be run.

---

## 3. M1 — the measurement that shapes Part A

**This milestone produces numbers and a recommendation. It changes no production code.**

**R-gv-1 — measure what a z-CHAIN costs, on L9's own phase-gate fixture** (the 300 × 100 µm airbridge,
MMIC starter, shipping mesh, N = 1,023 with 8 vertical unknowns; 149.9 s per de-embedded point is the
number the via z-integral brief was scheduled against, and 65.5 s at N = 514 is what it measured):

1. **How many height PAIRINGS an n-segment chain asks for.** A chain of n equal segments is n distinct
   z spans, so the ẑẑ block wants n(n+1)/2 span pairs and the mixed block n per level — each
   multiplied by the z-quadrature's own node pairs. Report the fit count for n ∈ {1, 2, 4, 8} against
   today's 13, and say whether it grows with n² as the naive count suggests or whether equal-length
   segments share pairings.
2. **What that costs per frequency.** A `Dcim.FitAtHeights` measured 89.5 ms and a radial table 15.6 ms
   on that fixture. Report the seconds, and the percentage of a de-embedded point.
3. **Whether the chain is NEEDED at the frequencies that matter.** §0.2 item 3 says k·ℓ = 0.23 on a
   100 µm GaAs backside via at 30 GHz. Measure what the uniform-current basis actually costs there:
   solve the same shorted-stub fixture with n = 1, 2, 4, 8 and report how the resonance moves. **If it
   stops moving at n = 2 this is a much cheaper phase than it looks; if it is still moving at n = 8,
   say so and say what that implies for R17's budget.**

**The pass condition is a RECOMMENDATION, not a threshold.** Report the n the rule should derive, what
it costs, and whether `MaxElectricalLength` can be retired, narrowed, or must stay.

---

## 4. The formulation, stated as requirements

### Part A — the ground via and the current profile

**R-gv-2 — the attachment basis is a HALF ROOFTOP and its charge is +1.** One divergence pulse, at the
meshed foot; none at the grounded foot. Its vertical weight is the same uniform 1/Area over the
footprint. **Assert the +1 as an equality**, and assert separately — this is D4's real content — that
the basis **plus its image** is neutral, by measuring the ρ → ∞ decay of the scalar block's own row: a
neutral pair falls faster than a monopole and that is a measurement, not a comment.

**R-gv-3 — the segment chain, and `n = 1` is BIT-IDENTICAL.** A via between two meshed levels split
into n segments introduces n − 1 new interior levels that carry no metal. The junction continuity at
each is the same ±1/Area pair L9c's D5 gates, so charge conservation stays exact by construction.
**At n = 1, every entry of `Z` must equal today's to the last bit** on a two-level mesh with an
interior via.

**R-gv-4 — the segment count is DERIVED, and the rule is reported.** From k·ℓ, by R-msh-3's own
"fastest-slowing medium anywhere in the stack" rule, so that `n` is a property of the analysis rather
than a number the user has to know. Expose the override on `PlanarMeshSettings` (D3), and **make the
ceiling interact with R17 honestly**: n segments per via footprint cell multiplies the vertical
unknown count, and `SurfaceMesher.UnknownCeiling` must see it before anything is allocated.

**R-gv-5 — the mesher must not repeat L9c's two silent failures.** A ground-via footprint must still
contribute GRIDLINES (or the via vanishes with no error — measured at zero vertical unknowns on a
40 µm footprint) and must still NOT get the edge grading a conductor rim gets (measured at 2,448
unknowns against 424). **Assert both for the ground-via path specifically**; they were fixed for the
two-meshed-feet path and the new path does not inherit the tests.

**R-gv-6 — the extractor produces a ground via only when the ground is the one the kernel models.**
`PlanarExtractor.BuildVias` currently drops any via naming Backside Metal. It must now produce a
ground attachment **when the stackup's bottom termination is the PEC the Green's function terminates
on**, and must still refuse — by name, with the reason — when the named ground reference is anything
else. The existing note must not simply disappear; a via that silently becomes something different is
the failure mode L9c's own finding is about.

**R-gv-7 — reciprocity stays STRUCTURAL and must be re-verified as such.** `Z[m,n]` and `Z[n,m]`
bit-identical on a mesh carrying a ground via AND an interior via AND a mixed block, with the mixed
block asserted non-zero so the test cannot pass for the wrong reason. Extend `ViaBasisTests.M5_2`
rather than replacing it.

**R-gv-8 — the low-frequency limit is where §0.2 item 4(b) will bite, and it needs its own rung.**
With a net-charged row the extracted constant no longer cancels in `Z^φ`. Gate the ω → 0 behaviour of a
grounded stub against the static capacitance harness `PlanarStaticLimitTests` already has, so that a
sign error or a double-counted image shows up as a wrong capacitance rather than as a plausible
s-parameter.

### Part B — the interior electrostatic Green's function

**R-es-1 — extend `LayeredStaticGreens.Evaluate`, do not write a second solver** (D5). Four families,
by exact analogy with L9b's D6 and L9c's D3, with the static cascade supplying every coefficient:
direct at Δ, down-reflection at Σ_b, up-reflection at 2d − Σ_b, double at 2d − Δ. **Two of them are
non-decaying when the source sits ON an interior interface, which is precisely where metal goes** —
L9c's M2/D3 found exactly this in the full-wave case and the same statement is what makes the tail
converge here. Extract both in closed form.

**R-es-2 — the top-half-space path must be BIT-IDENTICAL.** L9a's D5 precedent, and it is cheap here
because the existing branch can simply be kept. Pin it by exact equality on a dump of configurations,
as L9b's R-dcm-1 did.

**R-es-3 — `PlanarKernelTerms` gains a static factory at heights**, beside `StaticScalar`, with the
same `Inverse`/`Log`/`Constant`/`Linear` decomposition. **There is no logarithm** — the static kernel
has no surface wave — and saying so explicitly is worth a line, because R-fil-3's trap is exactly the
assumption that there isn't one when there is.

**R-es-4 — `PlanarDeembed.CapacitancePerMetre` takes the level, and the refusal narrows.**
`PlanarSolve`'s buried-level refusal should survive only for the cases that genuinely remain (an
ungrounded stack, a stack `Dcim.CanFit` refuses), and its wording must name what is left rather than
what has been fixed. **`EmRefusalWordingTests`' sweep must stay green.**

**R-es-5 — L9c's Tier 4 gets RUN.** The interior DCIM fit's ω → 0 limit against this solver, over the
six stacks and both interior pairings L9c's Tier 5 used. Its value is that it catches an error the
full-wave oracle SHARES with the fit — so **it must not be built from `LayeredSpectralGreens`'s own
cascade at ω → 0**, or it shares the error too and measures nothing. Build it from the static cascade
and gate the static cascade independently (Tier 1 below).

**R-es-6 — no widening of anything.** `Dcim.ValidatedRhoOverLambda`, `ValidatedRhoOverLambdaLayered`,
`ValidatedRhoOverLambdaAtHeights` and `Dcim.CanFit` are untouched. A static solver changes what can be
DE-EMBEDDED; it changes nothing about where the full-wave kernel is accurate.

---

## 5. The oracle ladders

### Part A

| Tier | What | Where it comes from |
|---|---|---|
| 0 | **The oracles check out before anything is concluded from them** | `ViaPhysicsTests.T2_0`, extended to the half-bar-plus-image closed form |
| 1 | **The ground via's own inductance** at εᵣ = 1 over a PEC, against the closed form of a bar plus its equal-direction image, over ℓ/w ∈ [0.01, 5] | `ViaPhysicsTests`' existing machinery |
| 2 | **R-gv-3 bit-identity**: `n = 1` on an interior via equals today's matrix to the last ulp | reconstruction at full precision |
| 3 | **Charge**: the attachment basis's own is +1 as an EQUALITY, and the basis-plus-image pair decays like a dipole | new, `ViaBasisTests` |
| 4 | **Convergence in n**, and it is a CONVERGENCE not an invariance — the chain has degrees of freedom a single segment does not, so at k·ℓ ≪ 0.05 the answers must agree and at k·ℓ ~ 0.2 they must differ, in the direction the physics says | `T3_1b`'s shape, with the opposite claim |
| 5 | **THE GATE: a shunt λ_g/4 SHORTED stub**, on the MMIC starter, through the product path. Three claims: \|S₂₁\| PEAKS near the prediction; the peak moves DOWN by an amount consistent with the via's own partial inductance; and removing the via turns the peak into an OPEN-stub NOTCH | new, `tests/Ui.Tests/Em/` |
| 6 | **Reciprocity and passivity** (R-gv-7); no losslessness, ever | `ViaBasisTests.M5_2`, extended |
| 7 | **R-gv-8's ω → 0 capacitance** | `PlanarStaticLimitTests` |
| 8 | **Cost**, measured ALONE, against 65.5 s at N = 514 and the phase gate's own N = 1,023 | L9's phase-gate fixture |

**Tier 5's third claim is the one that would have caught L9's own bug** — before its phase gate,
`PlanarExtractor` silently dropped every drawn via and a with/without comparison was the only assertion
that could tell. Do not weaken it into "the stub resonates".

### Part B

| Tier | What |
|---|---|
| 0 | **R-es-2**: the top-half-space path is bit-identical |
| 1 | **The static cascade against an INDEPENDENT electrostatic oracle** — L9a's Tier 3 precedent, which used a genuinely electrostatic solver rather than an ω → 0 limit of the same code |
| 2 | **The one-slab reduction**: an interior source in a single-layer grounded stack against `StaticGreens`' own closed-form image series |
| 3 | **Reciprocity in the heights**: `G(ρ; z, z′) = G(ρ; z′, z)` |
| 4 | **L9c's Tier 4, RUN** (R-es-5) |
| 5 | **C_pul of a buried-level standard** is positive, converges under mesh refinement, and lands between the two bracketing one-level answers |
| 6 | **THE GATE: a buried-level port de-embeds a uniform section to something matched**, at L8d's own measured drift (3.9e-4 at 2 GHz, 6.0e-3 at 10 GHz on FR-4) rather than at an invented tolerance |
| 7 | **The refusal narrows and its legitimate neighbour is accepted in the same test** |

---

## 6. What must NOT be built here

- **Anything about `Dcim.ValidatedRhoOverLambdaAtHeights = 0.1`.** It has its own brief — see §10. It
  is the limit that refuses §10.7's own FR-4 hero once a via is present, and it is a different kind of
  work from either part here.
- **A general complex eigensolver, GPOF-with-SVD, vector fitting, ACA, or a new package** (D6).
- **Any widening of any `Dcim` constant or refusal string** (R-es-6).
- **A losslessness check.** Still not added anywhere, and still more true with vias.
- **The wirebond kernel (LW1/LW2/LW3).** Named in §11, downstream, not this.
- **Any change to `PlanarPort`'s model, to L8d's calibration algebra, or to the horizontal vector and
  scalar blocks.** Part A adds vertical unknowns; Part B changes one static kernel.
- **A port ON a via.** L9d refused it and EARNED the refusal by showing it is a different object; a
  ground via does not change that argument.
- **A new starter technology.** Hand-build fixtures beside the tests that need them.

---

## 7. Milestones, each with its own gate

| M | What | Gate |
|---|---|---|
| **M1** | The cost and necessity measurement (§3) | Numbers reported; a legitimate stopping point |
| **M2** | The z-segment chain between two meshed levels, `n` derived (R-gv-3, R-gv-4) | Tier 2's bit-identity at n = 1; Tier 4's convergence |
| **M3** | The attachment basis and the ground-via path through mesher and extractor (R-gv-2, R-gv-5, R-gv-6) | Tiers 1, 3 |
| **M4** | Part A's product path and its gate (R-gv-7, R-gv-8) | Tiers 5, 6, 7, 8 |
| **M5** | `LayeredStaticGreens` at interior heights (R-es-1, R-es-2, R-es-3) | Part B Tiers 0–3 |
| **M6** | C_pul and the narrowed refusal (R-es-4, R-es-5) | Part B Tiers 4–7 |
| **M7** | Cost, the phase gates re-run, and the notes | Tier 8; L8's and L9's gates still green |

**The natural fault line is after M4** (D1). If Part A consumes the slice, stop and report — Part B is
independent and loses nothing by waiting. A second fault line sits after M2: a via with a resolved
current profile between two meshed levels is already worth shipping even if the attachment basis is
not built, because it retires `MaxElectricalLength` for interior vias.

---

## 8. File map (indicative)

```
src/Engine/Mom/PlanarMesh.cs           a vertical basis that names the GROUND as one terminal
src/Engine/Mom/PlanarProblem.cs        PlanarVia gains a ground-terminal form; CanSolve's checks
src/Engine/Mom/SurfaceMesher.cs        the chain's intermediate levels; the ground-via footprint
src/Engine/Mom/PlanarBasisFunctions.cs the half rooftop, and Divergence's single pulse
src/Engine/Mom/PlanarKernelSet.cs      PlanarLevels learns about z = 0; MaxElectricalLength's fate
src/Engine/Mom/PlanarFill.cs           FillMultiLevel's ẑẑ and mixed branches take the new spans
src/Engine/Mom/LayeredMedium.cs        LayeredStaticGreens.Evaluate at interior heights (Part B)
src/Engine/Mom/SingularExtraction.cs   PlanarKernelTerms.StaticScalarAtHeights (Part B)
src/Engine/Mom/PlanarDeembed.cs        CapacitancePerMetre takes the level (Part B)
src/Engine/Mom/PlanarSolve.cs          the narrowed buried-level refusal (Part B)
src/Ui/Layout/Em/PlanarExtractor.cs    BuildVias produces a ground attachment (R-gv-6)
tests/Engine.Tests/Mom/ViaPhysicsTests.cs  Tier 1 and Tier 4 — extend, do not start a new file
tests/Engine.Tests/Mom/ViaBasisTests.cs    Tiers 2, 3, 6
tests/Ui.Tests/Em/                         Tier 5's shorted-stub gate
```

---

## 9. What to report back on, whatever else happens

1. **M1's three numbers**, and the `n` rule you derived — including whether the chain was needed at
   all at the frequencies §0.2 item 3 names.
2. **The ground via's inductance against the closed form**, over the same ℓ/w span the z-integral
   brief used. This is the deliverable for Part A.
3. **What the attachment basis did to the two structural invariants** (§0.2 item 4), and how each is
   now gated.
4. **What `MaxElectricalLength` became** — retired, narrowed, or kept, with the reason.
5. **Whether Tier 5's shorted stub resonated where the prediction said**, and by how much the via
   moved it. If the with/without comparison is the only thing that passed, say so.
6. **The cost of a de-embedded point** against 65.5 s at N = 514, measured ALONE, and what you added to
   the ~42-minute opt-in tier.
7. **Part B: what L9c's un-run Tier 4 actually said** when it was finally run, and what the buried-level
   refusal narrowed to.
8. **Any place the answer moved that should not have** (D7). If a structure with no ground via moved by
   an ulp, say so and say why rather than adjusting a tolerance.

---

## 10. The one thing deliberately NOT here, and why

**`Dcim.ValidatedRhoOverLambdaAtHeights = 0.1` — G_A^zz's accuracy ceiling — is a separate brief.** It
is the limit that refuses §10.7's own FR-4 hero outright at 10 GHz once a via is present, so it is
arguably more valuable than either part above; it is excluded because it is a different discipline
(numerical approximation), because its blast radius is one file, and because it has a genuine
stop-and-report decision that should not be entangled with deliverables that must land.

**Two things that brief should measure BEFORE anyone writes an eigensolver, and neither has been
measured:**

1. **The refusal may be asking the wrong question.** `PlanarKernelSet.WithinValidatedRange` asks it of
   the MESH DIAGONAL — but `G_A^zz`, the only component that fails, appears **only between two
   vertical bases**. Two vias 200 µm apart on a 20 mm board are asked about at 20 mm. Scoping the
   question to the largest ρ over pairs that actually carry vertical current is a few lines and may
   remove the refusal for most real structures with no numerical work at all. **This is the same shape
   as L9e's own ℓ/w finding: a bound measured against the wrong quantity.**
2. **The failing block is tiny and might not need a fit at all.** The ẑẑ block is 8 unknowns of 1,023
   on L9's own fixture, and its kernel is a function of ρ alone at a fixed height pairing — so a
   radial table built by DIRECT integration from `SommerfeldIntegral.EvaluateInterior`, which L9c
   measured accurate everywhere out there, would replace the fit for exactly the component that fails.
   The stated objection is that it is "far too slow to fill a matrix with" — but it does not have to
   fill a matrix, only a table, and L9c measured a 100× coarsening at 9.6e-10. **Whether that lands at
   ~5 s or ~500 s per pairing is one measurement and nobody has taken it.**

Only if both come back negative is the depth-search question live — and it should then be answered
with L7b-b's discipline: measure what the cheap route costs in accuracy before committing to a
hand-written complex eigensolver this repository has declined four times.
