# Brief CL6 — the one-slab kernel gets the same floor

**Series:** [`brief-conductor-loss-0-overview.md`](brief-conductor-loss-0-overview.md) ·
**Tags:** `R-cl6-n` · **Area:** `src/Engine/Mom` · **Depends on:** CL4 shipped, CL5 shipped

**Read first:** `RESOLVED.md` §CL4 in full — **§0 (the termination, and why `Z_s` is one-sided),
§1 (the PEC reduction and the static routes), §3 (why a surface impedance adds no branch point) and
§9 (what this brief exists to unblock)**; `SpectralGreens.cs:100-340` — the one-layer kernel's
header, its three reflection coefficients and the cross-multiplied form's own comment, which is the
thing being re-derived; `SpectralGreens.cs:1628-1830` — `LayeredSpectralGreens`' own
`ReflectionDifferenceOverW` and `ReflectionTaylor`, because that is this brief's named fallback;
`LayeredMedium.cs:26-85` (`GroundedSlab`) and `:155-215` (`Termination`);
`tests/Engine.Tests/Mom/GeneralLayeredMediumTests.cs` T1_1/T1_2 — D5's one-layer reduction, which is
this brief's strongest oracle and already exists.

---

## The problem

CL4 built a conducting ground plane and **no run a user can make gets one** (§9). The obstruction is
not the physics and not the fit: it is that a conducting floor is expressible only on `LayerStack`,
`PlanarProblem.RequiresGeneralKernel` turns on the moment a `MediumStack` is given, and
`PlanarExtractor.cs:1247` keeps a single-level design on a single dielectric band — **the FR-4 hero,
the shipped patch, every ordinary microstrip** — on L8's one-slab path, where there is no floor to
set.

CL4 §9 weighed three consequences of simply switching those runs to the general kernel. **CL5 has
since removed the first of them** (the lossless-dielectric refusal was the direct integrator's, not
the fit's). The second stands and is this brief's whole reason for existing:

> **`ValidatedRhoOverLambdaLayered` is ≤ 1.6e-2 against `ValidatedRhoOverLambda`'s ≤ 6e-3 — the
> general medium is about 2.6× worse — so every one-slab run would be re-based onto a looser tier to
> gain a term worth 11-25% of the conductor loss.**

That trade is refused. **The one-slab kernel gets its own floor instead, and every run stays on the
tier it was measured on.**

The third consequence — the re-bless — is real and is CL7's.

## What stays exactly the same

**The plane remains laterally infinite and analytic. It is never meshed, it adds no unknowns, and
R17's ceiling does not move.** Only the terminating reflection changes, exactly as in CL4, and for
the same reason: the impedance condition holds at every spectral component `k_ρ`, so the non-uniform
return current under a strip and the loss it dissipates fall out of the inverse transform with
nothing representing the plane's current distribution explicitly.

**`Z_s` is CL4's one-sided `η_c·coth(γ_c t)` — `PlanarSurfaceImpedance.Plane` — and this brief writes
no surface impedance of its own.** The plane carries its return current on its upper face alone; CL1.1's
two-sided halving does not apply, and CL4 measured `Re(Z_plane)·σδ → 1.000000` in the thick limit to
say so. **A second spelling of `Z_s` in this file would be a second loss model with no gate on it.**

**Every limit CL4 §0 inherited is inherited again, unchanged:** no ground edges, no patterned ground,
one σ and one thickness for the whole infinite plane. `FrontToBackDb` stays refused for the reason
CL4 §6 gives — a Leontovich condition computes no transmitted field — and this brief does not
re-open it.

## The change

`GroundedSlab` gains the floor. The natural spelling is the one CL4 already built: a `Termination`
member defaulting to `Termination.Pec`, so `LayerStack.FromGroundedSlab` becomes a straight pass-through
and D5's bridge keeps its meaning at every σ rather than only at σ = ∞.

The three reflection coefficients stop being the SHORTED slab's and become the TERMINATED slab's.
Looking down from z = h into a slab of thickness h closed by `Z_s`, per polarization,

```
Z_in^p = Z_1^p · (Z_s + j Z_1^p tan(k_z1 h)) / (Z_1^p + j Z_s tan(k_z1 h))
Γ^p    = (Z_in^p − Z_0^p) / (Z_in^p + Z_0^p)
Z_1^e  = k_z1/(ωε),  Z_1^h = ωµ/k_z1,   Z_0^e = k_z0/(ωε₀),  Z_0^h = ωµ/k_z0
```

**Written in the cross-multiplied form the file already uses** — `T = tan(k_z1 h)/k_z1`, which is
`TanOverArgument` and is finite as k_z1 → 0 — this comes out as

```
Γ^h = [ k_z0 Z_s/(ωµ) + j k_z0 T − D ] / [ k_z0 Z_s/(ωµ) + j k_z0 T + D ],   D = 1 + j Z_s k_z1² T/(ωµ)
Γ^e = [ ωε₀ε* Z_s + j k_z1² T − ε* k_z0 E ] / [ ωε₀ε* Z_s + j k_z1² T + ε* k_z0 E ],  E = 1 + jωε₀ε* Z_s T
```

**Derived for this brief and not yet verified in code — verifying it is milestone 1.** Three
properties of it are worth stating in advance because they are what the milestones check:

- **At `Z_s = 0` both collapse to the shipped `TeFrom` and `TmFrom` exactly**, term for term
  (`D = E = 1`), which is what makes R-cl6-1's bit-identity reachable rather than hopeful.
- **Neither form divides by `k_z0`**, so the branch point at k_ρ = k₀ is still finite: at k_z0 = 0,
  `Γ^h → −1` and `Γ^e → +1`, the same two limits the file's own comment records. **That is the
  property the whole cross-multiplied form exists for and it survives.**
- **Everything is still EVEN in k_z1** — `T` and `k_z1²T` both are, and `Z_s` does not depend on k_z1
  at all — so the k_z1 branch choice still cannot matter and there is still no cut at k_ρ = k₁.
  This is the one-layer statement of CL4 §3's finding, and it is why DCIM's basis is untouched.

### The one real risk, named, with its landing

**`ScalarFrom`'s exact cancellation.** Γ^q is `Γ^e − (k₀²/k_ρ²)(Γ^e − Γ^h)`, and the file computes
the difference through the algebraically cancelled identity

```
Γ^e − Γ^h = 2jT k_ρ²(εᵣ−1) / [(jk_z1T + εᵣk_z0)(jk_z0T + 1)]
```

so that the `k₀²/k_ρ²` prefactor meets an **exact** `k_ρ²` and nothing is divided by a small number.
Its own comment says why: writing it the obvious way is fine at k_ρ ~ k₀ and quietly loses every
digit as k_ρ → 0, **which is precisely where the DCIM sampling path starts.** That identity is
derived from the shorted-slab algebra and does not survive `Z_s ≠ 0` as written.

**The ZERO itself does survive, structurally, and the argument is physical rather than algebraic.**
At k_ρ = 0 the two lines are the same line: `Z_1^e = k₁/(ωε) = √(µ/ε) = Z_1^h` and `Z_0^e = Z_0^h = η₀`,
and `Z_s` is one scalar for both. So `Z_in^e = Z_in^h` and `Γ^e = Γ^h` at k_ρ = 0 **whatever the floor
is**, and the difference still has a double zero there. What has to be re-derived is not the zero's
existence but a factored form that REALISES it in floating point.

**If that re-derivation does not land cleanly, do not force it.** `LayeredSpectralGreens` faced the
same problem and did not solve it algebraically at all: `ReflectionTaylor` (`SpectralGreens.cs:1767`)
takes the difference's Taylor coefficients in `w = k_ρ²` **numerically, off a small contour**, by a
Cauchy integral — which is why CL4 needed no re-derivation of this quantity and why its measured fit
quality did not move. **Adopting that construction here is a named, acceptable outcome**, not a
fallback to apologise for; it costs a handful of contour samples once per (slab, frequency) and it
is already validated on the general side.

## The four things to MEASURE, in this order, stopping at the first that refuses

1. **Do the two new forms reduce?** At `Z_s = 0` (all three of `PlanarSurfaceImpedance.IsPerfect`'s
   spellings: σ = +∞, σ = 0, t = 0) every Γ must reproduce the shipped kernel **bit for bit**, not to
   1e-15. CL4 §1 got a structural zero by returning the PEC value exactly on the perfect spellings
   rather than computing `(0 − Z)/(0 + Z)`; do the same here and for the same reason.
2. **Does Γ^q's zero survive in arithmetic?** The gate is not the identity, it is the VALUE: sweep
   k_ρ/k₀ down through the DCIM path's own starting decade and compare Γ^q against
   `LayeredSpectralGreens.TopInterfaceReflection` on the SAME stack with the SAME floor. D5's
   existing tolerance is 1e-13 for k_ρ ≤ 40k₀; report whether it holds at a real σ. **If the algebraic
   route cannot hold it, take the contour route and say so.**
3. **Does the DCIM fit stay inside the un-widened `ValidatedRhoOverLambda` of 6e-3?** This is the
   whole point of the brief — the tier is what is being bought — so it is measured against direct
   Sommerfeld integration on both starters at 2 / 10 / 20 GHz, at §5's own ρ/λ points, both kernels.
   CL4 measured the general kernel's fit getting BETTER on four of six cases with a lossy floor; a
   repeat of that here would be a good sign and is not to be assumed.
4. **Where do the one-slab poles go?** `SpectralGreens.FindSurfaceWaveModes` currently searches the
   shorted slab's own denominators. **Prefer delegating to CL4's `SurfaceWavePoles`** over the
   `LayerStack` the slab now maps to, rather than writing a second search: `PlanarMetrics` refuses
   `PowerSurfaceWave` past `|Im k_ρ|/Re k_ρ = 0.05` and the two searches disagreeing about a pole
   would be a metric refusing on one path and not the other. Report the worst ratio on both starters
   across the band; CL4's own worst anywhere was 1.5705e-2.

## Gates

- **R-cl6-1 — the PEC reduction, bit-identical.** Both starters × 2 / 10 / 20 GHz × all three
  spellings of a perfect conductor, on `G_A` and `G_q` at five ρ/λ each, against the pre-CL6 kernel.
  **0 bits moved**, as CL4 §1 got. **Do NOT collapse a perfect floor back onto the old code path to
  achieve it** — that makes the gate vacuous, and CL4 §1 refused the same shortcut for the same
  reason: the new path must be TAKEN and reproduce the old bits.
- **R-cl6-2 — D5's one-layer reduction, now at every σ.** `GeneralLayeredMediumTests` T1_1 and T1_2
  re-run with the SAME floor on both sides: `GroundedSlab` with `Termination.LossyGround(σ, t)` against
  `LayerStack.FromGroundedSlab` of it, which CL4 already solves. **This is the strongest oracle in
  the brief and it costs nothing to build**, because CL4 built the other half. Today it holds at
  1e-13 for k_ρ ≤ 40k₀; state what it holds at with a real floor.
- **R-cl6-3 — the εᵣ = 1 image reduction survives.** §3.1's strongest closed-form oracle (free space
  plus one negative image) at σ = ∞. CL4's own row was 1.233e-10 worst relative; the lossy row there
  was 5.838e-4 and is the measured size of the imperfection, not a failure.
- **R-cl6-4 — the static routes are bit-identical at every σ.** `LayeredMedium.ScalarPotential(GroundedSlab…)`'s
  image series and `SingularExtraction.StaticScalar`/`StaticVector` read a conducting floor as a PEC,
  and CL4 §1 established that this is the **ω → 0 limit rather than a simplification**
  (`Γ → −1` as ω → 0 with `Z_s → 1/(σt)` finite). Assert it rather than assume it, at a REAL σ, on
  both starters — CL4 got 0/20 bits moved and this is the one-slab statement of the same thing.
- **R-cl6-5 — `AsymptoticReflection` does not move.** G_A's 0 and G_q's `(1−εᵣ)/(1+εᵣ)` are k_ρ → ∞
  limits and the floor reaches the top only through `e^{−2k_ρh}`. **Assert it**; DCIM's first
  extraction is that constant and a silent move there would spoil every fit's decay without failing
  anything obvious.
- **R-cl6-6 — `SpectralGreens.CanSolveAt`'s floor is unchanged.** `MinElectricalThickness` is about
  the static limit and the floor does not move it; if it does, that is a finding and gets a number.
- **R-cl6-7 — the four measurements above, tabulated whether they pass or refuse.**
- **R-cl6-8** — `dotnet test tests/Engine.Tests`, run ONCE and triaged from the TRX.
  **Nothing outside this brief's own files should move**, because the extractor still writes a
  perfect floor until CL7. A move here means R-cl6-1 is not saying what it claims.

## Must NOT

- **Widen `Dcim.ValidatedRhoOverLambda`** — 6e-3, the one-slab tier. **Buying that tier is the entire
  purpose of this brief**; widening it to make measurement 3 pass would spend the thing being bought.
  `ValidatedRhoOverLambdaLayered`, `PoleLossCeiling` and `CalibratedPathProduct` are likewise untouched.
- Use the two-sided `coth`, or write any surface impedance not already in `PlanarSurfaceImpedance`.
- **Change `PlanarExtractor`, or anything under `src/Design` or `src/Ui`.** No run's answer moves in
  this brief. That is CL7's, deliberately, so the kernel change and the re-bless can be attributed
  separately — CL3 §3's lesson, one surface over.
- Touch the general kernel's `WallReflection`, CL1's sheet term, CL2's residual, or the port algebra.
- Collapse a perfect floor to the `Pec` kind to satisfy R-cl6-1. See CL4 §1.
- Mesh the ground plane, or give `GroundedSlab` more than one σ and one thickness.
- Ship a partial result silently. If measurement 2 refuses on both routes, the brief lands as a
  refusal with all four numbers and CL7 becomes a decision about the 2.6× tier instead.

## On completion

- `src/Engine/Mom/RESOLVED.md` `§CL6`: the four measurements with their numbers, which route Γ^q took
  and why, D5's reduction at a real σ, and the poles' table. If it refused, the named reason in §7's
  own style and what would have to change.
- **No `CLAUDE.md` edit.** §5's validated-range table gains the one-slab floor's own row and §7's
  refusal list may need a line. **Report them to the owner by file and line.**
