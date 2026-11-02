# Brief CL5 — the lossless-stack refusal belongs to the integrator, not to the fit

**Series:** [`brief-conductor-loss-0-overview.md`](brief-conductor-loss-0-overview.md) ·
**Tags:** `R-cl5-n` · **Area:** `src/Engine/Mom` · **Depends on:** CL4 shipped

**Read first:** `RESOLVED.md` §CL4 §8 (first trap) and §9 (obstruction 1), because this brief exists
to correct the second of those; `SommerfeldIntegral.cs:438-460` — `CanIntegrateLayered`, in full,
including its own doc comment, which already says the thing this brief measures; `Dcim.cs:690-700`
(`FitAtHeights`' precondition) and `SommerfeldIntegral.cs:565-585` (`CanIntegrateInterior`);
`src/Engine/Mom/CLAUDE.md` §7's refusal list.

---

## The problem

`SommerfeldIntegral.CanIntegrateLayered` refuses a stack that is GUIDED and has no lossy layer:

> *"The direct Sommerfeld integrator needs at least one LOSSY layer (tanδ > 0). A lossless guided
> stack puts its surface-wave poles exactly on the real-k_ρ contour this integrator uses. **Dcim has
> no such restriction: it extracts the poles in closed form.**"*

**That sentence is correct, and the last clause of it is the brief.** The refusal is the DIRECT
integrator's — it walks the real k_ρ axis and a pole sitting on that axis is fatal to it. It reaches
the product only because `Dcim.FitAtHeights` calls `CanIntegrateInterior` as a precondition, and
`CanIntegrateInterior` asks `CanIntegrateLayered` first. **`FitAtHeights` integrates nothing.** It
samples `KernelAtHeights` along a path in the source region's own k_zm and Prony-fits it, having
already subtracted every pole in closed form through `PoleSum`.

So the general kernel refuses a case it can answer, and CL4 §9 recorded that refusal as the FIRST of
the three obstructions standing between a built capability and a user.

### Two defects, and they are separable

1. **The predicate is BORROWED.** What `FitAtHeights` actually needs from `CanIntegrateInterior` is
   the two wall checks (a source cannot sit inside a solid wall; the closed forms are referenced to
   an open top). It does not need the contour's restriction.
2. **The predicate is BLIND TO THE TERMINATION.** It tests `stack.Layers.Any(l => l.Material.TanD > 0)`.
   A CL4 conducting floor dissipates and moves the poles off the real axis — which is exactly the
   condition the refusal exists to guarantee — and the test cannot see it, because a `Termination` is
   not a `MediumLayer`. **A stack that has just been made admissible by the very feature this series
   built is still refused by name.**

## What was already measured

A scratch probe, FR-4 1.6 mm εᵣ = 4.4 at 10 GHz. Columns 3-4 are the worst disagreement between the
one-slab `Dcim.Fit(SpectralGreens…)` and the layered high-high `Dcim.Fit(LayeredSpectralGreens…)`
over ρ/λ ∈ [1e-3, 2], as a fraction of the free-space kernel; column 5 is `FitAtHeights(h, h)`.

| stack | pole \|Im k_ρ\|/Re k_ρ | `CanIntegrateLayered` | G_A | G_q | `FitAtHeights` |
|---|---|---|---|---|---|
| tanδ = 0.02 | 7.692e-4 | yes | 2.258e-6 | 1.827e-5 | OK, residual 3.17e-5 |
| **tanδ = 0** | **0** | **NO** | **2.273e-6** | **3.613e-6** | **THREW** |
| tanδ = 1e-12 | 3.846e-14 | yes | 6.659e-7 | 1.644e-5 | OK, residual 3.81e-5 |
| **tanδ = 1e-16** | **3.846e-18** | **yes** | **7.573e-7** | **1.048e-5** | **OK, residual 3.86e-5** |
| tanδ = 0, **35 µm Cu floor** | **2.607e-5** | **NO** | — | — | **THREW** |
| εᵣ = 1, tanδ = 0 (air) | — no guided mode | yes | 1.212e-15 | 1.387e-15 | OK |

Three readings, and the brief's whole case is in them:

- **At tanδ = 1e-16 the pole is 3.8e-18 off the axis — numerically ON it — and the fit is
  indistinguishable from the lossy one.** Nothing in the DCIM path degrades as the loss goes to
  zero. The refusal is the only thing in the way.
- **A conducting floor on a lossless dielectric puts the pole at 2.6e-5** — three orders further off
  the axis than the tanδ = 1e-12 case that is freely admitted — **and is still refused.**
- **CL4 §9's named casualty is not one.** `CoplanarDeembedTests`' fixtures are `EmMaterial(1.0, 0.0)`;
  at εᵣ = 1 there is no guided mode, `guided` is false, and `CanIntegrateLayered` already says yes
  today. The case that actually refuses is **εᵣ > 1 with tanδ = 0**, which nothing in CL4 named.

**That last case is reachable now, and it is a live defect rather than a future one.** `TanD` comes
straight off the technology's dielectric entry (`PlanarExtractor.cs:890`), a user may legitimately
enter 0, and `generalMedium` (`PlanarExtractor.cs:1247`) is true for any design with more than one
level or more than one dielectric band. **A two-level design on an ideal substrate refuses today,
with a sentence about an integrator its run never reaches.**

## What this brief is NOT

- **It is not permission to add artificial loss to a run.** The ε-limit below is an ORACLE
  construction, used to measure a fit against a path that can be integrated. Nothing in the product
  may quietly displace a pole to make a contour work; that is a wrong answer with a plausible shape,
  which is what §7's whole refusal list exists to prevent.
- **It does not delete a refusal.** `CanIntegrateLayered` keeps its restriction for its own caller —
  `EvaluateLayered` and `EvaluateInterior` still cannot walk a contour through a pole. It is
  narrowed and re-pointed, which is R-mom-17's standing rule and the same move CL4 §5 made on the
  ground-attachment check.
- **It does not touch the validated ranges.** `ValidatedRhoOverLambdaLayered` (1.6e-2),
  `ValidatedRhoOverLambdaAtHeights` (0.1) and `ValidatedRhoOverLambdaInteriorHorizontal` (1.0) are
  measurements and stay exactly where they are.

## Milestones

1. **Split the precondition.** `Dcim.FitAtHeights` asks for what it needs — the wall checks and the
   open-top reference — rather than for the contour's restriction. The cleanest shape is a second
   predicate beside `CanIntegrateInterior` (`CanFitAtHeightsStructurally`, or `CanIntegrateInterior`
   keeping its name and a new `CanFitInterior` carrying the narrower question), so the direct
   integrator's own callers are untouched and the two questions stop sharing one sentence.
   **Whichever shape is chosen, the direct integrator's refusal must still fire for the direct
   integrator** — R-cl5-4.

2. **Teach the loss test about the termination.** A stack dissipates if any LAYER has tanδ > 0 **or**
   either termination is dissipative. `Termination.IsConductor` already exists (CL4 §0) and
   `SurfaceImpedanceAt` returns exactly `Complex.Zero` on `PlanarSurfaceImpedance.IsPerfect`'s three
   spellings — so a σ = ∞ floor is correctly NOT dissipative and a real one is. A `HalfSpace`
   termination into a lossy medium counts too. **Write the test once and read it from both places.**

3. **Build the ε-limit oracle and measure the fit down to it.** Fit the same lossless guided stack at
   tanδ = 1e-3, 1e-6, 1e-9, 1e-12, 1e-16 and at 0, and show the fitted kernel converges rather than
   degrades. At the largest of those the DIRECT integrator is admitted and is the independent
   reference; below it, the ladder is its own continuity statement. **Report the spectral residual
   at §5's own ρ/λ points, against the un-widened 1.6e-2.**

4. **Decide, explicitly, whether the direct integrator gains a contour indentation** — an
   arbitrarily small excursion around a real-axis pole, with the residue added in closed form, which
   is the standard remedy and would make the oracle exact at tanδ = 0 rather than only in the limit.
   **A NO is a perfectly good answer here** and the ε-ladder is what makes it one. It is recorded as
   a decision either way, because a refusal whose remedy is never written down gets re-discovered.

## Gates

- **R-cl5-1 — nothing that runs today moves.** Every stack `CanIntegrateLayered` admits before this
  brief must produce **bit-identical** fits after it, on both starters, both kernels, at 2 / 10 /
  20 GHz. The predicate is being widened and re-pointed, not re-tuned.
- **R-cl5-2 — the lossless guided stack, measured.** The ε-ladder of milestone 3, tabulated, on both
  starters made lossless, both kernels, inside `Dcim.ValidatedRhoOverLambdaLayered` un-widened.
- **R-cl5-3 — the live defect closes.** A two-level problem on a guided stack with tanδ = 0 solves,
  and its S-parameters agree with the same problem at tanδ = 1e-16 to the tolerance that case's own
  loss justifies. **This is the gate that says the brief reached a user**, because it is the only one
  driven through `PlanarSolve` rather than through the fit.
- **R-cl5-4 — the refusal still refuses.** `SommerfeldIntegral.EvaluateLayered` and
  `EvaluateInterior` still refuse a lossless guided stack, by name, with a reason that is about the
  CONTOUR. Assert the message, not just the verdict: this refusal has now been wrong in its
  attribution once and the assertion is what stops that recurring.
- **R-cl5-5 — a CL4 floor is admitted.** The `tanδ = 0` + conducting-floor row above runs and reports
  its pole off the axis. That row is the reason this brief is in this series and not a standalone
  tidy-up.
- **R-cl5-6** — `dotnet test tests/Engine.Tests`, run ONCE and triaged from the TRX.

## Must NOT

- Widen `ValidatedRhoOverLambdaLayered`, `ValidatedRhoOverLambdaAtHeights` or
  `ValidatedRhoOverLambdaInteriorHorizontal`. Each was set by a measurement and §5 says so.
- Add loss — artificial, minimum, floored or clamped — anywhere a RUN can see it.
- Delete `CanIntegrateLayered`'s lossless refusal, or relax it to a warning. Narrow it and re-point
  it; its own caller still needs it.
- Change `Dcim.Fit`'s high-high path. It never carried this refusal and the measurements above show
  it never needed one.
- Touch the CL4 termination, CL1's sheet term, CL2's residual, or the port algebra.

## On completion

- `src/Engine/Mom/RESOLVED.md` `§CL5`: the ε-ladder, the two predicates and which question each now
  asks, the milestone-4 decision, and — stated plainly — **the correction to §CL4 §9's obstruction 1
  and its `CoplanarDeembedTests` example**, since a later brief reading §9 as written would size CL6
  and CL7 against an obstruction that is not there.
- **No `CLAUDE.md` edit.** §7's refusal list carries the lossless-stack line as an absolute; it is
  now the direct integrator's only. **Report it to the owner by file and line.**
