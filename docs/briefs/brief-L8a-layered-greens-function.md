# Sonnet Brief — Phase L8a: the layered Green's function

**Design:** `docs/design/layout-view.md` §10.1 (what 2.5D MoM is), **§10.2 (the honest cost — read it
first)**, §10.5 (surface bases, deliberately left open until L8), §10.7 (the N budget and R17), §10.9
(the oracle ladder). Phase table row **L8 — Full-wave, single dielectric (B)**, whose gate is *"a
quarter-wave open stub resonates at the right frequency; a bend's s-parameters are physically sane;
A and B agree on a uniform line."*

**Read `src/Engine/Mom/CLAUDE.md` end to end before planning anything.** Kernel A is complete and
validated through L7b-b; every sign convention, every oracle that caught a real defect, and the two
occasions the closed-form "oracle" turned out to be the wrong one are recorded there. This brief
reuses that discipline unchanged and almost none of that code.

---

## Gate command — and it is NOT the full solution

**Run `dotnet test tests/Engine.Tests`**, plus `dotnet test tests/Firewall.Tests` whenever an
assembly reference could have moved. (This slice is engine-only per D6; a slice that touches
`src/Ui` runs `dotnet test tests/Ui.Tests` as a second invocation — this SDK's `dotnet test` rejects
two explicit project paths in one call.) **Do not run the full-solution `dotnet test` at the repo
root as a routine gate for this slice.**

**This applies to every L8 slice — L8a through L8d.** The full-solution run is required **once, at
the end of L8**, as part of L8e's gate. Three reasons, and the second is measured rather than
assumed:

1. **The slices touch two directories.** L8a–L8d live in `src/Engine/Mom/` and `src/Ui/Layout/Em`.
   The seams that could affect anything else — `IEmKernel`, `EmCapabilities`, the kernel registry,
   the narrowed refusals — are deliberately *not opened* until L8e (see §2 and §5). Running 6,800
   tests after each slice is cost with no signal.
2. **The full-solution run is where an unrelated test's timing budget bites.** `Hero1BTests` gates on
   a 10 s import-plus-solve wall clock. Measured under full-solution load **with L8's tests
   excluded**: 4.2 / 9.6 / 8.6 s — it already reaches its own gate on that machine, independently of
   this phase. Per-slice full runs therefore produce intermittent red that is not about the work, and
   the standing rule here is that a race is never called disproven (or proven) from a filtered run.
   **Do not "fix" `Hero1BTests` by widening its budget.** It is not this phase's test; if it becomes
   a genuine problem, report it and let the owner decide.
3. **L8e is where the full run earns itself**, because L8e is where the interface actually changes.

If a slice does something that could plausibly reach outside those directories — it should not, but
if it does — say so in the report rather than quietly running the full suite and moving on.

**Tagging:** if this slice adds CPU-heavy sweeps whose job is to *report* rather than to guard, tag
them `Category=Benchmark` and keep one representative case in the routine gate — **tagged for
another test's budget, not their own runtime.** `src/Engine/Mom/CLAUDE.md` records the precedent.

---

## 0. Read this before planning anything

L8 is not another increment on kernel A. It is a **different kernel**, and §10.2 says so in terms
worth quoting rather than paraphrasing:

> Item 3 is a research-grade numerics problem. The spatial-domain Green's function for a layered
> medium requires inverting the spectral-domain form through a Sommerfeld integral, which is
> oscillatory, slowly-converging, and has branch points and surface-wave poles. … it is fiddly,
> numerically delicate, and it is where a schedule goes to die. Item 4 (singular self- and near-term
> integrals) is the second such place.
>
> **Plan for this honestly rather than discovering it in month four.**

Kernel A's whole design was an escape from this: carrying bound charge explicitly kept the kernel at
the free-space logarithmic potential, so there were no Sommerfeld integrals, no DCIM and no special
functions anywhere in L6/L7. **That escape is not available here.** A full-wave planar solver on a
grounded slab needs the layered-medium Green's function, and there is no formulation trick that
removes it.

So this brief does one thing: **build the Green's function, and measure how good it is, with nothing
else in the way.** No mesher. No basis functions. No matrix fill. No solver. No ports. No UI. If you
find yourself writing a mesh, stop — that is L8b.

---

## 1. Decisions taken

**D1 (owner). L8 is SPLIT, and this brief is only its first piece.** Every phase in this area has
been staged — L6/L7 into an engine half and a Ui half, L7b into the symmetric pair and then L7b-b's
general case — and each time the staging is what made the risk visible early. L8 is the largest
phase in the plan and the only one §10.2 flags as schedule-uncertain, so it gets the same treatment:

| | Content |
|---|---|
| **L8a** (this brief) | The layered Green's function for a grounded slab, and its oracle ladder. Nothing else. |
| **L8b** | The 2D surface mesher + the plan-view mesh overlay + the pre-solve N report (R17). §10.5 is explicit that the viewer lands **before** the solver. → [`brief-L8b-planar-mesher-and-overlay.md`](brief-L8b-planar-mesher-and-overlay.md) |
| **L8c** | Surface basis functions, matrix fill, and the singular/near-singular integrals — §10.2's *second* place a schedule dies. |
| **L8d** | Ports, the two-line de-embedding calibration that is finally real work, per-frequency solve. |
| **L8e** | Results, the current-density heat map, the kernel registry, the narrowed refusals, and the phase gate. |

**Do not widen this brief toward L8b.** The point of stopping at the Green's function is that if it
does not reach acceptable accuracy, everything downstream is worthless and the cheapest possible
moment to find out is now — before a mesher exists to make the failure ambiguous.

**D2. ONE conductor layer, metal on the top surface of the slab, and this is a real simplification
rather than an arbitrary limit.** L8's own phase-table content is "single dielectric"; multiple metal
levels, z-directed current and vias are L9's. With one metal layer, source and observer are always at
the **same height** — `z = z' = h` — so the Green's function is a function of lateral separation ρ
alone at each frequency. That collapses what would be a two-variable fit into a one-variable one, and
it is enough for all three of L8's own gates: a quarter-wave open stub, a bend, and a uniform line
each need exactly one metal layer. **Say the limit out loud in the code and refuse anything else by
name**, per R-mom-17's standing rule.

**D3. DCIM is validated against DIRECT NUMERICAL SOMMERFELD INTEGRATION — a second, independent
formulation — never against itself.** This is not a general preference, it is the technique that
actually worked twice in L6/L7 and once in L7b-b:

- kernel A's exact image ground was proved sound by replacing it with an explicitly meshed 60 h
  ground plate — ~4800 unknowns, no image at all — which reproduced ε_eff to 0.14%;
- Route A's error at L7b-b was measured against a closed-form 2×2 eigen-decomposition that shared the
  block construction with production so the *only* difference was the one thing under test.

Direct Sommerfeld integration along a deformed contour is slow, fiddly to make robust, and completely
unsuitable as the production path — and it is exactly right as an oracle, because it shares no
approximation with DCIM. **Build both. Report the disagreement as a function of ρ/λ.**

**D4. No formula is transcribed from memory, and if a verifiable source cannot be obtained, that is
reported rather than guessed.** This project has been here three times: Garg-Bahl's bend/T/cross
reactance values at L5a (unobtainable, reported, an ideal junction shipped with a loud note),
Hammerstad-Jensen's thickness model at L7 (obtainable, and *wrong* outside its own regime), and the
coupled even/odd fit at L7b (the one reachable calculator returned identical output for εr = 4.4 and
εr = 1.0 while reporting an input error — building a gate on it would have been worse than having
none). The layered-medium Green's function is far more intricate than any of those. **Derive it from
a source you can actually read, name the source in the code, and if you cannot, say so and stop.** A
plausible-looking Green's function produces smooth, plausible, wrong s-parameters at every frequency
— the worst failure mode this codebase has.

**D5. Licensing — learn from, never copy.** The root `CLAUDE.md` rule applies with unusual force
here, because a large fraction of the reference DCIM implementations in circulation are GPL. Read
papers and textbooks; do not lift code. Note in the file header which source each formula came from.

**D6. Engine only.** `src/Engine/Mom/`, tests in `tests/Engine.Tests/Mom/`. No `.clay`, no
`EmSetup`, no panel, no `.snp`. The Ui half of L8 is its own brief, exactly as L6/L7's was.

---

## 2. What already exists, and what genuinely does not

**Exists and is reused unchanged:**

- `EmSuitability` / the R-mom-17 refusal vocabulary — name the feature, name where it arrives.
- `EmMaterial`, `EmDielectricRegion`, `EmGroundPlane`, `EmConstants` (`EmConstants.Eps0` is *derived*
  as `1/(µ₀c²)`, so µ₀ε₀ = 1/c² to the last bit — keep using it rather than a literal).
- The complex-permittivity convention: `ε* = ε_r(1 − j·tanδ)` throughout, so dielectric loss costs
  nothing extra and falls out rather than being asserted (R-mom-6).
- The oracle-ladder discipline of §10.9 and `src/Engine/Mom/CLAUDE.md`'s "what the oracles actually
  established" section.

**Does NOT exist, and one of these is a decision you must surface rather than take silently:**

- **There are no Bessel or Hankel functions anywhere in this repository.** Grep confirms it: kernel A
  needed none, and kernel W's Bessel internal impedance is only named in a design note. A Sommerfeld
  integral needs `J₀` (and `J₁` for the derivative terms) over a complex argument.
  **The root `CLAUDE.md` says to ask before adding a native dependency; a managed one still deserves
  a note.** Abramowitz & Stegun §9.4's polynomial approximations are public domain and adequate for
  real arguments; complex-argument Bessel is a harder problem. **State which route you took and why,
  and do not transcribe Numerical Recipes — it is copyrighted and its licence does not permit it.**
- `EmProblem` is a **cross-section** model — conductor outlines in the (x, y) plane of a *slice*,
  horizontal dielectric slabs, one ground plane. It cannot describe a planar layout and must not be
  stretched to. Kernel B's problem type is a sibling, not a subtype, and it arrives with L8b's
  mesher; **this brief needs neither, because a Green's function takes a stackup and two points.**
- `IEmKernel` is typed on `EmProblem`, so it cannot host kernel B as it stands. That is L8e's
  problem — and it is precisely the moment the file's own note comes due: *"There is deliberately no
  kernel registry yet. One kernel, constructed directly. A registry earns its place when kernel W or
  B exists."* **Do not open that seam here.**

---

## 3. The formulation, stated as requirements rather than as derivation

**R-lgf-1. The mixed-potential form, not the electric-field form.** MPIE keeps the singularity at
1/R rather than 1/R³, which is what makes the near-terms in L8c integrable at all. The standard
layered-media treatment is Michalski–Zheng's; **formulation C** is the usual production choice
because it keeps the scalar-potential kernel scalar. Name the formulation you implemented in the file
header — the three formulations differ in how the vector and scalar parts split, and a reader six
months from now needs to know which one the code is.

**R-lgf-2. The spectral-domain form is closed-form; only the inverse transform is hard.** In the
Hankel-transform domain the slab's response is built from its TE and TM reflection coefficients,
which for a single grounded slab are elementary. **This half must be exercised on its own before any
inverse transform exists**: a spectral-domain function that is wrong will produce a spatial-domain
function that is wrong in a way no oracle can localise afterward.

**R-lgf-3. The surface-wave pole is real physics, not a numerical nuisance, and a grounded slab
always has one.** The TM₀ mode of a grounded dielectric slab has **no cutoff** — it propagates at
every frequency, however thin the slab. Two consequences, both load-bearing:

- **It must be extracted before any exponential fit.** DCIM approximates the spectral function as a
  sum of complex exponentials; a pole is not well approximated by a sum of exponentials, so the
  quasi-static term and the surface-wave pole(s) are subtracted first, fitted separately, and added
  back in closed form. Skipping the extraction is the classic way DCIM "works" in the near field and
  falls apart at a few wavelengths.
- **Its location is independently checkable.** The TM₀ dispersion relation for a grounded slab is a
  transcendental equation in one unknown; solve it independently and assert the extracted pole sits
  on it. That is an oracle, and it costs almost nothing.

**R-lgf-4. Report where DCIM stops being accurate, as a number, as a function of ρ/λ.** DCIM is
strongest in the near and intermediate field and degrades furthest out, where the surface wave
dominates. **The deliverable of this brief is that curve**, not a claim that DCIM works. Mirror
L7b-b's M1 exactly: measure, report the number, and state the regime where it stops being acceptable
— then decide (see §6) whether the range that matters is covered.

**R-lgf-5. Frequency-dependence is total, and this is the single biggest change from kernel A.**
Kernel A's whole performance story is R-mom-11: `[C]`, `[C₀]` and `∂L/∂n` are frequency-independent,
so a 1001-point sweep costs the same four matrix fills as a 3-point one, enforced by a counter rather
than a comment. **None of that survives here.** The Green's function is a function of frequency, so
the DCIM fit is redone per frequency and (at L8c) so is the matrix fill and the LU. Say so plainly in
the code, and do **not** add a `MatrixFillCount`-style counter that implies otherwise. §10.7's
adaptive frequency sampling is the eventual answer and is explicitly an L9 item — do not build it.

**R-lgf-6. The static limit is a genuinely different regime and must be handled, not extrapolated
into.** As ω → 0 the spectral function's structure changes and a fit tuned at 10 GHz will not hold at
10 MHz. Either handle the quasi-static branch explicitly or **refuse below a stated frequency by
name**. Silently returning a fit outside its range is the failure mode this brief exists to prevent.

---

## 4. The oracle ladder — this IS the deliverable

Same philosophy as §10.9 and the same ordering rule that worked at L7: **each tier passes before the
next is written**, and the exact closed forms come before anything empirical.

**Tier 0 — the spectral-domain function alone, before any inverse transform.**
- The TE/TM reflection coefficients reduce to the textbook single-interface Fresnel forms as h → ∞.
- With ε_r = 1 the slab vanishes and the reflection coefficients reduce to those of a bare ground
  plane — i.e. a perfect conductor, exactly.
- Reciprocity of the spectral kernel in its source/observer heights.

**Tier 1 — the exact reductions, where the answer is known without any integration at all.**
- **ε_r = 1 must reduce EXACTLY to free space plus one image**: `e^{-jkR}/4πR − e^{-jkR'}/4πR'`.
  This is the strongest single check in the ladder, it needs no external data, and it is the direct
  analogue of kernel A's `T0_7`/`T1_2` image gate — which is the test that actually validated
  R-mom-7. If this does not hold to solver tolerance, nothing further is worth running.
- **The Sommerfeld identity itself**, verified numerically: the identity that turns one complex
  exponential in the spectral domain into one complex image in the spatial domain is the entire
  mechanism DCIM rests on. Check it standalone, on a single term, before checking a sum of them.
- **The static limit against the classic image series.** A grounded slab's static potential has a
  convergent image series whose ratio is set by the interface reflection coefficient. Derive the
  coefficients — do not take them from this sentence — and use it as the ω → 0 oracle. Its slow
  convergence at high ε_r is exactly why it is an oracle and not the production method.

**Tier 2 — the second independent formulation (D3).** Direct numerical integration along a deformed
Sommerfeld contour, agreeing with DCIM. **Report the relative error against ρ/λ on a grid** covering
at least ρ/λ ∈ [10⁻⁴, 10] — the low end is the near-singular regime L8c's fill will live in, the high
end is where the surface wave dominates. Sweep ε_r over the two starter technologies' own substrates
(FR-4 ε_r = 4.4, GaAs ε_r = 12.9 — the second is the harder case) and h over the starter thicknesses.

**Tier 3 — the pole and branch structure.**
- The extracted TM₀ pole satisfies the independently-solved dispersion relation, across frequency.
- The number of surface-wave modes increases at the frequencies where the higher modes' cutoffs
  predict it, and the code notices rather than silently fitting one pole to two.

**Tier 4 — behaviour, not values.**
- Reciprocity: `G(ρ; z, z') = G(ρ; z', z)`.
- Monotone convergence under refinement of whatever the DCIM sampling parameters are — the same
  "converge monotonically rather than wander" rule §10.9 already states.
- Determinism: identical inputs give bit-identical output across runs. GPOF/matrix-pencil involves an
  SVD and it is easy to leak a tolerance-dependent branch.

> **A forward warning that belongs here because it is a fact about this physics, not about L8e.**
> **Kernel A's losslessness oracle does NOT carry over to kernel B.** With σ = ∞ and tanδ = 0, a
> closed 2D cross-section is exactly lossless — but an *open* planar structure radiates and launches
> surface waves, both of which carry real power away, so `|S₁₁|² + |S₂₁|² < 1` **legitimately**.
> Reciprocity and passivity carry over; losslessness does not. Whoever writes L8d/L8e must not copy
> `TC2_TheFourPortIsLosslessWhenEveryLossIsIdeal` across and then "fix" the kernel until it passes —
> that would mean suppressing radiation, which is one of the two things L8 exists to model.

---

## 5. What must NOT be built here

Stated explicitly because each is a plausible next keystroke and each would make the measurement
above ambiguous:

- No mesher, no basis functions, no matrix fill, no solve, no ports, no de-embedding (L8b–L8d).
- No planar problem type, no extractor, no `EmSetup` change, no panel, no `.snp` (the Ui half).
- No kernel registry and no change to `IEmKernel` (L8e).
- No DCIM for **N** dielectrics — one grounded slab, per D2. The general stack is L9, and it is the
  place §10.2's warning actually bites hardest.
- No adaptive frequency sampling (§10.7 says build it when the kernel that needs it exists; it does
  not yet).
- Nothing in `src/Core`, `src/Engine` outside `Mom/`, `RfCore`, or `src/Ui`.

---

## 6. Milestones, each with its own gate

| | Content | Gate |
|---|---|---|
| **M1** | The spectral-domain function for a grounded slab: reflection coefficients, the MPIE kernels, the surface-wave pole located and checked against its own dispersion relation. **No inverse transform.** | **Tier 0 + the Tier 3 pole check green.** |
| **M2** | Direct numerical Sommerfeld integration along a deformed contour — the ORACLE, not the product. Slow is fine; robust is not optional. | **Tier 1 green**, in particular the exact ε_r = 1 reduction to free space + one image. |
| **M3** | DCIM: quasi-static and surface-wave extraction, GPOF/matrix-pencil fit, closed-form inversion by the Sommerfeld identity. | **Tier 2 green AND THE ERROR CURVE REPORTED.** Stop here and report before M4 — this is the number the rest of L8 is scheduled against. |
| **M4** | Whatever M3's measurement says is needed: a two-level fit, more extracted poles, a wider contour, or a stated refusal outside a validated range. | Tier 3 + Tier 4 green; the accuracy claim is a measured range, not "it works". |

**M3's gate is a REPORTING gate as much as a testing one, exactly as L7b-b's M1 was.** The question
L8's schedule turns on is not "does DCIM run" but "over what ρ/λ and ε_r range is it accurate enough
that a matrix filled with it produces trustworthy s-parameters". Answer it before building the thing
that consumes it.

Stop and report at any gate that does not go green rather than proceeding with a tolerance loosened
to make it pass.

---

## 7. File map (indicative)

```
src/Engine/Mom/
  LayeredMedium.cs          the grounded-slab stackup as the Green's function sees it (new)
  SpectralGreens.cs         M1 — reflection coefficients, MPIE kernels, pole location (new)
  SommerfeldIntegral.cs     M2 — the direct contour integration ORACLE (new)
  Dcim.cs                   M3 — extraction, GPOF/matrix-pencil, complex images (new)
  Bessel.cs                 J₀/J₁, if a managed dependency is not taken — see §2 (new, maybe)

tests/Engine.Tests/Mom/
  LayeredGreensFunctionTests.cs   Tiers 0–4, and the M3 error curve
```

---

## 8. Three things to report back on, whatever else happens

1. **The DCIM-vs-direct-integration error curve**, as a table over ρ/λ and ε_r, and the stated range
   where it is trustworthy. This is what M3 exists to produce, and L8b–L8e are scheduled against it.
2. **Which source each formula came from, and whether any of them could not be obtained.** D4 makes
   "not obtainable, reported, stopped" an acceptable outcome and "transcribed from memory" not one.
   Say which, plainly.
3. **Whether the Bessel dependency was added, written, or avoided** — and, if written, against which
   public-domain reference and to what accuracy. §2 flags this as a decision to surface rather than
   take quietly, because the root `CLAUDE.md` reserves dependency additions to the owner.
